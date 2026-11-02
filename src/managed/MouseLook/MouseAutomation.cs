using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // [Debug] AutoMouseSpeed: synthetic relative mouse motion for unattended
    // mouse look measurements, sent with SendInput every millisecond, and only
    // while the game window is in the foreground and the player looks in
    // first person (the pattern's clock stops otherwise). Repeating 8 s
    // pattern, rates in counts per second for a speed V:
    //   0-2 s    yaw at V
    //   2.5-4.5  yaw at 1.5 V sin(2 pi t / 1 s), back and forth
    //   5-7      pitch at 0.5 V sin(2 pi t / 2 s), down and back up
    // and still in between. With 'alternate', the lead is switched on for
    // even and off for odd repetitions (at the start of a repetition, after a
    // second of stillness), for A/B comparisons within one session.
    internal static class MouseAutomation
    {
        private const double Period = 8.0;

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public int Dx;
            [FieldOffset(12)] public int Dy;
            [FieldOffset(16)] public uint MouseData;
            [FieldOffset(20)] public uint Flags;
            [FieldOffset(24)] public uint Time;
            [FieldOffset(32)] public IntPtr ExtraInfo;
        }

        private const uint InputMouse = 0;
        private const uint MouseEventMove = 0x0001;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        // Asks Windows to bring the game window to the front. Windows grants
        // it only while nobody is using another program (otherwise it flashes
        // the taskbar button instead), so it never takes the focus from a
        // user at the keyboard.
        public static void RequestFocus()
        {
            IntPtr window = Process.GetCurrentProcess().MainWindowHandle;
            bool granted = window != IntPtr.Zero && SetForegroundWindow(window);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
            bool focused = foreground == (uint)Process.GetCurrentProcess().Id;
            Log.Info($"Debug: asked for the foreground: {(granted ? "granted" : "refused")}, game window {(focused ? "focused" : "not focused")}.");
        }

        public static void Start(double speed, bool alternate)
        {
            var thread = new Thread(() => Run(speed, alternate)) { IsBackground = true, Name = Plugin.Name + " mouse automation" };
            thread.Start();
        }

        // A menu the game opened over the loaded world in a test run (its
        // pause menu, or a "controller disconnected" message box when it saw
        // no mouse or keyboard input) is closed, on the update thread.
        private static void CloseMenuOverWorld()
        {
            Sandbox.MySandboxGame.Static?.Invoke(() =>
            {
                Sandbox.Graphics.GUI.MyGuiScreenBase screen = Sandbox.Graphics.GUI.MyScreenManager.GetScreenWithFocus();
                if (Sandbox.Game.World.MySession.Static?.Ready != true || screen == null ||
                    ReferenceEquals(screen, Sandbox.Game.Gui.MyGuiScreenGamePlay.Static))
                    return;
                string name = screen.GetType().Name;
                if (name != "MyGuiScreenMainMenu" && name != "MyGuiScreenMessageBox")
                    return;
                Log.Info("Debug: closing " + name + " over the test world.");
                screen.CloseScreen(false);
            }, Plugin.Name);
        }

        public static void Rates(double phase, double speed, out double rateX, out double rateY)
        {
            rateX = rateY = 0;
            if (phase < 2.0)
                rateX = speed;
            else if (phase >= 2.5 && phase < 4.5)
                rateX = 1.5 * speed * Math.Sin(2.0 * Math.PI * (phase - 2.5));
            else if (phase >= 5.0 && phase < 7.0)
                rateY = 0.5 * speed * Math.Sin(Math.PI * (phase - 5.0));
        }

        private static void Run(double speed, bool alternate)
        {
            try
            {
                timeBeginPeriod(1);
                uint self = (uint)Process.GetCurrentProcess().Id;
                var input = new Input[1];
                input[0].Type = InputMouse;
                input[0].Flags = MouseEventMove;
                double frequency = Stopwatch.Frequency;
                long last = Stopwatch.GetTimestamp();
                long nextReport = last + Stopwatch.Frequency * 5;
                double phase = 0, active = 0, accumulatedX = 0, accumulatedY = 0;
                long sentX = 0, sentY = 0;
                int repetition = -1;
                bool waiting = true;
                long nextClose = last;
                Log.Info($"Debug: mouse automation at up to {speed} counts/s ({Period} s pattern" +
                         (alternate ? ", lead on and off by turns)." : ")."));
                while (true)
                {
                    Thread.Sleep(1);
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = Math.Min(0.05, (now - last) / frequency);
                    last = now;
                    if (now >= nextReport)
                    {
                        nextReport = now + Stopwatch.Frequency * 5;
                        Log.Debug($"Debug: mouse automation sent {sentX}, {sentY} counts in {active:F1} s of pattern.");
                    }
                    GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
                    if (foreground != self || !TickCapture.LastFirstPerson)
                    {
                        if (!waiting)
                            Log.Info("Debug: mouse automation paused (game window not focused, or not in first person).");
                        waiting = true;
                        if (foreground == self && now >= nextClose)
                        {
                            nextClose = now + Stopwatch.Frequency * 2;
                            CloseMenuOverWorld();
                        }
                        continue;
                    }
                    if (waiting)
                    {
                        Log.Info("Debug: mouse automation running.");
                        waiting = false;
                    }
                    if ((int)(active / Period) != repetition)
                    {
                        repetition = (int)(active / Period);
                        if (alternate)
                        {
                            Interpolation.SceneInterpolator.MouseLookApply = repetition % 2 == 0;
                            Log.Info($"Debug: repetition {repetition}, lead {(repetition % 2 == 0 ? "on" : "off")}.");
                        }
                    }
                    Rates((phase + 0.5 * elapsed) % Period, speed, out double rateX, out double rateY);
                    phase = (phase + elapsed) % Period;
                    active += elapsed;
                    accumulatedX += rateX * elapsed;
                    accumulatedY += rateY * elapsed;
                    int dx = (int)(Math.Round(accumulatedX) - sentX);
                    int dy = (int)(Math.Round(accumulatedY) - sentY);
                    if (dx == 0 && dy == 0)
                        continue;
                    input[0].Dx = dx;
                    input[0].Dy = dy;
                    if (SendInput(1, input, Marshal.SizeOf(typeof(Input))) == 1)
                    {
                        sentX += dx;
                        sentY += dy;
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error("Debug: mouse automation failed", exception);
            }
        }
    }
}
