using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static FontChangerForDalamud.CantBelieveItsNotCpp;

namespace FontChangerForDalamud {
    public unsafe sealed class Plugin : IDalamudPlugin {
        private class MemorySignatures {
            internal static readonly string CallingFdtTexReloadFromAtkModuleVf43 = string.Join(' ', new string[] {
                /* 0x00 */ "83 f8 04",         // CMP EAX, 0x4
                /* 0x03 */ "74 ??",            // JZ ...
                /* 0x05 */ "83 f8 06",         // CMP EAX, 0x6
                /* 0x08 */ "74 ??",            // JZ ...
                /* 0x0a */ "40 80 fd 01",      // CMP BPL, 0x1
                /* 0x0e */ "75 ??",            // JNZ ...
                /* 0x10 */ "8d 73 ??",         // LEA ESI, [RBX + ?]
                /* 0x13 */ "eb ??",            // JMP ...
                /* 0x15 */ "be 03 00 00 00",   // MOV ESI, 0x3
                /* 0x1a */ "eb ??",            // JMP ...
                /* 0x1c */ "be 02 00 00 00",   // MOV ESI, 0x2
                /* 0x21 */ "45 0f b6 c6",      // MOVZX R8D, R14B    // R8D = Force Reload?
                /* 0x25 */ "8b d6",            // MOV EDX, ESI       // EDX = Font Set Type (0=in-game, 1=lobby)
                /* 0x27 */ "48 8b cf",         // MOV RCX, RDI       // RCX = this = AtkModule
                /* 0x2a */ "e8",               // CALL ...
            });
        }

        public string Name => "Font Changer for Dalamud";
        private readonly string SlashCommand = "/fontchanger";
        private readonly string SlashCommandHelpMessage = "Open font changer interface.";

        private readonly DalamudPluginInterface _pluginInterface;
        private readonly CommandManager _commandManager;
        private readonly ChatGui _chatGui;
        private readonly Framework _framework;
        private readonly SigScanner _sigScanner;
        private readonly Configuration _config;

        private readonly List<DisposableItem> _disposableList = new();

        private delegate void AtkModuleVf43Delegate(AtkModule* pAtkModule, bool bFallback, bool bForceReload);
        private delegate void ReloadFdtDelegate(AtkModule* pAtkModule, int nFontType, bool bForceReload);

        private readonly Hook<ReloadFdtDelegate> _reloadFdtHook;
        private readonly AtkModuleVf43Delegate _atkModuleVf43;

        public Plugin(
            [RequiredVersion("1.0")] DalamudPluginInterface pluginInterface,
            [RequiredVersion("1.0")] CommandManager commandManager,
            [RequiredVersion("1.0")] ClientState clientState,
            [RequiredVersion("1.0")] ChatGui chatGui,
            [RequiredVersion("1.0")] Framework framework,
            [RequiredVersion("1.0")] SigScanner sigScanner) {
            try {
                _pluginInterface = pluginInterface;
                _commandManager = commandManager;
                _chatGui = chatGui;
                _framework = framework;
                _sigScanner = sigScanner;

                _config = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
                _config.Initialize(_pluginInterface);

                _pluginInterface.UiBuilder.Draw += DrawUI;
                _disposableList.Add(new(() => _pluginInterface.UiBuilder.Draw -= DrawUI));
                _pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
                _disposableList.Add(new(() => _pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi));

                var callOpAddress = SigScanner.Scan(sigScanner.TextSectionBase, sigScanner.TextSectionSize, MemorySignatures.CallingFdtTexReloadFromAtkModuleVf43) + 0x2a;
                var callTargetRelativeAddress = Marshal.ReadInt32(callOpAddress + 1);
                var reloadFdtAddress = callOpAddress + 5 + callTargetRelativeAddress;
                _disposableList.Add(new(_reloadFdtHook = new Hook<ReloadFdtDelegate>(reloadFdtAddress, ReloadFdtDetour)));
                _reloadFdtHook.Enable();
                _atkModuleVf43 = Marshal.GetDelegateForFunctionPointer<AtkModuleVf43Delegate>(((IntPtr*)GetAtkModule()->vtbl)[43]);
                _atkModuleVf43(GetAtkModule(), false, true);

                commandManager.AddHandler(SlashCommand, new(OnCommand) {
                    HelpMessage = SlashCommandHelpMessage,
                });
                _disposableList.Add(new(() => _commandManager.RemoveHandler(SlashCommand)));

                _disposableList.Add(new(() => _config.Save()));

            } catch {
                Dispose();
                throw;
            }
        }

        private void Save() {
            if (_config == null)
                return;

            _config.Save();
        }

        public void Dispose() {
            while (_disposableList.Any()) {
                try {
                    _disposableList.Last().Dispose();
                    _disposableList.RemoveAt(_disposableList.Count - 1);
                } catch (Exception e) {
                    PluginLog.Warning(e, "Partial dispose failure");
                }
            }
        }

        private void ReloadFdtDetour(AtkModule* pAtkModule, int nFontType, bool bForceReload) {
            _reloadFdtHook!.Original(pAtkModule, 0, bForceReload);
        }

        private void OpenConfigUi() {
            _config.ConfigVisible = !_config.ConfigVisible;
        }

        private void DrawUI() {
            if (!_config.ConfigVisible)
                return;

            if (ImGui.Begin("FontChangerForDalamud Configuration###MainWindow", ref _config.ConfigVisible)) {
                try {
                    if (ImGui.Button("Refresh fonts")) {
                        _atkModuleVf43(GetAtkModule(), false, true);
                    }
                } finally { ImGui.End(); }
            } else {
                Save();
            }
        }

        private void OnCommand(string command, string arguments) {
            _config.ConfigVisible = true;
            Save();
        }

        private static AtkModule* GetAtkModule() {
            return &FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->GetUiModule()->GetRaptureAtkModule()->AtkModule;
        }

        class DisposableItem : IDisposable {
            private readonly Action _action;

            public DisposableItem(IDisposable? disposable) {
                _action = () => disposable?.Dispose();
            }

            public DisposableItem(Action action) {
                _action = action;
            }

            public void Dispose() {
                _action.Invoke();
            }
        }
    }
}
