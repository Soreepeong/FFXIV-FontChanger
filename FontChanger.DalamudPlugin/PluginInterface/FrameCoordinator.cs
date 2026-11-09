using System;
using System.Threading.Tasks;

using Dalamud.Plugin.Services;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Shaders;
using FontChanger.DalamudPlugin.Game;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>Calls the parts in order at the two points of a frame the plugin owns, and passes on what each reports.</summary>
/// <remarks>
/// <ul>
/// <li><see cref="OnUpdate"/> runs at the framework update before the game's. It does settings, fonts and resets, then what keeps laid out text.</li>
/// <li>Hooks run in the game's update and draw between the two, and record in a <see cref="FrameLog"/> what others must react to.</li>
/// <li><see cref="OnPresent"/> runs before the plugin's windows draw. It does the atlases and the caches.</li>
/// </ul>
/// </remarks>
internal sealed class FrameCoordinator : IAsyncDisposable
{
    private readonly Configuration.Configuration _configuration;
    private readonly FrameLog _log = new();
    private readonly FrameClock _clock = new();
    private readonly SubpixelShaders _subpixel;
    private readonly SubpixelSource _subpixelSource = new();
    private readonly EdgeShader _edge = new();
    private readonly FontLoader _loader = null!;
    private readonly LineBreaker? _lineBreaker;

    /// <summary>On the framework thread.</summary>
    public FrameCoordinator(Configuration.Configuration configuration)
    {
        this._configuration = configuration;
        this._subpixel = new(this._log);
        this.Replacer = new(this._log, this._clock);
        try
        {
            this.NamePlates = Optional("Nameplate text", () => new NamePlateText(this.Replacer));
            this.ChatLog = Optional("Chat log text", () => new ChatLogText(this.Replacer));
            this._lineBreaker = Optional("Line breaking", () => new LineBreaker(this.Replacer));
            this._loader = new(this.Replacer.Services, this.Replacer.FallbackFonts);
            this.Presets = new(configuration, this.Replacer.Services, this._loader);
            this.OnUpdate(null!);
        }
        catch
        {
            this.Unhook();
            this.Replacer.Dispose();
            throw;
        }

        static T? Optional<T>(string part, Func<T> create)
            where T : class
        {
            try
            {
                return create();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "{Part} is off", part);
                return null;
            }
        }
    }

    public FontReplacer Replacer { get; }

    public NamePlateText? NamePlates { get; }

    public ChatLogText? ChatLog { get; }

    public PresetController Presets { get; } = null!;

    public AppliedSettings Applied { get; } = new();

    /// <summary>Waits out the frames the game may still use glyphs and copies for before freeing them.</summary>
    public async ValueTask DisposeAsync()
    {
        await Plugin.Framework.RunOnFrameworkThread(this.Unhook);
        await Plugin.Framework.DelayTicks(GlyphStore.GraceFrames);
        await Plugin.Framework.RunOnFrameworkThread(() => Step("font replacer", this.Replacer.Dispose));
    }

    public void OnUpdate(IFramework framework)
    {
        var log = this._log.Drain();
        foreach (var part in log.Failures)
            this.Applied.Fail(part);

        var settings = this.Applied.Diff(this._configuration, this._subpixelSource.Get(this._configuration));
        var (fonts, error) = this._loader.TryTake();
        if (error is not null)
            this.Presets.ReportFailure(error);

        // Every cause is known before anything is made again, so that it is made once.
        var scope = ResetScope.None;
        if (settings?.Enabled is { } enabled)
            scope |= this.Replacer.SetEnabled(enabled);
        if (settings?.Edge is { } edge)
        {
            this._edge.Apply(edge);
            scope |= this.Replacer.SetEdge(edge);
        }

        if (settings?.Subpixel is { } subpixel)
        {
            var active = this._subpixel.TryApply(subpixel);
            scope |= this.Replacer.SetSubpixel(active ? subpixel.Order : SubpixelOrder.None, active);
        }

        if (settings?.VerticalSubpixel is { } vertical)
            scope |= this.Replacer.SetVerticalSubpixel(vertical);
        if (settings?.Atlas is { } atlas)
            scope |= this.Replacer.SetAtlas(atlas);
        if (log.GameFontsChanged)
            scope |= ResetScope.Text;
        this.Replacer.Resetter.Reset(scope, fonts);

        this.ChatLog?.ReadPanels();
        if (scope.HasFlag(ResetScope.Text) || fonts is not null)
            this.RedoText();
        else if (log.PlaneReused)
            this.NamePlates?.Rebake();

        if (settings is not null)
        {
            if (settings.NamePlates is { } mode && this.NamePlates is not null)
                this.NamePlates.Mode = mode;
            for (var tab = 0; tab < settings.ChatFontSizes?.Length; tab++)
            {
                if (settings.ChatFontSizes[tab] is { } size)
                    this.ChatLog?.SetFontSize(tab, size);
            }

            this.Applied.Accept(settings);
        }

        this.NamePlates?.Update();
    }

    public void OnPresent()
    {
        this._clock.BeginPresent();
        this.Replacer.ReadGameGlyphs();
        if (this.Replacer.EvictFullPlanes())
            this._log.PlaneReused();
        this.Replacer.UploadAtlases();

        // This frame's UI draw commands are processed by now (Present, which is called even while the UI is hidden).
        this._subpixel.ForgetDrawState();
        this.Replacer.Sweep();
        this._clock.EndFrame();
    }

    private static void Step(string name, Action? step)
    {
        try
        {
            step?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Undoing the {name} failed", name);
        }
    }

    /// <summary>What keeps laid out text lays it out again.</summary>
    private void RedoText()
    {
        this.NamePlates?.Remeasure();
        this.ChatLog?.Relayout();
    }

    /// <summary>Everything but the replacer's glyphs and copies, which the game may still use.</summary>
    private void Unhook()
    {
        Step("presets", this.Presets is { } presets ? presets.Dispose : null);

        // Laid out with the game's fonts while the hooks that know the plates' bake scales are still there.
        Step("text", () => {
            this.Replacer.SetEnabled(false);
            this.RedoText();
        });
        Step("line breaking", this._lineBreaker is { } lineBreaker ? lineBreaker.Dispose : null);
        Step("chat log text", this.ChatLog is { } chatLog ? chatLog.Dispose : null);
        Step("nameplate text", this.NamePlates is { } namePlates ? namePlates.Dispose : null);
        Step("edge shader", this._edge.Dispose);
        Step("subpixel shaders", this._subpixel.Dispose);
        Step("hooks", this.Replacer.Unhook);
        Step("font loader", this._loader is { } loader ? loader.Dispose : null);
    }
}
