using System.Diagnostics;
using System.Globalization;
using VRageRender;

namespace SpaceEngineers.HighFPSFix
{
    // Render-thread counters, reported every five seconds with Diagnostics on.
    internal static class FrameStats
    {
        private const double ReportSeconds = 5.0;

        private static long s_windowStart;
        private static long s_lastScene;
        private static int s_sceneFrames;
        private static int s_updateFrames;
        private static int s_skippedFrames;
        private static double s_maxSceneGap;
        private static long s_lastUpdateTimestamp;
        private static double s_maxUpdateGap;

        public static void OnUpdateFrame(MyUpdateFrame frame, bool skipped)
        {
            if (skipped)
                s_skippedFrames++;
            else
                s_updateFrames++;
            long timestamp = frame.UpdateTimestamp.Ticks;
            if (s_lastUpdateTimestamp != 0)
            {
                double gap = (timestamp - s_lastUpdateTimestamp) / (double)Stopwatch.Frequency;
                if (gap > s_maxUpdateGap)
                    s_maxUpdateGap = gap;
            }
            s_lastUpdateTimestamp = timestamp;
        }

        public static void OnSceneFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (s_windowStart == 0)
            {
                s_windowStart = now;
                s_lastScene = now;
                return;
            }
            double gap = (now - s_lastScene) / (double)Stopwatch.Frequency;
            if (gap > s_maxSceneGap)
                s_maxSceneGap = gap;
            s_lastScene = now;
            s_sceneFrames++;

            double elapsed = (now - s_windowStart) / (double)Stopwatch.Frequency;
            if (elapsed < ReportSeconds)
                return;
            if (Log.Diagnostics)
            {
                Log.Debug(string.Format(CultureInfo.InvariantCulture,
                    "render {0:F1} fps (max gap {1:F2} ms), update frames {2:F1}/s (max gap {3:F2} ms, skipped {4})",
                    s_sceneFrames / elapsed, s_maxSceneGap * 1000.0, s_updateFrames / elapsed,
                    s_maxUpdateGap * 1000.0, s_skippedFrames));
            }
            s_windowStart = now;
            s_sceneFrames = 0;
            s_updateFrames = 0;
            s_skippedFrames = 0;
            s_maxSceneGap = 0;
            s_maxUpdateGap = 0;
        }
    }
}
