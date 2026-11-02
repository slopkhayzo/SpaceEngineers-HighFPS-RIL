using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceEngineers.HighFPSFix
{
    internal sealed class Settings
    {
        // [General]
        public bool Enabled = true;
        public bool Diagnostics;

        // [FrameRate]
        // 0 = the refresh rate of the display the game runs on, -1 = no limit.
        public int TargetFps;

        // [Interpolation]
        public bool Interpolation = true;
        public bool InterpolateCamera = true;
        public bool InterpolateObjects = true;
        public bool InterpolateCharacters = true;
        // Extra presentation delay on top of one simulation step plus the
        // measured update latency, in milliseconds.
        public double ExtraDelayMs = 0.5;
        // First person on foot: the camera turns with the mouse every drawn
        // frame (the game still receives every mouse count once, per step).
        public bool MouseLook = true;

        // [Debug]
        public string AutoLoadWorld = "";
        public double AutoQuitSeconds;
        public string TracePath = "";
        // Once the world is ready, the spectator camera circles the player:
        // one turn per AutoOrbitSeconds (0 = off), at AutoOrbitRadius metres.
        public double AutoOrbitSeconds;
        public double AutoOrbitRadius = 25.0;
        // false = no render hooks and no frame-rate change (control runs).
        public bool RenderHooks = true;
        // Once the world is ready, the largest movable ship is kept moving
        // forward at AutoPushSpeed m/s and turning at AutoPushSpin deg/s, and
        // the orbit (if on) follows it; its presented position is traced.
        public double AutoPushSpeed;
        public double AutoPushSpin;
        // Once the world is ready, synthetic mouse motion (SendInput, only
        // while the game window is in the foreground and the player looks in
        // first person): a repeating pattern of turns at up to this many
        // counts per second (0 = off).
        public double AutoMouseSpeed;
        // The automation switches the lead on and off for every other
        // repetition of its pattern (A/B within one session).
        public bool AutoMouseAlternate;
        // Once the world is ready, ask Windows to bring the game window to the
        // front (granted only while the machine is not in use).
        public bool AutoFocus;
        // Once the world is ready, the player's character takes this hand
        // tool from its inventory (physical item subtype, e.g. WelderItem).
        public string AutoEquip = "";
        // false = mouse look reads, learns and traces, but the camera is not
        // led (A/B runs).
        public bool MouseLookApply = true;

        public static Settings Load(string path)
        {
            var settings = new Settings();
            settings.Enabled = ReadBool(path, "General", "Enabled", settings.Enabled);
            settings.Diagnostics = ReadBool(path, "General", "Diagnostics", settings.Diagnostics);
            settings.TargetFps = ReadInt(path, "FrameRate", "TargetFPS", settings.TargetFps);
            settings.Interpolation = ReadBool(path, "Interpolation", "Enabled", settings.Interpolation);
            settings.InterpolateCamera = ReadBool(path, "Interpolation", "Camera", settings.InterpolateCamera);
            settings.InterpolateObjects = ReadBool(path, "Interpolation", "Objects", settings.InterpolateObjects);
            settings.InterpolateCharacters = ReadBool(path, "Interpolation", "Characters", settings.InterpolateCharacters);
            settings.ExtraDelayMs = ReadDouble(path, "Interpolation", "ExtraDelayMs", settings.ExtraDelayMs);
            settings.MouseLook = ReadBool(path, "Interpolation", "MouseLook", settings.MouseLook);
            settings.AutoLoadWorld = ReadString(path, "Debug", "AutoLoadWorld", settings.AutoLoadWorld);
            settings.AutoQuitSeconds = ReadDouble(path, "Debug", "AutoQuitSeconds", settings.AutoQuitSeconds);
            settings.TracePath = ReadString(path, "Debug", "TracePath", settings.TracePath);
            settings.AutoOrbitSeconds = ReadDouble(path, "Debug", "AutoOrbitSeconds", settings.AutoOrbitSeconds);
            settings.AutoOrbitRadius = ReadDouble(path, "Debug", "AutoOrbitRadius", settings.AutoOrbitRadius);
            settings.RenderHooks = ReadBool(path, "Debug", "RenderHooks", settings.RenderHooks);
            settings.AutoPushSpeed = ReadDouble(path, "Debug", "AutoPushSpeed", settings.AutoPushSpeed);
            settings.AutoPushSpin = ReadDouble(path, "Debug", "AutoPushSpin", settings.AutoPushSpin);
            settings.AutoMouseSpeed = ReadDouble(path, "Debug", "AutoMouseSpeed", settings.AutoMouseSpeed);
            settings.MouseLookApply = ReadBool(path, "Debug", "MouseLookApply", settings.MouseLookApply);
            settings.AutoMouseAlternate = ReadBool(path, "Debug", "AutoMouseAlternate", settings.AutoMouseAlternate);
            settings.AutoFocus = ReadBool(path, "Debug", "AutoFocus", settings.AutoFocus);
            settings.AutoEquip = ReadString(path, "Debug", "AutoEquip", settings.AutoEquip);
            return settings;
        }

        public override string ToString() =>
            $"TargetFPS={TargetFps}, Interpolation={Interpolation} (camera {InterpolateCamera}, objects {InterpolateObjects}, " +
            $"characters {InterpolateCharacters}, extra delay {ExtraDelayMs.ToString(CultureInfo.InvariantCulture)} ms, " +
            $"mouse look {MouseLook})";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileStringW(string section, string key, string fallback,
            StringBuilder value, int size, string path);

        private static string ReadString(string path, string section, string key, string fallback)
        {
            var value = new StringBuilder(1024);
            GetPrivateProfileStringW(section, key, fallback, value, value.Capacity, path);
            string text = value.ToString();
            int comment = text.IndexOf(';');
            if (comment >= 0)
                text = text.Substring(0, comment);
            return text.Trim();
        }

        private static bool ReadBool(string path, string section, string key, bool fallback)
        {
            switch (ReadString(path, section, key, "").ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        private static int ReadInt(string path, string section, string key, int fallback) =>
            int.TryParse(ReadString(path, section, key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value : fallback;

        private static double ReadDouble(string path, string section, string key, double fallback) =>
            double.TryParse(ReadString(path, section, key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : fallback;
    }
}
