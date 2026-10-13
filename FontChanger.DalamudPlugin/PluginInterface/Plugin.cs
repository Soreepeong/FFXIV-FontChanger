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

    // How often System mode re-reads Windows' ClearType settings and the game's monitor.
    private const long SubpixelRefreshMs = 2000;
    private long lastSubpixelQuery;

    public Plugin()
    {
        try
        {
            Host.Current = new DalamudHost();

            // The FFXIVClientStructs files the plugin compiles (see the project file) carry signatures of their own, which
            // the resolver Dalamud ran at startup never saw: register them, and resolve them with it now.
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            InteropGenerator.Runtime.Resolver.GetInstance.Resolve();

            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new();

            // On the framework thread: the hooked functions run there, and the replacer's state isn't locked against them.
            // Without the nameplates and the line breaker, text is still drawn with the replaced fonts: nameplates as the
            // game bakes them, and words split by the game's rules.
            (this.Replacer, this.NamePlates, this.LineBreaker) = Framework.RunOnFrameworkThread(() =>
            {
                var replacer = new FontReplacer();
                replacer.SetAtlas(this.Configuration.Atlas);
                replacer.SetEdge(this.Configuration.Edge);
                replacer.SetSubpixel(SubpixelSettings.Of(this.Configuration.Subpixel, GameWindow));
                return (replacer,
                        Optional("Nameplate text", () => new NamePlateText(replacer)),
                        Optional("Line breaking", () => new LineBreaker(replacer)));
            }).Result;
            this.Presets = new(this.Configuration, this.Replacer);
            this.MainWindow = new(this);
            this.windowSystem.AddWindow(this.MainWindow);

            // The game calls Present on the main thread after the frame's UI was drawn; glyphs added during it are uploaded
            // here and show from the next frame.
            PluginInterface.UiBuilder.Draw += this.Replacer.Upload;

            // Draw may not run while the UI is hidden; the subpixel state must still be let go of every frame.
            Framework.Update += this.OnFrameworkUpdate;
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
            Framework.Update -= this.OnFrameworkUpdate;
            if (this.MainWindow is not null)
                PluginInterface.UiBuilder.OpenMainUi -= this.MainWindow.Toggle;
        });
        TearDown("windows", this.windowSystem.RemoveAllWindows);
        TearDown("presets", () => this.Presets?.Dispose());

        // Between frames on the framework thread: Dalamud calls Dispose there, so this runs inline.
        TearDown("hooks", () => Framework.RunOnFrameworkThread(() =>
        {
            TearDown("line breaker", () => this.LineBreaker?.Dispose());
            TearDown("nameplate text", () => this.NamePlates?.Dispose());
            TearDown("font replacer", () => this.Replacer?.Dispose());
        }).Wait());

        // So the resolver, which lives as long as Dalamud, keeps nothing of this assembly's addresses.
        TearDown("signatures", FFXIVClientStructs.Interop.Generated.Addresses.Unregister);
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

    private static nint GameWindow => PluginInterface.UiBuilder.WindowHandlePtr;

    /// <summary>Applies the configured subpixel mode. Framework thread, between frames.</summary>
    internal void ApplySubpixel()
    {
        this.lastSubpixelQuery = Environment.TickCount64;
        try
        {
            this.Replacer.SetSubpixel(SubpixelSettings.Of(this.Configuration.Subpixel, GameWindow));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Applying the subpixel text setting failed");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.Replacer.EndFrame();
        if (this.Configuration.Subpixel == SubpixelMode.System && Environment.TickCount64 - this.lastSubpixelQuery >= SubpixelRefreshMs)
            this.ApplySubpixel();
    }
}
