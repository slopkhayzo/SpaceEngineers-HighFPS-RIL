using System;

namespace SpaceEngineers.HighFPSFix
{
    // Reads sehff::NativeApi (src/native/native_api.h) by explicit offsets:
    //
    //   0  uint32 size        8  createHook     24 removeHook   40 diagnosticsEnabled
    //   4  uint32 version     16 applyQueued    32 log
    //
    // A C# struct with function-pointer fields would not do: .NET Framework
    // does not count those fields as blittable and may reorder them.
    internal static unsafe class NativeApi
    {
        public const uint ExpectedVersion = 2;
        public const uint ExpectedSize = 48;

        private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int> s_createHook;
        private static delegate* unmanaged[Cdecl]<int> s_applyQueued;
        private static delegate* unmanaged[Cdecl]<IntPtr, int> s_removeHook;
        private static delegate* unmanaged[Cdecl]<int, char*, void> s_log;
        private static delegate* unmanaged[Cdecl]<int> s_diagnosticsEnabled;

        public static bool IsBound => s_log != null;

        public static string Bind(IntPtr table)
        {
            if (table == IntPtr.Zero)
                return "no native API table";
            byte* bytes = (byte*)table;
            uint size = *(uint*)bytes;
            uint version = *(uint*)(bytes + 4);
            if (size != ExpectedSize)
                return $"native API table size {size}, expected {ExpectedSize}";
            if (version != ExpectedVersion)
                return $"native API version {version}, expected {ExpectedVersion}";
            s_createHook = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int>)*(IntPtr*)(bytes + 8);
            s_applyQueued = (delegate* unmanaged[Cdecl]<int>)*(IntPtr*)(bytes + 16);
            s_removeHook = (delegate* unmanaged[Cdecl]<IntPtr, int>)*(IntPtr*)(bytes + 24);
            s_log = (delegate* unmanaged[Cdecl]<int, char*, void>)*(IntPtr*)(bytes + 32);
            s_diagnosticsEnabled = (delegate* unmanaged[Cdecl]<int>)*(IntPtr*)(bytes + 40);
            return null;
        }

        // Creates a hook and queues it; ApplyQueued enables everything queued.
        public static int CreateHook(IntPtr target, IntPtr detour, out IntPtr original)
        {
            IntPtr trampoline = IntPtr.Zero;
            int status = s_createHook(target, detour, &trampoline);
            original = trampoline;
            return status;
        }

        public static int ApplyQueued() => s_applyQueued();

        public static int RemoveHook(IntPtr target) => s_removeHook(target);

        public static void Log(int level, string message)
        {
            if (s_log == null || message == null)
                return;
            fixed (char* text = message)
                s_log(level, text);
        }

        public static bool DiagnosticsEnabled => s_diagnosticsEnabled != null && s_diagnosticsEnabled() != 0;
    }
}
