using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Hooking;

using FontChanger.Presets.Records;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Shaders;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's over TerraFX's.
using FFXIVClientStructs.Interop;

/// <summary>
/// Makes the game's text renderer draw glyphs rasterized at the size they are drawn at.
/// </summary>
/// <remarks>
/// PickFont is swapped for a copy sized exactly as requested, so the renderer's scale is 1; GetGlyph is answered for a copy
/// from its own glyphs, rasterized on first use. Nothing keeps glyph pointers between frames.
/// </remarks>
internal sealed unsafe class FontReplacer : IDisposable
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
    private readonly TextShaper _shaper;

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
    private readonly GameTextureReader _gameTextures = new();
    private readonly GlyphRasterizer _rasterizer;

    // The glyphs of each game font family go to an atlas of its own, so that one family's text can't push another's out:
    // AXIS, the family of most of the UI's text, has pages of its own size and count (AtlasSettings).
    private readonly Dictionary<string, GlyphAtlas> _atlases = new(StringComparer.OrdinalIgnoreCase);

    // What a copy's texture slots without a page hold, so that a game glyph slipping through draws nothing.
    private readonly GameTextureWrapper _blankTexture;

    // Copy texture slots are changed only on the thread the game lays text out on; pages added elsewhere wait for it.
    private readonly int _mainThreadId;
    private readonly HashSet<GlyphAtlas> _pendingPageAtlases = [];

    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx), Pointer<AtkFontManager.Font>> _copiesByKey = [];
    private readonly Dictionary<Pointer<AtkFontManager.Font>, CopyInfo> _copies = [];
    private readonly Dictionary<(ReplacementFace Face, int HalfPx, string FontName), SizedFont> _sizedFonts = [];

    // The faces glyphs come from: the preset's by game font name, or the built-in one.
    private readonly ReplacementFace _builtInFace;

    // The face of game fonts no preset gives one: their own glyphs, with fallbacks for what they lack.
    private ReplacementFace _gameFace;
    private readonly Dictionary<string, ReplacementFace> _presetFaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled = true;

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

            this._rasterizer = new();
            this._builtInFace = ReplacementFace.CreateBuiltIn(this._rasterizer);
            this._gameFace = ReplacementFace.CreateGame(this._rasterizer, true, this._builtInFace);
            try
            {
                GameFontNames.Initialize();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "The game's font names weren't found; presets can't be applied");
            }

            this._emptyGlyph = (GlyphSlot*)NativeMemory.AllocZeroed((nuint)sizeof(GlyphSlot));
            this._emptyGlyph->Id = -1;
            this._blankTexture = CreateBlankTexture();
            this._shaper = new(this._rasterizer, this, &this._emptyGlyph->Glyph);
            this._pickFontHook = Plugin.GameInterop.HookFromAddress<PickFontDelegate>(pickFont, this.PickFontDetour);
            this._getGlyphHook = Plugin.GameInterop.HookFromAddress<GetGlyphDelegate>(getGlyph, this.GetGlyphDetour);
            this._buildFontCacheHook = Plugin.GameInterop.HookFromAddress<FontCacheDelegate>(buildFontCache, this.BuildFontCacheDetour);
            this._layOutCharacterHook = Plugin.GameInterop.HookFromAddress<LayOutCharacterDelegate>(layOutCharacter, this.LayOutCharacterDetour);
            this._buildFontHook = Plugin.GameInterop.HookFromAddress<BuildFontDelegate>(buildFont, this.BuildFontDetour);
            this._unloadFontsHook = Plugin.GameInterop.HookFromAddress<UnloadFontsDelegate>(unloadFonts, this.UnloadFontsDetour);
            this._getGlyphHook.Enable();
            this._pickFontHook.Enable();
            this._buildFontCacheHook.Enable();
            this._layOutCharacterHook.Enable();
            this._buildFontHook.Enable();
            this._unloadFontsHook.Enable();

            // Caches built so far hold the game's glyphs.
            this.UpdateFontCaches();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    private delegate AtkFontManager.Font* PickFontDelegate(AtkFontManager.FontSet* set, byte useCache);

    private delegate AtkFontManager.Glyph* GetGlyphDelegate(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font);

    private delegate void FontCacheDelegate(AtkTextNode* node);

    private delegate byte* LayOutCharacterDelegate(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state);

    private delegate int BuildFontDelegate(AtkFontManager* manager, ushort index);

    private delegate int UnloadFontsDelegate(AtkFontManager* manager);

    private static void NullSafeDispose<T>(T? obj) where T : class, IDisposable => obj?.Dispose();

    /// <summary>Makes the texture of copy slots without a page: a few texels of nothing.</summary>
    private static GameTextureWrapper CreateBlankTexture()
    {
        const int Side = 4;
        var texture = new GameTextureWrapper(Side, Side, "FontChanger.DalamudPlugin blank");
        try
        {
            using var device = default(ComPtr<ID3D11Device>);
            texture.Resource->GetDevice(device.GetAddressOf());
            using var context = default(ComPtr<ID3D11DeviceContext>);
            device.Get()->GetImmediateContext(context.GetAddressOf());
            var zeros = stackalloc byte[Side * Side * 4];
            new Span<byte>(zeros, Side * Side * 4).Clear();
            context.Get()->UpdateSubresource(texture.Resource, 0, null, zeros, Side * 4, 0);
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    /// <summary>Gets the shaper, which also finds line breaks.</summary>
    public TextShaper Shaper => this._shaper;

    /// <summary>
    /// Raised on the framework thread when text drawn earlier no longer matches (the replacement was switched, or the
    /// glyphs were rebuilt), for what keeps drawn text around (the nameplate bakes).
    /// </summary>
    public event Action? TextInvalidated;

    /// <summary>Gets the built-in face: system fonts, which draw what faces lack glyph by glyph.</summary>
    public ReplacementFace BuiltInFace => this._builtInFace;

    /// <summary>
    /// Uses a preset's faces for the game fonts of their names (the game's glyphs for the others, or for all if null),
    /// drawing characters they lack with system fonts if <paramref name="systemFallback"/>, else the game's. Framework
    /// thread, between frames. Every glyph is made again.
    /// </summary>
    public void SetPreset(Preset? preset, bool systemFallback)
    {
        this._shaper.ClearFormats();
        this.DisposePresetFaces();
        this._sizedFonts.Clear();
        this._gameFace = ReplacementFace.CreateGame(this._rasterizer, systemFallback, this._builtInFace);
        foreach (var (name, def) in preset?.Faces ?? new Dictionary<string, Face>())
        {
            try
            {
                this._presetFaces[name] = new(this._rasterizer, def, systemFallback, this._builtInFace);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Setting up the face {name} failed; its fonts use the game's glyphs", name);
            }
        }

        // Copies made so far take the glyphs and metrics of their new faces.
        foreach (var (copy, info) in this._copies)
        {
            info.Sized = this.GetSized(info.Original, info.HalfPx);
            this.Sync(copy, info);
        }

        this.RebuildGlyphs();
    }

    private void DisposePresetFaces()
    {
        foreach (var face in this._presetFaces.Values)
            NullSafeDispose(face);
        this._presetFaces.Clear();
        NullSafeDispose(this._gameFace);
    }

    /// <summary>
    /// Gets the glyphs of a game font's family at a size, shared by the fonts of one name (a font and its lobby version),
    /// going to the atlas of the family. At the size of one of the family's faces, that face's glyphs with the metrics they
    /// have for its game font; at another, each glyph comes from the largest face that draws it itself (<see
    /// cref="SizedFont.Pick"/>), with the metrics of the face that draws the most glyphs itself.
    /// </summary>
    private SizedFont GetSized(Pointer<AtkFontManager.Font> original, int halfPx)
    {
        var px = halfPx / 2f;
        GameFontNames.TryGetFaceName(original, out var originalName);
        var names = new List<(string Name, float Size)>();
        var faces = new List<(ReplacementFace Face, Pointer<AtkFontManager.Font> Game)>();
        if (originalName is not null)
        {
            foreach (var (name, size) in GameFontNames.FacesOf(Preset.FamilyOf(originalName)).OrderByDescending(f => f.Size))
            {
                var font = original;
                if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase) && !GameFontNames.TryFindFont(name, out font))
                    continue;
                if (font.IsNull || !font.Value->Flags.HasFlag(AtkFontManager.FontFlags.Ready) || font.Value->Size <= 0)
                    continue;
                faces.Add((this.FaceOf(name), font));
                names.Add((name, size));
            }
        }

        string fontName;
        ReplacementFace face;
        Pointer<AtkFontManager.Font> game;
        if (faces.Count == 0)
        {
            fontName = originalName ?? $"0x{(nint)original.Value:X}";
            face = originalName is not null ? this.FaceOf(originalName) : this._gameFace;
            game = original;
            faces = [(face, game)];
        }
        else if (names.FindIndex(f => MathF.Abs(f.Size - px) < 0.01f) is var exact and >= 0)
        {
            fontName = names[exact].Name;
            (face, game) = faces[exact];
            faces = [(face, game)];
        }
        else
        {
            var best = 0;
            for (var i = 1; i < faces.Count; i++)
            {
                if (faces[i].Face.OwnGlyphCount > faces[best].Face.OwnGlyphCount)
                    best = i;
            }

            fontName = names[best].Name;
            (face, game) = faces[best];
        }

        if (!this._sizedFonts.TryGetValue((face, halfPx, fontName), out var sized))
        {
            var (ascent, lineHeight) = face.GetLineMetrics(px, game);
            sized = new(face, px, ascent, lineHeight, game, this.GetAtlas(Preset.FamilyOf(fontName)), faces);
            this._sizedFonts.Add((face, halfPx, fontName), sized);
        }

        return sized;
    }

    /// <summary>Gets the face of a game font name: the preset's, or the game's.</summary>
    private ReplacementFace FaceOf(string faceName) => this._presetFaces.GetValueOrDefault(faceName) ?? this._gameFace;

    /// <summary>Gets the atlas of a game font family, made on its first use.</summary>
    private GlyphAtlas GetAtlas(string family)
    {
        if (this._atlases.TryGetValue(family, out var atlas))
            return atlas;

        // FontSubpixelPS tells subpixel pages from the game's textures by their odd width; grayscale pages get an even one.
        var subpixel = this._subpixelShaders.Active;
        var s = this.Atlas;
        var (width, height, maxPages) = string.Equals(family, "AXIS", StringComparison.OrdinalIgnoreCase)
                                            ? (s.AxisPageWidth, s.AxisPageHeight, s.AxisMaxPages)
                                            : (s.OtherPageWidth, s.OtherPageHeight, s.OtherMaxPages);
        if (subpixel == (width % 2 == 0))
            width--;
        var format = s.Format == AtlasTextureFormat.Bgra4444 && this.Supports4444 ? AtlasTextureFormat.Bgra4444 : AtlasTextureFormat.Bgra8888;
        atlas = new(width, height, maxPages, family, subpixel, format);
        atlas.PageAdded += () => this.OnPageAdded(atlas);
        this._atlases.Add(family, atlas);
        atlas.EnsurePage();
        return atlas;
    }

    /// <summary>
    /// Gets whether the game's device can sample and update B4G4R4A4 textures: DXGI 1.2 devices can, but the game also runs
    /// on Direct3D 11 devices without it. Atlases take B8G8R8A8 pages if not, whatever the settings say.
    /// </summary>
    public bool Supports4444
    {
        get
        {
            if (this._supports4444 is { } known)
                return known;

            var supported = false;
            try
            {
                if (this._blankTexture is { } texture)
                {
                    using var device = default(ComPtr<ID3D11Device>);
                    texture.Resource->GetDevice(device.GetAddressOf());
                    uint support;
                    var hr = device.Get()->CheckFormatSupport(DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM, &support);
                    const uint Needed = (uint)(D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_SHADER_SAMPLE);
                    supported = hr.SUCCEEDED && (support & Needed) == Needed;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Checking the support for B4G4R4A4 textures failed");
            }

            if (!supported)
                Plugin.Log.Warning("The game's device doesn't support B4G4R4A4 textures; atlases use B8G8R8A8");
            this._supports4444 = supported;
            return supported;
        }
    }

    private bool? _supports4444;

    /// <summary>Gets the size and number of the atlas pages.</summary>
    public AtlasSettings Atlas { get; private set; } = new AtlasSettings().Clamped();

    /// <summary>Sets the size and number of the atlas pages. Framework thread, between frames; a change remakes the atlases and every glyph.</summary>
    public void SetAtlas(AtlasSettings settings)
    {
        settings = settings.Clamped();
        if (settings == this.Atlas)
            return;
        this.Atlas = settings;
        this.RemakeAtlases();
    }

    /// <summary>Remakes the atlases with the current settings, and every glyph. Framework thread, between frames.</summary>
    private void RemakeAtlases()
    {
        this.RebuildGlyphs();

        // Glyphs and copies refer to the old atlases' pages, so they go first.
        this._lastEviction.Clear();
        lock (this._pendingPageAtlases)
            this._pendingPageAtlases.Clear();
        this._sizedFonts.Clear();
        foreach (var atlas in this._atlases.Values)
            atlas.Dispose();
        this._atlases.Clear();
        foreach (var (copy, info) in this._copies)
        {
            info.Sized = this.GetSized(info.Original, info.HalfPx);
            this.Sync(copy, info);
        }

        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>
    /// Gets the edge outline's (FontEdgePS) width at each text size. A font's edge width is the step the shader samples
    /// by, one texel of the texture width the font claims; above 1 px, glyph boxes get empty margins for it, as the edge
    /// is only drawn within a glyph's box and one pixel around it.
    /// </summary>
    public EdgeSettings Edge { get; private set; } = new();

    /// <summary>Sets the edge outline's width at each text size. Framework thread; glyphs are made again if a size's margin changes.</summary>
    public void SetEdge(EdgeSettings edge)
    {
        edge = edge.Clamped();
        if (edge == this.Edge)
            return;

        // Glyphs made now show only from the frame after (Upload), so remaking them blanks text for a frame: only when a
        // size's margin changes.
        var marginChanged = this._sizedFonts.Values.Any(s => GetEdgeMargin(edge, s.Px) != GetEdgeMargin(this.Edge, s.Px));
        this.Edge = edge;

        // The plugin's edge shader draws a round edge of any width, which each font sets by the texture width it claims;
        // unless edges are the game's own (1 px at every size). The game's draws a fixed pattern, made wider only by
        // claiming a narrower texture (blocky past about 1.5 px): the fallback if the plugin's can't be used.
        try
        {
            this._edgeShader.Set(edge != new EdgeSettings(), edge.Softness);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Using the plugin's edge shader failed; edges are widened with the game's");
            this._edgeShader.Set(false);
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

    private readonly SubpixelTextShaders _subpixelShaders = new();

    /// <summary>Between frames, after the UI's draw commands are processed; <see cref="Upload"/> does it too.</summary>
    public void EndFrame()
    {
        if (Environment.CurrentManagedThreadId == this._mainThreadId)
            this.ApplyPendingPages();
        this._subpixelShaders.EndFrame();
    }

    /// <summary>Gets the subpixel text settings.</summary>
    public SubpixelSettings Subpixel { get; private set; } = new();

    /// <summary>
    /// Sets the subpixel text settings. Framework thread, between frames; switching it on or off remakes the atlases and
    /// every glyph.
    /// </summary>
    public void SetSubpixel(SubpixelSettings settings)
    {
        if (settings == this.Subpixel)
            return;
        this.Subpixel = settings;
        try
        {
            this._subpixelShaders.Set(settings);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Subpixel text is off");
            this._subpixelShaders.Set(new());
        }

        var active = this._subpixelShaders.Active;
        var order = active ? settings.Order : SubpixelOrder.None;
        var atlasesChange = this._atlases.Values.Any(a => a.Subpixel != active);
        if (order == this._rasterizer.SubpixelOrder && !atlasesChange)
            return;
        this._rasterizer.SubpixelOrder = order;
        if (atlasesChange)
            this.RemakeAtlases();
        else
            this.RebuildGlyphs();
    }

    /// <summary>Gets the empty pixels <see cref="FinishGlyph"/> adds around a glyph of a size, on each side.</summary>
    internal int GetEdgeMargin(SizedFont sized) => GetEdgeMargin(this.Edge, sized.Px);

    private static int GetEdgeMargin(EdgeSettings edge, float px) => Math.Max(0, (int)MathF.Ceiling(edge.GetReach(px)) - 1);

    /// <summary>
    /// Makes a glyph rasterized for a size (and adjusted as its element says) ready to be placed: surrounded with empty
    /// pixels for a wide edge, which is only drawn within a glyph's box and the pixel around it.
    /// </summary>
    internal RasterGlyph FinishGlyph(SizedFont sized, RasterGlyph r)
    {
        var m = this.GetEdgeMargin(sized);
        if (m == 0 || r.Width == 0)
            return r;
        var w = r.Width + (2 * m);
        var h = r.Height + (2 * m);
        var alpha = new byte[w * h];
        for (var y = 0; y < r.Height; y++)
            r.Alpha.AsSpan(y * r.Width, r.Width).CopyTo(alpha.AsSpan(((y + m) * w) + m));
        byte[]? lcd = null;
        if (r.Lcd is { } source)
        {
            lcd = new byte[w * h * 3];
            for (var y = 0; y < r.Height; y++)
                source.AsSpan(y * r.Width * 3, r.Width * 3).CopyTo(lcd.AsSpan((((y + m) * w) + m) * 3));
        }

        return r with { Left = r.Left - m, Top = r.Top - m, Width = w, Height = h, Alpha = alpha, Lcd = lcd };
    }

    /// <summary>
    /// Forgets every glyph and empties the atlases, so they are rasterized again with the current settings. Framework
    /// thread, between frames. Nameplates already baked keep their pixels until the game bakes them again.
    /// </summary>
    public void RebuildGlyphs()
    {
        this._shaper.Clear();
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
            if (this._enabled == value)
                return;
            this._enabled = value;
            ForgetPickedFonts();
            this.UpdateFontCaches();
            this.TextInvalidated?.Invoke();
        }
    }

    public void Dispose()
    {
        this.Enabled = false;
        NullSafeDispose(this._edgeShader);
        NullSafeDispose(this._subpixelShaders);
        NullSafeDispose(this._unloadFontsHook);
        NullSafeDispose(this._buildFontHook);
        NullSafeDispose(this._layOutCharacterHook);
        NullSafeDispose(this._buildFontCacheHook);
        NullSafeDispose(this._pickFontHook);
        NullSafeDispose(this._getGlyphHook);
        ForgetPickedFonts();

        foreach (var copy in this._copies.Keys)
            NativeMemory.Free(copy.Value);
        this._copies.Clear();
        this._copiesByKey.Clear();
        NullSafeDispose(this._shaper);
        this.FreeGlyphs();
        if (this._emptyGlyph is not null)
        {
            NativeMemory.Free(this._emptyGlyph);
            this._emptyGlyph = null;
        }

        foreach (var atlas in this._atlases.Values)
            NullSafeDispose(atlas);
        this._atlases.Clear();
        NullSafeDispose(this._blankTexture);
        NullSafeDispose(this._gameTextures);
        this.DisposePresetFaces();
        NullSafeDispose(this._builtInFace);
        NullSafeDispose(this._rasterizer);
    }

    /// <summary>Makes every font set pick its font again on its next use.</summary>
    private static void ForgetPickedFonts()
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->AtkFontManager;
        if (manager is null || manager->FontSets is null)
            return;
        for (var i = 0; i < AtkFontManager.FontSetCount; i++)
        {
            var set = manager->FontSets + i;
            set->CurrentFont = null;
            set->CurrentFontSize = 0;
        }
    }

    /// <summary>
    /// A font cache keeps the glyph pointers of the text as laid out when it was built, at the node's unscaled size
    /// (the cache build), and draws use them while picking the font for the on-screen size. Glyphs of a copy
    /// only fit the copy of their size, and a copy's glyphs must not outlive the plugin, so while the replacement is on
    /// nodes get no cache: their text is laid out at each draw, as for nodes that never asked for one.
    /// </summary>
    private void BuildFontCacheDetour(AtkTextNode* node)
    {
        if (this._enabled)
            node->FreeFontCache();
        else
            this._buildFontCacheHook.Original(node);
    }

    /// <summary>
    /// Brings every loaded text node's font cache in line with <see cref="Enabled"/>: freed while on, rebuilt (for the
    /// nodes that ask for one) while off.
    /// </summary>
    private void UpdateFontCaches()
    {
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is null)
            return;
        ref var loaded = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            var unit = loaded.Entries[i].Value;
            if (unit is not null)
                this.UpdateFontCaches(&unit->UldManager, 0);
        }
    }

    private void UpdateFontCaches(AtkUldManager* uld, int depth)
    {
        if (depth > 16 || uld->NodeList is null)
            return;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
                continue;
            var type = (ushort)node->Type;
            if (type == (ushort)NodeType.Text)
            {
                var text = (AtkTextNode*)node;
                if (this._enabled)
                {
                    if (text->FontCacheHandle != 0)
                        text->FreeFontCache();
                }
                else if ((text->TextFlags & TextFlags.FontCache) != 0)
                {
                    this._buildFontCacheHook.Original(text);
                }
            }
            else if (type >= FirstComponentNodeType)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null)
                    this.UpdateFontCaches(&component->UldManager, depth + 1);
            }
        }
    }

    /// <summary>
    /// The game built a font again in place (a reload, a switch between the lobby's fonts and the game's), freeing the
    /// glyph map and glyphs its copies borrow: they take them again, with the face of the font's name now.
    /// </summary>
    private int BuildFontDetour(AtkFontManager* manager, ushort index)
    {
        var result = this._buildFontHook.Original(manager, index);
        try
        {
            if (index < manager->FontCount)
                this.OnFontBuilt(manager->Fonts + index);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Refreshing the copies of a rebuilt font failed; disabling");
            this._enabled = false;
            ForgetPickedFonts();
        }

        return result;
    }

    private void OnFontBuilt(AtkFontManager.Font* font)
    {
        var any = this.ForgetGameGlyphsOf(font);
        foreach (var (copy, info) in this._copies)
        {
            if (info.Original != font)
                continue;
            info.Sized = this.GetSized(font, info.HalfPx);
            this.Sync(copy, info);
            any = true;
        }

        if (any)
            this.TextInvalidated?.Invoke();
    }

    /// <summary>
    /// Forgets the glyphs made from a game font's glyphs, whose pixels came from its old textures (and the converted ones
    /// are keyed by its old glyph pointers). True if any sized font had some.
    /// </summary>
    private bool ForgetGameGlyphsOf(Pointer<AtkFontManager.Font> font)
    {
        var any = false;
        foreach (var sized in this._sizedFonts.Values)
        {
            if (!sized.Sources.Contains(font) && sized.ConvertedGlyphs.Keys.All(k => k.Font != font))
                continue;
            sized.GameGlyphs.Clear();
            sized.ConvertedGlyphs.Clear();
            sized.Picks.Clear();
            any = true;
        }

        return any;
    }

    /// <summary>
    /// The game unloads every font (a switch between the lobby's fonts and the game's, a UI shader reload, shutdown),
    /// releasing their textures' resource handles: the game glyph pixels still to be read from those textures are dropped,
    /// and no font set may keep a copy picked until its font is built again.
    /// </summary>
    private int UnloadFontsDetour(AtkFontManager* manager)
    {
        try
        {
            var textures = new HashSet<Pointer<KernelTexture>>();
            for (var i = 0; manager->Fonts is not null && i < manager->FontCount; i++)
            {
                var font = manager->Fonts + i;
                if (!font->Flags.HasFlag(AtkFontManager.FontFlags.Built))
                    continue;
                for (var t = 0; t < font->TextureCount && t < AtkFontManager.Font.MaxTextures; t++)
                {
                    if (font->Textures[t].Value is not null)
                        textures.Add(font->Textures[t]);
                }

                this.ForgetGameGlyphsOf(font);
            }

            this.DropGameReads(textures);
            ForgetPickedFonts();
            this.TextInvalidated?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Letting go of the fonts being unloaded failed; disabling");
            this._enabled = false;
            this._gameReads.Clear();
            ForgetPickedFonts();
        }

        return this._unloadFontsHook.Original(manager);
    }

    /// <summary>Drops the queued reads of game glyph pixels from any of some textures; the glyphs stay blank.</summary>
    private void DropGameReads(HashSet<Pointer<KernelTexture>> textures)
    {
        this._gameReads.RemoveAll(g => {
            ref var cell = ref this.CellOf(g);
            if (cell.Source is not { } s || !textures.Contains(s.Texture))
                return false;
            cell.Raster = default;
            cell.Source = null;
            return true;
        });
    }

    private AtkFontManager.Font* PickFontDetour(AtkFontManager.FontSet* set, byte useCache)
    {
        var font = this._pickFontHook.Original(set, useCache);
        if (font is null)
            return font;

        // The picker's cached choice may be a copy picked before: decide again from its game font, since the picker's
        // cache is keyed by its own size, which isn't the one copies are chosen by.
        if (this._copies.TryGetValue(font, out var picked))
            font = picked.Original;

        if (!this._enabled)
            return font;

        // A font not built yet (its glyph map may be gone) is left alone.
        if (!font->Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return font;

        try
        {
            this.ApplyPendingPages();

            // Picked for the on-screen size, even where the game picked for another (the unscaled size of a node with a
            // fixed font resolution, or a pick scale of its own), so the renderer doesn't scale the glyphs.
            var size = set->ScaledSizeY;
            var halfPx = Math.Clamp((int)Rounding.Round(size * 2), MinHalfPx, MaxHalfPx);
            var copy = this.GetOrCreateCopy(font, halfPx);

            // Rasterized at half-pixel sizes, but the copy claims the exact size: any renderer scale but exactly 1 resamples
            // bilinearly (soft text), while a quarter pixel of size error doesn't show.
            if (halfPx < MaxHalfPx && halfPx > MinHalfPx)
                copy->Size = size;
            set->CurrentFont = copy;
            return copy;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Replacing a font failed; disabling");
            this._enabled = false;
            ForgetPickedFonts();
            return font;
        }
    }

    private byte* LayOutCharacterDetour(AtkFontAnalyzerBase* analyzer, byte** text, AtkFontAnalyzerState* state)
    {
        var outer = _current;
        var vtbl = *(nint*)analyzer;
        _current = new()
        {
            Character = *text,
            MeasuringOnly = vtbl != this._rendererVtbl && vtbl != this._renderCountVtbl,
            State = state,
        };
        try
        {
            return this._layOutCharacterHook.Original(analyzer, text, state);
        }
        finally
        {
            // The italic macro reads the bit when italics end (the glyph after moves by the font's italic correction).
            if (_current.ItalicCleared)
                state->Flags |= AtkFontManager.DrawFlags.Italic;
            _current = outer;
        }
    }

    /// <summary>
    /// Gets how the character being laid out is in italics: by an italic macro (which measuring sees too), or by its text
    /// node (which only drawing sees: the bit is in the font set's own flags, before any macro changed the copy).
    /// </summary>
    private ItalicMode GetItalicMode(AtkFontManager.FontSet* set)
    {
        if (_current.State is null || (_current.State->Flags & AtkFontManager.DrawFlags.Italic) == 0)
            return ItalicMode.Upright;
        return set is not null && (set->DrawFlags & AtkFontManager.DrawFlags.Italic) != 0
                   ? ItalicMode.Images
                   : ItalicMode.Real;
    }

    /// <summary>Marks a cell as a real italic glyph, which the game must not shear.</summary>
    internal void MarkRealItalic(AtkFontManager.Glyph* cell) => this.CellOf(cell).RealItalic = true;

    /// <summary>
    /// Gets what a character a copy has no glyph for is laid out as, never null. LayOutCharacter would take the geta mark
    /// (U+3013), else '-', else U+3400, from the font's glyph map itself rather than through GetGlyph, which would give the
    /// game font's glyph, of a texture index the copy has no texture of. The copy's own glyphs of them are given instead,
    /// else a glyph of no width.
    /// </summary>
    private AtkFontManager.Glyph* GetMissingGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        foreach (var substitute in MissingGlyphSubstitutes)
        {
            if (utf8Value == substitute)
                continue;
            var glyph = this.GetCopyGlyph(set, substitute, info);
            if (glyph is not null)
                return glyph;
        }

        return &this._emptyGlyph->Glyph;
    }

    private AtkFontManager.Glyph* GetGlyphDetour(AtkFontManager.FontSet* set, uint utf8Value, AtkFontManager.Font* font)
    {
        if (!this._copies.TryGetValue(font, out var info))
            return this._getGlyphHook.Original(set, utf8Value, font);
        var glyph = this.GetCopyGlyph(set, utf8Value, info);
        return glyph is not null ? glyph : this.GetMissingGlyph(set, utf8Value, info);
    }

    /// <summary>Gets a copy's glyph of a character, all of whose pixels are in its atlas; null if neither it nor its game font has one.</summary>
    private AtkFontManager.Glyph* GetCopyGlyph(AtkFontManager.FontSet* set, uint utf8Value, CopyInfo info)
    {
        var sized = info.Sized;

        // Laid out by LayOutCharacter at a known place in the text: the glyph of its shaped cluster. The character there must
        // be the one asked for (the lookups of the game's fallback characters, if any, are not).
        var p = _current.Character;
        if (p is not null && GameUtf8.Pack(p) == utf8Value)
        {
            try
            {
                var italic = this.GetItalicMode(set);
                var shaped = this._shaper.TryGetGlyph(sized, p, italic).Value;
                if (shaped is not null)
                {
                    // A real italic glyph is drawn as it is: the italic bit is cleared while the character is laid out
                    // (the quad emitter runs after this lookup), and set again after (LayOutCharacterDetour).
                    if (italic != ItalicMode.Upright && ((GlyphSlot*)shaped)->Id >= 0 && this.CellOf(shaped).RealItalic)
                    {
                        _current.State->Flags &= ~AtkFontManager.DrawFlags.Italic;
                        _current.ItalicCleared = true;
                    }

                    return this.WithPixels(shaped);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Shaping failed; the character is laid out by itself");
            }
        }

        if (!sized.Glyphs.TryGetValue(utf8Value, out var glyph))
        {
            try
            {
                glyph = this.CreateGlyph(sized, utf8Value);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Rasterizing 0x{value:X} at {px} px failed", utf8Value, sized.Px);
                glyph = default;
            }

            sized.Glyphs.Add(utf8Value, glyph);
        }

        if (glyph != default)
            return this.WithPixels(glyph);

        try
        {
            var own = this.GetGameGlyph(sized, utf8Value);
            if (own is not null)
                return this.WithPixels(own);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
        }

        // The game's glyph, looked up in the game font, whose glyph map the copy borrows: its texture index is of the game
        // font's textures, so it is copied into the atlas too.
        var original = (AtkFontManager.Font*)info.Original;
        var game = this._getGlyphHook.Original(set, utf8Value, original);
        if (game is null)
            return null;
        try
        {
            return this.WithPixels(this.ConvertGameGlyph(sized, original, game, utf8Value));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Copying the game's glyph 0x{value:X} failed", utf8Value);
            return &this._emptyGlyph->Glyph;
        }
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph of the largest of the sized font's game fonts that has it, scaled
    /// to its size: drawn from the game's texture, the edge would step by texels of the atlas's width (all of a font's
    /// textures share one), not the game texture's. Its pixels are read from the game's texture at the next upload. Null
    /// if none has it.
    /// </summary>
    private AtkFontManager.Glyph* GetGameGlyph(SizedFont sized, uint utf8Value)
    {
        if (sized.GameGlyphs.TryGetValue(utf8Value, out var made))
            return made;

        AtkFontManager.Glyph* glyph = null;
        IReadOnlyList<Pointer<AtkFontManager.Font>> sources = GameUtf8.TryUnpack(utf8Value, out var rune) ? sized.Sources : [];
        foreach (var source in sources)
        {
            var font = (AtkFontManager.Font*)source;
            glyph = this.PlaceGameGlyph(sized, font, font->FindGlyph(rune), utf8Value);
            if (glyph is not null)
                break;
        }

        sized.GameGlyphs[utf8Value] = glyph;
        return glyph;
    }

    /// <summary>
    /// Gets a glyph of the atlas made from a game glyph the game font's own lookup gave a copy, scaled to the copy's
    /// size; one of no width at the glyph's advance if its texture can't be read.
    /// </summary>
    private Pointer<AtkFontManager.Glyph> ConvertGameGlyph(
        SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        var key = (font, game);
        if (sized.ConvertedGlyphs.TryGetValue(key, out var made))
            return (AtkFontManager.Glyph*)made;
        var glyph = this.PlaceGameGlyph(sized, font, game, utf8Value);
        if (glyph.IsNull)
        {
            var advance = (int)Rounding.Round((game.Value->Width + game.Value->OffsetX) * GetGameScale(sized, font));
            glyph = this.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), utf8Value);
        }

        sized.ConvertedGlyphs[key] = glyph;
        return glyph;
    }

    /// <summary>Puts a game glyph into the atlas, its pixels read from its font's texture at the next upload; null if they can't be.</summary>
    private Pointer<AtkFontManager.Glyph> PlaceGameGlyph(SizedFont sized, Pointer<AtkFontManager.Font> font, Pointer<AtkFontManager.Glyph> game, uint utf8Value)
    {
        if (font.IsNull || game.IsNull)
            return null;
        var textureIndex = game.Value->TextureIndex;
        var texture = textureIndex < font.Value->TextureCount && textureIndex < AtkFontManager.Font.MaxTextures ? font.Value->Textures[textureIndex] : default;
        if (texture.IsNull || !GameTextureReader.CanRead(texture))
            return null;
        var scale = GetGameScale(sized, font);
        var top = game.Value->OffsetY - font.Value->Ascent;
        var (left, t, w, h) = RasterGlyph.ScaledBounds(0, top, game.Value->Width, game.Value->Height, scale, 0, 0);
        var r = new RasterGlyph((int)Rounding.Round((game.Value->Width + game.Value->OffsetX) * scale), left, t, w, h, new byte[w * h]);
        var pixels = new GameTextureSource(texture, game.Value->X, game.Value->Y, game.Value->Width, game.Value->Height, game.Value->Channel, 0, top, scale);
        return this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value, pixels);
    }

    /// <summary>Gets the scale from a game font's glyphs to a sized font's: 1 within the half-pixel rounding of copy sizes.</summary>
    private static float GetGameScale(SizedFont sized, AtkFontManager.Font* font) =>
        font->Size <= 0 || MathF.Abs(sized.Px - font->Size) <= 0.25f ? 1 : sized.Px / font->Size;

    /// <summary>
    /// Fills the cells of game glyphs from the game's textures, then copies the atlas's changes to the GPU. On the thread
    /// that calls Present, once a frame.
    /// </summary>
    public void Upload()
    {
        this._frameTick = Environment.TickCount64;
        foreach (var g in this._gameReads)
        {
            var glyph = g.Value;
            ref var cell = ref this.CellOf(glyph);
            try
            {
                var s = cell.Source!;
                var pixels = this._gameTextures.Read(s.Texture, s.X, s.Y, s.Width, s.Height, s.Plane);
                cell.Raster = WithSourcePixels(cell.Raster, s, pixels);
                WriteCell(glyph, cell);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Reading a game glyph's pixels failed");
            }

            cell.Raster = default;
            cell.Source = null;
        }

        this._gameReads.Clear();

        foreach (var atlas in this._fullAtlases.ToArray())
        {
            if (Environment.TickCount64 - this._lastEviction.GetValueOrDefault(atlas) < EvictIntervalMs)
                continue;
            try
            {
                this.EvictPlane(atlas);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Making room in the {family} glyph atlas failed; making every glyph again", atlas.Name);
                this.RebuildGlyphs();
                break;
            }
        }

        foreach (var atlas in this._atlases.Values)
            atlas.Upload();

        // This frame's UI draw commands are processed by now.
        this._subpixelShaders.EndFrame();
    }

    /// <summary>Gets a glyph's box filled with its game texture's pixels (read as 8-bit coverage), scaled.</summary>
    private static RasterGlyph WithSourcePixels(RasterGlyph box, GameTextureSource s, byte[] sourceAlpha)
    {
        var scaled = new RasterGlyph(0, s.Left, s.Top, s.Width, s.Height, sourceAlpha);
        if (s.Scale != 1)
            scaled = scaled.Scaled(s.Scale, 0, 0);
        var alpha = new byte[box.Width * box.Height];
        RasterGlyph.BlitMax(alpha, box.Width, box.Height, scaled, scaled.Left - box.Left, scaled.Top - box.Top);
        return box with { Alpha = alpha };
    }

    /// <summary>
    /// Takes what a copy borrows from its game font (glyph map, glyphs, flags), keeping its own size, metrics and texture
    /// slots, which are all its atlas's. When the game builds the font again in place (BuildFont), what was borrowed is
    /// freed, and taken again.
    /// </summary>
    private void Sync(AtkFontManager.Font* copy, CopyInfo info)
    {
        var sized = info.Sized;
        *copy = *(AtkFontManager.Font*)info.Original;
        copy->Size = sized.Px;
        copy->Ascent = sized.Ascent;
        copy->LineHeight = sized.LineHeight;

        // Neither the narrower-glyph substitution nor the game's kerning pairs apply to these glyphs.
        copy->Secondary = null;
        copy->SecondaryRatio = 0;
        copy->KerningCount = 0;

        // Real italics are spaced by shaping: no gap after them for sheared glyphs leaning past their advance. Measuring
        // reads it from the copy too, so it stays as drawn.
        copy->ItalicCorrection = 0;

        // The edge and glare shaders step by one texel of this width (it goes into every vertex): the pages', which the
        // game font's textures may not share (the lobby fonts' are smaller), else outlines sample several texels apart.
        // Claiming a narrower width makes the step, and so the edge, that many times wider; the plugin's edge shader takes
        // the step as its radius.
        var width = (ushort)Math.Clamp(Rounding.Round(sized.Atlas.Width / this.Edge.GetWidth(sized.Px)), 256, ushort.MaxValue);
        copy->TextureWidth = width;
        copy->TextureHeight = width;

        // None of the game font's textures: they belong to its resource handles, which the game releases on its own.
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->TextureResourceHandles[i] = default;
        this.ApplyPages(copy, sized.Atlas);
    }

    private AtkFontManager.Font* GetOrCreateCopy(AtkFontManager.Font* original, int halfPx)
    {
        var key = ((Pointer<AtkFontManager.Font>)original, halfPx);
        if (this._copiesByKey.TryGetValue(key, out var existing))
            return existing;

        var copy = (AtkFontManager.Font*)NativeMemory.Alloc((nuint)sizeof(AtkFontManager.Font));
        var info = new CopyInfo(original, halfPx, this.GetSized(original, halfPx));
        this.Sync(copy, info);
        this._copies.Add(copy, info);
        this._copiesByKey.Add(key, copy);
        return copy;
    }

    /// <summary>
    /// Gives a copy every texture slot: its family atlas's pages, and the blank texture in the slots after them, so that
    /// the renderer has a vertex buffer and a texture for every texture index. Main thread only.
    /// </summary>
    private void ApplyPages(AtkFontManager.Font* copy, GlyphAtlas atlas)
    {
        var blank = this._blankTexture.Kernel;
        var pages = Math.Min(atlas.PageCount, AtkFontManager.Font.MaxTextures);
        for (var i = 0; i < AtkFontManager.Font.MaxTextures; i++)
            copy->Textures[i] = i < pages ? atlas.GetKernelTexture(i) : blank;
        copy->TextureCount = AtkFontManager.Font.MaxTextures;
    }

    private void OnPageAdded(GlyphAtlas atlas)
    {
        if (Environment.CurrentManagedThreadId != this._mainThreadId)
        {
            lock (this._pendingPageAtlases)
                this._pendingPageAtlases.Add(atlas);
            return;
        }

        foreach (var (copy, info) in this._copies)
        {
            if (info.Sized.Atlas == atlas)
                this.ApplyPages(copy, atlas);
        }
    }

    /// <summary>Gives copies the pages added off the main thread. Main thread.</summary>
    private void ApplyPendingPages()
    {
        GlyphAtlas[] pending;
        lock (this._pendingPageAtlases)
        {
            if (this._pendingPageAtlases.Count == 0)
                return;
            pending = [.. this._pendingPageAtlases];
            this._pendingPageAtlases.Clear();
        }

        foreach (var atlas in pending)
            this.OnPageAdded(atlas);
    }

    /// <summary>Rasterizes a glyph into the atlas; null to leave it to the game.</summary>
    private AtkFontManager.Glyph* CreateGlyph(SizedFont sized, uint utf8Value)
    {
        if (!GameUtf8.TryUnpack(utf8Value, out var rune))
            return null;
        var pick = sized.Pick(rune);
        if (pick.Element is { DrawsGame: true })
            return null;
        return pick.Face.TryRasterize(rune, sized.Px, pick.Game, out var r)
                   ? this.PlaceCell(sized, this.FinishGlyph(sized, r), utf8Value)
                   : null;
    }

    /// <summary>
    /// Puts a rasterized glyph or cluster into the atlas as a game glyph advancing by <see cref="RasterGlyph.Advance"/>.
    /// Its pixels go to the atlas when it is first drawn; for a glyph made from a game glyph, they are read from
    /// <paramref name="source"/> then (<paramref name="r"/> is blank).
    /// </summary>
    internal AtkFontManager.Glyph* PlaceCell(SizedFont sized, RasterGlyph r, uint utf8Value, GameTextureSource? source = null)
    {
        // The game has no left bearing: overhanging ink moves right into the box; past 255 wide it is cut on the right.
        var boxLeft = Math.Min(r.Left, 0);
        var width = r.Width == 0 ? 0 : Math.Min(r.Left + r.Width - boxLeft, byte.MaxValue);

        // A box is a full line high, as the game's are, so that italics slant alike; ink beyond the line grows the box.
        var inkTop = sized.Ascent + r.Top;
        var top = 0;
        var height = sized.LineHeight;
        if (width != 0)
        {
            top = Math.Clamp(inkTop, sbyte.MinValue, 0);
            height = Math.Min(Math.Max(sized.LineHeight, inkTop + r.Height) - top, byte.MaxValue);
        }

        var glyph = this.AllocateGlyph();
        glyph->Utf8Value = utf8Value;
        glyph->Packed = (0, 0, 0, 0);
        glyph->Width = (byte)width;
        glyph->Height = (byte)height;
        glyph->OffsetX = (sbyte)Math.Clamp(r.Advance - width, sbyte.MinValue, sbyte.MaxValue);
        glyph->OffsetY = (sbyte)top;

        // Text is measured far more than drawn (every text change, at the node's unscaled size too): a glyph keeps its
        // pixels here until it is drawn (WithPixels, which every glyph handed to the game goes through).
        if (width != 0)
        {
            this.CellOf(glyph) = new()
            {
                State = CellState.Pending,
                Atlas = sized.Atlas,
                Raster = r,
                BoxLeft = boxLeft,
                InkTop = inkTop - top,
                Source = source,
            };
        }

        return glyph;
    }

    /// <summary>
    /// Puts a glyph's pixels into the atlas if they aren't yet, for a glyph about to be drawn, and notes that it was drawn.
    /// A glyph that finds no room draws nothing this time (the same advance), and room is made at the next upload (EvictPlane).
    /// </summary>
    private AtkFontManager.Glyph* WithPixels(AtkFontManager.Glyph* glyph)
    {
        if (_current.MeasuringOnly || ((GlyphSlot*)glyph)->Id < 0)
            return glyph;

        ref var cell = ref this.CellOf(glyph);
        if (cell.State == CellState.Placed)
        {
            cell.Drawn = this._frameTick;
            return glyph;
        }

        if (cell.State != CellState.Pending)
            return glyph;
        if (!cell.Atlas!.CanHold(glyph->Width, glyph->Height))
        {
            cell.State = CellState.None;
            cell.Raster = default;
            cell.Source = null;
            DropPixels(glyph);
            return glyph;
        }

        try
        {
            if (this.WritePixels(glyph, ref cell, this._frameTick))
                return glyph;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Placing a glyph in the atlas failed");
            cell.State = CellState.None;
            cell.Raster = default;
            cell.Source = null;
            DropPixels(glyph);
            return glyph;
        }

        this._fullAtlases.Add(cell.Atlas!);
        return this.GetBlank(glyph);
    }

    /// <summary>
    /// Places a pending glyph's pixels in its atlas, drawn last at <paramref name="drawn"/>; false if there is no room. A
    /// game glyph's pixels are read at the next upload.
    /// </summary>
    private bool WritePixels(AtkFontManager.Glyph* glyph, ref Cell cell, long drawn)
    {
        if (!cell.Atlas!.TryAllocate(glyph->Width, glyph->Height, out var page, out var plane, out var x, out var y))
            return false;

        glyph->Packed = (x, y, plane, page);
        cell.State = CellState.Placed;
        cell.Drawn = drawn;
        if (cell.Source is not null)
        {
            this._gameReads.Add(glyph);
        }
        else
        {
            WriteCell(glyph, cell);
            cell.Raster = default;
        }

        return true;
    }

    /// <summary>Gets a glyph that advances as a glyph does but draws nothing, for one whose pixels found no room yet.</summary>
    private AtkFontManager.Glyph* GetBlank(AtkFontManager.Glyph* glyph)
    {
        var blank = this.CellOf(glyph).Blank;
        if (blank is not null)
            return blank;
        var b = this.AllocateGlyph();
        *b = *glyph;
        DropPixels(b);
        this.CellOf(glyph).Blank = b;
        return b;
    }

    /// <summary>
    /// Makes room in a full atlas by emptying one of its planes: the one whose glyphs not drawn in the last
    /// <see cref="KeepDrawnMs"/> take the most room. Its glyphs drawn since are placed in it again (the most recently drawn
    /// first), from their pixels; the others leave the atlas, keeping their pixels to be placed again when they are next
    /// drawn. If every glyph was drawn recently, the plane's least recently drawn leave until half of it is free. On the
    /// thread that calls Present, after this frame's text was laid out: the next frame lays out with the new places, and
    /// the upload that follows puts them in the texture.
    /// </summary>
    private void EvictPlane(GlyphAtlas atlas)
    {
        var now = Environment.TickCount64;
        var stale = new long[atlas.MaxPlanes];
        var oldest = new long[atlas.MaxPlanes];
        Array.Fill(oldest, long.MaxValue);
        var all = CollectionsMarshal.AsSpan(this._cells);
        for (var id = 0; id < all.Length; id++)
        {
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas)
                continue;
            var glyph = &this.SlotAt(id)->Glyph;
            var plane = PlaneOf(glyph);
            if (now - all[id].Drawn > KeepDrawnMs)
                stale[plane] += GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            oldest[plane] = Math.Min(oldest[plane], all[id].Drawn);
        }

        // The most stale room; without any, the plane whose glyphs were drawn least recently.
        var victim = Array.IndexOf(stale, stale.Max());
        if (stale[victim] == 0)
            victim = Array.IndexOf(oldest, oldest.Min());

        var moved = new List<(int Id, long Drawn, byte[] Pixels, byte[]? Lcd)>();
        for (var id = 0; id < all.Length; id++)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            if (all[id].State != CellState.Placed || all[id].Atlas != atlas || PlaneOf(glyph) != victim)
                continue;
            var page = glyph->TextureIndex;
            var pixels = atlas.Read(page, glyph->Channel, glyph->X, glyph->Y, glyph->Width, glyph->Height);
            var lcd = atlas.Subpixel ? atlas.ReadLcd(page, glyph->X, glyph->Y, glyph->Width, glyph->Height) : null;
            moved.Add((id, all[id].Drawn, pixels, lcd));
        }

        moved.Sort((a, b) => b.Drawn.CompareTo(a.Drawn));
        atlas.ClearPlane(victim / GlyphAtlas.PlanesPerPage, victim % GlyphAtlas.PlanesPerPage);

        // What may stay: the glyphs drawn recently, but no more than half of the plane if none were stale.
        var budget = stale[victim] == 0 ? (long)atlas.Width * atlas.Height / 2 : long.MaxValue;
        var kept = 0;
        foreach (var (id, drawn, pixels, lcd) in moved)
        {
            var glyph = &this.SlotAt(id)->Glyph;
            ref var cell = ref all[id];
            cell.State = CellState.Pending;
            cell.Raster = new(0, 0, 0, glyph->Width, glyph->Height, pixels, lcd);
            cell.BoxLeft = 0;
            cell.InkTop = 0;
            glyph->Packed = (0, 0, 0, 0);
            var area = GlyphAtlas.PaddedArea(glyph->Width, glyph->Height);
            if (now - drawn <= KeepDrawnMs && area <= budget && this.WritePixels(glyph, ref cell, drawn))
            {
                budget -= area;
                kept++;
            }
        }

        this._fullAtlases.Remove(atlas);
        this._lastEviction[atlas] = now;
        Plugin.Log.Information(
            "The {family} glyph atlas was full: emptied plane {plane}, kept {kept} recently drawn glyphs of its {count}",
            atlas.Name,
            victim,
            kept,
            moved.Count);
    }

    /// <summary>Gets the atlas plane of a placed glyph (page * planes per page + channel).</summary>
    private static int PlaneOf(AtkFontManager.Glyph* glyph) =>
        (glyph->TextureIndex * GlyphAtlas.PlanesPerPage) + glyph->Channel;

    /// <summary>Writes a placed glyph's pixels to its place in the atlas.</summary>
    private static void WriteCell(AtkFontManager.Glyph* glyph, in Cell cell)
    {
        var r = cell.Raster;

        // The ink's rows in the box (cut off only past the game's byte-sized fields).
        var firstRow = Math.Max(0, -cell.InkTop);
        var lastRow = Math.Min(r.Height, glyph->Height - cell.InkTop);
        if (lastRow > firstRow)
        {
            cell.Atlas!.Write(
                glyph->TextureIndex,
                glyph->Channel,
                glyph->X,
                glyph->Y + cell.InkTop + firstRow,
                glyph->Width,
                lastRow - firstRow,
                r.Alpha.AsSpan(firstRow * r.Width, (lastRow - firstRow) * r.Width),
                r.Width,
                r.Left - cell.BoxLeft,
                r.Lcd is { } lcd ? lcd.AsSpan(firstRow * r.Width * 3, (lastRow - firstRow) * r.Width * 3) : default);
        }
    }

    /// <summary>Makes a glyph whose pixels found no room draw nothing, advancing as measured.</summary>
    private static void DropPixels(AtkFontManager.Glyph* glyph)
    {
        glyph->OffsetX = (sbyte)Math.Clamp(glyph->OffsetX + glyph->Width, sbyte.MinValue, sbyte.MaxValue);
        glyph->Width = 0;
    }

    private AtkFontManager.Glyph* AllocateGlyph()
    {
        var id = this._cells.Count;
        if (id % SlotBlockSize == 0)
            this._slotBlocks.Add((GlyphSlot*)NativeMemory.AllocZeroed((nuint)(SlotBlockSize * sizeof(GlyphSlot))));
        var slot = this.SlotAt(id);
        slot->Id = id;
        this._cells.Add(default);
        return &slot->Glyph;
    }

    private GlyphSlot* SlotAt(int id) => this._slotBlocks[id / SlotBlockSize].Value + (id % SlotBlockSize);

    /// <summary>Gets what is kept of a glyph made here. The reference is good until the next glyph is made.</summary>
    private ref Cell CellOf(AtkFontManager.Glyph* glyph) => ref CollectionsMarshal.AsSpan(this._cells)[((GlyphSlot*)glyph)->Id];

    private void FreeGlyphs()
    {
        foreach (var block in this._slotBlocks)
            NativeMemory.Free(block.Value);
        this._slotBlocks.Clear();
        this._cells.Clear();
    }

    /// <summary>
    /// A glyph made here, where the game reads it (it must not move): the glyph, then the id of what is kept of it
    /// (<see cref="Cell"/>), -1 for the empty glyph. Glyph pointers handed to the game are of these.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphSlot
    {
        public AtkFontManager.Glyph Glyph;
        public int Id;
    }

    private enum CellState : byte
    {
        /// <summary>No pixels: an empty glyph, a blank, or one whose pixels couldn't be placed.</summary>
        None,

        /// <summary>Pixels waiting to go to the atlas when the glyph is next drawn.</summary>
        Pending,

        /// <summary>In the atlas, where <see cref="AtkFontManager.Glyph.Packed"/> says.</summary>
        Placed,
    }

    /// <summary>What is kept of a glyph made here, by its slot's id.</summary>
    private struct Cell
    {
        public CellState State;

        /// <summary>The atlas its pixels go to.</summary>
        public GlyphAtlas? Atlas;

        /// <summary>
        /// Its coverage while pending (and for a game glyph, until its pixels are read), with the ink's left in the box and
        /// its top row in the box.
        /// </summary>
        public RasterGlyph Raster;

        public int BoxLeft;

        public int InkTop;

        /// <summary>For a glyph made from a game glyph: where its pixels are read from.</summary>
        public GameTextureSource? Source;

        /// <summary>When it was last drawn (Environment.TickCount64 of the frame), once placed.</summary>
        public long Drawn;

        /// <summary>The glyph drawn instead while its pixels find no room; null until one is needed.</summary>
        public AtkFontManager.Glyph* Blank;

        /// <summary>Whether it is a real italic glyph, which the game must not shear.</summary>
        public bool RealItalic;
    }

    /// <summary>
    /// A game glyph's pixels: a rectangle of a plane of a game font texture (a Kernel::Texture), whose top left is at
    /// (<see cref="Left"/>, <see cref="Top"/>) from the pen in the game font's pixels, scaled by <see cref="Scale"/>.
    /// </summary>
    internal sealed record GameTextureSource(Pointer<KernelTexture> Texture, int X, int Y, int Width, int Height, int Plane, int Left, int Top, float Scale);

    /// <summary>A copy of a game font at a size in half pixels: its game font, and the glyphs of its family at its size.</summary>
    private sealed class CopyInfo(Pointer<AtkFontManager.Font> original, int halfPx, SizedFont sized)
    {
        public Pointer<AtkFontManager.Font> Original { get; } = original;

        public int HalfPx { get; } = halfPx;

        public SizedFont Sized { get; set; } = sized;
    }

    /// <summary>
    /// The character the game is laying out on a thread, for GetGlyph to look up in its shaped run: whether it is laid out
    /// by an analyzer that only measures (its glyph needs no pixels yet), its layout state (whose flags have italics), and
    /// whether its italic bit was cleared for a real italic glyph, to be set again once the character is laid out.
    /// </summary>
    private struct LayoutContext
    {
        public byte* Character;
        public bool MeasuringOnly;
        public AtkFontAnalyzerState* State;
        public bool ItalicCleared;
    }

    /// <summary>
    /// Where a sized font's glyph of a codepoint comes from: <see cref="Face"/>'s <see cref="Element"/> (null for the
    /// system's fonts), with <see cref="Game"/> for the face's game glyphs.
    /// </summary>
    internal sealed record GlyphSource(ReplacementFace Face, Pointer<AtkFontManager.Font> Game, ReplacementElement? Element);

    /// <summary>
    /// A family's glyphs at one pixel size, shared by the copies of its fonts of that size (a font and its lobby version).
    /// <see cref="Face"/>, with <see cref="GameFont"/> (its game font), gives the line metrics and how text is laid out.
    /// </summary>
    internal sealed class SizedFont(
        ReplacementFace face,
        float px,
        int ascent,
        int lineHeight,
        Pointer<AtkFontManager.Font> gameFont,
        GlyphAtlas atlas,
        IReadOnlyList<(ReplacementFace Face, Pointer<AtkFontManager.Font> Game)> faces)
    {
        public ReplacementFace Face { get; } = face;

        /// <summary>Gets the atlas of the font's family, which its glyphs go to.</summary>
        public GlyphAtlas Atlas { get; } = atlas;

        public Pointer<AtkFontManager.Font> GameFont { get; } = gameFont;

        public float Px { get; } = px;

        public int Ascent { get; } = ascent;

        public int LineHeight { get; } = lineHeight;

        /// <summary>
        /// Gets the faces glyphs may come from with their game fonts, largest first: only <see cref="Face"/> at the size of
        /// one of the family's faces.
        /// </summary>
        public IReadOnlyList<(ReplacementFace Face, Pointer<AtkFontManager.Font> Game)> Faces { get; } = faces;

        /// <summary>Gets the game fonts the game's glyphs are copied from, largest first.</summary>
        public IReadOnlyList<Pointer<AtkFontManager.Font>> Sources { get; } = faces.Select(f => f.Game).ToList();

        /// <summary>By packed UTF-8 value; 0 for a glyph left to the game.</summary>
        public Dictionary<uint, Pointer<AtkFontManager.Glyph>> Glyphs { get; } = [];

        /// <summary>Copied from <see cref="Sources"/>, by packed UTF-8 value; 0 if none could be.</summary>
        public Dictionary<uint, Pointer<AtkFontManager.Glyph>> GameGlyphs { get; } = [];

        /// <summary>Copied from the glyphs the game fonts' own lookups gave copies, by game font and game glyph.</summary>
        public Dictionary<(Pointer<AtkFontManager.Font> Font, Pointer<AtkFontManager.Glyph> Glyph), Pointer<AtkFontManager.Glyph>> ConvertedGlyphs { get; } =
            [];

        /// <summary><see cref="Pick"/>'s, by codepoint.</summary>
        public Dictionary<Rune, GlyphSource> Picks { get; } = [];

        /// <summary>
        /// Gets where a codepoint's glyph comes from: of several faces, the largest whose element other than the game's
        /// has it, else one leaving it to the system's fonts, else the game's.
        /// </summary>
        public GlyphSource Pick(Rune rune)
        {
            if (this.Picks.TryGetValue(rune, out var known))
                return known;
            this.Face.TryGetElement(rune, this.GameFont, out var gameElement);
            var pick = new GlyphSource(this.Face, this.GameFont, gameElement);
            if (this.Faces.Count > 1)
            {
                GlyphSource? system = null, game = null, own = null;
                foreach (var (face, font) in this.Faces)
                {
                    face.TryGetElement(rune, font, out var element);
                    if (element is { DrawsGame: false })
                    {
                        own = new(face, font, element);
                        break;
                    }

                    if (element is null)
                        system ??= new(face, font, null);
                    else
                        game ??= new(face, font, element);
                }

                pick = own ?? system ?? game ?? pick;
            }

            this.Picks.Add(rune, pick);
            return pick;
        }
    }
}
