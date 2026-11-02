using System;
using System.IO;
using System.Threading;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Screens;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineers.HighFPSFix
{
    // [Debug] options for unattended test runs: load a world once the main
    // menu is up, quit after a while.
    internal static class DebugAutomation
    {
        public static void Start(Settings settings)
        {
            if (string.IsNullOrEmpty(settings.AutoLoadWorld) && settings.AutoQuitSeconds <= 0)
                return;
            var thread = new Thread(() => Run(settings)) { IsBackground = true, Name = Plugin.Name + " automation" };
            thread.Start();
        }

        private static void Run(Settings settings)
        {
            try
            {
                WaitUntil(() => MySandboxGame.IsGameReady && MySandboxGame.Static != null, TimeSpan.FromMinutes(5));
                // The intro (logos, video) ends by opening the main menu; a
                // world loaded before that gets the main menu on top of it.
                if (!WaitUntil(() => MyScreenManager.GetScreenWithFocus() is MyGuiScreenMainMenuBase, TimeSpan.FromMinutes(2)))
                    Log.Warning("Debug: the main menu did not come up; loading anyway.");
                Thread.Sleep(1000);

                if (!string.IsNullOrEmpty(settings.AutoLoadWorld))
                {
                    string world = FindWorld(settings.AutoLoadWorld);
                    if (world == null)
                    {
                        Log.Error("Debug: world not found: " + settings.AutoLoadWorld);
                        return;
                    }
                    Log.Info("Debug: loading world " + world);
                    MySandboxGame.Static.Invoke(() => MySessionLoader.LoadSingleplayerSession(world), Plugin.Name);
                    if (!WaitUntil(() => MySession.Static != null && MySession.Static.Ready, TimeSpan.FromMinutes(5)))
                        Log.Warning("Debug: the world did not finish loading in time.");
                    else
                        Log.Info("Debug: world ready.");
                    if (settings.AutoOrbitSeconds > 0 || settings.AutoPushSpeed != 0 || settings.AutoPushSpin != 0)
                        StartMotion(settings);
                    if (!string.IsNullOrEmpty(settings.AutoEquip))
                        Equip(settings.AutoEquip);
                    if (settings.AutoFocus)
                        MouseLook.MouseAutomation.RequestFocus();
                    if (settings.AutoMouseSpeed > 0)
                        MouseLook.MouseAutomation.Start(settings.AutoMouseSpeed, settings.AutoMouseAlternate);
                }

                if (settings.AutoQuitSeconds > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(settings.AutoQuitSeconds));
                    Log.Info("Debug: quitting.");
                    Interpolation.SceneInterpolator.Shutdown();
                    MySandboxGame.ExitThreadSafe();
                    // A test run must end by itself; the game's exit can stall
                    // with its window in the background.
                    Thread.Sleep(TimeSpan.FromSeconds(20));
                    Log.Warning("Debug: the game did not exit within 20 s; ending the process.");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }
            }
            catch (Exception exception)
            {
                Log.Error("Debug automation failed", exception);
            }
        }

        // Deterministic motion for smoothness measurements, driven once per
        // simulation step on the update thread: the largest movable ship is
        // kept moving and turning, and the spectator camera circles it (or the
        // player) at a constant rate.
        private static void StartMotion(Settings settings)
        {
            double period = settings.AutoOrbitSeconds;
            double radius = settings.AutoOrbitRadius;
            bool push = settings.AutoPushSpeed != 0 || settings.AutoPushSpin != 0;
            MyCubeGrid grid = null;
            Vector3D center = Vector3D.Zero;
            long step = 0;
            MySandboxGame.Static.Invoke(() =>
            {
                MySession session = MySession.Static;
                center = session?.ControlledEntity?.Entity?.PositionComp?.GetPosition() ?? Vector3D.Zero;
                if (push)
                {
                    grid = FindLargestMovableGrid();
                    if (grid != null)
                    {
                        center = grid.PositionComp.GetPosition();
                        Interpolation.SceneInterpolator.TraceObjectId = grid.Render.GetRenderObjectID();
                        Log.Info($"Debug: pushing {grid.DisplayName} ({grid.BlocksCount} blocks) at {settings.AutoPushSpeed} m/s, {settings.AutoPushSpin} deg/s.");
                    }
                    else
                    {
                        Log.Warning("Debug: no movable ship to push.");
                    }
                }
                if (period > 0)
                {
                    session?.SetCameraController(MyCameraControllerEnum.Spectator, null, center + new Vector3D(radius, radius * 0.2, 0));
                    Log.Info($"Debug: spectator orbit, radius {radius} m, {period} s per turn.");
                }
            }, Plugin.Name);
            MySandboxGame.Static.Invoke(() =>
            {
                if (grid != null && grid.Physics != null && !grid.MarkedForClose)
                {
                    MatrixD world = grid.WorldMatrix;
                    grid.Physics.LinearVelocity = (Vector3)(world.Forward * settings.AutoPushSpeed);
                    grid.Physics.AngularVelocity = (Vector3)(world.Up * (settings.AutoPushSpin * Math.PI / 180.0));
                    center = grid.PositionComp.GetPosition();
                }
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                if (period <= 0 || spectator == null)
                    return;
                double angle = 2.0 * Math.PI * step++ / (60.0 * period);
                spectator.Position = center + new Vector3D(Math.Cos(angle) * radius, radius * 0.2, Math.Sin(angle) * radius);
                spectator.SetTarget(center, Vector3D.Up);
            }, Plugin.Name, -1, int.MaxValue);
        }

        private static void Equip(string physicalItem)
        {
            MySandboxGame.Static.Invoke(() =>
            {
                if (!(MySession.Static?.LocalCharacter is MyCharacter character))
                {
                    Log.Warning("Debug: no character to equip.");
                    return;
                }
                if (!MyDefinitionId.TryParse("MyObjectBuilder_PhysicalGunObject/" + physicalItem, out MyDefinitionId physical))
                {
                    Log.Warning("Debug: bad item " + physicalItem);
                    return;
                }
                MyHandItemDefinition hand = MyDefinitionManager.Static.TryGetHandItemForPhysicalItem(physical);
                if (hand == null)
                {
                    Log.Warning("Debug: no hand item for " + physical);
                    return;
                }
                character.SwitchToWeapon(hand.Id);
                Log.Info($"Debug: equipping {hand.Id} (now holding {(character.CurrentWeapon as MyEntity)?.GetType().Name ?? "nothing yet"}).");
            }, Plugin.Name);
        }

        private static MyCubeGrid FindLargestMovableGrid()
        {
            MyCubeGrid best = null;
            foreach (MyEntity entity in MyEntities.GetEntities())
            {
                if (entity is MyCubeGrid grid && grid.Physics != null && !grid.Physics.IsStatic &&
                    (best == null || grid.BlocksCount > best.BlocksCount))
                    best = grid;
            }
            return best;
        }

        // A full path, or a world folder name under Saves\<user id>\.
        private static string FindWorld(string world)
        {
            if (Path.IsPathRooted(world))
                return Directory.Exists(world) ? world : null;
            string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "Saves");
            if (!Directory.Exists(saves))
                return null;
            foreach (string user in Directory.GetDirectories(saves))
            {
                string candidate = Path.Combine(user, world);
                if (Directory.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition())
                        return true;
                }
                catch (Exception)
                {
                    // Game state read from another thread mid-change.
                }
                Thread.Sleep(100);
            }
            return false;
        }
    }
}
