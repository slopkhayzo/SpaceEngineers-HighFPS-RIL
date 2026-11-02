using System;
using System.Collections.Generic;
using System.Reflection;
using VRage.Library.Utils;
using VRage.Render.Scene;
using VRageMath;
using VRageRender;
using VRageRender.ExternalApp;
using VRageRender.Messages;

namespace SpaceEngineers.HighFPSFix.Interpolation
{
    // Internal renderer entry points the interpolation calls directly, as
    // function pointers (no access checks on calli, no delegate allocation).
    internal static unsafe class RenderAccess
    {
        public static delegate*<MyRenderMessageSetCameraViewMatrix, void> SetupCameraMatrices;
        public static delegate*<IMyActor, object> GetSkinning;
        public static delegate*<object, Matrix[], IReadOnlyList<MyBoneDecalUpdate>, void> SetAnimationBones;
        private static FieldInfo s_skeletonHierarchy;
        private static MyGameTimer s_timer;

        public static void Bind(Assembly render11)
        {
            Type render = GameReflection.Type(render11, "VRageRender.MyRender11");
            Type extensions = GameReflection.Type(render11, "VRage.Render11.Scene.ActorExtensions");
            Type skinning = GameReflection.Type(render11, "VRage.Render11.Scene.Components.MySkinningComponent");

            SetupCameraMatrices = (delegate*<MyRenderMessageSetCameraViewMatrix, void>)Pointer(
                GameReflection.Method(render, "SetupCameraMatrices", typeof(MyRenderMessageSetCameraViewMatrix)));
            GetSkinning = (delegate*<IMyActor, object>)Pointer(
                GameReflection.Method(extensions, "GetSkinning", typeof(IMyActor)));
            SetAnimationBones = (delegate*<object, Matrix[], IReadOnlyList<MyBoneDecalUpdate>, void>)Pointer(
                GameReflection.Method(skinning, "SetAnimationBones", typeof(Matrix[]), typeof(IReadOnlyList<MyBoneDecalUpdate>)));
            s_skeletonHierarchy = GameReflection.Field(skinning, "m_skeletonHierarchy");
        }

        // Bone count the skinning component expects (its skeleton), or -1.
        public static int SkeletonLength(object skinningComponent) =>
            (s_skeletonHierarchy.GetValue(skinningComponent) as Array)?.Length ?? -1;

        // The timer the game stamps update frames with (Game.m_gameTimer, also
        // the render thread's).
        public static long NowTicks()
        {
            if (s_timer == null)
            {
                MyRenderThread thread = MyRenderProxy.RenderThread;
                if (thread != null)
                    s_timer = GameReflection.Field(typeof(MyRenderThread), "m_timer").GetValue(thread) as MyGameTimer;
            }
            return s_timer != null ? s_timer.ElapsedTicks : 0;
        }

        private static IntPtr Pointer(MethodInfo method)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return method.MethodHandle.GetFunctionPointer();
        }
    }
}
