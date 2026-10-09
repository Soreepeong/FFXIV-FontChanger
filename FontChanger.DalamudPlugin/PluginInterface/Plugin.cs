using System;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CustomFonts;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/customfonts";

    private readonly WindowSystem windowSystem = new("CustomFonts");
    private bool commandRegistered;
    private bool disposed;

    public Plugin()
    {
        try
        {
            // On the framework thread: the hooked functions run there, and the replacer's state isn't locked against them.
            this.Replacer = Framework.RunOnFrameworkThread(() => new FontReplacer()).Result;
            // Without these, text is still drawn with the replaced fonts: nameplates as the game bakes them, and words split
            // by the game's rules.
            this.NamePlates = Optional("Nameplate text", () => Framework.RunOnFrameworkThread(() => new NamePlateText(this.Replacer)).Result);
            this.LineBreaker = Optional("Line breaking", () => Framework.RunOnFrameworkThread(() => new LineBreaker(this.Replacer)).Result);
            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();
            Framework.RunOnFrameworkThread(() => this.Replacer.SetEdge(this.Configuration.Edge)).Wait();
            this.Presets = new(this.Configuration, this.Replacer);
            this.MainWindow = new(this);
            this.windowSystem.AddWindow(this.MainWindow);

            // The game calls Present on the main thread after the frame's UI was drawn; glyphs added during it are uploaded
            // here and show from the next frame.
            PluginInterface.UiBuilder.Draw += this.Replacer.Upload;
            PluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += this.MainWindow.Toggle;
            this.commandRegistered = CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
            {
                HelpMessage = "Open the Custom Fonts window.",
            });
        }
        catch
        {
            // Dalamud doesn't call Dispose after a failed load; undo what was set up so far.
            Log.Warning("Loading failed; undoing what was set up");
            this.Dispose();
            throw;
        }
    }

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    internal MainWindow MainWindow { get; }

    internal FontReplacer Replacer { get; }

    /// <summary>Gets the nameplate text, or null if it couldn't be set up for this version of the game.</summary>
    internal NamePlateText? NamePlates { get; }

    /// <summary>Gets the line breaker, or null if it couldn't be set up for this version of the game.</summary>
    internal LineBreaker? LineBreaker { get; }

    internal Configuration Configuration { get; } = null!;

    internal PresetController Presets { get; } = null!;

    /// <summary>
    /// Undoes everything, one step at a time: a step that fails is logged and the others still run. Also the cleanup
    /// after a failed constructor, when the fields it hadn't set yet are still null.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        TearDown("command", () =>
        {
            if (this.commandRegistered)
                CommandManager.RemoveHandler(CommandName);
        });
        TearDown("drawing", () =>
        {
            PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
            if (this.Replacer is not null)
                PluginInterface.UiBuilder.Draw -= this.Replacer.Upload;
            if (this.MainWindow is not null)
                PluginInterface.UiBuilder.OpenMainUi -= this.MainWindow.Toggle;
        });
        TearDown("windows", this.windowSystem.RemoveAllWindows);
        TearDown("presets", () => this.Presets?.Dispose());

        // Between frames on the framework thread: Dalamud calls Dispose there, so this runs inline.
        TearDown("line breaker", () => Framework.RunOnFrameworkThread(() => this.LineBreaker?.Dispose()).Wait());
        TearDown("nameplate text", () => Framework.RunOnFrameworkThread(() => this.NamePlates?.Dispose()).Wait());
        TearDown("font replacer", () => Framework.RunOnFrameworkThread(() => this.Replacer?.Dispose()).Wait());
    }

    /// <summary>Sets up a part the plugin works without; null, with why logged, if it can't be.</summary>
    private static T? Optional<T>(string part, Func<T> create)
        where T : class
    {
        try
        {
            return create();
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException { InnerExceptions: [var single] } ? single : ex;
            Log.Warning(inner, "{Part} is off", part);
            return null;
        }
    }

    /// <summary>Runs one step of <see cref="Dispose"/>; what it throws is logged, and the steps after it still run.</summary>
    private static void TearDown(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Teardown step failed: {step}", step);
        }
    }

    private void OnCommand(string command, string args) => this.MainWindow.Toggle();
}
