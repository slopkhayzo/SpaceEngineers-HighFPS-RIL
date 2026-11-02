using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpDX.DirectInput;
using VRage.Input;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // The game reads the mouse once per simulation step, on the update thread
    // (MyVRageInput.UpdateStates -> MyDirectInput.GetMouseState: DirectInput
    // relative counts since the previous read). The render thread now also
    // reads the device every draw, so that mouse motion is visible between
    // steps. Every count still reaches the game exactly once: whatever the
    // render thread read since the game's last read is added to what the game
    // reads itself. Both reads are serialized by one lock.
    //
    // Totals are cumulative counts read from the device by anyone; the game's
    // read delivers everything read so far.
    internal static unsafe class MouseInput
    {
        private static readonly object s_lock = new object();
        private static delegate*<object, ref MyMouseState, void> s_getMouseState;
        private static FieldInfo s_mouseField;
        private static Mouse s_mouse;
        private static MouseState s_poll = new MouseState();
        // After a failed read (the device is not acquired while the window is
        // in the background, or it is being replaced), the render thread waits
        // for the game's next read, which acquires it again, and a moment more.
        private static bool s_pollBlocked;
        private static long s_retryTicks;

        private static long s_readX;
        private static long s_readY;
        private static int s_undeliveredX;
        private static int s_undeliveredY;
        private static int s_undeliveredZ;
        private static long s_deliveredX;
        private static long s_deliveredY;

        // Diagnostics.
        public static int Polls;
        public static int PollFailures;
        public static int GameReads;

        public static void Prepare(HookTransaction hooks, Assembly platformWindows)
        {
            Type directInput = GameReflection.Type(platformWindows, "VRage.Platform.Windows.Input.MyDirectInput");
            s_mouseField = GameReflection.Field(directInput, "m_mouse");
            MethodInfo getMouseState = GameReflection.Method(directInput, "GetMouseState", typeof(MyMouseState).MakeByRefType());
            s_getMouseState = (delegate*<object, ref MyMouseState, void>)hooks.Install(getMouseState,
                typeof(MouseInput).GetMethod(nameof(GetMouseStateDetour), BindingFlags.NonPublic | BindingFlags.Static));
        }

        // Render thread, once per draw. Returns the totals read so far.
        public static void Poll(out long readX, out long readY)
        {
            lock (s_lock)
            {
                if (s_mouse != null && (!s_pollBlocked || Stopwatch.GetTimestamp() >= s_retryTicks))
                {
                    try
                    {
                        s_mouse.GetCurrentState(ref s_poll);
                        s_readX += s_poll.X;
                        s_readY += s_poll.Y;
                        s_undeliveredX += s_poll.X;
                        s_undeliveredY += s_poll.Y;
                        s_undeliveredZ += s_poll.Z;
                        s_pollBlocked = false;
                        Polls++;
                    }
                    catch (Exception)
                    {
                        // SharpDXException (not acquired, input lost) or a
                        // device the game disposed.
                        s_pollBlocked = true;
                        s_retryTicks = long.MaxValue;
                        PollFailures++;
                    }
                }
                readX = s_readX;
                readY = s_readY;
            }
        }

        // Update thread: totals the game has received with its last read.
        public static void Delivered(out long deliveredX, out long deliveredY)
        {
            lock (s_lock)
            {
                deliveredX = s_deliveredX;
                deliveredY = s_deliveredY;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void GetMouseStateDetour(object self, ref MyMouseState state)
        {
            lock (s_lock)
            {
                s_getMouseState(self, ref state);
                // The game may replace its device (and disposes the old one).
                s_mouse = s_mouseField.GetValue(self) as Mouse;
                s_readX += state.X;
                s_readY += state.Y;
                state.X += s_undeliveredX;
                state.Y += s_undeliveredY;
                state.ScrollWheelValue += s_undeliveredZ;
                s_undeliveredX = s_undeliveredY = s_undeliveredZ = 0;
                s_deliveredX = s_readX;
                s_deliveredY = s_readY;
                if (s_pollBlocked && s_retryTicks == long.MaxValue)
                    s_retryTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                GameReads++;
            }
        }
    }
}
