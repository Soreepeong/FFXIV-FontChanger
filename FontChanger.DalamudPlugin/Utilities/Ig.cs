using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace FontChanger.DalamudPlugin.Utilities;

public static class Ig
{
    public static void TabBar<T>(ImU8String label, T ctx, Action<T> cb) => Scope(ImGui.BeginTabBar(label), ctx, cb, ImGui.EndTabBar);

    public static void TabItem<T>(ImU8String label, T ctx, Action<T> cb) => Scope(ImGui.BeginTabItem(label), ctx, cb, ImGui.EndTabItem);

    public static void Popup<T>(ImU8String label, T ctx, Action<T> cb) => Scope(ImGui.BeginPopup(label), ctx, cb, ImGui.EndPopup);

    public static void PopupContextItem<T>(ImU8String label, T ctx, Action<T> cb) => Scope(ImGui.BeginPopupContextItem(label), ctx, cb, ImGui.EndPopup);

    public static void Child<T>(ImU8String label, Vector2 size, bool border, T ctx, Action<T> cb) =>
        Scope(ImGui.BeginChild(label, size, border), ctx, cb, ImGui.EndChild, true);

    public static bool Radios(ImU8String label, ref int value, params ReadOnlySpan<string> names)
    {
        var changed = false;
        ImGui.PushID(label);
        ImGui.TextUnformatted(label);
        for (var i = 0; i < names.Length; i++)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton(names[i], value == i) && value != i)
            {
                value = i;
                changed = true;
            }
        }

        ImGui.PopID();
        return changed;
    }

    private static void Scope<T>(bool cond, T ctx, Action<T> cb, Action end, bool endUnconditionally = false)
    {
        if (cond)
        {
            try
            {
                cb(ctx);
            }
            finally
            {
                end();
            }
        }
        else if (endUnconditionally)
        {
            end();
        }
    }
}
