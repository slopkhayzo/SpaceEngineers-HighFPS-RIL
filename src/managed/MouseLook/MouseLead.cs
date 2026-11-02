using System;
using System.Globalization;
using System.IO;
using VRageMath;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // Leads the interpolated first-person camera with the mouse: the shown
    // look direction is the interpolated one plus every mouse count read so
    // far that the interpolated state does not contain yet, turned into yaw
    // and pitch with the radians per count the game itself was seen to apply.
    //
    //   lead = scale * (counts read now - counts contained at the display time)
    //
    // The second term is the cumulative count of the two frames around the
    // display time, blended like everything else, so the lead is continuous:
    // a count moves from "read" to "contained" without the view moving.
    //
    // Nothing about the game's sensitivity is assumed. Per axis, the turn of
    // each step is compared with the counts it consumed (the game turns the
    // character in the same step that reads the mouse): a turn of exactly
    // scale * counts attaches the axis; counts that turned nothing detach it
    // (an interaction, a mode where the mouse does something else); two steps
    // agreeing on a new ratio adopt it as the scale (sensitivity, zoom,
    // jetpack); three strong misses in a row detach. Turns without counts
    // (the ground or a ship turning the player) are left alone, and the lead
    // stays right with them because it is added on top. The lead fades in and
    // out over a few draws instead of appearing or vanishing at once.
    internal static class MouseLead
    {
        private const int Capacity = 32;
        private const double RampSeconds = 0.05;

        private struct Frame
        {
            public long Seq;
            public bool HasTick;
            public TickInfo Tick;
            public bool HasCamera;
            public bool CameraCut;
            public Vector3D Right;
            public Vector3D Forward;
        }

        internal sealed class Axis
        {
            // Radians per count (signed); 0 = not learned yet.
            public double Scale;
            public bool Attached;
            public double Weight;
            private double m_candidate;
            private int m_strongMisses;

            // Diagnostics.
            public int Steps, Exact, Late, Adopts, Detaches, Limited, Misses;

            // One simulation step: counts it consumed, counts the step before
            // consumed, the turn it made, and whether the axis is pushing
            // against its limit.
            public void Observe(long counts, long previousCounts, double turn, bool atLimit)
            {
                if (counts == 0)
                    return;
                Steps++;
                double absScale = Math.Abs(Scale);
                double tolerance = Math.Max(0.3 * absScale, 1e-6);
                if (Scale != 0 && Math.Abs(turn - Scale * counts) <= tolerance)
                {
                    Attached = true;
                    m_strongMisses = 0;
                    Exact++;
                    return;
                }
                // A step that matches the previous step's counts instead: the
                // game would be turning a step late (not expected; reported).
                if (Scale != 0 && previousCounts != 0 && Math.Abs(turn - Scale * previousCounts) <= tolerance)
                    Late++;
                if (Math.Abs(turn) <= Math.Max(0.05 * absScale, 1e-7))
                {
                    // Counts that turned nothing. One step of them can be the
                    // edge of a mode change; two in a row are not.
                    if (Math.Abs(counts) >= 2 && previousCounts != 0)
                    {
                        if (atLimit)
                            Limited++;
                        else
                            Detach();
                    }
                    return;
                }
                if (Math.Abs(counts) >= 3)
                {
                    double ratio = turn / counts;
                    if (m_candidate != 0 && Math.Abs(ratio - m_candidate) <= 0.03 * Math.Abs(m_candidate))
                    {
                        Scale = 0.5 * (ratio + m_candidate);
                        m_candidate = 0;
                        m_strongMisses = 0;
                        Attached = true;
                        Adopts++;
                        return;
                    }
                    m_candidate = ratio;
                }
                if (Scale != 0 && Math.Abs(turn - Scale * counts) > 0.25 * Math.Abs(Scale * counts) + 2 * absScale)
                {
                    Misses++;
                    if (++m_strongMisses >= 3)
                        Detach();
                }
            }

            public void Detach()
            {
                if (Attached)
                    Detaches++;
                Attached = false;
                m_strongMisses = 0;
            }

            public void Ramp(bool active, double seconds)
            {
                double target = active && Attached && Scale != 0 ? 1.0 : 0.0;
                double step = Math.Max(0, seconds) / RampSeconds;
                Weight = target > Weight ? Math.Min(target, Weight + step) : Math.Max(target, Weight - step);
            }

            // The world was unloaded: start over attached-wise, keep the scale.
            public void Release()
            {
                Attached = false;
                Weight = 0;
                m_strongMisses = 0;
            }

            public void Forget()
            {
                Release();
                Scale = 0;
                m_candidate = 0;
                Steps = Exact = Late = Adopts = Detaches = Limited = Misses = 0;
            }
        }

        private static readonly Frame[] s_frames = new Frame[Capacity];
        private static long s_latestSeq = -1;
        private static long s_lastFirstPersonSeq = -1;

        public static readonly Axis Yaw = new Axis();
        public static readonly Axis Pitch = new Axis();

        // Learning trace, one line per frame, or null.
        public static TextWriter Trace { get; set; }

        // Diagnostics.
        public static int Frames;
        public static int UnmatchedFrames;
        public static int FirstPersonFrames;

        // The newest frame was recorded in first person: the mouse is worth
        // reading on the render thread.
        public static bool WantsInput { get; private set; }

        // The up axis of the newest frame recorded in first person.
        public static Vector3D LastUp { get; private set; }

        private static int Slot(long seq) => (int)(((seq % Capacity) + Capacity) % Capacity);

        public static void Reset()
        {
            Array.Clear(s_frames, 0, s_frames.Length);
            s_latestSeq = -1;
            s_lastFirstPersonSeq = -1;
            WantsInput = false;
            Yaw.Release();
            Pitch.Release();
        }

        // Render thread, once per new frame, after its messages were read.
        // right/forward: the frame's camera axes (hasCamera: it carried one).
        public static void OnFrame(long seq, bool hasTick, in TickInfo tick, bool hasCamera, bool cameraCut,
            Vector3D right, Vector3D forward)
        {
            ref Frame frame = ref s_frames[Slot(seq)];
            frame = default;
            frame.Seq = seq;
            frame.HasTick = hasTick;
            frame.Tick = tick;
            frame.HasCamera = hasCamera;
            frame.CameraCut = cameraCut;
            frame.Right = right;
            frame.Forward = forward;
            s_latestSeq = seq;
            Frames++;
            if (!hasTick)
                UnmatchedFrames++;
            WantsInput = hasTick && tick.FirstPerson;
            if (WantsInput)
            {
                FirstPersonFrames++;
                s_lastFirstPersonSeq = seq;
                LastUp = tick.Up;
            }
            Learn(seq);
        }

        private static void Learn(long seq)
        {
            ref Frame current = ref s_frames[Slot(seq)];
            ref Frame previous = ref s_frames[Slot(seq - 1)];
            if (!current.HasTick || !current.Tick.FirstPerson || !current.HasCamera || current.CameraCut ||
                previous.Seq != seq - 1 || !previous.HasTick || !previous.Tick.FirstPerson || !previous.HasCamera)
                return;
            ref Frame before = ref s_frames[Slot(seq - 2)];
            bool hasBefore = before.Seq == seq - 2 && before.HasTick;

            long countsX = current.Tick.CountsX - previous.Tick.CountsX;
            long countsY = current.Tick.CountsY - previous.Tick.CountsY;
            long previousX = hasBefore ? previous.Tick.CountsX - before.Tick.CountsX : 0;
            long previousY = hasBefore ? previous.Tick.CountsY - before.Tick.CountsY : 0;
            Vector3D up = current.Tick.Up;
            LookMath.MeasureTurn(previous.Right, current.Right, previous.Forward, current.Forward, up, out double yaw, out double pitch);
            double elevation = LookMath.Elevation(current.Forward, up);
            Yaw.Observe(countsX, previousX, yaw, false);
            Pitch.Observe(countsY, previousY, pitch, AtPitchLimit(elevation, countsY));

            Trace?.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1},{2},{3},{4},{5:E6},{6:E6},{7:F6},{8:E6},{9:E6},{10},{11}",
                seq, current.Tick.CountsX, current.Tick.CountsY, countsX, countsY, yaw, pitch, elevation,
                Yaw.Scale, Pitch.Scale, Yaw.Attached ? 1 : 0, Pitch.Attached ? 1 : 0));
        }

        public const string TraceHeader = "seq,counts_x,counts_y,dx,dy,yaw,pitch,elevation,yaw_scale,pitch_scale,yaw_attached,pitch_attached";

        // Pushing against the head's pitch limit turns nothing; that is not a
        // detach. Which limit is pushed is known once the scale is.
        private static bool AtPitchLimit(double elevation, long counts)
        {
            const double tolerance = 0.5 * Math.PI / 180.0;
            bool nearTop = elevation > LookMath.PitchMax - tolerance;
            bool nearBottom = elevation < LookMath.PitchMin + tolerance;
            if (Pitch.Scale == 0)
                return nearTop || nearBottom;
            double push = Pitch.Scale * counts;
            return (push > 0 && nearTop) || (push < 0 && nearBottom);
        }

        // Render thread, once per draw: the lead to add to a camera blended
        // between frames seqA and seqB with factor t, given the counts read so
        // far. seconds: time since the previous draw (fades).
        public static bool Lead(long seqA, long seqB, double t, long readX, long readY, double seconds,
            out double yaw, out double pitch, out Vector3D up, out uint[] owners)
        {
            yaw = pitch = 0;
            up = default;
            owners = null;
            if (s_latestSeq < 0)
                return false;
            ref Frame newest = ref s_frames[Slot(s_latestSeq)];
            ref Frame a = ref s_frames[Slot(seqA)];
            ref Frame b = ref s_frames[Slot(seqB)];
            // Only frames that were looked at through the mouse get a lead:
            // after a menu opened, the counts the game consumed meanwhile did
            // not turn anything.
            bool shownFirstPerson = a.Seq == seqA && a.HasTick && a.Tick.FirstPerson &&
                                    b.Seq == seqB && b.HasTick && b.Tick.FirstPerson;
            if (!shownFirstPerson)
            {
                Yaw.Weight = Pitch.Weight = 0;
                return false;
            }
            bool newestFirstPerson = newest.Seq == s_latestSeq && newest.HasTick && newest.Tick.FirstPerson;
            Yaw.Ramp(newestFirstPerson, seconds);
            Pitch.Ramp(newestFirstPerson, seconds);
            if (Yaw.Weight == 0 && Pitch.Weight == 0)
                return false;

            // While fading out (the newest frame is no longer in first person),
            // lead only up to what the game applied before it left.
            long leadX = readX, leadY = readY;
            if (!newestFirstPerson)
            {
                ref Frame last = ref s_frames[Slot(s_lastFirstPersonSeq)];
                if (last.Seq != s_lastFirstPersonSeq || !last.HasTick)
                {
                    Yaw.Weight = Pitch.Weight = 0;
                    return false;
                }
                leadX = last.Tick.CountsX;
                leadY = last.Tick.CountsY;
            }
            double shownX = a.Tick.CountsX + t * (b.Tick.CountsX - a.Tick.CountsX);
            double shownY = a.Tick.CountsY + t * (b.Tick.CountsY - a.Tick.CountsY);
            yaw = Yaw.Scale * (leadX - shownX) * Yaw.Weight;
            pitch = Pitch.Scale * (leadY - shownY) * Pitch.Weight;
            up = b.Tick.Up;
            owners = b.Tick.Owners;
            return true;
        }
    }
}
