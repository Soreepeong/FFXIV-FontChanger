using System;
using System.Linq;

using Dalamud.Utility;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Hooks;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Replaces the game's fonts. It makes the parts, holds their settings, and does their work at Present.</summary>
/// <remarks>
/// <ul>
/// <li>Its setters skip what didn't change, and return how much must be made again, which the caller passes to <see cref="Reset"/>.</li>
/// <li><see cref="Reset"/> is the one way to make glyphs, sized fonts, atlases or fonts again, called between frames or in a
/// hook outside any layout. Nameplates already baked keep their pixels until baked again.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class FontReplacer : IDisposable
{
    // Runs in reverse, so that what the hooks hand the game outlives them.
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    // Undone first, by Unhook.
    private readonly DisposeSafety.ScopedFinalizer _hooks = new();

    // What copy texture slots without a page hold, so that a game glyph slipping through draws nothing.
    private readonly KernelTextureWrapper _blankTexture;

    private readonly FrameState _state;
    private readonly FontHooks _fontHooks;
    private readonly GlyphStore _store;
    private readonly AtlasSet _atlases;
    private readonly SizedFontRegistry _sized;
    private readonly CopyFonts _copies;
    private readonly GlyphFactory _factory;
    private readonly CellPlacer _placer;
    private readonly GameTextLayout _layout;
    private bool? _supports4444;

    // As last asked for, which an error may have turned off since.
    private bool? _enabled;

    public FontReplacer(FrameState state)
    {
        this._state = state;
        try
        {
            this.Services = this._finalizer.Add(new FontServices());
            this.FallbackFonts = this._finalizer.Add(new SystemFallbackFonts(this.Services));
            try
            {
                FontNames.Initialize();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "The game's font names weren't found; presets can't be applied");
            }

            this._store = this._finalizer.Add(new GlyphStore());
            this._blankTexture = this._finalizer.Add(CreateBlankTexture());
            this._atlases = this._finalizer.Add(new AtlasSet(() => this.Supports4444));
            this._sized = new(FontSet.CreateGame(this.Services, true, this.FallbackFonts), this._atlases, this._store, state);
            this._finalizer.Add(() => this._sized.Fonts.Dispose());
            this._copies = this._finalizer.Add(new CopyFonts(this._sized, this._blankTexture, state));
            this._placer = this._finalizer.Add(new CellPlacer(this._store, this._copies));
            this._factory = new(this._store);
            this._layout = new(this.Services, this._store, this._factory, state);
            this._fontHooks = this._hooks.Add(
                new FontHooks(this._copies, this._sized, this._layout, this._factory, this._placer, this._store, state, this.Context));
            this._hooks.Add(new FontLifecycleHooks(scope => this.Reset(scope), state));

            // Caches built so far hold the game's glyphs.
            this._fontHooks.UpdateFontCaches();
        }
        catch
        {
            try
            {
                this.Unhook();
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

    public SystemFallbackFonts FallbackFonts { get; }

    /// <summary>Gets what is being laid out. Text nodes are given faces of their own there.</summary>
    public LayoutContext Context { get; } = new();

    /// <summary>Gets a value indicating whether fonts are replaced, which an error turns off at once.</summary>
    public bool IsEnabled => this._state.IsOn;

    /// <summary>Gets a value indicating whether DXGI 1.2 devices support B4G4R4A4, but the game also runs on Direct3D 11 devices without it.</summary>
    public bool Supports4444 => this._supports4444 ??= this.CheckSupports4444();

    public bool HasOwnFonts(TextFace face) => this._sized.Fonts.HasOwnFonts(Misc.DebugAssertMainThread(face));

    /// <summary>Applied when it differs from what was last asked for, even if an error turned it off already, so that the font caches follow.</summary>
    public ResetScope SetEnabled(bool value)
    {
        Misc.DebugAssertMainThread();
        if (this._enabled == value)
            return ResetScope.None;
        this._enabled = value;
        this._state.IsOn = value;
        CopyFonts.ForgetPicked();
        this._fontHooks.UpdateFontCaches();
        return ResetScope.Text;
    }

    /// <summary>Glyphs made now show only from the next frame, so they are made again, blanking text for a frame, only if a margin changes.</summary>
    public ResetScope SetEdge(EdgeSettings value)
    {
        Misc.DebugAssertMainThread();
        value = value.Drawn();
        var old = this._factory.Edge;
        if (value == old)
            return ResetScope.None;

        var marginChanged = this._sized.All.Any(s => GlyphFactory.GetEdgeMargin(value, s.Px) != GlyphFactory.GetEdgeMargin(old, s.Px));
        this._factory.Edge = value;
        this._copies.Edge = value;
        this._copies.SyncAll();
        return marginChanged ? ResetScope.Glyphs : ResetScope.Text;
    }

    public ResetScope SetAtlas(AtlasSettings value)
    {
        Misc.DebugAssertMainThread();
        value = value.Clamped();
        if (value == this._atlases.Settings)
            return ResetScope.None;
        this._atlases.Settings = value;
        return ResetScope.Atlases;
    }

    /// <param name="order">Of the glyphs, None unless <paramref name="active"/>.</param>
    /// <param name="active">Whether the subpixel shaders are on, which atlases are made for.</param>
    public ResetScope SetSubpixel(SubpixelOrder order, bool active)
    {
        Misc.DebugAssertMainThread();
        if (order == this.Services.SubpixelOrder && active == this._atlases.Subpixel)
            return ResetScope.None;
        this._atlases.Subpixel = active;
        var atlasesChange = this._atlases.All.Any(a => a.Subpixel != active);
        this.Services.SubpixelOrder = order;
        return atlasesChange ? ResetScope.Atlases : ResetScope.Glyphs;
    }

    public ResetScope SetVerticalSubpixel(VerticalSubpixelRenderer value)
    {
        Misc.DebugAssertMainThread();
        var withFreeType = value == VerticalSubpixelRenderer.FreeType;
        if (withFreeType == this.Services.DirectWrite.VerticalWithFreeType)
            return ResetScope.None;
        this.Services.DirectWrite.VerticalWithFreeType = withFreeType;
        return this.Services.SubpixelOrder.IsVertical ? ResetScope.Glyphs : ResetScope.None;
    }

    /// <summary>Makes what <paramref name="scope"/> names again, each step once and in order.</summary>
    /// <param name="scope">How much to make again.</param>
    /// <param name="fonts">If given, replaces the fonts, and the old ones are disposed.</param>
    public void Reset(ResetScope scope, FontSet? fonts = null)
    {
        if (fonts is not null)
            scope |= ResetScope.Fonts;
        if (!scope.HasFlag(ResetScope.Glyphs))
            return;

        // Nothing of ours refers to the glyphs anymore, and they are freed once the game is done with them.
        this._layout.Clear();
        if (scope.HasFlag(ResetScope.SizedFonts))
        {
            // The game's textures may have gone too.
            this._placer.ClearAll();
            this._sized.Clear();
            this._sized.Fonts.ForgetGameFonts();
        }
        else
        {
            this._placer.Clear();
            this._sized.ReleaseAllGlyphs();
        }

        // Copies refer to the old atlases' pages until marked stale below.
        if (scope.HasFlag(ResetScope.Atlases))
            this._atlases.Dispose();
        else
            this._atlases.ClearAll();

        if (fonts is not null)
            this._sized.SwapFonts(fonts);
        if (scope.HasFlag(ResetScope.SizedFonts))
            this._copies.MarkStale();
        CopyFonts.ForgetPicked();
    }

    /// <summary>
    /// At Present, after this frame's text was laid out: reads game glyphs' pixels, makes room in full atlases (every glyph is
    /// made again if it can't be), uploads the atlases, and sweeps the caches.
    /// </summary>
    /// <remarks>Caches are swept after the game's reads, which hold glyphs without a reference. Runs go first, then the cells they let go of.</remarks>
    public void Present()
    {
        this._placer.ReadGameGlyphs();
        try
        {
            if (this._placer.EvictFull())
                this._state.PlaneReused();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Making room in a glyph atlas failed; making every glyph again");
            this.Reset(ResetScope.Glyphs);
            this._state.PlaneReused();
        }

        this._atlases.Upload();
        this._layout.Sweep();
        this._sized.Sweep();
        this._store.FreeUnreferenced(this._state.Frame);
    }

    /// <summary>Removes the hooks and has every font set pick its own font again. <see cref="Dispose"/> frees the rest once the game holds none of it.</summary>
    public void Unhook()
    {
        Misc.DebugAssertMainThread();
        try
        {
            this._hooks.Dispose();
        }
        finally
        {
            CopyFonts.ForgetPicked();
        }
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
}
