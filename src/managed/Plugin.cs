using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace SpaceEngineers.HighFPSFix
{
    // Managed entry point, run by the native plugin in the game's default
    // AppDomain once Sandbox.Game.dll is loaded.
    public static class Plugin
    {
        public const string Name = "SpaceEngineers.HighFPSFix";

        internal static Settings Settings { get; private set; }
        internal static string Directory { get; private set; }

        private static int s_started;

        // ICLRRuntimeHost::ExecuteInDefaultAppDomain needs static int(string).
        public static int Start(string argument)
        {
            if (Interlocked.Exchange(ref s_started, 1) != 0)
                return 0;
            Dictionary<string, string> arguments = ParseArguments(argument);
            string bindError = NativeApi.Bind(arguments.TryGetValue("api", out string api)
                ? new IntPtr(long.Parse(api.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                : IntPtr.Zero);
            if (bindError != null)
                return 2;

            try
            {
                Directory = arguments.TryGetValue("dir", out string directory) ? directory : "";
                Settings = Settings.Load(arguments.TryGetValue("ini", out string ini) ? ini : "");
                Log.Diagnostics = Settings.Diagnostics || NativeApi.DiagnosticsEnabled;
                Log.Info($"Managed part running on .NET {Environment.Version}; {Settings}");

                string selfTest = Detour.SelfTest();
                if (selfTest != null)
                {
                    Log.Error("Hooking does not work in this process (" + selfTest + "); the game runs unmodified.");
                    return 3;
                }
                Log.Info("Detour self-test passed.");

                var startup = new Thread(StartupThread) { IsBackground = true, Name = Name + " startup" };
                startup.Start();
                return 0;
            }
            catch (Exception exception)
            {
                Log.Error("Managed start failed", exception);
                return 1;
            }
        }

        private static void StartupThread()
        {
            try
            {
                Assembly render11 = WaitForAssembly("VRage.Render11", TimeSpan.FromMinutes(5));
                if (render11 == null)
                {
                    Log.Error("VRage.Render11 was never loaded; the game runs unmodified.");
                    return;
                }
                if (Settings.RenderHooks)
                {
                    RenderHooks.Install(render11);
                    RenderRate.Apply(Settings);
                    if (Interpolation.SceneInterpolator.MouseLookWanted)
                    {
                        try
                        {
                            MouseLook.MouseLookSetup.Install();
                        }
                        catch (Exception exception)
                        {
                            Log.Error("Mouse look is off; the camera turns once per simulation step", exception);
                        }
                    }
                }
                else
                {
                    Log.Warning("[Debug] RenderHooks = false: no hooks, the game renders as usual.");
                }
                DebugAutomation.Start(Settings);
            }
            catch (Exception exception)
            {
                Log.Error("Startup failed; features that were not installed stay off", exception);
            }
        }

        private static Assembly WaitForAssembly(string name, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                Assembly found = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                if (found != null)
                    return found;
                Thread.Sleep(10);
            }
            return null;
        }

        private static Dictionary<string, string> ParseArguments(string argument)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in (argument ?? "").Split(';'))
            {
                int equals = part.IndexOf('=');
                if (equals > 0)
                    result[part.Substring(0, equals).Trim()] = part.Substring(equals + 1).Trim();
            }
            return result;
        }
    }
}
