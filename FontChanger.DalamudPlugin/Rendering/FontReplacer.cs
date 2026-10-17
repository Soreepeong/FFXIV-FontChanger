using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Hooking;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Enums;
using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Shaders;
using FontChanger.DalamudPlugin.Rendering.Text;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's and FontNames is ours, over TerraFX's.
using FFXIVClientStructs.Interop;

/// <summary>Makes the game's text renderer draw glyphs rasterized at the size they are drawn at.</summary>
internal sealed unsafe partial class FontReplacer : IDisposable
{
    // Sizes in half pixels: below 4 px nothing is legible; a request over 255 px uses the 255 px copy, scaled up by the renderer.
    private const int MinHalfPx = 8;
    private const int MaxHalfPx = 2 * byte.MaxValue;

    private const int SlotBlockSize = 1024;

    // AtkResNode.Type of the first component node type; those after are components too.
    private const ushort FirstComponentNodeType = 1000;

    // When the atlas is full, a plane is emptied of glyphs not drawn this recently, at most this often.
    private const long KeepDrawnMs = 2000;
    private const long EvictIntervalMs = 1000;

    // What LayOutCharacter lays out a character a font has no glyph of as, in order of preference (packed UTF-8 values):
    // U+3013, '-', U+3400.
    private static readonly uint[] MissingGlyphSubstitutes = [0xE38093, 0x2D, 0xE39080];

    private readonly Hook<PickFontDelegate> _pickFontHook;
    private readonly Hook<GetGlyphDelegate> _getGlyphHook;
    private readonly Hook<FontCacheDelegate> _buildFontCacheHook;
    private readonly Hook<LayOutCharacterDelegate> _layOutCharacterHook;
    private readonly Hook<BuildFontDelegate> _buildFontHook;
    private readonly Hook<UnloadFontsDelegate> _unloadFontsHook;
    private readonly GameTextLayout _layout;

    // The character the game is laying out on this thread (LayOutCharacter).
    [ThreadStatic]
    private static LayoutContext _current;

    // A glyph of no width: the characters of a shaped cluster after its first.
    private GlyphSlot* _emptyGlyph;
    private readonly nint _rendererVtbl;
    private readonly nint _renderCountVtbl;

    // The glyphs made: blocks of slots the game reads them from (they must not move), and what is kept of each, by its
    // slot's id.
    private readonly List<Pointer<GlyphSlot>> _slotBlocks = [];
    private readonly List<Cell> _cells = [];
    private long _frameTick;

    // The atlases that ran out of room since they were last made room in, and when that was.
    private readonly HashSet<GlyphAtlas> _fullAtlases = [];
    private readonly Dictionary<GlyphAtlas, long> _lastEviction = [];

    // Glyphs made from game glyphs, placed in the atlas, whose pixels are read from the game's textures at the next upload.
    private readonly List<Pointer<AtkFontManager.Glyph>> _gameReads = [];
    private readonly TextureReader _gameTextures = new();
    private readonly FontServices _services;

    // The glyphs of each game font family go to an atlas of its own, so that one family's text can't push another's out:
    // AXIS, the family of most of the UI's text, has pages of its own size and count (AtlasSettings).
    private readonly Dictionary<string, GlyphAtlas> _atlases = new(StringComparer.OrdinalIgnoreCase);

    // What a copy's texture slots without a page hold, so that a game glyph slipping through draws nothing.
    private readonly KernelTextureWrapper _blankTexture;

    // Copy texture slots are changed only on the thread the game lays text out on; pages added elsewhere wait for it.
    private readonly int _mainThreadId;
    private readonly HashSet<GlyphAtlas> _pendingPageAtlases = [];

    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx), Pointer<AtkFontManager.Font>> _copiesByKey = [];
    private readonly Dictionary<Pointer<AtkFontManager.Font>, CopyInfo> _copies = [];
    private readonly Dictionary<(MaterializedFont Face, int HalfPx, string FontName), SizedFont> _sizedFonts = [];

    // The system fonts faces draw what they lack with.
    private readonly SystemFallbackFonts _fallbackFonts;

    // The faces glyphs come from: the preset's by game font name. The face of game fonts no preset gives one: their own
    // glyphs, with system fonts for what they lack.
    private MaterializedFont _gameFace;
    private readonly Dictionary<string, MaterializedFont> _presetFaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled = true;

    /// <summary>
    /// Undoes the setup in reverse: what the hooks hand the game (copies, glyphs) outlives them, and the faces and the
    /// services outlive what draws with them. Mutable fields are registered as actions, read when they run.
    /// </summary>
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    public FontReplacer()
    {
        try
        {
            var pickFont = AtkFontManager.FontSet.Addresses.PickFont.Value;
            var getGlyph = AtkFontManager.FontSet.Addresses.GetGlyph.Value;
            var layOutCharacter = AtkFontAnalyzerBase.Addresses.LayOutCharacter.Value;
            var buildFontCache = AtkTextNode.Addresses.BuildFontCache.Value;
            var buildFont = AtkFontManager.Addresses.BuildFont.Value;
            var unloadFonts = AtkFontManager.Addresses.UnloadFonts.Value;
            this._mainThreadId = Environment.CurrentManagedThreadId;
            this._rendererVtbl = AtkFontAnalyzerRenderer.Addresses.StaticVirtualTable.Value;
            this._renderCountVtbl = AtkFontAnalyzerRenderCount.Addresses.StaticVirtualTable.Value;

            this._services = this._finalizer.Add(new FontServices());
            this._fallbackFonts = this._finalizer.Add(new SystemFallbackFonts(this._services));
            this._finalizer.Add(this.DisposeFaces);
            this._gameFace = MaterializedFont.CreateGame(this._services, true, this._fallbackFonts);
            try
            {
                FontNames.Initialize();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "The game's font names weren't found; presets can't be applied");
            }

            this._finalizer.Add(this._gameTextures);
            this._blankTexture = this._finalizer.Add(CreateBlankTexture());
            this._finalizer.Add(() => {
                foreach (var atlas in this._atlases.Values)
                    atlas.Dispose();
                this._atlases.Clear();
            });
            this._emptyGlyph = (GlyphSlot*)NativeMemory.AllocZeroed((nuint)sizeof(GlyphSlot));
            this._emptyGlyph->Id = -1;
            this._finalizer.Add(() => {
                NativeMemory.Free(this._emptyGlyph);
                this._emptyGlyph = null;
            });
            this._finalizer.Add(this.FreeGlyphs);
            this._layout = this._finalizer.Add(new GameTextLayout(this._services, this, &this._emptyGlyph->Glyph));
            this._finalizer.Add(() => {
                foreach (var copy in this._copies.Keys)
                    NativeMemory.Free(copy.Value);
                this._copies.Clear();
                this._copiesByKey.Clear();
            });

            // Font sets may still have a copy picked: forgotten once no hook can pick one again.
            this._finalizer.Add(ForgetPickedFonts);
            this._getGlyphHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<GetGlyphDelegate>(getGlyph, this.GetGlyphDetour));
            this._pickFontHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<PickFontDelegate>(pickFont, this.PickFontDetour));
            this._buildFontCacheHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<FontCacheDelegate>(buildFontCache, this.BuildFontCacheDetour));
            this._layOutCharacterHook =
                this._finalizer.Add(Plugin.GameInterop.HookFromAddress<LayOutCharacterDelegate>(layOutCharacter, this.LayOutCharacterDetour));
            this._buildFontHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<BuildFontDelegate>(buildFont, this.BuildFontDetour));
            this._unloadFontsHook = this._finalizer.Add(Plugin.GameInterop.HookFromAddress<UnloadFontsDelegate>(unloadFonts, this.UnloadFontsDetour));
            this._finalizer.Add(this._subpixelShaders);
            this._finalizer.Add(this._edgeShader);
            this._getGlyphHook.Enable();
            this._pickFontHook.Enable();
            this._buildFontCacheHook.Enable();
            this._layOutCharacterHook.Enable();
            this._buildFontHook.Enable();
            this._unloadFontsHook.Enable();

            // First of all: the game's font caches come back while the hooks are still there to build them.
            this._finalizer.Add(() => this.Enabled = false);

            // Caches built so far hold the game's glyphs.
            this.UpdateFontCaches();
        }
        catch
        {
            try
            {
                this._finalizer.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Undoing a failed font replacer setup failed");
            }

            throw;
        }
    }

    private delegate AtkFontManager.Font* PickFontDelegate(AtkFontManager.FontSet* set, byte useCache);

    private delegate AtkFontManager.Glyph* GetGlyphDelegate(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font);

    private delegate void FontCacheDelegate(AtkTextNode* node);

    private delegate byte* LayOutCharacterDelegate(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state);

    private delegate int BuildFontDelegate(AtkFontManager* manager, ushort index);

    private delegate int UnloadFontsDelegate(AtkFontManager* manager);

    /// <summary>Makes the texture of copy slots without a page: a few texels of nothing.</summary>
    private static KernelTextureWrapper CreateBlankTexture()
    {
        const int Side = 4;
        var texture = new KernelTextureWrapper(Side, Side, "FontChanger.DalamudPlugin blank");
        try
        {
            using var context = texture.Resource.Value->GetImmediateContext();
            var zeros = stackalloc byte[Side * Side * 4];
            context.Value->UpdateSubresource(texture.Resource.Value, 0, null, zeros, Side * 4, 0);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Gets the services: the DirectWrite factory and the system's fonts the glyphs are drawn with.</summary>
    public FontServices Services => this._services;

    /// <summary>
    /// Raised on the framework thread when text drawn earlier no longer matches (the replacement was switched, or the
    /// glyphs were rebuilt), for what keeps drawn text around (the nameplate bakes).
    /// </summary>
    public event Action? TextInvalidated;

    /// <summary>
    /// Gets or sets the faces of the game fonts. Framework thread, between frames; setting it makes every glyph again, even
    /// to the same value.
    /// </summary>
    public FaceSettings Faces
    {
        get => this._faces;
        set
        {
            Misc.DebugAssertMainThread();
            this._faces = value;
            this.ApplyFaces(value.Preset, value.SystemFallback);
        }
    }

    private FaceSettings _faces;

    private void ApplyFaces(Preset? preset, bool systemFallback)
    {
        this._layout.Clear();
        this.DisposeFaces();
        this._sizedFonts.Clear();
        this._gameFace = MaterializedFont.CreateGame(this._services, systemFallback, this._fallbackFonts);
        foreach (var (name, def) in preset?.Faces ?? new Dictionary<string, Face>())
        {
            try
            {
                this._presetFaces[name] = new(this._services, def, systemFallback, this._fallbackFonts);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Setting up the face {name} failed; its fonts use the game's glyphs", name);
            }
        }

        // Copies made so far take the glyphs and metrics of their new faces.
        this.ResyncCopies();
        this.RebuildGlyphs();
    }

    private void DisposeFaces()
    {
        foreach (var face in this._presetFaces.Values)
            face.Dispose();
        this._presetFaces.Clear();
        this._gameFace?.Dispose();
    }

    /// <summary>
    /// Gets whether the game's device can sample and update B4G4R4A4 textures: DXGI 1.2 devices can, but the game also runs
    /// on Direct3D 11 devices without it. Atlases take B8G8R8A8 pages if not, whatever the settings say.
    /// </summary>
    public bool Supports4444 => this._supports4444 ??= this.CheckSupports4444();

    private bool? _supports4444;

    private bool CheckSupports4444()
    {
        var supported = false;
        try
        {
            using var device = this._blankTexture.Resource.Value->GetDevice();
            uint support;
            var hr = device.Value->CheckFormatSupport(DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM, &support);
            const uint Needed = (uint)(D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_SHADER_SAMPLE);
            supported = hr.SUCCEEDED && (support & Needed) == Needed;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Checking the support for B4G4R4A4 textures failed");
        }

        if (!supported)
            Plugin.Log.Warning("The game's device doesn't support B4G4R4A4 textures; atlases use B8G8R8A8");
        return supported;
    }

    /// <summary>
    /// Gets or sets the size and number of the atlas pages, clamped. Framework thread, between frames; a change remakes the
    /// atlases and every glyph.
    /// </summary>
    public AtlasSettings Atlas
    {
        get => this._atlas;
        set
        {
            Misc.DebugAssertMainThread();
            value = value.Clamped();
            if (value == this._atlas)
                return;
            this._atlas = value;
            this.RemakeAtlases();
        }
    }

    private AtlasSettings _atlas = new AtlasSettings().Clamped();

    /// <summary>Remakes the atlases with the current settings, and every glyph. Framework thread, between frames.</summary>
    private void RemakeAtlases()
    {
        Misc.DebugAssertMainThread();
        this.RebuildGlyphs();

        // Glyphs and copies refer to the old atlases' pages, so they go first.
        this._lastEviction.Clear();
        lock (this._pendingPageAtlases)
            this._pendingPageAtlases.Clear();
        this._sizedFonts.Clear();
        foreach (var atlas in this._atlases.Values)
            atlas.Dispose();
        this._atlases.Clear();
        this.ResyncCopies();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>
    /// Gets or sets the edge outline's (FontEdgePS) width at each text size. A font's edge width is the step the shader
    /// samples by, one texel of the texture width the font claims; above 1 px, glyph boxes get empty margins for it, as
    /// the edge is only drawn within a glyph's box and one pixel around it. Framework thread; glyphs are made again if a
    /// size's margin changes.
    /// </summary>
    public EdgeSettings Edge
    {
        get => this._edge;
        set => this.ApplyEdge(value.Drawn());
    }

    private EdgeSettings _edge = new();

    private void ApplyEdge(EdgeSettings edge)
    {
        Misc.DebugAssertMainThread();
        if (edge == this._edge)
            return;

        // Glyphs made now show only from the frame after (Upload), so remaking them blanks text for a frame: only when a
        // size's margin changes.
        var marginChanged = this._sizedFonts.Values.Any(s => GetEdgeMargin(edge, s.Px) != GetEdgeMargin(this._edge, s.Px));
        this._edge = edge;

        // The plugin's edge shader draws a round edge of any width, which each font sets by the texture width it claims;
        // unless edges are the game's own (1 px at every size). The game's draws a fixed pattern, made wider only by
        // claiming a narrower texture (blocky past about 1.5 px): the fallback if the plugin's can't be used.
        try
        {
            this._edgeShader.SetSoftness(edge.Enabled && edge != new EdgeSettings { Enabled = true } ? edge.Softness : null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Using the plugin's edge shader failed; edges are widened with the game's");
            this._edgeShader.SetSoftness(null);
        }

        // Copies claim their texture width by the edge.
        foreach (var (copy, info) in this._copies)
            this.Sync(copy, info);
        if (marginChanged)
            this.RebuildGlyphs();
        else
            this.TextInvalidated?.Invoke();
    }

    private readonly EdgeShader _edgeShader = new();

    private readonly SubpixelShaders _subpixelShaders = new();

    /// <summary>Between frames, on the framework thread: gives copies the atlas pages added off it.</summary>
    public void EndFrame() => this.ApplyPendingPages();

    /// <summary>
    /// Gets or sets the subpixel text settings. Framework thread, between frames; switching it on or off remakes the
    /// atlases and every glyph.
    /// </summary>
    public SubpixelShaders.SettingsRecord Subpixel
    {
        get => this._subpixel;
        set => this.ApplySubpixel(value);
    }

    private SubpixelShaders.SettingsRecord _subpixel;

    /// <summary>
    /// Gets or sets what draws fonts set to DirectWrite for vertical subpixel orders; changing it makes their glyphs again.
    /// Framework thread.
    /// </summary>
    public VerticalSubpixelRenderer VerticalSubpixel
    {
        get => this._services.DirectWrite.VerticalWithFreeType ? VerticalSubpixelRenderer.FreeType : VerticalSubpixelRenderer.DirectWrite;
        set
        {
            Misc.DebugAssertMainThread();
            var withFreeType = value == VerticalSubpixelRenderer.FreeType;
            if (withFreeType == this._services.DirectWrite.VerticalWithFreeType)
                return;
            this._services.DirectWrite.VerticalWithFreeType = withFreeType;
            if (this._services.SubpixelOrder.IsVertical)
                this.RebuildGlyphs();
        }
    }

    private void ApplySubpixel(SubpixelShaders.SettingsRecord settings)
    {
        Misc.DebugAssertMainThread();
        if (settings == this._subpixel)
            return;
        this._subpixel = settings;
        try
        {
            this._subpixelShaders.Apply(settings);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Subpixel text is off");
            this._subpixelShaders.Apply(new());
        }

        var active = this._subpixelShaders.Active;
        var order = active ? settings.Order : SubpixelOrder.None;
        var atlasesChange = this._atlases.Values.Any(a => a.Subpixel != active);
        if (order == this._services.SubpixelOrder && !atlasesChange)
            return;
        this._services.SubpixelOrder = order;
        if (atlasesChange)
            this.RemakeAtlases();
        else
            this.RebuildGlyphs();
    }

    /// <summary>
    /// Forgets every glyph and empties the atlases, so they are rasterized again with the current settings. Framework
    /// thread, between frames. Nameplates already baked keep their pixels until the game bakes them again.
    /// </summary>
    public void RebuildGlyphs()
    {
        Misc.DebugAssertMainThread();
        this._layout.Clear();
        this._fullAtlases.Clear();
        this._gameReads.Clear();
        foreach (var sized in this._sizedFonts.Values)
        {
            sized.Glyphs.Clear();
            sized.GameGlyphs.Clear();
            sized.ConvertedGlyphs.Clear();
        }

        this.FreeGlyphs();
        foreach (var atlas in this._atlases.Values)
            atlas.Clear();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>Gets or sets whether the game draws with the replaced fonts. Framework thread (or the draw callback).</summary>
    public bool Enabled
    {
        get => this._enabled;
        set
        {
            Misc.DebugAssertMainThread();
            if (this._enabled == value)
                return;
            this._enabled = value;
            ForgetPickedFonts();
            this.UpdateFontCaches();
            this.TextInvalidated?.Invoke();
        }
    }

    /// <summary>Undoes everything; a step that fails is collected, and the others still run. Framework thread.</summary>
    public void Dispose()
    {
        Misc.DebugAssertMainThread();
        this._finalizer.Dispose();
    }

    /// <summary>
    /// The faces of the game fonts: a preset's for the game fonts of their names (the game's glyphs for the others, or for
    /// all if null), drawing characters they lack with system fonts if <paramref name="SystemFallback"/>, else the game's.
    /// </summary>
    internal readonly record struct FaceSettings(Preset? Preset = null, bool SystemFallback = true);
}
