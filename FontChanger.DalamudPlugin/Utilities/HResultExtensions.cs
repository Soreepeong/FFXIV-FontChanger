using System.Runtime.InteropServices;

using TerraFX.Interop.Windows;

namespace CustomFonts;

internal static class HResultExtensions
{
    /// <summary>Throws the exception matching a failed HRESULT.</summary>
    public static void ThrowOnError(this HRESULT hr)
    {
        if (hr.FAILED)
            Marshal.ThrowExceptionForHR(hr.Value);
    }
}
