using System;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Shaders;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>The subpixel settings wanted, read from the system every <see cref="RefreshMs"/> while on, and at once when the setting changes.</summary>
internal sealed class SubpixelSource
{
    private const long RefreshMs = 2000;

    private (SubpixelMode Mode, SubpixelRenderingParameters ClearType)? _setting;
    private SubpixelShaders.SettingsRecord _wanted;
    private long _lastQuery;

    public SubpixelShaders.SettingsRecord Get(Configuration.Configuration configuration)
    {
        var setting = (configuration.Subpixel, configuration.ClearType);
        var now = Environment.TickCount64;
        if (setting == this._setting && (setting.Subpixel == SubpixelMode.Off || now - this._lastQuery < RefreshMs))
            return this._wanted;

        this._setting = setting;
        this._lastQuery = now;
        try
        {
            this._wanted = SubpixelShaders.SettingsRecord.From(setting.Subpixel, setting.ClearType, Plugin.PluginInterface.UiBuilder.WindowHandlePtr);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Reading the subpixel text setting failed");
        }

        return this._wanted;
    }
}
