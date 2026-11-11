using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Dalamud.Plugin.Services;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Shaders;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>Calls the parts in order at the two points of a frame the plugin owns, and passes on what each reports.</summary>
/// <remarks>
/// <ul>
/// <li><see cref="OnUpdate"/> runs at the framework update before the game's. It does settings, fonts and resets, then what keeps laid out text.</li>
/// <li>Hooks run in the game's update and draw between the two, and record in a <see cref="FrameState"/> what others must react to.</li>
/// <li><see cref="OnPresent"/> runs before the plugin's windows draw. It does the atlases and the caches.</li>
/// </ul>
/// </remarks>
internal sealed class FrameCoordinator : IAsyncDisposable
{
    // The configuration's names of the chat tabs' faces, by tab.
    private static readonly string[] ChatTabFaceNames =
        Enumerable.Range(0, TextFaceNames.ChatTabCount).Select(tab => TextFaceNames.NameOf(TextFaceNames.ChatTab(tab))).ToArray();

    private readonly Configuration.Configuration _configuration;
    private readonly FrameState _state = new();
    private readonly SubpixelShaders _subpixel;
    private readonly SubpixelSettingsSource _subpixelSource = new();
    private readonly EdgeShader _edge = new();
    private readonly FontSetLoader _loader;
    private readonly NamePlateText? _namePlates;
    private readonly ChatLogFonts? _chatLog;
    private readonly LineBreaker? _lineBreaker;

    /// <summary>On the framework thread.</summary>
    public FrameCoordinator(Configuration.Configuration configuration)
    {
        this._configuration = configuration;
        this._subpixel = new(this._state);
        this.Replacer = new(this._state);
        try
        {
            this._namePlates = Optional("Nameplate text", () => new NamePlateText(this.Replacer));
            this._chatLog = Optional("Chat log text", () => new ChatLogFonts(this.Replacer));
            this._lineBreaker = Optional("Line breaking", () => new LineBreaker(this.Replacer));
            this._loader = new(this.Replacer.Services, this.Replacer.FallbackFonts);
            this.Presets = new(configuration, this._loader);
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

    public FontSourceEditor Presets { get; }

    /// <summary>Gets what subpixel rendering was last applied with, such as the system's ClearType settings.</summary>
    public SubpixelShaders.SettingsRecord AppliedSubpixel { get; private set; }

    /// <summary>A part that failed, at its setup or since, stays off until the plugin is loaded again.</summary>
    public bool Failed(PluginFeature pluginFeature) => pluginFeature switch
    {
        PluginFeature.NamePlates => this._namePlates is null,
        PluginFeature.ChatLog => this._chatLog is null,
        _ => this._state.Failed(pluginFeature),
    };

    /// <summary>Waits out the frames the game may still use glyphs and copies for before freeing them.</summary>
    public async ValueTask DisposeAsync()
    {
        await Plugin.Framework.RunOnFrameworkThread(this.Unhook);
        await Plugin.Framework.DelayTicks(GlyphStore.GraceFrames);
        await Plugin.Framework.RunOnFrameworkThread(() => Step("font replacer", this.Replacer.Dispose));
    }

    /// <summary>Applies the settings, each part skipping what didn't change.</summary>
    public void OnUpdate(IFramework framework)
    {
        var log = this._state.Drain();

        var c = this._configuration;
        var fonts = this.Presets.Update();

        // Every cause is known before anything is made again, so that it is made once.
        var scope = this.Replacer.SetEnabled(c.Enabled && !this._state.Failed(PluginFeature.Replacer));

        // The shaders get a value once, so that one that can't be applied isn't tried again until it changes.
        if (c.Edge.Drawn() != this._state.Edge)
            this._edge.Apply(c.Edge);
        this._edge.Ensure();
        scope |= this.Replacer.SetEdge(c.Edge);

        var subpixel = this._state.Failed(PluginFeature.Subpixel) ? default : this._subpixelSource.Get(c);
        if (subpixel != this.AppliedSubpixel)
            this._subpixel.TryApply(subpixel);
        this.AppliedSubpixel = subpixel;
        this._subpixel.Ensure();
        scope |= this.Replacer.SetSubpixel(this._subpixel.Active ? subpixel.Order : SubpixelOrder.None);
        scope |= this.Replacer.SetVerticalSubpixel(c.VerticalSubpixel);
        scope |= this.Replacer.SetAtlas(c.Atlas);
        if (log.GameFontsChanged)
            scope |= ResetScope.Text;
        this.Replacer.Reset(scope, fonts);

        if (scope.HasFlag(ResetScope.Text) || fonts is not null)
            this.RedoText();
        else if (log.PlaneReused)
            this._namePlates?.Rebake();

        if (this._namePlates is not null)
            this._namePlates.Mode = c.NamePlates;
        for (var tab = 0; tab < ChatTabFaceNames.Length; tab++)
            this._chatLog?.SetFontSize(tab, c.FaceFontSizes.GetValueOrDefault(ChatTabFaceNames[tab]));
    }

    public void OnPresent()
    {
        Misc.DebugAssertMainThread();
        this.Replacer.Present();

        // This frame's UI draw commands are processed by now (Present, which is called even while the UI is hidden).
        this._subpixel.ForgetDrawState();
        this._state.EndFrame();
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
        this._namePlates?.Remeasure();
        this._chatLog?.Relayout();
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
        Step("chat log text", this._chatLog is { } chatLog ? chatLog.Dispose : null);
        Step("nameplate text", this._namePlates is { } namePlates ? namePlates.Dispose : null);
        Step("edge shader", this._edge.Dispose);
        Step("subpixel shaders", this._subpixel.Dispose);
        Step("hooks", this.Replacer.Unhook);
        Step("font loader", this._loader is { } loader ? loader.Dispose : null);
    }
}
