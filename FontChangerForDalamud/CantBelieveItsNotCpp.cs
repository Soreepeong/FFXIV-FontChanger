using Dalamud.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

#pragma warning disable CA1069 // Enums values should not be duplicated
#pragma warning disable CA1401 // P/Invokes should not be visible
#pragma warning disable CA2211 // Non-constant fields should not be visible

namespace FontChangerForDalamud {
    public unsafe sealed class CantBelieveItsNotCpp {
        public static string MainModuleRva(IntPtr ptr) {
            var modules = Process.GetCurrentProcess().Modules;
            List<ProcessModule> mh = new();
            for (int i = 0; i < modules.Count; i++)
                mh.Add(modules[i]);

            mh.Sort((x, y) => (long)x.BaseAddress > (long)y.BaseAddress ? -1 : 1);
            foreach (var module in mh) {
                if ((long)module.BaseAddress <= (long)ptr)
                    return $"[{module.ModuleName}+0x{(long)ptr - (long)module.BaseAddress:X}]";
            }
            return $"[0x{(long)ptr:X}]";
        }
    }
}
