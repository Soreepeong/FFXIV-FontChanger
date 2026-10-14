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

    /// <summary>
    /// Gets the level and contrast from Windows for the window's monitor; <see cref="SubpixelMode.System"/> takes the stripe
    /// order from there too.
    /// </summary>
    public static unsafe SubpixelSettings Of(SubpixelMode mode, nint window)
    {
        var res = new SubpixelSettings();
        if (mode == SubpixelMode.Off)
            return res;

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
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Windows' ClearType settings couldn't be read; using the defaults");
        }

        switch (mode)
        {
            case SubpixelMode.System:
                uint smoothing = 0, type = 0;
                if (!TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHING, 0, &smoothing, 0) || smoothing == 0
                    || !TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHINGTYPE, 0, &type, 0) || type != TerraFX.Interop.Windows.Windows.FE_FONTSMOOTHINGCLEARTYPE)
                    return res;
                return res with
                {
                    Order = geometry switch
                    {
                        DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB => SubpixelOrder.Rgb,
                        DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR => SubpixelOrder.Bgr,
                        _ => SubpixelOrder.None,
                    },
                };
            case SubpixelMode.Rgb:
                return res with { Order = SubpixelOrder.Rgb };
            case SubpixelMode.Bgr:
                return res with { Order = SubpixelOrder.Bgr };
            default:
                return res;
        }
    }
}