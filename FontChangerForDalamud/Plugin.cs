using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace FontChangerForDalamud;

public sealed unsafe class Plugin : IDalamudPlugin {
    private static readonly string SlashCommand = "/fontchanger";
    private static readonly string SlashCommandHelpMessage = "Open font changer interface.";

    private readonly IPluginLog _pluginLog;
    private readonly Configuration _config;

    private readonly List<IDisposable> _disposableList = new();

    private delegate void AtkModuleVf43Delegate(AtkModule* pAtkModule, bool bFallback, bool bForceReload);

    private delegate void ReloadFdtDelegate(AtkModule* pAtkModule, int nFontType, bool bForceReload);

    private readonly Hook<ReloadFdtDelegate> _reloadFdtHook;
    private readonly AtkModuleVf43Delegate _atkModuleVf43;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider gameInteropProvider,
        IPluginLog pluginLog,
        ISigScanner sigScanner) {
        this._pluginLog = pluginLog;
        try {
            this._config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this._config.Initialize(pluginInterface);

            pluginInterface.UiBuilder.Draw += this.DrawUI;
            this._disposableList.Add(Disposable.Create(() => pluginInterface.UiBuilder.Draw -= this.DrawUI));

            pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
            this._disposableList.Add(
                Disposable.Create(() => pluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi));

            var callOpAddress = SigScanner.Scan(sigScanner.TextSectionBase, sigScanner.TextSectionSize,
                MemorySignatures.CallingFdtTexReloadFromAtkModuleVf43) + 0x2a;
            var callTargetRelativeAddress = Marshal.ReadInt32(callOpAddress + 1);
            var reloadFdtAddress = callOpAddress + 5 + callTargetRelativeAddress;
            this._disposableList.Add(this._reloadFdtHook =
                gameInteropProvider.HookFromAddress<ReloadFdtDelegate>(reloadFdtAddress, this.ReloadFdtDetour));
            this._reloadFdtHook.Enable();

            this._atkModuleVf43 =
                Marshal.GetDelegateForFunctionPointer<AtkModuleVf43Delegate>(
                    ((nint*) GetAtkModule()->VirtualTable)[43]);
            this._atkModuleVf43(GetAtkModule(), false, true);

            commandManager.AddHandler(SlashCommand, new(this.OnCommand) {
                HelpMessage = SlashCommandHelpMessage,
            });
            this._disposableList.Add(Disposable.Create(() => commandManager.RemoveHandler(SlashCommand)));

            this._disposableList.Add(Disposable.Create(() => this._config.Save()));
        } catch {
            this.Dispose();
            throw;
        }
    }

    private void Save() => this._config.Save();

    public void Dispose() {
        while (this._disposableList.Count != 0) {
            try {
                this._disposableList[^1].Dispose();
                this._disposableList.RemoveAt(this._disposableList.Count - 1);
            } catch (Exception e) {
                this._pluginLog.Warning(e, "Partial dispose failure");
            }
        }
    }

    private void ReloadFdtDetour(AtkModule* pAtkModule, int nFontType, bool bForceReload) {
        this._reloadFdtHook!.Original(pAtkModule, 0, bForceReload);
    }

    private void OpenConfigUi() {
        this._config.ConfigVisible = !this._config.ConfigVisible;
    }

    private void DrawUI() {
        if (!this._config.ConfigVisible)
            return;

        if (ImGui.Begin("FontChangerForDalamud Configuration###MainWindow", ref this._config.ConfigVisible)) {
            try {
                if (ImGui.Button("Refresh fonts")) {
                    this._atkModuleVf43(GetAtkModule(), false, true);
                }
            } finally {
                ImGui.End();
            }
        } else {
            this.Save();
        }
    }

    private void OnCommand(string command, string arguments) {
        this._config.ConfigVisible = true;
        this.Save();
    }

    private static AtkModule* GetAtkModule() => &RaptureAtkModule.Instance()->AtkModule;

    private static class MemorySignatures {
        internal static readonly string CallingFdtTexReloadFromAtkModuleVf43 = string.Join(
            ' ',
            /* 0x00 */ "83 f8 04", // CMP EAX, 0x4
            /* 0x03 */ "74 ??", // JZ ...
            /* 0x05 */ "83 f8 06", // CMP EAX, 0x6
            /* 0x08 */ "74 ??", // JZ ...
            /* 0x0a */ "40 80 fd 01", // CMP BPL, 0x1
            /* 0x0e */ "75 ??", // JNZ ...
            /* 0x10 */ "8d 73 ??", // LEA ESI, [RBX + ?]
            /* 0x13 */ "eb ??", // JMP ...
            /* 0x15 */ "be 03 00 00 00", // MOV ESI, 0x3
            /* 0x1a */ "eb ??", // JMP ...
            /* 0x1c */ "be 02 00 00 00", // MOV ESI, 0x2
            /* 0x21 */ "45 0f b6 c6", // MOVZX R8D, R14B    // R8D = Force Reload?
            /* 0x25 */ "8b d6", // MOV EDX, ESI       // EDX = Font Set Type (0=in-game, 1=lobby)
            /* 0x27 */ "48 8b cf", // MOV RCX, RDI       // RCX = this = AtkModule
            /* 0x2a */ "e8" // CALL ...
        );
    }
}
