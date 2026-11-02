using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using VRageRender;
using VRageRender.ExternalApp;

namespace SpaceEngineers.HighFPSFix
{
    // Hooks on the render thread (VRage.Render11.dll).
    //
    // Per draw the renderer processes the update frames the simulation queued
    // (MyRender11.ProcessMessageQueue: ProcessPreFrame for frames it skips,
    // then ProcessRenderFrame for the newest one, which it keeps redrawing
    // until a newer one arrives), applies the object transforms those frames
    // carried (MyPostponedUpdateManager.Apply, the last step of message
    // processing), then updates and draws the scene. New frames are observed
    // in the processing hooks; interpolated state is applied right after Apply.
    //
    // The game's assemblies are NGen-compiled, so small methods may be inlined
    // into their callers inside the native image. Every hooked method has an
    // exception handler, which keeps it out of line.
    internal static unsafe class RenderHooks
    {
        private static delegate*<MyUpdateFrame, bool, void> s_processRenderFrame;
        private static delegate*<MyUpdateFrame, bool, void> s_processPreFrame;
        private static delegate*<object, void> s_applyPostponed;
        private static FieldInfo s_sharedDataField;
        private static MySharedData s_sharedData;

        public static bool Installed { get; private set; }

        public static void Install(Assembly render11)
        {
            Type render = GameReflection.Type(render11, "VRageRender.MyRender11");
            Type postponed = GameReflection.Type(render11, "VRage.Render11.Render.MyPostponedUpdateManager");
            s_sharedDataField = GameReflection.Field(render, "SharedData");

            MethodInfo processRenderFrame = GameReflection.Method(render, "ProcessRenderFrame", typeof(MyUpdateFrame), typeof(bool));
            MethodInfo processPreFrame = GameReflection.Method(render, "ProcessPreFrame", typeof(MyUpdateFrame), typeof(bool));
            MethodInfo apply = GameReflection.Method(postponed, "Apply");

            Interpolation.RenderAccess.Bind(render11);
            Interpolation.SceneInterpolator.Configure(Plugin.Settings);

            // Patched from the render thread itself, between two draws: the
            // only thread that runs these methods is then guaranteed not to be
            // inside one of them. (Patching them from another thread while the
            // renderer was drawing crashed the runtime.)
            RunOnRenderThread(() =>
            {
                using (var hooks = new HookTransaction())
                {
                    s_processRenderFrame = (delegate*<MyUpdateFrame, bool, void>)hooks.Install(processRenderFrame, Own(nameof(ProcessRenderFrameDetour)));
                    s_processPreFrame = (delegate*<MyUpdateFrame, bool, void>)hooks.Install(processPreFrame, Own(nameof(ProcessPreFrameDetour)));
                    s_applyPostponed = (delegate*<object, void>)hooks.Install(apply, Own(nameof(ApplyPostponedDetour)));
                    hooks.Commit();
                }
            });
            Installed = true;
            Log.Info("Render hooks installed (ProcessRenderFrame, ProcessPreFrame, MyPostponedUpdateManager.Apply).");
        }

        // Runs the action once on the render thread, from its BeforeDraw event
        // (raised in MyRenderThread.RenderFrame before message processing and
        // drawing), and waits for it.
        private static void RunOnRenderThread(Action action)
        {
            MyRenderThread thread = RenderRate.WaitForRenderThread(TimeSpan.FromMinutes(5))
                ?? throw new DetourException("the render thread never started");
            Exception failure = null;
            int ran = 0;
            using (var done = new System.Threading.ManualResetEventSlim(false))
            {
                Action handler = null;
                handler = () =>
                {
                    if (System.Threading.Interlocked.Exchange(ref ran, 1) != 0)
                        return;
                    thread.BeforeDraw -= handler;
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        done.Set();
                    }
                };
                thread.BeforeDraw += handler;
                if (!done.Wait(TimeSpan.FromMinutes(5)))
                {
                    thread.BeforeDraw -= handler;
                    if (System.Threading.Interlocked.Exchange(ref ran, 1) == 0)
                        throw new DetourException("the render thread did not draw within 5 minutes");
                    done.Wait();
                }
            }
            if (failure != null)
                throw failure;
        }

        private static MethodInfo Own(string name) =>
            typeof(RenderHooks).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

        // The renderer's own frame for messages posted from the render thread;
        // it carries no simulation step.
        private static bool IsRenderThreadFrame(MyUpdateFrame frame)
        {
            if (s_sharedData == null)
                s_sharedData = s_sharedDataField.GetValue(null) as MySharedData;
            return s_sharedData != null && ReferenceEquals(frame, s_sharedData.MessagesForNextFrame);
        }

        // An update frame is new the first time either processing path sees it:
        // as the newest frame (drawn), or as an older one processed without
        // being drawn because the renderer fell behind.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProcessRenderFrameDetour(MyUpdateFrame frame, bool draw)
        {
            if (frame != null && !frame.Processed && !IsRenderThreadFrame(frame))
            {
                FrameStats.OnUpdateFrame(frame, skipped: false);
                Interpolation.SceneInterpolator.OnUpdateFrame(frame, skipped: false);
            }
            s_processRenderFrame(frame, draw);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProcessPreFrameDetour(MyUpdateFrame frame, bool returnShared)
        {
            if (frame != null && !frame.Processed && returnShared && !IsRenderThreadFrame(frame))
            {
                FrameStats.OnUpdateFrame(frame, skipped: true);
                Interpolation.SceneInterpolator.OnUpdateFrame(frame, skipped: true);
            }
            s_processPreFrame(frame, returnShared);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPostponedDetour(object self)
        {
            s_applyPostponed(self);
            FrameStats.OnSceneFrame();
            Interpolation.SceneInterpolator.Apply();
        }
    }
}
