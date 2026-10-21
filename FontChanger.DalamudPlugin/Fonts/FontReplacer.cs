using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private MaterializedFont _gameFont;

    // A glyph of no width, for the characters of a shaped cluster after its first.
    private GlyphSlot* _emptyGlyph;

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
            this._gameFont = MaterializedFont.CreateGame(this.Services, true, this._fallbackFonts);
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
            this._finalizer.Add(this._subpixelShaders);
            this._finalizer.Add(this._edgeShader);
            this._getGlyphHook.Enable();
            this._pickFontHook.Enable();
            this._buildFontCacheHook.Enable();
            this._layOutCharacterHook.Enable();
            this._buildFontHook.Enable();
            this._unloadFontsHook.Enable();

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

    /// <summary>Set between frames; makes every glyph again, even for the same value.</summary>
    public FontSettings Fonts
    {
        get;
        set
        {
            field = Misc.DebugAssertMainThread(value);
            this._layout.Clear();
            this.DisposeFonts();
            this._sizedFonts.Clear();
            this._gameFont = MaterializedFont.CreateGame(this.Services, value.SystemFallback, this._fallbackFonts);
            foreach (var (name, def) in value.Preset?.Fonts ?? new Dictionary<string, Font>())
            {
                try
                {
                    this._presetFonts[name] = new(this.Services, def, value.SystemFallback, this._fallbackFonts);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, "Setting up the font {name} failed; its fonts use the game's glyphs", name);
                }
            }

            // Copies made so far take the glyphs and metrics of their new fonts.
            this.ResyncCopies();
            this.RebuildGlyphs();
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
        this.ApplyPendingPages();
    }

    /// <summary>Between frames; nameplates already baked keep their pixels until the game bakes them again.</summary>
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

    private void DisposeFonts()
    {
        foreach (var font in this._presetFonts.Values)
            font.Dispose();
        this._presetFonts.Clear();
        this._gameFont?.Dispose();
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
        this._sizedFonts.Clear();
        this.DisposeAtlases();
        this.ResyncCopies();
        ForgetPickedFonts();
        this.TextInvalidated?.Invoke();
    }

    /// <summary>
    /// Game fonts the preset has no font of (all, if it is null) keep the game's glyphs, and characters a font lacks come from
    /// the game's glyphs unless <paramref name="SystemFallback"/>.
    /// </summary>
    internal readonly record struct FontSettings(Preset? Preset = null, bool SystemFallback = true);
}
