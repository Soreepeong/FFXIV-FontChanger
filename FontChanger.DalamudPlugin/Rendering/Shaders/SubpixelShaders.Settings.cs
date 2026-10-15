using System;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

internal sealed partial class SubpixelShaders
{
    internal readonly record struct SettingsRecord(SubpixelOrder Order = SubpixelOrder.None, float Level = 1, float Contrast = 0.5f, float Gamma = 1.8f)
    {
        public static SettingsRecord FromWindow(nint window) =>
            FromMonitor(window == 0 ? default : TerraFX.Interop.Windows.Windows.MonitorFromWindow((HWND)window, MONITOR.MONITOR_DEFAULTTONEAREST));

        public static unsafe SettingsRecord FromMonitor(nint monitor)
        {
            var res = new SettingsRecord();
            var geometry = DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB;
            try
            {
                using var factory = default(ComPtr<IDWriteFactory>);
                using var parameters = default(ComPtr<IDWriteRenderingParams>);
                var iid = IID.IID_IDWriteFactory;
                DirectX.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)factory.GetAddressOf()).ThrowOnError();
                if (monitor != 0)
                    factory.Get()->CreateMonitorRenderingParams((HMONITOR)monitor, parameters.GetAddressOf()).ThrowOnError();
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

        public static unsafe SettingsRecord From(SubpixelMode mode, SubpixelRenderingParameters clearType, nint window)
        {
            switch (mode)
            {
                case SubpixelMode.System:
                    var res = FromWindow(window);
                    uint smoothing = 0, type = 0;
                    if (!TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHING, 0, &smoothing, 0) || smoothing == 0
                        || !TerraFX.Interop.Windows.Windows.SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHINGTYPE, 0, &type, 0) ||
                        type != TerraFX.Interop.Windows.Windows.FE_FONTSMOOTHINGCLEARTYPE)
                        return res with { Order = SubpixelOrder.None };
                    return res;
                case SubpixelMode.Rgb or SubpixelMode.Bgr or SubpixelMode.VerticalRgb or SubpixelMode.VerticalBgr:
                    clearType = clearType.Clamped();
                    return new()
                    {
                        Order = mode switch
                        {
                            SubpixelMode.Rgb => SubpixelOrder.Rgb,
                            SubpixelMode.Bgr => SubpixelOrder.Bgr,
                            SubpixelMode.VerticalRgb => SubpixelOrder.VerticalRgb,
                            _ => SubpixelOrder.VerticalBgr,
                        },
                        Level = clearType.Level,
                        Contrast = clearType.Contrast,
                        Gamma = clearType.Gamma,
                    };
                default:
                    return new();
            }
        }
    }
}
