using System;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>Subpixel text settings: the stripe order, and Windows' ClearType level and enhanced contrast.</summary>
internal readonly record struct SubpixelSettings
{
    public SubpixelSettings()
    {
    }

    public SubpixelOrder Order { get; init; }

    /// <summary>Gets the ClearType level, 0 to 1.</summary>
    public float Level { get; init; } = 1;

    /// <summary>Gets k of <c>coverage * (k + 1) / (coverage * k + 1)</c>.</summary>
    public float Contrast { get; init; } = 0.5f;

    /// <summary>Gets the display's gamma, which blending corrects for.</summary>
    public float Gamma { get; init; } = 1.8f;

    /// <summary>Gets the level, contrast, gamma and stripe order from Windows for the window's monitor (the ClearType Text Tuner's).</summary>
    public static unsafe SubpixelSettings OfWindows(nint window)
    {
        var res = new SubpixelSettings();
        var geometry = DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB;
        try
        {
            using var factory = default(ComPtr<IDWriteFactory>);
            using var parameters = default(ComPtr<IDWriteRenderingParams>);
            var iid = IID.IID_IDWriteFactory;
            DirectX.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)factory.GetAddressOf()).ThrowOnError();
            var monitor = window == 0 ? default : TerraFX.Interop.Windows.Windows.MonitorFromWindow((HWND)window, MONITOR.MONITOR_DEFAULTTONEAREST);
            if (monitor != default)
                factory.Get()->CreateMonitorRenderingParams(monitor, parameters.GetAddressOf()).ThrowOnError();
            else
                factory.Get()->CreateRenderingParams(parameters.GetAddressOf()).ThrowOnError();
            geometry = parameters.Get()->GetPixelGeometry();
            res = res with
            {
                Level = Math.Clamp(parameters.Get()->GetClearTypeLevel(), 0, 1),
                Contrast = Math.Max(parameters.Get()->GetEnhancedContrast(), 0),
                Gamma = Math.Max(parameters.Get()->GetGamma(), 1),
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Windows' ClearType settings couldn't be read; using the defaults");
        }

        return res with
        {
            Order = geometry switch
            {
                DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB => SubpixelOrder.Rgb,
                DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR => SubpixelOrder.Bgr,
                _ => SubpixelOrder.None,
            },
        };
    }

    /// <summary>
    /// Gets the settings for the mode: all of Windows' for <see cref="SubpixelMode.System"/>, none if its ClearType is off; else the
    /// stripe order of the mode, with the configured level, contrast and gamma.
    /// </summary>
    public static unsafe SubpixelSettings Of(SubpixelMode mode, ClearTypeSettings clearType, nint window)
    {
        switch (mode)
        {
            case SubpixelMode.System:
                var res = OfWindows(window);
                uint smoothing = 0, type = 0;
                if (!TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHING, 0, &smoothing, 0) || smoothing == 0
                    || !TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHINGTYPE, 0, &type, 0) || type != TerraFX.Interop.Windows.Windows.FE_FONTSMOOTHINGCLEARTYPE)
                    return res with { Order = SubpixelOrder.None };
                return res;
            case SubpixelMode.Rgb or SubpixelMode.Bgr:
                clearType = clearType.Clamped();
                return new()
                {
                    Order = mode == SubpixelMode.Rgb ? SubpixelOrder.Rgb : SubpixelOrder.Bgr,
                    Level = clearType.Level,
                    Contrast = clearType.Contrast,
                    Gamma = clearType.Gamma,
                };
            default:
                return new();
        }
    }
}