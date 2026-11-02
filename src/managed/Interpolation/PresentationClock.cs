using System;
using System.Diagnostics;

namespace SpaceEngineers.HighFPSFix.Interpolation
{
    // Maps render-thread time to a point between two simulation frames.
    //
    // Every update frame carries the time its tick started (UpdateTimestamp,
    // same timer as the render thread). The frame becomes visible to the render
    // thread some time later (its "latency": the tick's own duration plus the
    // wait for the next draw). Each draw shows the simulation as it was at the
    // display time
    //
    //     display = now - (update interval + latency + extra delay)
    //
    // interpolated between the two frames whose tick times bracket it. With
    // that delay the newer frame of the pair has (nearly) always arrived, and
    // since tick start times are evenly spaced, so is the motion. The display
    // time advances with real time and is slewed (never stepped) toward its
    // target when the delay changes; it only steps after a stall or a reset.
    internal sealed class PresentationClock
    {
        private const int FrameCapacity = 16;
        private const int LatencyWindow = 64;
        private const double LatencyPercentile = 0.9;
        // How much faster or slower than real time the display clock may run
        // while it converges on its target.
        private const double MaxSlew = 0.08;
        // Falling further behind than this (a stall, a load, a hitch) is not
        // slewed but jumped; being ahead is always slewed, since jumping back
        // would freeze the picture until real time caught up.
        private const double SnapSeconds = 0.1;
        // Latencies above this are hitches, not the normal update time.
        private const double MaxLatencySample = 0.05;

        private readonly long[] m_frameSeq = new long[FrameCapacity];
        private readonly long[] m_frameTime = new long[FrameCapacity];
        private int m_frameCount;
        private int m_frameHead = -1;

        private readonly double[] m_latency = new double[LatencyWindow];
        private readonly double[] m_latencySorted = new double[LatencyWindow];
        private int m_latencyCount;
        private int m_latencyHead;
        private double m_latencyEstimate;
        private double m_intervalEstimate = 1.0 / 60.0;

        private long m_lastNow;
        private double m_display;
        private bool m_displayValid;
        private bool m_snapping;

        public double ExtraDelaySeconds { get; set; } = 0.0005;

        public long LatestSeq { get; private set; }

        // Counters for the diagnostics report.
        public int HeldDraws;
        public int SnapCount;
        public double LastDelay;

        public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        public void Reset()
        {
            m_frameCount = 0;
            m_frameHead = -1;
            m_latencyCount = 0;
            m_latencyHead = 0;
            m_displayValid = false;
        }

        // A new simulation frame reached the render thread. Returns its sequence
        // number. measureLatency is false when the frame waited on the render
        // thread rather than on the simulation (it was skipped, or the
        // previous draw was long ago): such waits say nothing about how late
        // frames normally are, and would push the delay up for a whole window.
        public long OnFrame(long updateTicks, long nowTicks, bool measureLatency = true)
        {
            long seq = ++LatestSeq;
            if (m_frameHead >= 0)
            {
                double interval = Seconds(updateTicks - m_frameTime[m_frameHead]);
                if (interval <= 0 || interval > 0.5)
                {
                    // Timer discontinuity or a long pause (loading): start over.
                    m_frameCount = 0;
                    m_displayValid = false;
                }
                else
                {
                    m_intervalEstimate += (interval - m_intervalEstimate) * 0.05;
                }
            }
            m_frameHead = (m_frameHead + 1) % FrameCapacity;
            m_frameSeq[m_frameHead] = seq;
            m_frameTime[m_frameHead] = updateTicks;
            if (m_frameCount < FrameCapacity)
                m_frameCount++;

            double latency = Seconds(nowTicks - updateTicks);
            if (measureLatency && latency >= 0 && latency < MaxLatencySample)
            {
                m_latency[m_latencyHead] = latency;
                m_latencyHead = (m_latencyHead + 1) % LatencyWindow;
                if (m_latencyCount < LatencyWindow)
                    m_latencyCount++;
                Array.Copy(m_latency, m_latencySorted, m_latencyCount);
                Array.Sort(m_latencySorted, 0, m_latencyCount);
                m_latencyEstimate = m_latencySorted[(int)Math.Min(m_latencyCount - 1, Math.Floor(m_latencyCount * LatencyPercentile))];
            }
            return seq;
        }

        // The frame pair and blend factor for a draw at nowTicks. Returns false
        // while there are not yet two frames to interpolate between.
        public bool Evaluate(long nowTicks, out long seqA, out long seqB, out double alpha)
        {
            seqA = seqB = LatestSeq;
            alpha = 0;
            if (m_frameCount < 2)
                return false;

            double now = Seconds(nowTicks);
            double delay = m_intervalEstimate + m_latencyEstimate + ExtraDelaySeconds;
            LastDelay = delay;
            double target = now - delay;
            if (!m_displayValid)
            {
                m_display = target;
                m_displayValid = true;
            }
            else
            {
                double elapsed = Math.Max(0, now - Seconds(m_lastNow));
                double free = m_display + elapsed;
                double error = target - free;
                if (error > SnapSeconds)
                {
                    free = target;
                    // While the newest frame stays late, every draw snaps
                    // again; count the event once.
                    if (!m_snapping)
                        SnapCount++;
                    m_snapping = true;
                }
                else
                {
                    m_snapping = false;
                    double limit = elapsed * MaxSlew;
                    free += Math.Max(-limit, Math.Min(limit, error));
                }
                // Presented time never runs backwards.
                m_display = Math.Max(m_display, free);
            }
            m_lastNow = nowTicks;

            // Oldest to newest; find the last frame at or before the display time.
            int oldest = (m_frameHead - m_frameCount + 1 + FrameCapacity) % FrameCapacity;
            double oldestTime = Seconds(m_frameTime[oldest]);
            if (m_display <= oldestTime)
            {
                seqA = seqB = m_frameSeq[oldest];
                return true;
            }
            for (int i = m_frameCount - 1; i >= 1; i--)
            {
                int newer = (oldest + i) % FrameCapacity;
                int older = (oldest + i - 1) % FrameCapacity;
                double newerTime = Seconds(m_frameTime[newer]);
                double olderTime = Seconds(m_frameTime[older]);
                if (m_display >= olderTime)
                {
                    if (m_display >= newerTime)
                    {
                        // Past the newest frame: it is late. Hold it, and keep
                        // the display clock from running ahead of the data.
                        seqA = seqB = m_frameSeq[newer];
                        m_display = newerTime;
                        HeldDraws++;
                        return true;
                    }
                    seqA = m_frameSeq[older];
                    seqB = m_frameSeq[newer];
                    alpha = (m_display - olderTime) / (newerTime - olderTime);
                    return true;
                }
            }
            seqA = seqB = m_frameSeq[oldest];
            return true;
        }
    }
}
