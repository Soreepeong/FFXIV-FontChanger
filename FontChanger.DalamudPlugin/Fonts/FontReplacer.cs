using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Shaders;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts;

internal sealed unsafe partial class FontReplacer : IDisposable
{
    private readonly GameTextLayout _layout;
    private readonly SystemFallbackFonts _fallbackFonts;

    private readonly Dictionary<string, MaterializedFont> _presetFonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TextFace, Dictionary<string, MaterializedFont>> _faceFonts = [];
    private MaterializedFont _gameFont;
    private FaceSourceCache _fontSources = new();

    // Fonts are made on a task and swapped in at EndFrame. A request supersedes those before it, and their fonts are dropped.
    private readonly Lock _loadSync = new();
    private LoadedFonts? _loaded;
    private long _loadSerial;
    private bool _loadStopped;
    private Task _loader = Task.CompletedTask;

    // A glyph of no width, for the characters of a shaped cluster after its first.
    private GlyphSlot* _emptyGlyph;

    // Release, made once for the caches' sweeps.
    private readonly Action<Pointer<AtkFontManager.Glyph>> _release;

    // What copy texture slots without a page hold, so that a game glyph slipping through draws nothing.
    private readonly KernelTextureWrapper _blankTexture;

    private readonly TextureReader _gameTextures = new();
    private bool _enabled = true;
    private bool? _supports4444;

    // Runs in reverse, so that what the hooks hand the game outlives them; mutable fields are registered as actions, read when they run.
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    private readonly EdgeShader _edgeShader = new();
    private readonly SubpixelShaders _subpixelShaders = new();

    public FontReplacer()
    {
        this._release = g => this.Release(g);
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

            this.Services = this._finalizer.Add(new FontServices());
            this._fallbackFonts = this._finalizer.Add(new SystemFallbackFonts(this.Services));
            this._finalizer.Add(this.DisposeFonts);
            this._finalizer.Add(this.StopLoader);
            this._gameFont = MaterializedFont.CreateGame(this.Services, true, this._fallbackFonts, this._fontSources);
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
            this._finalizer.Add(this.DisposeAtlases);
            this._emptyGlyph = (GlyphSlot*)NativeMemory.AllocZeroed((nuint)sizeof(GlyphSlot));
            this._emptyGlyph->Id = -1;
            this._finalizer.Add(() => {
                NativeMemory.Free(this._emptyGlyph);
                this._emptyGlyph = null;
            });
            this._finalizer.Add(this.FreeGlyphs);
            this._layout = new(this.Services, this, &this._emptyGlyph->Glyph);
            this._finalizer.Add(this.FreeCopies);

            // Font sets may still have a copy picked: forgotten once no hook can pick one again.
            this._finalizer.Add(ForgetPickedFonts);
            this._getGlyphHook = this.CreateHook<GetGlyphDelegate>(getGlyph, this.GetGlyphDetour);
            this._pickFontHook = this.CreateHook<PickFontDelegate>(pickFont, this.PickFontDetour);
            this._buildFontCacheHook = this.CreateHook<FontCacheDelegate>(buildFontCache, this.BuildFontCacheDetour);
            this._layOutCharacterHook = this.CreateHook<LayOutCharacterDelegate>(layOutCharacter, this.LayOutCharacterDetour);
            this._buildFontHook = this.CreateHook<BuildFontDelegate>(buildFont, this.BuildFontDetour);
            this._unloadFontsHook = this.CreateHook<UnloadFontsDelegate>(unloadFonts, this.UnloadFontsDetour);
            if (Plugin.SigScanner.TryScanText(PrepareMeasureSignature, out var prepareMeasure))
                this._prepareMeasureHook = this.CreateHook<PrepareMeasureDelegate>(prepareMeasure, this.PrepareMeasureDetour);
            else
                Plugin.Log.Warning("Text measuring setup not found: faces and a node's bold are measured as the game's");

            if (Plugin.SigScanner.TryScanText(TextWrapLayoutSignature, out var textWrapLayout))
                this._textWrapLayoutHook = this.CreateHook<TextWrapLayoutDelegate>(textWrapLayout, this.TextWrapLayoutDetour);
            else
                Plugin.Log.Warning("Text wrapper's layout not found: text is wrapped with the glyphs of its unscaled size");

            if (Plugin.SigScanner.TryScanText(PrepareNodeSignature, out var prepareNode))
                this._prepareNodeHook = this.CreateHook<PrepareNodeDelegate>(prepareNode, this.PrepareNodeDetour);
            else
                Plugin.Log.Warning("Text drawing setup not found: chat tabs are drawn in their family's fonts");

            this._finalizer.Add(this._subpixelShaders);
            this._finalizer.Add(this._edgeShader);
            this._getGlyphHook.Enable();
            this._pickFontHook.Enable();
            this._buildFontCacheHook.Enable();
            this._layOutCharacterHook.Enable();
            this._buildFontHook.Enable();
            this._unloadFontsHook.Enable();
            this._prepareMeasureHook?.Enable();
            this._textWrapLayoutHook?.Enable();
            this._prepareNodeHook?.Enable();

            // Runs first, so that the game's font caches are rebuilt while the hooks are still there to build them.
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

    public FontServices Services { get; }

    /// <summary>Raised on the framework thread when text drawn earlier no longer matches, for what keeps drawn text around (the nameplate bakes).</summary>
    public event Action? TextInvalidated;

    /// <summary>Raised on the framework thread once a full atlas has made room, as baked text may have kept the blanks it handed out.</summary>
    public event Action? AtlasRoomMade;

    /// <summary>
    /// Set on the framework thread. The fonts are made on another thread, and the earlier ones stay until <see cref="EndFrame"/>
    /// swaps them in. Every glyph is made again then, even for the same value.
    /// </summary>
    public FontSettings Fonts
    {
        get;
        set
        {
            field = Misc.DebugAssertMainThread(value);
            LoadedFonts? superseded;
            long serial;
            lock (this._loadSync)
            {
                serial = ++this._loadSerial;
                superseded = this._loaded;
                this._loaded = null;
            }

            superseded?.Dispose();
            this._loader = this._loader.ContinueWith(_ => this.Load(value, serial), TaskScheduler.Default);
        }
    }

    /// <summary>DXGI 1.2 devices support B4G4R4A4, but the game also runs on Direct3D 11 devices without it.</summary>
    public bool Supports4444 => this._supports4444 ??= this.CheckSupports4444();

    /// <summary>Set between frames.</summary>
    public AtlasSettings Atlas
    {
        get;
        set
        {
            Misc.DebugAssertMainThread();
            value = value.Clamped();
            if (value == field)
                return;
            field = value;
            this.RemakeAtlases();
        }
    } = new AtlasSettings().Clamped();

    public EdgeSettings Edge
    {
        get;
        set
        {
            value = value.Drawn();
            Misc.DebugAssertMainThread();
            if (value == field)
                return;

            // Glyphs made now show only from the next frame (Upload), so they are remade, blanking text for a frame, only if a margin changes.
            var marginChanged = this._sizedFonts.Values.Any(s => GetEdgeMargin(value, s.Px) != GetEdgeMargin(field, s.Px));
            field = value;

            // The plugin's shader draws a round edge of any width unless edges are the game's own 1 px; the game's shader, widened
            // only by claiming a narrower texture (blocky past about 1.5 px), is the fallback.
            try
            {
                this._edgeShader.SetSoftness(value.Enabled && value != new EdgeSettings { Enabled = true } ? value.Softness : null);
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
    } = new();

    /// <summary>Set between frames.</summary>
    public SubpixelShaders.SettingsRecord Subpixel
    {
        get;
        set
        {
            Misc.DebugAssertMainThread();
            if (value == field)
                return;
            field = value;
            try
            {
                this._subpixelShaders.Apply(value);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Subpixel text is off");
                this._subpixelShaders.Apply(new());
            }

            var active = this._subpixelShaders.Active;
            var order = active ? value.Order : SubpixelOrder.None;
            var atlasesChange = this._atlases.Values.Any(a => a.Subpixel != active);
            if (order == this.Services.SubpixelOrder && !atlasesChange)
                return;
            this.Services.SubpixelOrder = order;
            if (atlasesChange)
                this.RemakeAtlases();
            else
                this.RebuildGlyphs();
        }
    }

    public VerticalSubpixelRenderer VerticalSubpixel
    {
        get => this.Services.DirectWrite.VerticalWithFreeType ? VerticalSubpixelRenderer.FreeType : VerticalSubpixelRenderer.DirectWrite;
        set
        {
            Misc.DebugAssertMainThread();
            var withFreeType = value == VerticalSubpixelRenderer.FreeType;
            if (withFreeType == this.Services.DirectWrite.VerticalWithFreeType)
                return;
            this.Services.DirectWrite.VerticalWithFreeType = withFreeType;
            if (this.Services.SubpixelOrder.IsVertical)
                this.RebuildGlyphs();
        }
    }

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

    /// <summary>Between frames.</summary>
    public void EndFrame()
    {
        Misc.DebugAssertMainThread();
        this.ApplyLoadedFonts();
        this.ApplyPendingPages();
        if (this._planeReused)
        {
            this._planeReused = false;
            this.AtlasRoomMade?.Invoke();
        }
    }

    /// <summary>Between frames; nameplates already baked keep their pixels until the game bakes them again.</summary>
    public void RebuildGlyphs()
    {
        Misc.DebugAssertMainThread();
        this._layout.Clear();
        this._fullAtlases.Clear();
        this._gameReads.Clear();

        // Nothing of ours refers to the glyphs anymore, and they are freed once the game is done with them.
        foreach (var sized in this._sizedFonts.Values)
            this.ReleaseGlyphs(sized);
        foreach (var atlas in this._atlases.Values)
            atlas.Clear();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>A step that fails is collected, and the others still run.</summary>
    public void Dispose()
    {
        Misc.DebugAssertMainThread();
        this._finalizer.Dispose();
    }

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

    /// <summary>Loader task, using nothing of the replacer but the services.</summary>
    private void Load(FontSettings value, long serial)
    {
        LoadedFonts? loaded = null;
        try
        {
            loaded = LoadFonts(this.Services, this._fallbackFonts, value, () => Volatile.Read(ref this._loadStopped) || Interlocked.Read(ref this._loadSerial) != serial);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Setting up the fonts failed; the earlier ones stay");
        }

        if (loaded is null)
            return;

        // Swapped, so that stale fonts are disposed after the lock.
        lock (this._loadSync)
        {
            if (!this._loadStopped && this._loadSerial == serial)
                (this._loaded, loaded) = (loaded, this._loaded);
        }

        loaded?.Dispose();
    }

    /// <summary>A font that can't be made is left out with a warning.</summary>
    /// <returns>Null once <paramref name="stale"/>.</returns>
    private static LoadedFonts? LoadFonts(FontServices services, SystemFallbackFonts fallbackFonts, FontSettings value, Func<bool> stale)
    {
        var loaded = new LoadedFonts();
        var sources = loaded.Sources;
        var complete = false;
        try
        {
            loaded.Game = MaterializedFont.CreateGame(services, value.SystemFallback, fallbackFonts, sources);
            loaded.Game.Prepare();
            if (!Materialize(loaded.Preset, value.Preset))
                return null;
            foreach (var (face, preset) in value.Faces ?? new Dictionary<TextFace, Preset>())
            {
                var fonts = new Dictionary<string, MaterializedFont>(StringComparer.OrdinalIgnoreCase);
                loaded.Faces[face] = fonts;
                if (!Materialize(fonts, preset))
                    return null;
                if (fonts.Count == 0)
                    loaded.Faces.Remove(face);
            }

            complete = !stale();
            return complete ? loaded : null;
        }
        finally
        {
            if (!complete)
                loaded.Dispose();
        }

        bool Materialize(Dictionary<string, MaterializedFont> fonts, Preset? preset)
        {
            foreach (var (name, def) in preset?.Fonts ?? new Dictionary<string, Font>())
            {
                if (stale())
                    return false;

                MaterializedFont? font = null;
                try
                {
                    font = new(services, def, value.SystemFallback, fallbackFonts, sources);
                    font.Prepare();
                    fonts[name] = font;
                }
                catch (Exception ex)
                {
                    font?.Dispose();
                    Plugin.Log.Warning(ex, "The font {name} can't be set up; its text falls back", name);
                }
            }

            return true;
        }
    }

    /// <summary>Between frames.</summary>
    private void ApplyLoadedFonts()
    {
        LoadedFonts? loaded;
        lock (this._loadSync)
        {
            loaded = this._loaded;
            this._loaded = null;
        }

        if (loaded is null)
            return;

        // The sized fonts and laid out runs refer to the fonts replaced.
        this._layout.Clear();
        this.ClearSizedFonts();
        this.DisposeFonts();
        this._gameFont = loaded.Game!;
        this._fontSources = loaded.Sources;
        foreach (var (name, font) in loaded.Preset)
            this._presetFonts[name] = font;
        foreach (var (face, fonts) in loaded.Faces)
            this._faceFonts[face] = fonts;

        // Copies made so far take the glyphs and metrics of their new fonts.
        this.ResyncCopies();
        this.RebuildGlyphs();
    }

    /// <summary>Waits for the loader, which stops at its next font.</summary>
    private void StopLoader()
    {
        lock (this._loadSync)
            this._loadStopped = true;
        this._loader.Wait();
        lock (this._loadSync)
        {
            this._loaded?.Dispose();
            this._loaded = null;
        }
    }

    private void ReleaseGlyphs(SizedFont sized)
    {
        sized.Glyphs.Clear(this._release);
        sized.GameGlyphs.Clear(this._release);
        sized.ConvertedGlyphs.Clear(this._release);
        sized.WrapCells.Clear(this._release);
    }

    /// <summary>Lets go of their glyphs first.</summary>
    private void ClearSizedFonts()
    {
        foreach (var sized in this._sizedFonts.Values)
            this.ReleaseGlyphs(sized);
        this._sizedByRequest.Clear();
        this._sizedFonts.Clear();
    }

    private void DisposeFonts()
    {
        foreach (var font in this._presetFonts.Values.Concat(this._faceFonts.Values.SelectMany(f => f.Values)))
            font.Dispose();
        this._presetFonts.Clear();
        this._faceFonts.Clear();
        this._gameFont?.Dispose();
        this._fontSources.Dispose();
    }

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

    /// <summary>Between frames.</summary>
    private void RemakeAtlases()
    {
        Misc.DebugAssertMainThread();
        this.RebuildGlyphs();

        // Glyphs and copies refer to the old atlases' pages, so they go first.
        this._lastEviction.Clear();
        lock (this._pendingPageAtlases)
            this._pendingPageAtlases.Clear();
        this.ClearSizedFonts();
        this.DisposeAtlases();
        this.ResyncCopies();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>
    /// Game fonts the preset has no font of (all, if it is null) keep the game's glyphs, and characters a font lacks come from
    /// the game's glyphs unless <paramref name="SystemFallback"/>.
    /// </summary>
    internal readonly record struct FontSettings(
        Preset? Preset = null, bool SystemFallback = true, IReadOnlyDictionary<TextFace, Preset>? Faces = null);

    /// <summary>Fonts made together, sharing their face sources.</summary>
    private sealed class LoadedFonts : IDisposable
    {
        public FaceSourceCache Sources { get; } = new();

        public MaterializedFont? Game { get; set; }

        public Dictionary<string, MaterializedFont> Preset { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<TextFace, Dictionary<string, MaterializedFont>> Faces { get; } = [];

        public void Dispose()
        {
            foreach (var font in this.Preset.Values.Concat(this.Faces.Values.SelectMany(f => f.Values)))
                font.Dispose();
            this.Game?.Dispose();
            this.Sources.Dispose();
        }
    }
}
