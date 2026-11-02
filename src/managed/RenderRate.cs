using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using VRage.Library.Utils;
using VRageRender;
using VRageRender.ExternalApp;

namespace SpaceEngineers.HighFPSFix
{
    // The render thread paces itself with a WaitForTargetFrameRate built from
    // MyPerGameSettings.MaxFrameRate (120). Its target is raised to the
    // configured rate, or the waiter is told to stop waiting. With TargetFPS =
    // 0 the target follows the refresh rate of the monitor the game window is
    // on, checked every two seconds.
    internal static class RenderRate
    {
        private const int MinimumTarget = 30;

        private static WaitForTargetFrameRate s_waiter;
        private static FieldInfo s_frequencyField;

        public static int AppliedTarget { get; private set; }

        public static void Apply(Settings settings)
        {
            MyRenderThread thread = WaitForRenderThread(TimeSpan.FromMinutes(5));
            if (thread == null)
            {
                Log.Error("The render thread never started; the frame rate stays capped.");
                return;
            }
            if (!thread.IsSeparateThread)
            {
                Log.Warning("The game renders on its update thread (SyncRendering); the frame rate stays at the update rate.");
                return;
            }

            FieldInfo waiterField = GameReflection.Field(typeof(MyRenderThread), "m_waiter");
            s_frequencyField = GameReflection.Field(typeof(WaitForTargetFrameRate), "m_targetFrequency");
            s_waiter = (WaitForTargetFrameRate)waiterField.GetValue(thread);
            float previous = (float)s_frequencyField.GetValue(s_waiter);

            if (settings.TargetFps < 0)
            {
                s_waiter.EnableMaxSpeed = true;
                AppliedTarget = -1;
                Log.Info($"Render frame rate: unlimited (was capped at {previous:F0}).");
                return;
            }
            if (settings.TargetFps > 0)
            {
                SetTarget(settings.TargetFps);
                Log.Info($"Render frame rate: capped at {AppliedTarget} (was {previous:F0}).");
                return;
            }

            SetTarget(WindowRefreshRate());
            Log.Info($"Render frame rate: capped at the display's {AppliedTarget} Hz (was {previous:F0}).");
            var follower = new Thread(FollowDisplay) { IsBackground = true, Name = Plugin.Name + " refresh rate" };
            follower.Start();
        }

        private static void SetTarget(int target)
        {
            target = Math.Max(target, MinimumTarget);
            s_frequencyField.SetValue(s_waiter, (float)target);
            AppliedTarget = target;
        }

        private static void FollowDisplay()
        {
            for (;;)
            {
                Thread.Sleep(2000);
                try
                {
                    int rate = Math.Max(WindowRefreshRate(), MinimumTarget);
                    if (rate != AppliedTarget)
                    {
                        Log.Info($"Render frame rate: the display now runs at {rate} Hz (was {AppliedTarget}).");
                        SetTarget(rate);
                    }
                }
                catch (Exception exception)
                {
                    Log.Warning("Refresh-rate check failed: " + exception.Message);
                }
            }
        }

        public static MyRenderThread WaitForRenderThread(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                MyRenderThread thread = MyRenderProxy.RenderThread;
                if (thread != null)
                    return thread;
                Thread.Sleep(10);
            }
            return null;
        }

        // Refresh rate of the monitor showing the game's main window, or of
        // the primary monitor when there is no window yet.
        public static int WindowRefreshRate()
        {
            string device = null;
            IntPtr window;
            using (Process process = Process.GetCurrentProcess())
                window = process.MainWindowHandle;
            if (window != IntPtr.Zero)
            {
                IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
                var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
                    device = info.Device;
            }
            var mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            if (EnumDisplaySettingsW(device, CurrentSettings, ref mode) && mode.DisplayFrequency > 1)
                return (int)mode.DisplayFrequency;
            mode = new DevMode { Size = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            if (device != null && EnumDisplaySettingsW(null, CurrentSettings, ref mode) && mode.DisplayFrequency > 1)
                return (int)mode.DisplayFrequency;
            return 60;
        }

        private const int CurrentSettings = -1;
        private const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string deviceName, int modeNumber, ref DevMode mode);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);
    }
}
