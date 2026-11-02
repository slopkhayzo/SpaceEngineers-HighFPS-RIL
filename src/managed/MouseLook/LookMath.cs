using System;
using VRageMath;

namespace SpaceEngineers.HighFPSFix.MouseLook
{
    // Yaw/pitch geometry of a first-person camera: yaw turns about the
    // character's up axis, pitch about the camera's right axis, and the right
    // axis is unaffected by pitch.
    internal static class LookMath
    {
        // The character's head pitch limits (MyCharacter.HeadLocalXAngle is
        // clamped to [-89.9, 89] degrees), as camera elevation.
        public const double PitchMin = -89.9 * Math.PI / 180.0;
        public const double PitchMax = 89.0 * Math.PI / 180.0;

        // Elevation of a direction above the plane perpendicular to up.
        public static double Elevation(Vector3D forward, Vector3D up) =>
            Math.Asin(Clamp(Vector3D.Dot(Vector3D.Normalize(forward), up), -1.0, 1.0));

        // Turn between two camera orientations: yaw is the signed angle the
        // right axis turned about up (right-hand rule), pitch the change of the
        // forward axis' elevation.
        public static void MeasureTurn(Vector3D right0, Vector3D right1, Vector3D forward0, Vector3D forward1,
            Vector3D up, out double yaw, out double pitch)
        {
            Vector3D a = Vector3D.Normalize(right0 - up * Vector3D.Dot(right0, up));
            Vector3D b = Vector3D.Normalize(right1 - up * Vector3D.Dot(right1, up));
            yaw = Math.Atan2(Vector3D.Dot(up, Vector3D.Cross(a, b)), Clamp(Vector3D.Dot(a, b), -1.0, 1.0));
            pitch = Elevation(forward1, up) - Elevation(forward0, up);
        }

        // World rotation (about the origin, row vectors) that turns an
        // orientation by yaw about up and then by pitch about its new right
        // axis. The pitch lead does not carry the forward axis past
        // [pitchMin, pitchMax] (it never pulls back a camera the game itself
        // left slightly outside); the pitch actually used is returned.
        public static MatrixD LeadRotation(ref MatrixD orientation, Vector3D up, double yaw, double pitch,
            double pitchMin, double pitchMax, out double pitchUsed)
        {
            MatrixD yawRotation = MatrixD.CreateFromAxisAngle(up, yaw);
            MatrixD basis = orientation;
            basis.Translation = Vector3D.Zero;
            MatrixD turned = basis * yawRotation;
            double elevation = Elevation(turned.Forward, up);
            pitchUsed = pitch > 0
                ? Math.Min(pitch, Math.Max(0, pitchMax - elevation))
                : Math.Max(pitch, Math.Min(0, pitchMin - elevation));
            Vector3D right = Vector3D.Normalize(turned.Right);
            return yawRotation * MatrixD.CreateFromAxisAngle(right, pitchUsed);
        }

        // Applies a world rotation to a transform about a pivot point.
        public static MatrixD RotateAbout(ref MatrixD transform, ref MatrixD rotation, Vector3D pivot)
        {
            MatrixD result = transform;
            result.Translation = Vector3D.Zero;
            result *= rotation;
            result.Translation = pivot + Vector3D.TransformNormal(transform.Translation - pivot, rotation);
            return result;
        }

        public static double Clamp(double value, double min, double max) =>
            value < min ? min : value > max ? max : value;
    }
}
