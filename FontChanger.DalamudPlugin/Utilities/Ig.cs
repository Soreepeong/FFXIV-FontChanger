using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace FontChanger.DalamudPlugin.Utilities;

public static class Ig
{
    public static void TabBar<T>(ImU8String label, T ctx, Action<T> cb)
    {
        if (!ImGui.BeginTabBar(label))
            return;
        try
        {
            cb(ctx);
        }
        finally
        {
            ImGui.EndTabBar();
        }
    }

    public static void TabItem<T>(ImU8String label, T ctx, Action<T> cb)
    {
        if (!ImGui.BeginTabItem(label))
            return;
        try
        {
            cb(ctx);
        }
        finally
        {
            ImGui.EndTabItem();
        }
    }

    public static void Popup<T>(ImU8String label, T ctx, Action<T> cb)
    {
        if (!ImGui.BeginPopup(label))
            return;
        try
        {
            cb(ctx);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    public static void PopupContextItem<T>(ImU8String label, T ctx, Action<T> cb)
    {
        if (!ImGui.BeginPopupContextItem(label))
            return;
        try
        {
            cb(ctx);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    public static void Child<T>(ImU8String label, Vector2 size, bool border, T ctx, Action<T> cb)
    {
        if (!ImGui.BeginChild(label, size, border))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            cb(ctx);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

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
}
