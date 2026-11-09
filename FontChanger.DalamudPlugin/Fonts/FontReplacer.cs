using System;
using System.Linq;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

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
/// <remarks>Its setters return how much must be made again, which the caller resets.</remarks>
internal sealed unsafe class FontReplacer : IDisposable
{
    // Runs in reverse, so that what the hooks hand the game outlives them.
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    // Undone first, by Unhook.
    private readonly DisposeSafety.ScopedFinalizer _hooks = new();

    // What copy texture slots without a page hold, so that a game glyph slipping through draws nothing.
    private readonly KernelTextureWrapper _blankTexture;

    private readonly FrameClock _clock;
    private readonly EnabledState _enabled;
    private readonly FontSetFlags _flags = new();
    private readonly FontHooks _fontHooks;
    private readonly GlyphStore _store;
    private readonly AtlasSet _atlases;
    private readonly SizedFontRegistry _sized;
    private readonly GameGlyphReader _reader;
    private readonly CopyFonts _copies;
    private readonly GlyphFactory _factory;
    private readonly CellPlacer _placer;
    private readonly GameTextLayout _layout;
    private bool? _supports4444;

    public FontReplacer(FrameLog log, FrameClock clock)
    {
        this._clock = clock;
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
            this._sized = new(FontSet.CreateGame(this.Services, true, this.FallbackFonts), this._atlases, this._store);
            this._finalizer.Add(() => this._sized.Fonts.Dispose());
            this._reader = this._finalizer.Add(new GameGlyphReader(this._store));
            this._copies = this._finalizer.Add(new CopyFonts(this._sized, this._blankTexture));
            this._factory = new(this._store, clock);
            this._placer = new(this._store, this._copies, this._reader);
            this._layout = new(this.Services, this._store, this._factory, clock);
            this.Resetter = new(this._layout, this._sized, this._placer, this._reader, this._atlases, this._copies);
            this._enabled = new(log);
            this._fontHooks = this._hooks.Add(
                new FontHooks(this._copies, this._sized, this._layout, this._factory, this._placer, this._store, clock, this._enabled, this._flags));
            this._hooks.Add(new FontLifecycleHooks(this._reader, this.Resetter, this._enabled, log));

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

    public Resetter Resetter { get; }

    /// <summary>Gets a value indicating whether fonts are replaced, which an error turns off at once.</summary>
    public bool IsEnabled => this._enabled.IsOn;

    /// <summary>Gets a value indicating whether DXGI 1.2 devices support B4G4R4A4, but the game also runs on Direct3D 11 devices without it.</summary>
    public bool Supports4444 => this._supports4444 ??= this.CheckSupports4444();

    public bool HasOwnFonts(TextFace face) => this._sized.Fonts.HasOwnFonts(Misc.DebugAssertMainThread(face));

    public void AddNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._flags.AddNodeFaces(faces);

    public void RemoveNodeFaces(Func<Pointer<AtkTextNode>, NodeFaces?> faces) => this._flags.RemoveNodeFaces(faces);

    public void ApplyNodeFace(AtkFontManager.FontSet* set, AtkTextNode* node, NodeFaces faces) => this._flags.ApplyNodeFace(set, node, faces);

    /// <summary>Applied even if an error turned it off already, so that the font caches follow.</summary>
    public ResetScope SetEnabled(bool value)
    {
        Misc.DebugAssertMainThread();
        this._enabled.IsOn = value;
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
        this._atlases.Subpixel = active;
        var atlasesChange = this._atlases.All.Any(a => a.Subpixel != active);
        if (order == this.Services.SubpixelOrder && !atlasesChange)
            return ResetScope.None;
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

    /// <summary>At Present, after this frame's glyphs were placed.</summary>
    public void ReadGameGlyphs() => this._reader.ReadAll();

    /// <summary>At Present. Every glyph is made again if room can't be made.</summary>
    /// <returns>Whether glyphs of full atlases are placed again, so that baked text may hold blanks.</returns>
    public bool EvictFullPlanes()
    {
        try
        {
            return this._placer.EvictFull(this._clock.Tick);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Making room in a glyph atlas failed; making every glyph again");
            this.Resetter.Reset(ResetScope.Glyphs);
            return true;
        }
    }

    public void UploadAtlases() => this._atlases.Upload();

    /// <summary>At Present, after the game's reads, which hold glyphs without a reference. Runs go first, then the cells they let go of.</summary>
    public void Sweep()
    {
        var frame = this._clock.Frame;
        this._layout.Sweep(frame);
        this._sized.Sweep(frame);
        this._store.FreeUnreferenced(frame);
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
