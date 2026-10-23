using System;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

internal sealed partial class SubpixelShaders
{
    internal readonly record struct SettingsRecord(SubpixelOrder Order = SubpixelOrder.None, float Level = 1, float Contrast = 0.5f, float Gamma = 1.8f,
        float GrayscaleContrast = 1)
    {
        public static SettingsRecord FromWindow(nint window) =>
            FromMonitor(window == 0 ? default : MonitorFromWindow((HWND)window, MONITOR.MONITOR_DEFAULTTONEAREST));

        public static unsafe SettingsRecord From(SubpixelMode mode, SubpixelRenderingParameters clearType, nint window)
        {
            switch (mode)
            {
                case SubpixelMode.System:
                    var res = FromWindow(window);
                    uint smoothing = 0, type = 0;
                    if (!SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHING, 0, &smoothing, 0) || smoothing == 0
                        || !SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHINGTYPE, 0, &type, 0) || type != FE_FONTSMOOTHINGCLEARTYPE)
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
                        GrayscaleContrast = clearType.GrayscaleContrast,
                    };
                default:
                    return new();
            }
        }

        private static unsafe SettingsRecord FromMonitor(nint monitor)
        {
            var res = new SettingsRecord();
            var geometry = DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB;
            try
            {
                using var factory = default(MyComPtr<IDWriteFactory7>);
                using var created = default(MyComPtr<IDWriteRenderingParams>);
                using var parameters = default(MyComPtr<IDWriteRenderingParams3>);
                var iid = IID.IID_IDWriteFactory7;
                DirectX.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)factory.GetAddressOf()).ThrowOnError();
                if (monitor != 0)
                    factory.Value->CreateMonitorRenderingParams((HMONITOR)monitor, created.GetAddressOf()).ThrowOnError();
                else
                    factory.Value->CreateRenderingParams(created.GetAddressOf()).ThrowOnError();
                created.CopyTo(parameters.GetAddressOf()).ThrowOnError();
                geometry = parameters.Value->GetPixelGeometry();
                res = res with
                {
                    Level = Math.Clamp(parameters.Value->GetClearTypeLevel(), 0, 1),
                    Contrast = Math.Max(parameters.Value->GetEnhancedContrast(), 0),
                    Gamma = Math.Max(parameters.Value->GetGamma(), 1),
                    GrayscaleContrast = Math.Max(parameters.Value->GetGrayscaleEnhancedContrast(), 0),
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Windows' ClearType settings couldn't be read; using the defaults");
            }

            // Windows' pixel geometry is the panel's, left to right, whichever way the picture is turned on it.
            var bgr = geometry == DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR;
            return res with
            {
                Order = geometry is not (DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB or DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR) ? SubpixelOrder.None
                        : GetOrientation(monitor) switch
                        {
                            // The picture turned clockwise on a panel turned counterclockwise has its left at the bottom.
                            DMDO_90 => bgr ? SubpixelOrder.VerticalRgb : SubpixelOrder.VerticalBgr,
                            DMDO_180 => bgr ? SubpixelOrder.Rgb : SubpixelOrder.Bgr,
                            DMDO_270 => bgr ? SubpixelOrder.VerticalBgr : SubpixelOrder.VerticalRgb,
                            _ => bgr ? SubpixelOrder.Bgr : SubpixelOrder.Rgb,
                        },
            };
        }

        /// <summary>Returns 0 (DMDO_DEFAULT) if unknown.</summary>
        private static unsafe uint GetOrientation(nint monitor)
        {
            if (monitor == 0)
                return 0;
            var info = new MONITORINFOEXW();
            info.Base.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!GetMonitorInfoW((HMONITOR)monitor, (MONITORINFO*)&info))
                return 0;
            var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
            return EnumDisplaySettingsW((char*)&info.szDevice, ENUM.ENUM_CURRENT_SETTINGS, &mode) ? mode.dmDisplayOrientation : 0;
        }
    }
}
