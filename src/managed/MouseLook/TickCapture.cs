using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRageRender;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // Records a TickInfo at the end of every simulation step, on the update
    // thread: MySharedData.AfterUpdate stamps the step's frame and hands it to
    // the renderer, so the record is queued before the render thread can see
    // the frame, and in frame order.
    internal static unsafe class TickCapture
    {
        private static delegate*<object, MyTimeSpan?, bool, void> s_afterUpdate;
        private static readonly ConcurrentQueue<TickInfo> s_queue = new ConcurrentQueue<TickInfo>();
        private static readonly List<uint> s_ownerScratch = new List<uint>();
        private static uint[] s_lastOwners = Array.Empty<uint>();
        // Render thread: a record for a frame that has not arrived yet.
        private static TickInfo s_held;
        private static bool s_hasHeld;

        // Update thread writes, other threads read.
        public static volatile bool LastFirstPerson;
        public static volatile string FocusedScreen = "";

        public static void Prepare(HookTransaction hooks)
        {
            MethodInfo afterUpdate = GameReflection.Method(typeof(MySharedData), "AfterUpdate", typeof(MyTimeSpan?), typeof(bool));
            s_afterUpdate = (delegate*<object, MyTimeSpan?, bool, void>)hooks.Install(afterUpdate,
                typeof(TickCapture).GetMethod(nameof(AfterUpdateDetour), BindingFlags.NonPublic | BindingFlags.Static));
        }

        // Render thread: the record of the frame stamped 'timestamp'. Records
        // of frames the renderer never sees (none expected) are dropped.
        public static bool Match(long timestamp, out TickInfo tick)
        {
            if (s_hasHeld)
            {
                if (s_held.Timestamp > timestamp)
                {
                    tick = default;
                    return false;
                }
                s_hasHeld = false;
                if (s_held.Timestamp == timestamp)
                {
                    tick = s_held;
                    return true;
                }
            }
            while (s_queue.TryDequeue(out TickInfo info))
            {
                if (info.Timestamp == timestamp)
                {
                    tick = info;
                    return true;
                }
                if (info.Timestamp > timestamp)
                {
                    s_held = info;
                    s_hasHeld = true;
                    break;
                }
            }
            tick = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AfterUpdateDetour(object self, MyTimeSpan? updateTimestamp, bool gate)
        {
            // Without a timestamp it is the render thread's final call at exit.
            if (updateTimestamp.HasValue)
            {
                try
                {
                    Capture(updateTimestamp.Value.Ticks);
                }
                catch (Exception exception)
                {
                    Log.Debug("Tick capture failed: " + exception.Message);
                }
            }
            s_afterUpdate(self, updateTimestamp, gate);
        }

        private static void Capture(long timestamp)
        {
            MouseInput.Delivered(out long countsX, out long countsY);
            var info = new TickInfo { Timestamp = timestamp, CountsX = countsX, CountsY = countsY };

            MySession session = MySession.Static;
            MyGuiScreenBase focused = MyScreenManager.GetScreenWithFocus();
            string screen = focused?.GetType().Name ?? "";
            if (screen != FocusedScreen)
            {
                Log.Debug("Focused screen: " + (screen.Length > 0 ? screen : "none"));
                FocusedScreen = screen;
            }
            if (session != null && session.Ready && !MySandboxGame.IsPaused &&
                focused != null && ReferenceEquals(focused, MyGuiScreenGamePlay.Static) &&
                session.CameraController is MyCharacter character &&
                ReferenceEquals(session.ControlledEntity, character) &&
                character.IsInFirstPersonView && !character.IsDead)
            {
                info.FirstPerson = true;
                info.Up = character.WorldMatrix.Up;
                info.Owners = CollectOwners(character);
            }
            LastFirstPerson = info.FirstPerson;
            s_queue.Enqueue(info);
            // A consumer that stopped (interpolation switched off) must not
            // let the queue grow.
            while (s_queue.Count > 256 && s_queue.TryDequeue(out _))
            {
            }
        }

        private static uint[] CollectOwners(MyCharacter character)
        {
            s_ownerScratch.Clear();
            AddRenderObjects(character);
            if (character.CurrentWeapon is MyEntity tool)
            {
                AddRenderObjects(tool);
                if (tool.Subparts != null)
                {
                    foreach (MyEntitySubpart subpart in tool.Subparts.Values)
                        AddRenderObjects(subpart);
                }
            }
            if (s_ownerScratch.Count == s_lastOwners.Length)
            {
                bool same = true;
                for (int i = 0; i < s_lastOwners.Length && same; i++)
                    same = s_lastOwners[i] == s_ownerScratch[i];
                if (same)
                    return s_lastOwners;
            }
            s_lastOwners = s_ownerScratch.ToArray();
            if (Log.Diagnostics)
            {
                Log.Debug($"Mouse look owners: character [{string.Join(", ", character.Render?.RenderObjectIDs ?? Array.Empty<uint>())}], " +
                          $"tool {(character.CurrentWeapon as MyEntity)?.GetType().Name ?? "none"}; render objects [{string.Join(", ", s_lastOwners)}]");
            }
            return s_lastOwners;
        }

        private static void AddRenderObjects(MyEntity entity)
        {
            uint[] ids = entity?.Render?.RenderObjectIDs;
            if (ids == null)
                return;
            foreach (uint id in ids)
            {
                if (id != uint.MaxValue)
                    s_ownerScratch.Add(id);
            }
        }
    }
}
