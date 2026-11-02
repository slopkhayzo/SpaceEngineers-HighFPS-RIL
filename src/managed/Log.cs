using System;

namespace SpaceEngineers.HighFPSFix
{
    // Writes into the native log (one file, one lock for both sides).
    internal static class Log
    {
        public static bool Diagnostics { get; set; }

        public static void Debug(string message)
        {
            if (Diagnostics)
                NativeApi.Log(0, message);
        }

        public static void Info(string message) => NativeApi.Log(1, message);

        public static void Warning(string message) => NativeApi.Log(2, message);

        public static void Error(string message) => NativeApi.Log(3, message);

        public static void Error(string message, Exception exception) =>
            NativeApi.Log(3, message + ": " + exception);
    }
}
