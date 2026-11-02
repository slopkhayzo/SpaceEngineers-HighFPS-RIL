using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SpaceEngineers.HighFPSFix.MouseLook;
using VRage.Render.Scene;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineers.HighFPSFix.Interpolation
{
    // Render-thread side of the interpolation. Each new update frame is read
    // before the renderer processes it, and its transforms are stored per
    // object (actor world or relative matrices), for the camera, and per
    // character skeleton. After the renderer applied the newest frame, every
    // draw overrides those with the state at the presentation clock's display
    // time, blended between the two frames around it.
    //
    // Only render-side copies change: the renderer's actors, its camera
    // matrices and its skinning palettes. The simulation never reads them.
    //
    // With mouse look, the first-person camera is also turned by the mouse
    // counts read since the display time (MouseLead), and the character and
    // its held tool turn with it about the camera.
    internal static unsafe class SceneInterpolator
    {
        // A move longer than this between two frames is a teleport, not motion.
        private const double CutDistance = 250.0;
        private const double CameraCutDistance = 50.0;
        private const double BoneCutDistance = 5.0;
        private const double ReportSeconds = 5.0;
        private const double StallSeconds = 0.04;

        private static readonly PresentationClock s_clock = new PresentationClock();
        private static readonly Dictionary<uint, ObjectTrack> s_objects = new Dictionary<uint, ObjectTrack>();
        private static readonly List<ObjectTrack> s_activeObjects = new List<ObjectTrack>();
        private static readonly Dictionary<uint, CharacterTrack> s_characters = new Dictionary<uint, CharacterTrack>();
        private static readonly SampleRing<CameraSample> s_camera = new SampleRing<CameraSample>();
        private static readonly MyRenderMessageSetCameraViewMatrix s_cameraMessage = new MyRenderMessageSetCameraViewMatrix();
        private static readonly MyRenderObjectUpdateData s_updateData = new MyRenderObjectUpdateData();
        private static readonly List<MyBoneDecalUpdate> s_noDecals = new List<MyBoneDecalUpdate>();
        private static long s_lastCameraSeqShown = long.MinValue;
        private static long s_cameraCutSeq = long.MinValue;
        private static long s_lastDrawTicks;

        // Mouse look: wanted by the settings, and on once its hooks are in.
        private static bool s_mouseLookWanted;
        private static volatile bool s_mouseLookApply = true;
        private static volatile bool s_mouseLookOn;
        private static bool s_leadActive;
        private static MatrixD s_leadRotation;
        private static Vector3D s_leadPivot;
        private static uint[] s_leadOwners;
        private static double s_leadYaw;
        private static double s_leadPitch;
        private static long s_readX;
        private static long s_readY;
        private static readonly Dictionary<uint, OwnerLead> s_owners = new Dictionary<uint, OwnerLead>();
        private static readonly List<uint> s_ownerScratch = new List<uint>();
        private static int s_drawIndex;

        // Render object whose presented position goes into the trace.
        public static uint TraceObjectId;

        // Interpolate: override the renderer's state. Observe: read frames and
        // run the clock, also with interpolation off, for the trace.
        private static bool s_enabled;
        private static bool s_observe;
        private static bool s_camerasOn;
        private static bool s_objectsOn;
        private static bool s_charactersOn;
        private static int s_failures;

        // Diagnostics.
        private static long s_reportStart;
        private static int s_draws;
        private static int s_frames;
        private static long s_applyTicks;
        private static int s_objectApplies;
        private static int s_characterApplies;
        private static double s_alphaSum;
        private static int s_leadDraws;
        private static int s_ownerApplies;
        private static StreamWriter s_trace;
        private static StreamWriter s_mouseTrace;

        public static void Configure(Settings settings)
        {
            s_camerasOn = settings.InterpolateCamera;
            s_objectsOn = settings.InterpolateObjects;
            s_charactersOn = settings.InterpolateCharacters;
            s_clock.ExtraDelaySeconds = Math.Max(0, settings.ExtraDelayMs) / 1000.0;
            s_enabled = settings.Interpolation;
            s_mouseLookWanted = s_enabled && s_camerasOn && settings.MouseLook;
            s_mouseLookApply = settings.MouseLookApply;
            if (s_mouseLookWanted && !s_mouseLookApply)
                Log.Warning("[Debug] MouseLookApply = false: mouse look learns and traces, but does not turn the camera.");
            if (!string.IsNullOrEmpty(settings.TracePath))
            {
                Directory.CreateDirectory(settings.TracePath);
                s_trace = new StreamWriter(Path.Combine(settings.TracePath, "present.csv"), false, new UTF8Encoding(false), 1 << 16);
                s_trace.WriteLine("now,display_delay,seq_a,seq_b,alpha,cam_x,cam_y,cam_z,fwd_x,fwd_y,fwd_z,objects,apply_us,obj_x,obj_y,obj_z," +
                                  "read_x,read_y,lead_yaw,lead_pitch,up_x,up_y,up_z,lead_mode,own_fwd_x,own_fwd_y,own_fwd_z");
                if (s_mouseLookWanted)
                {
                    s_mouseTrace = new StreamWriter(Path.Combine(settings.TracePath, "mouse.csv"), false, new UTF8Encoding(false), 1 << 16);
                    s_mouseTrace.WriteLine(MouseLead.TraceHeader);
                    MouseLead.Trace = s_mouseTrace;
                }
            }
            s_observe = s_enabled || s_trace != null;
            Log.Info($"Interpolation {(s_enabled ? "on" : "off")}: camera {s_camerasOn}, objects {s_objectsOn}, characters {s_charactersOn}, " +
                     $"mouse look {(s_mouseLookWanted ? "wanted" : "off")}.");
        }

        public static bool MouseLookWanted => s_mouseLookWanted;

        // Whether the lead turns the camera (A/B runs switch it while running).
        public static bool MouseLookApply
        {
            get => s_mouseLookApply;
            set => s_mouseLookApply = value;
        }

        // Called once the mouse look hooks are installed.
        public static void EnableMouseLook()
        {
            if (s_mouseLookWanted && s_enabled)
                s_mouseLookOn = true;
        }

        public static void Shutdown()
        {
            s_enabled = false;
            s_observe = false;
            s_mouseLookOn = false;
            s_trace?.Flush();
            s_mouseTrace?.Flush();
        }

        // Called on the render thread for each update frame, the first time
        // the renderer sees it, before its messages are processed. skipped:
        // processed without being drawn, because the renderer fell behind.
        public static void OnUpdateFrame(MyUpdateFrame frame, bool skipped)
        {
            if (!s_observe)
                return;
            try
            {
                long now = RenderAccess.NowTicks();
                // A frame that waited on the renderer (skipped, or seen after a
                // render stall) does not tell how late frames normally are.
                bool measure = !skipped && PresentationClock.Seconds(now - s_lastDrawTicks) < StallSeconds;
                long seq = s_clock.OnFrame(frame.UpdateTimestamp.Ticks, now, measure);
                s_frames++;
                foreach (MyRenderMessageBase message in frame.RenderInput)
                {
                    switch (message)
                    {
                        case MyRenderMessageUpdateRenderObject update when s_objectsOn && s_enabled:
                            OnObjectUpdate(seq, update);
                            break;
                        case MyRenderMessageSetCameraViewMatrix camera:
                            OnCamera(seq, camera);
                            break;
                        case MyRenderMessageSetCharacterTransforms bones when s_charactersOn && s_enabled:
                            OnBones(seq, bones);
                            break;
                        case MyRenderMessageRemoveRenderObject remove:
                            s_objects.Remove(remove.ID);
                            s_characters.Remove(remove.ID);
                            break;
                        case MyRenderMessageUnloadData _:
                            ClearAll();
                            break;
                    }
                }
                if (s_mouseLookOn)
                    OnMouseFrame(seq, frame.UpdateTimestamp.Ticks);
            }
            catch (Exception exception)
            {
                Fail("reading an update frame", exception);
            }
        }

        private static void OnMouseFrame(long seq, long timestamp)
        {
            bool hasTick = TickCapture.Match(timestamp, out TickInfo tick);
            bool hasCamera = s_camera.Count > 0 && s_camera.LatestSeq == seq;
            Vector3D right = default, forward = default;
            if (hasCamera)
            {
                right = s_camera.Latest.World.Matrix.Right;
                forward = s_camera.Latest.World.Matrix.Forward;
            }
            MouseLead.OnFrame(seq, hasTick, in tick, hasCamera, hasCamera && s_cameraCutSeq == seq, right, forward);
            if (hasTick && tick.Owners != null)
                s_latestOwners = tick.Owners;
        }

        // Diagnostics, on the draw after the owners changed (the renderer has
        // processed the frame's messages by then, new actors included).
        private static void LogOwners()
        {
            if (s_latestOwners == null || ReferenceEquals(s_latestOwners, s_loggedOwners) || !Log.Diagnostics)
                return;
            s_loggedOwners = s_latestOwners;
            var text = new StringBuilder("Mouse look owner actors:");
            foreach (uint id in s_latestOwners)
            {
                MyActor actor = MyIDTracker<MyActor>.FindByID(id);
                text.Append(' ').Append(id).Append(actor == null ? " (no actor)" :
                    actor.Parent != null ? $" (child of {actor.Parent.ID})" : " (root)");
            }
            Log.Debug(text.ToString());
        }

        private static uint[] s_loggedOwners;
        private static uint[] s_latestOwners;

        // Called on the render thread once per draw, after the newest frame's
        // transforms were applied and before the scene is updated and drawn.
        public static void Apply()
        {
            if (!s_observe)
                return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                long now = RenderAccess.NowTicks();
                double sinceLastDraw = s_lastDrawTicks != 0 ? PresentationClock.Seconds(now - s_lastDrawTicks) : 0;
                s_lastDrawTicks = now;
                if (!s_clock.Evaluate(now, out long seqA, out long seqB, out double alpha))
                    return;
                // The freshest mouse counts, read right before the camera is set.
                if (s_mouseLookOn && MouseLead.WantsInput)
                    MouseInput.Poll(out s_readX, out s_readY);
                Vector3D cameraPosition = default;
                Vector3D cameraForward = default;
                if (s_enabled)
                {
                    if (s_objectsOn)
                        ApplyObjects(seqA, seqB, alpha);
                    if (s_charactersOn)
                        ApplyCharacters(seqA, seqB, alpha);
                    s_leadActive = false;
                    if (s_camerasOn)
                        ApplyCamera(seqA, seqB, alpha, sinceLastDraw, out cameraPosition, out cameraForward);
                    if (s_leadActive || s_owners.Count > 0)
                        ApplyOwners();
                    if (s_mouseLookOn)
                        LogOwners();
                }
                else if (s_camera.Count > 0)
                {
                    // What the stock renderer shows: the newest frame's camera.
                    cameraPosition = s_camera.Latest.Position;
                    cameraForward = s_camera.Latest.World.Matrix.Forward;
                }

                long elapsed = Stopwatch.GetTimestamp() - start;
                s_applyTicks += elapsed;
                s_draws++;
                s_alphaSum += alpha;
                if (s_trace != null)
                {
                    Vector3D objectPosition = default;
                    if (TraceObjectId != 0)
                    {
                        MyActor traced = MyIDTracker<MyActor>.FindByID(TraceObjectId);
                        if (traced != null)
                            objectPosition = traced.WorldMatrix.Translation;
                    }
                    Vector3D up = MouseLead.LastUp;
                    // The player's character as presented (its root actor).
                    Vector3D ownerForward = default;
                    MyActor ownerActor = s_latestOwners != null && s_latestOwners.Length > 0
                        ? MyIDTracker<MyActor>.FindByID(s_latestOwners[0]) : null;
                    for (int depth = 0; ownerActor != null && ownerActor.Parent != null && depth < 8; depth++)
                        ownerActor = ownerActor.Parent as MyActor;
                    if (ownerActor != null)
                        ownerForward = ownerActor.WorldMatrix.Forward;
                    s_trace.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F6},{1:F5},{2},{3},{4:F4},{5:F4},{6:F4},{7:F4},{8:F7},{9:F7},{10:F7},{11},{12:F1},{13:F4},{14:F4},{15:F4}," +
                        "{16},{17},{18:E5},{19:E5},{20:F6},{21:F6},{22:F6},{23},{24:F7},{25:F7},{26:F7}",
                        PresentationClock.Seconds(now), s_clock.LastDelay, seqA, seqB, alpha,
                        cameraPosition.X, cameraPosition.Y, cameraPosition.Z,
                        cameraForward.X, cameraForward.Y, cameraForward.Z,
                        s_activeObjects.Count, elapsed * 1e6 / Stopwatch.Frequency,
                        objectPosition.X, objectPosition.Y, objectPosition.Z,
                        s_readX, s_readY, s_leadActive ? s_leadYaw : 0.0, s_leadActive ? s_leadPitch : 0.0,
                        up.X, up.Y, up.Z, s_mouseLookOn && s_mouseLookApply ? 1 : 0,
                        ownerForward.X, ownerForward.Y, ownerForward.Z));
                }
                Report(start);
            }
            catch (Exception exception)
            {
                Fail("applying interpolated state", exception);
            }
        }

        private static void Fail(string what, Exception exception)
        {
            s_failures++;
            Log.Error($"Interpolation failed while {what}", exception);
            if (s_failures >= 3)
            {
                s_enabled = false;
                s_observe = false;
                ClearAll();
                Log.Error("Interpolation switched off after repeated failures; the game draws the newest frame as usual.");
            }
        }

        private static void ClearAll()
        {
            RestoreOwners();
            s_objects.Clear();
            s_activeObjects.Clear();
            s_characters.Clear();
            s_camera.Clear();
            s_clock.Reset();
            MouseLead.Reset();
            s_latestOwners = s_loggedOwners = null;
        }

        // ---- objects -------------------------------------------------------

        private sealed class ObjectTrack
        {
            public uint Id;
            public bool Local;
            public bool Active;
            public MyActor Actor;
            public readonly SampleRing<Pose> Samples = new SampleRing<Pose>();
        }

        private static void OnObjectUpdate(long seq, MyRenderMessageUpdateRenderObject update)
        {
            MyRenderObjectUpdateData data = update.Data;
            if (data == null || (!data.HasWorldMatrix && !data.HasLocalMatrix))
                return;
            bool local = !data.HasWorldMatrix;
            MatrixD matrix = local ? (MatrixD)data.LocalMatrix : data.WorldMatrix;

            if (!s_objects.TryGetValue(update.ID, out ObjectTrack track))
            {
                track = new ObjectTrack { Id = update.ID, Local = local };
                s_objects.Add(update.ID, track);
            }
            if (track.Local != local)
            {
                track.Samples.Clear();
                track.Local = local;
            }
            if (track.Samples.Count == 0)
            {
                // First motion seen for this object: what the renderer shows
                // now is its state in the previous frame.
                MyActor actor = ResolveActor(track);
                if (actor != null)
                {
                    MatrixD current = local ? (MatrixD)actor.RelativeTransform : UnledWorld(track.Id, actor);
                    track.Samples.Push(seq - 1, cut: true) = Pose.From(ref current);
                }
            }
            else if (track.Samples.LatestSeq != seq && MatrixEquals(ref track.Samples.Latest.Matrix, ref matrix))
            {
                return;
            }

            Pose pose = Pose.From(ref matrix);
            bool cut = track.Samples.Count > 0 && !Pose.Continuous(ref track.Samples.Latest, ref pose, CutDistance);
            track.Samples.Push(seq, cut) = pose;
            if (!track.Active)
            {
                track.Active = true;
                s_activeObjects.Add(track);
            }
        }

        private static MyActor ResolveActor(ObjectTrack track)
        {
            MyActor actor = track.Actor;
            if (actor == null || actor.IsDestroyed || actor.ID != track.Id)
            {
                actor = MyIDTracker<MyActor>.FindByID(track.Id);
                track.Actor = actor;
            }
            return actor;
        }

        private static void ApplyObjects(long seqA, long seqB, double alpha)
        {
            for (int i = s_activeObjects.Count - 1; i >= 0; i--)
            {
                ObjectTrack track = s_activeObjects[i];
                SampleRing<Pose> samples = track.Samples;
                int b = samples.IndexAt(seqB);
                MyActor actor = ResolveActor(track);
                if (b < 0 || actor == null)
                {
                    if (actor == null || samples.Count == 0)
                        Deactivate(i, track);
                    continue;
                }
                int a = samples.IndexAt(seqA);
                if (a < 0)
                    a = b;
                double t = a == b ? 1.0 : samples.CutBetween(a, b) ? 0.0 : alpha;
                Pose.Blend(ref samples.At(a), ref samples.At(b), t, out MatrixD blended);

                s_updateData.HasLocalAABB = false;
                if (track.Local)
                {
                    s_updateData.HasWorldMatrix = false;
                    s_updateData.HasLocalMatrix = true;
                    s_updateData.LocalMatrix = blended;
                }
                else
                {
                    s_updateData.HasLocalMatrix = false;
                    s_updateData.HasWorldMatrix = true;
                    s_updateData.WorldMatrix = blended;
                }
                actor.SetTransforms(s_updateData);
                s_objectApplies++;

                // Once the display time has reached the newest sample, the
                // renderer holds the exact matrix again and the track rests.
                if (seqA >= samples.LatestSeq)
                    Deactivate(i, track);
            }
        }

        private static void Deactivate(int index, ObjectTrack track)
        {
            track.Active = false;
            int last = s_activeObjects.Count - 1;
            s_activeObjects[index] = s_activeObjects[last];
            s_activeObjects.RemoveAt(last);
        }

        private static bool MatrixEquals(ref MatrixD a, ref MatrixD b) =>
            a.M11 == b.M11 && a.M12 == b.M12 && a.M13 == b.M13 &&
            a.M21 == b.M21 && a.M22 == b.M22 && a.M23 == b.M23 &&
            a.M31 == b.M31 && a.M32 == b.M32 && a.M33 == b.M33 &&
            a.M41 == b.M41 && a.M42 == b.M42 && a.M43 == b.M43;

        // ---- camera --------------------------------------------------------

        private struct CameraSample
        {
            public Pose World;              // inverse of the view matrix
            public Vector3D Position;
            public float Fov;
            public float FovForSkybox;
            public MyRenderMessageSetCameraViewMatrix Message;  // our own copy
        }

        private static void OnCamera(long seq, MyRenderMessageSetCameraViewMatrix message)
        {
            MatrixD world = MatrixD.Invert(message.ViewMatrix);
            Pose pose = Pose.From(ref world);
            bool cut = !message.Smooth ||
                       (s_camera.Count > 0 && !Pose.Continuous(ref s_camera.Latest.World, ref pose, CameraCutDistance));
            if (cut)
                s_cameraCutSeq = seq;
            ref CameraSample sample = ref s_camera.Push(seq, cut);
            sample.World = pose;
            sample.Position = message.CameraPosition;
            sample.Fov = message.FOV;
            sample.FovForSkybox = message.FOVForSkybox;
            if (sample.Message == null)
                sample.Message = new MyRenderMessageSetCameraViewMatrix();
            CopyCamera(message, sample.Message);
        }

        private static void CopyCamera(MyRenderMessageSetCameraViewMatrix from, MyRenderMessageSetCameraViewMatrix to)
        {
            to.ViewMatrix = from.ViewMatrix;
            to.ProjectionMatrix = from.ProjectionMatrix;
            to.ProjectionFarMatrix = from.ProjectionFarMatrix;
            to.FOV = from.FOV;
            to.FOVForSkybox = from.FOVForSkybox;
            to.NearPlane = from.NearPlane;
            to.FarPlane = from.FarPlane;
            to.FarFarPlane = from.FarFarPlane;
            to.ProjectionOffsetX = from.ProjectionOffsetX;
            to.ProjectionOffsetY = from.ProjectionOffsetY;
            to.CameraPosition = from.CameraPosition;
            to.LastMomentUpdateIndex = from.LastMomentUpdateIndex;
            to.Smooth = from.Smooth;
            to.UpdateTime = from.UpdateTime;
        }

        private static void ApplyCamera(long seqA, long seqB, double alpha, double sinceLastDraw, out Vector3D position, out Vector3D forward)
        {
            position = default;
            forward = default;
            int b = s_camera.IndexAt(seqB);
            if (b < 0)
                return;
            int a = s_camera.IndexAt(seqA);
            if (a < 0)
                a = b;
            bool cut = s_camera.CutBetween(a, b);
            double t = a == b ? 1.0 : cut ? 0.0 : alpha;
            ref CameraSample sa = ref s_camera.At(a);
            ref CameraSample sb = ref s_camera.At(b);

            Pose.Blend(ref sa.World, ref sb.World, t, out MatrixD world);
            CopyCamera(sb.Message, s_cameraMessage);
            s_cameraMessage.CameraPosition = sa.Position + (sb.Position - sa.Position) * t;
            s_cameraMessage.FOV = (float)(sa.Fov + (sb.Fov - sa.Fov) * t);
            s_cameraMessage.FOVForSkybox = (float)(sa.FovForSkybox + (sb.FovForSkybox - sa.FovForSkybox) * t);

            if (s_mouseLookOn && MouseLead.Lead(s_camera.SeqAt(a), s_camera.SeqAt(b), t, s_readX, s_readY, sinceLastDraw,
                    out double yaw, out double pitch, out Vector3D up, out uint[] owners) && s_mouseLookApply)
            {
                s_leadRotation = LookMath.LeadRotation(ref world, up, yaw, pitch, LookMath.PitchMin, LookMath.PitchMax, out double pitchUsed);
                world = LookMath.RotateAbout(ref world, ref s_leadRotation, world.Translation);
                s_leadPivot = s_cameraMessage.CameraPosition;
                s_leadOwners = owners;
                s_leadYaw = yaw;
                s_leadPitch = pitchUsed;
                s_leadActive = true;
                s_leadDraws++;
            }
            s_cameraMessage.ViewMatrix = MatrixD.Invert(world);

            // The renderer resets temporal history on a camera cut; report one
            // only on the draw that first shows the frame after it.
            int shown = t > 0.0 ? b : a;
            long shownSeq = s_camera.SeqAt(shown);
            bool smooth = true;
            if (shownSeq > s_lastCameraSeqShown)
            {
                int previous = s_camera.IndexAt(s_lastCameraSeqShown);
                smooth = previous >= 0 && !s_camera.CutBetween(previous, shown);
                s_lastCameraSeqShown = shownSeq;
            }
            s_cameraMessage.Smooth = smooth;
            RenderAccess.SetupCameraMatrices(s_cameraMessage);

            position = s_cameraMessage.CameraPosition;
            forward = world.Forward;
        }

        // ---- mouse look: what turns with the view ---------------------------

        // A render object the lead turned: the matrix it was turned from and
        // the one written. An actor still holding the written matrix on a
        // later draw got nothing newer from the game or the interpolation, so
        // its unturned matrix is the base.
        private sealed class OwnerLead
        {
            public uint Id;
            public MyActor Actor;
            public MatrixD Base;
            public MatrixD Written;
            public bool Applied;
            public int Draw;
        }

        private static MyActor ResolveOwner(OwnerLead owner)
        {
            MyActor actor = owner.Actor;
            if (actor == null || actor.IsDestroyed || actor.ID != owner.Id)
            {
                actor = MyIDTracker<MyActor>.FindByID(owner.Id);
                owner.Actor = actor;
            }
            return actor;
        }

        private static MatrixD UnledWorld(uint id, MyActor actor)
        {
            MatrixD current = actor.WorldMatrix;
            if (s_owners.TryGetValue(id, out OwnerLead owner) && owner.Applied && MatrixEquals(ref current, ref owner.Written))
                return owner.Base;
            return current;
        }

        private static void SetWorld(MyActor actor, ref MatrixD world)
        {
            s_updateData.HasLocalAABB = false;
            s_updateData.HasLocalMatrix = false;
            s_updateData.HasWorldMatrix = true;
            s_updateData.WorldMatrix = world;
            actor.SetTransforms(s_updateData);
        }

        // The character and its tool turn with the camera, about the camera,
        // so they stay where they are on screen as in the game. What is
        // turned is the root of each one's actor chain: the character's mesh
        // hangs under its own cull object (MyRenderComponentCharacter), which
        // the game moves every step, and children follow their parent. A
        // root far from the camera or with many children is not the player's
        // own and is left alone.
        private const double OwnerMaxDistance = 5.0;
        private const int OwnerMaxChildren = 16;

        private static void ApplyOwners()
        {
            s_drawIndex++;
            if (s_leadActive && s_leadOwners != null)
            {
                foreach (uint ownerId in s_leadOwners)
                {
                    MyActor root = MyIDTracker<MyActor>.FindByID(ownerId);
                    for (int depth = 0; root != null && root.Parent != null && depth < 8; depth++)
                        root = root.Parent as MyActor;
                    if (root == null || root.IsDestroyed)
                        continue;
                    uint id = root.ID;
                    if (!s_owners.TryGetValue(id, out OwnerLead owner))
                    {
                        owner = new OwnerLead { Id = id };
                        s_owners.Add(id, owner);
                    }
                    if (owner.Draw == s_drawIndex)
                        continue;
                    owner.Draw = s_drawIndex;
                    MyActor actor = ResolveOwner(owner);
                    if (actor == null || actor.Children.Count > OwnerMaxChildren ||
                        Vector3D.DistanceSquared(actor.WorldMatrix.Translation, s_leadPivot) > OwnerMaxDistance * OwnerMaxDistance)
                        continue;
                    MatrixD current = actor.WorldMatrix;
                    MatrixD baseMatrix = owner.Applied && MatrixEquals(ref current, ref owner.Written) ? owner.Base : current;
                    MatrixD led = LookMath.RotateAbout(ref baseMatrix, ref s_leadRotation, s_leadPivot);
                    SetWorld(actor, ref led);
                    owner.Base = baseMatrix;
                    owner.Written = led;
                    owner.Applied = true;
                    s_ownerApplies++;
                }
            }
            if (s_owners.Count == 0)
                return;
            s_ownerScratch.Clear();
            foreach (OwnerLead owner in s_owners.Values)
            {
                if (owner.Draw != s_drawIndex)
                    s_ownerScratch.Add(owner.Id);
            }
            foreach (uint id in s_ownerScratch)
            {
                Restore(s_owners[id]);
                s_owners.Remove(id);
            }
        }

        private static void Restore(OwnerLead owner)
        {
            if (!owner.Applied)
                return;
            owner.Applied = false;
            MyActor actor = ResolveOwner(owner);
            if (actor == null)
                return;
            MatrixD current = actor.WorldMatrix;
            if (MatrixEquals(ref current, ref owner.Written))
                SetWorld(actor, ref owner.Base);
        }

        private static void RestoreOwners()
        {
            foreach (OwnerLead owner in s_owners.Values)
            {
                try
                {
                    Restore(owner);
                }
                catch (Exception)
                {
                    // Being unloaded anyway.
                }
            }
            s_owners.Clear();
            s_leadActive = false;
        }

        // ---- characters ------------------------------------------------------

        private sealed class CharacterTrack
        {
            public uint Id;
            public MyActor Actor;
            public object Skinning;
            public int SkeletonLength = -1;
            public Matrix[] Output;
            public readonly SampleRing<Pose[]> Samples = new SampleRing<Pose[]>();
        }

        private static void OnBones(long seq, MyRenderMessageSetCharacterTransforms message)
        {
            Matrix[] bones = message.BoneAbsoluteTransforms;
            if (bones == null || bones.Length == 0)
                return;
            if (!s_characters.TryGetValue(message.CharacterID, out CharacterTrack track))
            {
                track = new CharacterTrack { Id = message.CharacterID };
                s_characters.Add(message.CharacterID, track);
            }
            bool cut = track.Samples.Count > 0 && track.Samples.Latest != null && track.Samples.Latest.Length != bones.Length;
            ref Pose[] slot = ref track.Samples.Push(seq, cut);
            if (slot == null || slot.Length != bones.Length)
                slot = new Pose[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                MatrixD bone = bones[i];
                slot[i] = Pose.From(ref bone);
            }
        }

        private static void ApplyCharacters(long seqA, long seqB, double alpha)
        {
            foreach (CharacterTrack track in s_characters.Values)
            {
                SampleRing<Pose[]> samples = track.Samples;
                // Characters send their skeleton every frame they are drawn;
                // one that stopped (removed from view) is left alone.
                if (samples.Count == 0 || samples.LatestSeq < seqA)
                    continue;
                int b = samples.IndexAt(seqB);
                if (b < 0)
                    continue;
                int a = samples.IndexAt(seqA);
                if (a < 0)
                    a = b;
                Pose[] pa = samples.At(a);
                Pose[] pb = samples.At(b);
                if (pa.Length != pb.Length)
                    a = b;
                double t = a == b ? 1.0 : samples.CutBetween(a, b) ? 0.0 : alpha;

                if (track.Actor == null || track.Actor.IsDestroyed || track.Actor.ID != track.Id)
                {
                    track.Actor = MyIDTracker<MyActor>.FindByID(track.Id);
                    track.Skinning = track.Actor != null ? RenderAccess.GetSkinning(track.Actor) : null;
                    track.SkeletonLength = track.Skinning != null ? RenderAccess.SkeletonLength(track.Skinning) : -1;
                }
                if (track.Skinning == null)
                    continue;
                if (track.SkeletonLength < 0)
                    track.SkeletonLength = RenderAccess.SkeletonLength(track.Skinning);
                if (track.SkeletonLength <= 0 || track.SkeletonLength > pb.Length)
                    continue;

                if (track.Output == null || track.Output.Length != pb.Length)
                    track.Output = new Matrix[pb.Length];
                for (int i = 0; i < pb.Length; i++)
                {
                    if (!Pose.Continuous(ref pa[i], ref pb[i], BoneCutDistance))
                    {
                        track.Output[i] = t < 1.0 && a != b ? (Matrix)pa[i].Matrix : (Matrix)pb[i].Matrix;
                        continue;
                    }
                    Pose.Blend(ref pa[i], ref pb[i], t, out MatrixD bone);
                    track.Output[i] = bone;
                }
                RenderAccess.SetAnimationBones(track.Skinning, track.Output, s_noDecals);
                s_characterApplies++;
            }
        }

        // ---- diagnostics -----------------------------------------------------

        private static void Report(long now)
        {
            if (s_reportStart == 0)
            {
                s_reportStart = now;
                return;
            }
            double elapsed = (now - s_reportStart) / (double)Stopwatch.Frequency;
            if (elapsed < ReportSeconds)
                return;
            if (Log.Diagnostics && s_draws > 0)
            {
                Log.Debug(string.Format(CultureInfo.InvariantCulture,
                    "interpolation: {0:F1} draws/s, {1:F1} frames/s, delay {2:F2} ms, mean alpha {3:F2}, held {4}, snaps {5}, " +
                    "objects {6} tracked / {7} active ({8:F0} applies/draw), characters {9} ({10:F1} applies/draw), apply {11:F3} ms/draw",
                    s_draws / elapsed, s_frames / elapsed, s_clock.LastDelay * 1000.0, s_alphaSum / s_draws,
                    s_clock.HeldDraws, s_clock.SnapCount, s_objects.Count, s_activeObjects.Count,
                    s_objectApplies / (double)s_draws, s_characters.Count, s_characterApplies / (double)s_draws,
                    s_applyTicks * 1000.0 / Stopwatch.Frequency / s_draws));
                if (s_mouseLookOn)
                {
                    MouseLead.Axis yaw = MouseLead.Yaw, pitch = MouseLead.Pitch;
                    Log.Debug(string.Format(CultureInfo.InvariantCulture,
                        "mouse look: lead on {0:P0} of draws ({1:F1} owner applies/draw); frames {2} (first person {3}, unmatched {4}); " +
                        "yaw {5} scale {6:E4} (steps {7}, exact {8}, late {9}, adopt {10}, detach {11}, miss {12}); " +
                        "pitch {13} scale {14:E4} (steps {15}, exact {16}, late {17}, adopt {18}, detach {19}, miss {20}, limit {21}); " +
                        "polls {22} ({23} failed), game reads {24}, screen {25}",
                        s_leadDraws / (double)s_draws, s_leadDraws > 0 ? s_ownerApplies / (double)s_leadDraws : 0.0,
                        MouseLead.Frames, MouseLead.FirstPersonFrames, MouseLead.UnmatchedFrames,
                        yaw.Attached ? "attached" : "detached", yaw.Scale, yaw.Steps, yaw.Exact, yaw.Late, yaw.Adopts, yaw.Detaches, yaw.Misses,
                        pitch.Attached ? "attached" : "detached", pitch.Scale, pitch.Steps, pitch.Exact, pitch.Late, pitch.Adopts, pitch.Detaches,
                        pitch.Misses, pitch.Limited,
                        MouseInput.Polls, MouseInput.PollFailures, MouseInput.GameReads, TickCapture.FocusedScreen));
                }
            }
            s_reportStart = now;
            s_draws = 0;
            s_frames = 0;
            s_applyTicks = 0;
            s_objectApplies = 0;
            s_characterApplies = 0;
            s_alphaSum = 0;
            s_leadDraws = 0;
            s_ownerApplies = 0;
            s_clock.HeldDraws = 0;
            s_clock.SnapCount = 0;
            s_trace?.Flush();
            s_mouseTrace?.Flush();
        }
    }
}
