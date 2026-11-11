using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;

namespace FontChanger.DalamudPlugin.PluginInterface;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const string CommandName = "/fontchanger";

    private readonly WindowSystem _windowSystem = new(Assembly.GetExecutingAssembly().GetName().Name);
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    private ConfigWindow _configWindow = null!;

    public Plugin()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19043))
        {
            var errmsg = $"FontChanger needs Windows 10 21H1 (10.0.19043) or later; this is {Environment.OSVersion.Version}.";
            NotificationManager.AddNotification(new()
            {
                Content = errmsg,
                Icon = INotificationIcon.From(FontAwesomeIcon.ExclamationTriangle),
            });
            throw new PlatformNotSupportedException(errmsg);
        }

        this.Configuration = PluginInterface.GetPluginConfig() as Configuration.Configuration ?? new();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var disposeOnException = new DisposeSafety.ScopedFinalizer().With(_finalizer);

        await Framework.RunOnFrameworkThread(() => {
            foreach (var rule in this.Configuration.NamePlateRules)
                RegisterNamePlateRule(rule);
            this.Coordinator = new(this.Configuration);
        });
        this._finalizer.Add(() => this.Coordinator.DisposeAsync().AsTask());

        this._windowSystem.AddWindow(this._configWindow = new(this));
        this._finalizer.Add(this._windowSystem.RemoveAllWindows);

        PluginInterface.UiBuilder.Draw += this.Coordinator.OnPresent;
        this._finalizer.Add(() => PluginInterface.UiBuilder.Draw -= this.Coordinator.OnPresent);

        Framework.Update += this.Coordinator.OnUpdate;
        this._finalizer.Add(() => Framework.Update -= this.Coordinator.OnUpdate);

        // Draw doubles as the Present callback, so it must keep running while the UI is hidden.
        PluginInterface.UiBuilder.DisableAutomaticUiHide = true;
        PluginInterface.UiBuilder.Draw += this._windowSystem.Draw;
        this._finalizer.Add(() => PluginInterface.UiBuilder.Draw -= this._windowSystem.Draw);

        PluginInterface.UiBuilder.OpenConfigUi += this._configWindow.Toggle;
        this._finalizer.Add(() => PluginInterface.UiBuilder.OpenConfigUi -= this._configWindow.Toggle);

        CommandManager.AddHandler(CommandName, new(this.OnCommand) { HelpMessage = "Open the Custom Fonts window." });
        this._finalizer.Add(() => CommandManager.RemoveHandler(CommandName));

        disposeOnException.Cancel();
    }

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static ISigScanner SigScanner { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static ITargetManager TargetManager { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    [PluginService]
    internal static INotificationManager NotificationManager { get; private set; } = null!;

    internal FrameCoordinator Coordinator { get; private set; } = null!;

    internal FontReplacer Replacer => this.Coordinator.Replacer;

    internal Configuration.Configuration Configuration { get; }

    internal PresetController Presets => this.Coordinator.Presets;

    public ValueTask DisposeAsync() => this._finalizer.DisposeAsync();

    private void OnCommand(string command, string args) => this._configWindow.Toggle();

    // A rule's title may have fonts without its plate's, so plates are matched to the rule whether or not its faces have fonts.
    internal static void RegisterNamePlateRule(string rule)
    {
        TextFaceNames.TryGetFace(rule, out _);
        TextFaceNames.TryGetFace(TextFaceNames.NamePlateRuleTitleName(rule), out _);
    }

    /// <summary>Saves the configuration, whose changes are applied at the next framework update.</summary>
    internal void SaveConfiguration() => PluginInterface.SavePluginConfig(this.Configuration);
}
