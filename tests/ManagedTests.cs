using System;
using System.Collections.Generic;
using System.Diagnostics;
using SpaceEngineers.HighFPSFix.Interpolation;
using SpaceEngineers.HighFPSFix.MouseLook;
using VRageMath;

// Game-free checks of the interpolation math and the presentation clock.
// Built against the game's VRage.Math.dll; run by ctest.
internal static class ManagedTests
{
    private static int s_failures;

    private static int Main()
    {
        Run(nameof(DecomposeRoundTrip), DecomposeRoundTrip);
        Run(nameof(BlendEndpointsAndMidpoint), BlendEndpointsAndMidpoint);
        Run(nameof(BlendTakesShortestArc), BlendTakesShortestArc);
        Run(nameof(CutDetection), CutDetection);
        Run(nameof(SampleRingLookup), SampleRingLookup);
        Run(nameof(ClockIsSmoothAt360WithJitteryUpdates), ClockIsSmoothAt360WithJitteryUpdates);
        Run(nameof(ClockHoldsLateFramesAndRecovers), ClockHoldsLateFramesAndRecovers);
        Run(nameof(ClockIgnoresRenderStalls), ClockIgnoresRenderStalls);
        Run(nameof(LookMathSignConventions), LookMathSignConventions);
        Run(nameof(MouseLeadLearnsAndLeads), MouseLeadLearnsAndLeads);
        Run(nameof(MouseLeadDetachesAndAdopts), MouseLeadDetachesAndAdopts);
        Run(nameof(MouseLeadFadesOutToWhatTheGameApplied), MouseLeadFadesOutToWhatTheGameApplied);
        Run(nameof(MouseLeadIgnoresPitchLimit), MouseLeadIgnoresPitchLimit);
        Console.WriteLine(s_failures == 0 ? "all tests passed" : $"{s_failures} check(s) failed");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = s_failures;
        try
        {
            test();
        }
        catch (Exception exception)
        {
            s_failures++;
            Console.WriteLine($"  exception: {exception}");
        }
        Console.WriteLine($"{(s_failures == before ? "PASS" : "FAIL")} {name}");
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
            return;
        s_failures++;
        Console.WriteLine("  " + message);
    }

    private static bool Near(MatrixD a, MatrixD b, double epsilon)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (Math.Abs(a[row, column] - b[row, column]) > epsilon)
                    return false;
        return true;
    }

    private static MatrixD Transform(Vector3D axis, double angle, Vector3D translation, double scale = 1.0)
    {
        MatrixD m = MatrixD.CreateScale(scale) * MatrixD.CreateFromAxisAngle(Vector3D.Normalize(axis), angle);
        m.Translation = translation;
        return m;
    }

    private static void DecomposeRoundTrip()
    {
        var random = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            var axis = new Vector3D(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            if (axis.LengthSquared() < 1e-6)
                continue;
            MatrixD m = Transform(axis, (random.NextDouble() * 2 - 1) * Math.PI,
                new Vector3D(random.NextDouble() * 1e6, random.NextDouble() * 1e6, random.NextDouble() * 1e6),
                0.5 + random.NextDouble());
            Pose pose = Pose.From(ref m);
            Check(pose.Decomposed, "decomposition failed");
            Pose.Blend(ref pose, ref pose, 0.5, out MatrixD back);
            Check(Near(m, back, 1e-6), $"round trip mismatch at sample {i}");
        }
    }

    private static void BlendEndpointsAndMidpoint()
    {
        MatrixD a = Transform(Vector3D.Up, 0, new Vector3D(10, 0, 0));
        MatrixD b = Transform(Vector3D.Up, Math.PI / 2, new Vector3D(20, 4, -6));
        Pose pa = Pose.From(ref a);
        Pose pb = Pose.From(ref b);
        Pose.Blend(ref pa, ref pb, 0, out MatrixD at0);
        Pose.Blend(ref pa, ref pb, 1, out MatrixD at1);
        Pose.Blend(ref pa, ref pb, 0.5, out MatrixD mid);
        Check(Near(at0, a, 1e-9), "t=0 is not the first sample");
        Check(Near(at1, b, 1e-9), "t=1 is not the second sample");
        MatrixD expected = Transform(Vector3D.Up, Math.PI / 4, new Vector3D(15, 2, -3));
        Check(Near(mid, expected, 1e-9), "midpoint is not a 45 degree turn halfway along");
    }

    private static void BlendTakesShortestArc()
    {
        MatrixD a = Transform(Vector3D.Up, Math.PI * 0.95, Vector3D.Zero);
        MatrixD b = Transform(Vector3D.Up, -Math.PI * 0.95, Vector3D.Zero);
        Pose pa = Pose.From(ref a);
        Pose pb = Pose.From(ref b);
        Pose.Blend(ref pa, ref pb, 0.5, out MatrixD mid);
        MatrixD expected = Transform(Vector3D.Up, Math.PI, Vector3D.Zero);
        Check(Near(mid, expected, 1e-9), "blend went the long way round");
    }

    private static void CutDetection()
    {
        MatrixD a = Transform(Vector3D.Up, 0, Vector3D.Zero);
        MatrixD near = Transform(Vector3D.Up, 0.1, new Vector3D(5, 0, 0));
        MatrixD far = Transform(Vector3D.Up, 0.1, new Vector3D(5000, 0, 0));
        MatrixD turned = Transform(Vector3D.Up, Math.PI * 0.9, Vector3D.Zero);
        Pose pa = Pose.From(ref a), pNear = Pose.From(ref near), pFar = Pose.From(ref far), pTurned = Pose.From(ref turned);
        Check(Pose.Continuous(ref pa, ref pNear, 250), "small move flagged as a cut");
        Check(!Pose.Continuous(ref pa, ref pFar, 250), "teleport not flagged");
        Check(!Pose.Continuous(ref pa, ref pTurned, 250), "half turn in one frame not flagged");
    }

    private static void SampleRingLookup()
    {
        var ring = new SampleRing<int>();
        ring.Push(10, false) = 100;
        ring.Push(11, false) = 110;
        ring.Push(13, true) = 130;
        Check(ring.IndexAt(9) == -1, "value before the first sample");
        Check(ring.At(ring.IndexAt(10)) == 100, "value at 10");
        Check(ring.At(ring.IndexAt(12)) == 110, "value at 12 (gap)");
        Check(ring.At(ring.IndexAt(20)) == 130, "value after the last sample");
        Check(ring.CutBetween(ring.IndexAt(12), ring.IndexAt(13)), "cut flag lost");
        Check(!ring.CutBetween(ring.IndexAt(10), ring.IndexAt(11)), "spurious cut");
        ring.Push(13, false) = 131;
        Check(ring.At(ring.IndexAt(13)) == 131 && ring.Count == 3, "same-frame push should replace");
        for (int i = 14; i < 20; i++)
            ring.Push(i, false) = i;
        Check(ring.Count == SampleRing<int>.Capacity && ring.IndexAt(15) == -1, "old samples not dropped");
    }

    // Simulated 60 Hz ticks whose frames reach the render thread 2-9 ms after
    // they start, drawn at 360 fps: presented time must advance evenly.
    private static void ClockIsSmoothAt360WithJitteryUpdates()
    {
        long frequency = Stopwatch.Frequency;
        var clock = new PresentationClock();
        var random = new Random(7);
        double tick = 1.0 / 60.0, draw = 1.0 / 360.0;
        var frameTimes = new List<double>();
        var pending = new Queue<(double start, double arrival)>();
        double nextTick = 0;
        double previousPresented = double.NaN;
        double maxError = 0;
        int held = 0, evaluated = 0;
        for (double now = 0; now < 20.0; now += draw)
        {
            while (nextTick <= now + 0.05)
            {
                pending.Enqueue((nextTick, nextTick + 0.002 + random.NextDouble() * 0.007));
                nextTick += tick;
            }
            while (pending.Count > 0 && pending.Peek().arrival <= now)
            {
                var frame = pending.Dequeue();
                clock.OnFrame((long)(frame.start * frequency), (long)(now * frequency));
                frameTimes.Add(frame.start);
            }
            if (!clock.Evaluate((long)(now * frequency), out long seqA, out long seqB, out double alpha))
                continue;
            evaluated++;
            if (seqA == seqB)
            {
                held++;
                continue;
            }
            double presented = frameTimes[(int)seqA - 1] + alpha * (frameTimes[(int)seqB - 1] - frameTimes[(int)seqA - 1]);
            if (now > 2.0 && !double.IsNaN(previousPresented))
                maxError = Math.Max(maxError, Math.Abs((presented - previousPresented) - draw));
            previousPresented = presented;
        }
        Check(evaluated > 6000, "clock never produced pairs");
        Check(held < evaluated / 100, $"too many held draws: {held} of {evaluated}");
        Check(maxError < draw * 0.1, $"presented time steps vary by {maxError * 1000:F3} ms");
    }

    // The render thread stalls for 400 ms (shader compilation after a load):
    // the frames that piled up meanwhile are seen late, but must not raise
    // the presentation delay or freeze the picture afterwards.
    private static void ClockIgnoresRenderStalls()
    {
        long frequency = Stopwatch.Frequency;
        var clock = new PresentationClock();
        double tick = 1.0 / 60.0, draw = 1.0 / 360.0;
        var frameTimes = new List<double>();
        var pending = new Queue<(double start, double arrival)>();
        double nextTick = 0, previous = double.NaN, lastDraw = 0, maxDelayAfter = 0;
        int heldAfter = 0;
        bool backwards = false;
        for (double now = 0; now < 6.0; now += draw)
        {
            if (now > 3.0 && now < 3.4)
                continue;  // no draws during the stall
            while (nextTick <= now + 0.05)
            {
                pending.Enqueue((nextTick, nextTick + 0.004));
                nextTick += tick;
            }
            bool afterStall = now - lastDraw > 0.04;
            while (pending.Count > 0 && pending.Peek().arrival <= now)
            {
                var frame = pending.Dequeue();
                bool newest = pending.Count == 0 || pending.Peek().arrival > now;
                clock.OnFrame((long)(frame.start * frequency), (long)(now * frequency), newest && !afterStall);
                frameTimes.Add(frame.start);
            }
            lastDraw = now;
            if (!clock.Evaluate((long)(now * frequency), out long seqA, out long seqB, out double alpha))
                continue;
            if (now > 3.9)
            {
                maxDelayAfter = Math.Max(maxDelayAfter, clock.LastDelay);
                if (seqA == seqB)
                    heldAfter++;
            }
            double presented = frameTimes[(int)seqA - 1] + alpha * (frameTimes[(int)seqB - 1] - frameTimes[(int)seqA - 1]);
            if (!double.IsNaN(previous) && presented < previous - 1e-9)
                backwards = true;
            previous = presented;
        }
        Check(!backwards, "presented time ran backwards");
        Check(maxDelayAfter < 0.03, $"delay after the stall reached {maxDelayAfter * 1000:F1} ms");
        Check(heldAfter < 10, $"{heldAfter} held draws after the stall");
    }

    // One frame arriving 40 ms late: the clock holds, then catches up without
    // stepping backwards.
    private static void ClockHoldsLateFramesAndRecovers()
    {
        long frequency = Stopwatch.Frequency;
        var clock = new PresentationClock();
        double tick = 1.0 / 60.0, draw = 1.0 / 240.0;
        var frameTimes = new List<double>();
        var pending = new Queue<(double start, double arrival)>();
        double nextTick = 0, previous = double.NaN;
        bool backwards = false;
        for (double now = 0; now < 5.0; now += draw)
        {
            while (nextTick <= now + 0.1)
            {
                double latency = Math.Abs(nextTick - 2.5) < tick / 2 ? 0.045 : 0.004;
                pending.Enqueue((nextTick, nextTick + latency));
                nextTick += tick;
            }
            while (pending.Count > 0 && pending.Peek().arrival <= now)
            {
                var frame = pending.Dequeue();
                clock.OnFrame((long)(frame.start * frequency), (long)(now * frequency));
                frameTimes.Add(frame.start);
            }
            if (!clock.Evaluate((long)(now * frequency), out long seqA, out long seqB, out double alpha))
                continue;
            double presented = frameTimes[(int)seqA - 1] + alpha * (frameTimes[(int)seqB - 1] - frameTimes[(int)seqA - 1]);
            if (!double.IsNaN(previous) && presented < previous - 1e-9)
                backwards = true;
            previous = presented;
        }
        Check(!backwards, "presented time ran backwards");
        Check(previous > 4.9 - 0.1, $"clock did not recover (ended at {previous:F3} s)");
    }

    // ---- mouse look --------------------------------------------------------

    private static readonly Vector3D s_up = Vector3D.Normalize(new Vector3D(0.1, 1.0, 0.2));

    // A first-person camera as the game builds it: the body turned about its
    // up axis (MatrixD.CreateRotationY(angle) * world), the head pitched about
    // its right axis (CreateFromAxisAngle(Right, headAngle) * body).
    private static MatrixD GameCamera(double yaw, double pitch)
    {
        MatrixD body = MatrixD.CreateWorld(new Vector3D(10, 20, 30), Vector3D.CalculatePerpendicularVector(s_up), s_up);
        body = MatrixD.CreateRotationY(yaw) * body;
        return MatrixD.CreateFromAxisAngle(Vector3D.Right, pitch) * body;
    }

    private static void LookMathSignConventions()
    {
        MatrixD from = GameCamera(0.3, 0.2);
        MatrixD to = GameCamera(0.3 + 0.01, 0.2 - 0.004);
        LookMath.MeasureTurn(from.Right, to.Right, from.Forward, to.Forward, s_up, out double yaw, out double pitch);
        Check(Math.Abs(yaw - 0.01) < 1e-9, $"game yaw +0.01 measured as {yaw}");
        Check(Math.Abs(pitch + 0.004) < 1e-9, $"game pitch -0.004 measured as {pitch}");
        Check(Math.Abs(LookMath.Elevation(GameCamera(1.0, 0.7).Forward, s_up) - 0.7) < 1e-9, "elevation is not the head angle");

        // The lead turns a camera exactly as the game would.
        MatrixD led = from;
        MatrixD rotation = LookMath.LeadRotation(ref led, s_up, 0.01, -0.004, LookMath.PitchMin, LookMath.PitchMax, out double used);
        led = LookMath.RotateAbout(ref led, ref rotation, from.Translation);
        Check(Math.Abs(used + 0.004) < 1e-12, "pitch clamped away from the limits");
        Check(Near(led, to, 1e-9), "lead rotation differs from the game's turn");

        // Pitch stops at the limit, and never pulls back from beyond it.
        MatrixD high = GameCamera(0, LookMath.PitchMax - 0.01);
        LookMath.LeadRotation(ref high, s_up, 0, 0.05, LookMath.PitchMin, LookMath.PitchMax, out used);
        Check(Math.Abs(used - 0.01) < 1e-9, $"pitch past the upper limit: {used}");
        MatrixD beyond = GameCamera(0, LookMath.PitchMax + 0.01);
        LookMath.LeadRotation(ref beyond, s_up, 0, 0.05, LookMath.PitchMin, LookMath.PitchMax, out used);
        Check(used == 0, $"pitch pulled a camera beyond the limit: {used}");
        LookMath.LeadRotation(ref beyond, s_up, 0, -0.05, LookMath.PitchMin, LookMath.PitchMax, out used);
        Check(Math.Abs(used + 0.05) < 1e-12, "pitch away from the limit was clamped");

        // Owners turn about the pivot.
        MatrixD owner = MatrixD.CreateTranslation(from.Translation + from.Forward * 2);
        MatrixD turned = LookMath.RotateAbout(ref owner, ref rotation, from.Translation);
        Check(Vector3D.Distance(turned.Translation, from.Translation + led.Forward * 2) < 1e-9, "owner not carried around the camera");
    }

    // A simulated game: mouse counts arrive at 1 kHz, the game consumes them
    // once per 60 Hz step and turns by scale * counts in that step.
    private sealed class MouseSim
    {
        public double YawScale = 2.6e-4;
        public double PitchScale = -1.7e-4;
        public long Read;
        public long ReadY;
        public long Delivered;
        public long DeliveredY;
        public double Yaw;
        public double Pitch;
        public long Seq;
        public bool FirstPerson = true;
        public bool Turns = true;
        // Counts arrive by themselves (RateX/RateY).
        public bool Auto = true;
        private double m_time;
        private double m_carryX;
        private double m_carryY;

        // Counts per second at time t: varying speed, both directions.
        public static double RateX(double t) => 2500 * Math.Sin(t * 2.3) + 900 * Math.Sin(t * 7.1);
        public static double RateY(double t) => 700 * Math.Sin(t * 1.7);

        public void Advance(double seconds)
        {
            for (int i = 0; Auto && i < (int)Math.Round(seconds * 1000); i++)
            {
                m_time += 0.001;
                m_carryX += RateX(m_time) * 0.001;
                m_carryY += RateY(m_time) * 0.001;
                long dx = (long)Math.Truncate(m_carryX);
                long dy = (long)Math.Truncate(m_carryY);
                m_carryX -= dx;
                m_carryY -= dy;
                Read += dx;
                ReadY += dy;
            }
        }

        public void Step()
        {
            Advance(1.0 / 60.0);
            long dx = Read - Delivered;
            long dy = ReadY - DeliveredY;
            Delivered = Read;
            DeliveredY = ReadY;
            if (Turns)
            {
                Yaw += YawScale * dx;
                Pitch = LookMath.Clamp(Pitch + PitchScale * dy, LookMath.PitchMin, LookMath.PitchMax);
            }
            Seq++;
            MatrixD camera = GameCamera(Yaw, Pitch);
            var tick = new TickInfo { Timestamp = Seq, CountsX = Delivered, CountsY = DeliveredY, FirstPerson = FirstPerson, Up = s_up };
            MouseLead.OnFrame(Seq, true, in tick, true, false, camera.Right, camera.Forward);
        }
    }

    private static MouseSim NewMouseSim()
    {
        MouseLead.Reset();
        MouseLead.Yaw.Forget();
        MouseLead.Pitch.Forget();
        return new MouseSim();
    }

    private static void MouseLeadLearnsAndLeads()
    {
        MouseSim sim = NewMouseSim();
        for (int i = 0; i < 120; i++)
            sim.Step();
        Check(MouseLead.Yaw.Attached && Math.Abs(MouseLead.Yaw.Scale / sim.YawScale - 1) < 1e-6,
            $"yaw not learned: {MouseLead.Yaw.Attached} {MouseLead.Yaw.Scale}");
        Check(MouseLead.Pitch.Attached && Math.Abs(MouseLead.Pitch.Scale / sim.PitchScale - 1) < 1e-6,
            $"pitch not learned: {MouseLead.Pitch.Attached} {MouseLead.Pitch.Scale}");
        Check(MouseLead.Yaw.Misses == 0 && MouseLead.Yaw.Detaches == 0 && MouseLead.Yaw.Late == 0, "yaw learning saw misses");

        // Draws between the last two frames: interpolated yaw plus lead is the
        // turn of every count read so far, at every point of the step.
        long seqB = sim.Seq, seqA = seqB - 1;
        MouseLead.Lead(seqA, seqB, 0, sim.Read, sim.ReadY, 1.0, out _, out _, out _, out _);  // weight up
        long deliveredA = sim.Delivered;
        double yawB = sim.Yaw;
        sim.Step();   // frame seqB + 1 arrives; the display time is still between A and B
        MouseLead.Lead(seqA, seqB, 0, sim.Read, sim.ReadY, 1.0, out _, out _, out _, out _);
        double maxError = 0;
        for (int draw = 0; draw <= 10; draw++)
        {
            double t = draw / 10.0;
            sim.Advance(0.0015);
            Check(MouseLead.Lead(seqA + 1, seqB + 1, t, sim.Read, sim.ReadY, 0.0015, out double yaw, out double pitch, out Vector3D up, out _),
                "no lead in first person");
            // Yaw the camera shows: blend of frames seqA+1 and seqB+1, plus lead.
            double yawA1 = yawB;
            double yawB1 = sim.Yaw;
            double shown = yawA1 + t * (yawB1 - yawA1) + yaw;
            maxError = Math.Max(maxError, Math.Abs(shown - sim.YawScale * sim.Read));
            Check(up == s_up, "lead up axis");
        }
        Check(maxError < 1e-9, $"led yaw is off the counts read by {maxError}");
        Check(deliveredA != sim.Delivered, "simulation did not advance");
    }

    private static void MouseLeadDetachesAndAdopts()
    {
        MouseSim sim = NewMouseSim();
        for (int i = 0; i < 60; i++)
            sim.Step();
        Check(MouseLead.Yaw.Attached, "not attached before the test");

        // Counts that turn nothing (an interaction): detach, and fade out.
        sim.Turns = false;
        for (int i = 0; i < 6; i++)
            sim.Step();
        Check(!MouseLead.Yaw.Attached && MouseLead.Yaw.Detaches == 1,
            $"yaw still attached after counts turned nothing ({MouseLead.Yaw.Attached}, {MouseLead.Yaw.Detaches} detaches, " +
            $"{MouseLead.Yaw.Steps} steps, {MouseLead.Yaw.Exact} exact, {MouseLead.Yaw.Misses} misses)");
        MouseLead.Lead(sim.Seq - 1, sim.Seq, 0.5, sim.Read, sim.ReadY, 0.2, out _, out _, out _, out _);
        Check(MouseLead.Yaw.Weight == 0, "lead did not fade out");

        // Turning again at a different sensitivity: adopted within a few steps.
        sim.Turns = true;
        sim.YawScale *= 0.6;
        sim.PitchScale *= 0.6;
        for (int i = 0; i < 6; i++)
            sim.Step();
        Check(MouseLead.Yaw.Attached && Math.Abs(MouseLead.Yaw.Scale / sim.YawScale - 1) < 1e-6,
            $"new yaw scale not adopted: {MouseLead.Yaw.Attached} {MouseLead.Yaw.Scale} vs {sim.YawScale}");

        // Leaving first person: no lead once the shown frames are not first person.
        sim.FirstPerson = false;
        sim.Step();
        sim.Step();
        Check(!MouseLead.Lead(sim.Seq - 1, sim.Seq, 0.5, sim.Read, sim.ReadY, 0.001, out _, out _, out _, out _),
            "lead outside first person");
        Check(MouseLead.Yaw.Weight == 0 && !MouseLead.WantsInput, "lead weight or input left on outside first person");
    }

    private static void MouseLeadFadesOutToWhatTheGameApplied()
    {
        MouseSim sim = NewMouseSim();
        for (int i = 0; i < 59; i++)
            sim.Step();
        long countsBefore = sim.Delivered;
        sim.Step();
        long countsLast = sim.Delivered;
        long last = sim.Seq;
        MouseLead.Lead(last - 1, last, 0.5, sim.Read, sim.ReadY, 1.0, out _, out _, out _, out _);
        // A menu opens: the newest frame is not first person, the shown ones
        // still are, and the counts read from now on turn nothing.
        sim.FirstPerson = false;
        sim.Turns = false;
        sim.Step();
        sim.Advance(0.01);
        bool led = MouseLead.Lead(last - 1, last, 0.5, sim.Read, sim.ReadY, 0.001, out double yaw, out _, out _, out _);
        Check(led, "lead stopped at once instead of fading");
        double weight = MouseLead.Yaw.Weight;
        Check(weight < 1 && weight > 0.95, $"fade weight {weight}");
        // Only the counts the game applied in the last first-person frame and
        // that the half-blended camera does not show yet.
        double expected = MouseLead.Yaw.Scale * (countsLast - (countsBefore + 0.5 * (countsLast - countsBefore))) * weight;
        Check(Math.Abs(yaw - expected) < 1e-15, $"fading lead {yaw} instead of {expected}");
    }

    private static void MouseLeadIgnoresPitchLimit()
    {
        MouseSim sim = NewMouseSim();
        for (int i = 0; i < 60; i++)
            sim.Step();
        Check(MouseLead.Pitch.Attached, "pitch not attached before the test");
        // Look straight up and keep pushing: no turn, but no detach either.
        sim.Pitch = LookMath.PitchMax;
        sim.Auto = false;
        sim.Step();
        int detaches = MouseLead.Pitch.Detaches;
        for (int i = 0; i < 30; i++)
        {
            // Upwards: the pitch scale is negative.
            sim.ReadY -= 40;
            sim.Step();
        }
        Check(MouseLead.Pitch.Detaches == detaches, "pushing against the pitch limit detached the axis");
        Check(MouseLead.Pitch.Limited > 0, "limit pushes not recognised");
    }
}
