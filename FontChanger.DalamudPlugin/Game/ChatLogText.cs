using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>
/// Each tab has a ChatLogPanel of its own, the tab's index in its name, which the main window shows while the tab is selected and
/// which is the tab's window once moved out.
/// </summary>
internal sealed unsafe class ChatLogText : IDisposable
{
    public const int MinFontSize = 8;
    public const int MaxFontSize = 96;

    private static readonly string[] PanelNames = ["ChatLogPanel_0", "ChatLogPanel_1", "ChatLogPanel_2", "ChatLogPanel_3"];

    // LogViewer's OnRequestedUpdate lays its messages out again only if this is set or they changed.
    private const int LogViewerLayoutPendingOffset = 0x98;

    // LogViewer takes line spacing from a table of font sizes up to this. For larger ones, it moves the node's by the size change.
    private const int MaxTableFontSize = 20;
    private const float LineSpacingPerPixel = 26 / 20f;

    private readonly FontReplacer _replacer;
    private readonly DisposeSafety.ScopedFinalizer _finalizer = new();
    private readonly Dictionary<nint, int> _tabs = [];
    private readonly int[] _fontSizes = new int[TextFaceNames.ChatTabCount];
    private readonly int[] _gameFontSizes = new int[TextFaceNames.ChatTabCount];

    // Outlives the PreRefresh listener, which can only replace the refresh's values with a pointer.
    private AtkValue* _refreshValue;

    // The game wraps a tab's messages only when they change.
    private bool _relayoutPending = true;

    public ChatLogText(FontReplacer replacer)
    {
        this._replacer = replacer;
        try
        {
            this._replacer.AddNodeFaces(this.GetNodeFaces);
            this._finalizer.Add(() => this._replacer.RemoveNodeFaces(this.GetNodeFaces));
            this._replacer.TextInvalidated += this.OnTextInvalidated;
            this._finalizer.Add(() => this._replacer.TextInvalidated -= this.OnTextInvalidated);
            Plugin.Framework.Update += this.OnFrameworkUpdate;
            this._finalizer.Add(() => Plugin.Framework.Update -= this.OnFrameworkUpdate);
            this._refreshValue = (AtkValue*)NativeMemory.AllocZeroed((nuint)sizeof(AtkValue));
            this._finalizer.Add(() => NativeMemory.Free(this._refreshValue));
            Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreRefresh, PanelNames, this.OnPreRefresh);
            this._finalizer.Add(() => Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreRefresh, PanelNames, this.OnPreRefresh));

            // First when undone, so that the refreshes giving the game's sizes back still go through the listener.
            this._finalizer.Add(() => {
                for (var tab = 0; tab < this._fontSizes.Length; tab++)
                    this.SetFontSize(tab, 0);
            });
            this.ReadPanels();
        }
        catch
        {
            try
            {
                this._finalizer.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Undoing a failed chat log text setup failed");
            }

            throw;
        }
    }

    public void Dispose()
    {
        Misc.DebugAssertMainThread();
        this._finalizer.Dispose();
    }

    /// <summary>0 for the game's.</summary>
    public void SetFontSize(int tab, int size)
    {
        Misc.DebugAssertMainThread();
        size = size == 0 ? 0 : Math.Clamp(size, MinFontSize, MaxFontSize);
        if (tab < 0 || tab >= this._fontSizes.Length || this._fontSizes[tab] == size)
            return;
        this._fontSizes[tab] = size;
        var panel = GetPanel(tab);
        if (panel is null)
            return;

        // A panel not refreshed since the plugin loaded still has the game's size. The listener replaces it, as in the game's refreshes.
        if (this._gameFontSizes[tab] == 0)
            this._gameFontSizes[tab] = panel->LogViewer.FontSize;
        var value = new AtkValue { Type = AtkValueType.UInt, UInt = (uint)this._gameFontSizes[tab] };
        panel->AtkUnitBase.OnRefresh(1, &value);
    }

    private static AddonChatLogPanel* GetPanel(int tab)
    {
        var panel = Plugin.GameGui.GetAddonByName<AddonChatLogPanel>(PanelNames[tab]);
        return panel is not null && panel->AtkUnitBase.IsReady ? panel : null;
    }

    private void ReadPanels()
    {
        this._tabs.Clear();
        for (var tab = 0; tab < PanelNames.Length; tab++)
        {
            var panel = GetPanel(tab);
            if (panel is not null && panel->ChatText is not null)
                this._tabs[(nint)panel->ChatText] = tab;
        }
    }

    // The refresh's first value is the tab's font size.
    private void OnPreRefresh(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRefreshArgs { AtkValueCount: > 0 } refresh || Array.IndexOf(PanelNames, args.AddonName) is not (var tab and >= 0))
            return;
        var values = (AtkValue*)refresh.AtkValues;
        var panel = (AddonChatLogPanel*)args.Addon.Address;
        if (values is null || values->Type is not (AtkValueType.UInt or AtkValueType.Int) || panel->ChatText is null)
            return;
        this._gameFontSizes[tab] = (int)values->UInt;
        var size = this._fontSizes[tab];
        if (size == 0)
            return;

        var text = panel->ChatText;
        if (size > MaxTableFontSize)
            text->LineSpacing = (byte)Math.Clamp((int)MathF.Round(size * LineSpacingPerPixel) - size + text->FontSize, 0, byte.MaxValue);
        *this._refreshValue = new() { Type = AtkValueType.UInt, UInt = (uint)size };
        refresh.AtkValues = (nint)this._refreshValue;
        refresh.AtkValueCount = 1;
    }

    private NodeFaces? GetNodeFaces(Pointer<AtkTextNode> node) =>
        this._tabs.TryGetValue((nint)node.Value, out var tab) ? new NodeFaces(TextFaceNames.ChatTab(tab)) : null;

    private void OnTextInvalidated() => this._relayoutPending = true;

    // Between frames, as the game's update may be what invalidated the text (a font it built).
    private void OnFrameworkUpdate(IFramework framework)
    {
        this.ReadPanels();
        if (!this._relayoutPending)
            return;
        this._relayoutPending = false;
        var stage = AtkStage.Instance();
        for (var tab = 0; tab < PanelNames.Length; tab++)
        {
            var panel = GetPanel(tab);
            if (panel is null)
                continue;
            *((byte*)&panel->LogViewer + LogViewerLayoutPendingOffset) = 1;
            panel->AtkUnitBase.OnRequestedUpdate(stage->GetNumberArrayData(), stage->GetStringArrayData());
        }
    }
}
