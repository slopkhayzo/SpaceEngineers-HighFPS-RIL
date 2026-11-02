using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Sandbox;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // Installs the mouse look hooks. Both hooked methods run on the update
    // thread (the mouse read, and the hand-over of each step's frame), so they
    // are patched from the update thread itself, between two of its calls
    // (MySandboxGame.Invoke runs queued actions inside a step).
    internal static class MouseLookSetup
    {
        public static void Install()
        {
            Assembly platform = null;
            if (!WaitUntil(() => (platform = FindAssembly("VRage.Platform.Windows")) != null && MySandboxGame.Static != null,
                    TimeSpan.FromMinutes(5)))
                throw new DetourException("the game never started its update loop");

            Exception failure = null;
            // Not disposed: after a timeout the action may still run.
            var done = new ManualResetEventSlim(false);
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    using (var hooks = new HookTransaction())
                    {
                        MouseInput.Prepare(hooks, platform);
                        TickCapture.Prepare(hooks);
                        hooks.Commit();
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    done.Set();
                }
            }, Plugin.Name);
            if (!done.Wait(TimeSpan.FromMinutes(5)))
                throw new DetourException("the update thread did not run the installation within 5 minutes");
            if (failure != null)
                throw failure;
            Interpolation.SceneInterpolator.EnableMouseLook();
            Log.Info("Mouse look hooks installed (MyDirectInput.GetMouseState, MySharedData.AfterUpdate).");
        }

        private static Assembly FindAssembly(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }
    }
}
