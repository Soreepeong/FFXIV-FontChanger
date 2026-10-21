using System;
using System.Collections.Generic;
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

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Shaders;
using FontChanger.DalamudPlugin.Game;

namespace FontChanger.DalamudPlugin.PluginInterface;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private const string CommandName = "/fontchanger";
    private const long SubpixelRefreshMs = 2000;

    private readonly WindowSystem _windowSystem = new(Assembly.GetExecutingAssembly().GetName().Name);
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();

    private long _lastSubpixelQuery;
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
            this.Replacer = new();
            Optional("Nameplate text", () => this.NamePlates = new(this.Replacer));
            Optional("Chat log text", () => this.ChatLog = new(this.Replacer));
            Optional("Line breaking", () => this.LineBreaker = new(this.Replacer));
            this._finalizer.Add(() => Framework.RunOnFrameworkThread(() => {
                this.LineBreaker?.Dispose();
                this.ChatLog?.Dispose();
                this.NamePlates?.Dispose();
                this.Replacer.Dispose();
            }));

            this.Replacer.Atlas = this.Configuration.Atlas;
            this.Replacer.Edge = this.Configuration.Edge;
            this.Replacer.VerticalSubpixel = this.Configuration.VerticalSubpixel;
            this.ApplySubpixel();
            this.NamePlates?.Mode = this.Configuration.NamePlates;
            foreach (var (face, name) in TextFaceNames.Faces)
                this.SetFaceFontSize(face, this.Configuration.FaceFontSizes.GetValueOrDefault(name));
            foreach (var rule in this.Configuration.NamePlateRules)
                RegisterNamePlateRule(rule);
            return;

            static void Optional(string part, Action create)
            {
                try
                {
                    create();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "{Part} is off", part);
                }
            }
        });

        this.Presets = this._finalizer.Add<PresetController>(new(this.Configuration, this.Replacer));

        this._windowSystem.AddWindow(this._configWindow = new(this));
        this._finalizer.Add(this._windowSystem.RemoveAllWindows);

        PluginInterface.UiBuilder.Draw += this.Replacer.Upload;
        this._finalizer.Add(() => PluginInterface.UiBuilder.Draw -= this.Replacer.Upload);

        Framework.Update += this.OnFrameworkUpdate;
        this._finalizer.Add(() => Framework.Update -= this.OnFrameworkUpdate);

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

    internal FontReplacer Replacer { get; private set; } = null!;

    internal NamePlateText? NamePlates { get; private set; }

    internal ChatLogText? ChatLog { get; private set; }

    internal LineBreaker? LineBreaker { get; private set; }

    internal Configuration.Configuration Configuration { get; }

    internal PresetController Presets { get; private set; } = null!;

    public ValueTask DisposeAsync() => this._finalizer.DisposeAsync();

    private void OnCommand(string command, string args) => this._configWindow.Toggle();

    internal bool HasFace(TextFace face) => TextFaceNames.IsNamePlates(face) ? this.NamePlates is not null : this.ChatLog is not null;

    // A rule's title may have fonts without its plate's, so plates are matched to the rule whether or not its faces have fonts.
    internal static void RegisterNamePlateRule(string rule)
    {
        TextFaceNames.TryGetFace(rule, out _);
        TextFaceNames.TryGetFace(TextFaceNames.NamePlateRuleTitleName(rule), out _);
    }

    internal void SetFaceFontSize(TextFace face, int size)
    {
        if (TextFaceNames.ChatTabOf(face) is var tab and >= 0)
            this.ChatLog?.SetFontSize(tab, size);
    }

    internal void ApplySubpixel()
    {
        this._lastSubpixelQuery = Environment.TickCount64;
        try
        {
            this.Replacer.Subpixel =
                SubpixelShaders.SettingsRecord.From(this.Configuration.Subpixel, this.Configuration.ClearType, PluginInterface.UiBuilder.WindowHandlePtr);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Applying the subpixel text setting failed");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.Replacer.EndFrame();
        if (this.Configuration.Subpixel != SubpixelMode.Off && Environment.TickCount64 - this._lastSubpixelQuery >= SubpixelRefreshMs)
            this.ApplySubpixel();
    }
}
