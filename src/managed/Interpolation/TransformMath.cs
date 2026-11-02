using System;
using VRageMath;

namespace SpaceEngineers.HighFPSFix.Interpolation
{
    // A transform split into rotation, per-axis scale and translation, so that
    // a sample is decomposed once when it arrives and only blended per draw.
    // Transforms that do not decompose (degenerate or mirrored bases) are kept
    // as matrices and switch from one sample to the next without blending.
    internal struct Pose
    {
        public MatrixD Matrix;
        public TransformMath.Quat Rotation;
        public TransformMath.Vec Scale;
        public bool Decomposed;

        public static Pose From(ref MatrixD matrix)
        {
            var pose = new Pose { Matrix = matrix };
            pose.Decomposed = TransformMath.Decompose(ref matrix, out pose.Rotation, out pose.Scale);
            return pose;
        }

        // False when the pair should not be blended: a jump over maxDistance,
        // a turn over MaxRotationRadians, or a transform that does not decompose.
        public static bool Continuous(ref Pose a, ref Pose b, double maxDistance)
        {
            if (!a.Decomposed || !b.Decomposed)
                return false;
            if (TransformMath.DistanceSquared(ref a.Matrix, ref b.Matrix) > maxDistance * maxDistance)
                return false;
            double dot = Math.Abs(TransformMath.Quat.Dot(a.Rotation, b.Rotation));
            return 2.0 * Math.Acos(Math.Min(1.0, dot)) <= TransformMath.MaxRotationRadians;
        }

        public static void Blend(ref Pose a, ref Pose b, double t, out MatrixD result)
        {
            if (t <= 0)
            {
                result = a.Matrix;
                return;
            }
            if (t >= 1)
            {
                result = b.Matrix;
                return;
            }
            TransformMath.Compose(TransformMath.Quat.Slerp(a.Rotation, b.Rotation, t),
                TransformMath.Vec.Lerp(a.Scale, b.Scale, t), out result);
            result.M41 = a.Matrix.M41 + (b.Matrix.M41 - a.Matrix.M41) * t;
            result.M42 = a.Matrix.M42 + (b.Matrix.M42 - a.Matrix.M42) * t;
            result.M43 = a.Matrix.M43 + (b.Matrix.M43 - a.Matrix.M43) * t;
            result.M44 = 1;
        }
    }

    // Interpolation of VRageMath transforms (row vectors: rows 1-3 are the
    // scaled basis, row 4 the translation). Translation is interpolated
    // linearly in double precision, rotation by spherical interpolation of
    // unit quaternions, and per-axis scale linearly.
    internal static class TransformMath
    {
        // Above this rotation between two samples the pair is treated as a cut.
        public const double MaxRotationRadians = Math.PI * 0.5;

        public static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

        public static bool Interpolate(ref MatrixD a, ref MatrixD b, double t, out MatrixD result)
        {
            if (!Decompose(ref a, out Quat qa, out Vec sa) || !Decompose(ref b, out Quat qb, out Vec sb))
            {
                result = t < 0.5 ? a : b;
                return false;
            }
            Quat q = Quat.Slerp(qa, qb, t);
            Compose(q, Vec.Lerp(sa, sb, t), out result);
            result.M41 = a.M41 + (b.M41 - a.M41) * t;
            result.M42 = a.M42 + (b.M42 - a.M42) * t;
            result.M43 = a.M43 + (b.M43 - a.M43) * t;
            result.M14 = 0;
            result.M24 = 0;
            result.M34 = 0;
            result.M44 = 1;
            return true;
        }

        public static bool Interpolate(ref Matrix a, ref Matrix b, double t, out Matrix result)
        {
            MatrixD da = a;
            MatrixD db = b;
            bool smooth = Interpolate(ref da, ref db, t, out MatrixD r);
            result = r;
            return smooth;
        }

        // Rotation angle between the orientations of two transforms.
        public static double RotationAngle(ref MatrixD a, ref MatrixD b)
        {
            if (!Decompose(ref a, out Quat qa, out _) || !Decompose(ref b, out Quat qb, out _))
                return Math.PI;
            double dot = Math.Abs(Quat.Dot(qa, qb));
            return 2.0 * Math.Acos(Math.Min(1.0, dot));
        }

        public static double DistanceSquared(ref MatrixD a, ref MatrixD b)
        {
            double dx = b.M41 - a.M41, dy = b.M42 - a.M42, dz = b.M43 - a.M43;
            return dx * dx + dy * dy + dz * dz;
        }

        internal static bool Decompose(ref MatrixD m, out Quat rotation, out Vec scale)
        {
            var x = new Vec(m.M11, m.M12, m.M13);
            var y = new Vec(m.M21, m.M22, m.M23);
            var z = new Vec(m.M31, m.M32, m.M33);
            scale = new Vec(x.Length, y.Length, z.Length);
            rotation = default;
            if (scale.X < 1e-9 || scale.Y < 1e-9 || scale.Z < 1e-9 || double.IsNaN(scale.X + scale.Y + scale.Z))
                return false;
            x = x / scale.X;
            y = y / scale.Y;
            z = z / scale.Z;
            // A mirrored basis has no rotation quaternion.
            if (Vec.Dot(Vec.Cross(x, y), z) < 0)
                return false;
            rotation = Quat.FromBasis(x, y, z);
            return true;
        }

        internal static void Compose(Quat q, Vec scale, out MatrixD m)
        {
            double xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
            double xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
            double wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;
            m = default;
            m.M11 = (1 - 2 * (yy + zz)) * scale.X;
            m.M12 = 2 * (xy + wz) * scale.X;
            m.M13 = 2 * (xz - wy) * scale.X;
            m.M21 = 2 * (xy - wz) * scale.Y;
            m.M22 = (1 - 2 * (xx + zz)) * scale.Y;
            m.M23 = 2 * (yz + wx) * scale.Y;
            m.M31 = 2 * (xz + wy) * scale.Z;
            m.M32 = 2 * (yz - wx) * scale.Z;
            m.M33 = (1 - 2 * (xx + yy)) * scale.Z;
        }

        internal struct Vec
        {
            public double X, Y, Z;
            public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            public static Vec operator /(Vec v, double s) => new Vec(v.X / s, v.Y / s, v.Z / s);
            public static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static Vec Cross(Vec a, Vec b) => new Vec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            public static Vec Lerp(Vec a, Vec b, double t) => new Vec(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
        }

        // Quaternion matching the row-vector convention of VRageMath
        // (Matrix.CreateFromQuaternion layout).
        internal struct Quat
        {
            public double X, Y, Z, W;

            public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

            public static double Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

            public static Quat FromBasis(Vec x, Vec y, Vec z)
            {
                // m[row][column]: row 0 = x, row 1 = y, row 2 = z.
                double m11 = x.X, m12 = x.Y, m13 = x.Z;
                double m21 = y.X, m22 = y.Y, m23 = y.Z;
                double m31 = z.X, m32 = z.Y, m33 = z.Z;
                double trace = m11 + m22 + m33;
                Quat q;
                if (trace > 0)
                {
                    double s = Math.Sqrt(trace + 1.0) * 2.0;
                    q = new Quat((m23 - m32) / s, (m31 - m13) / s, (m12 - m21) / s, 0.25 * s);
                }
                else if (m11 > m22 && m11 > m33)
                {
                    double s = Math.Sqrt(1.0 + m11 - m22 - m33) * 2.0;
                    q = new Quat(0.25 * s, (m12 + m21) / s, (m13 + m31) / s, (m23 - m32) / s);
                }
                else if (m22 > m33)
                {
                    double s = Math.Sqrt(1.0 + m22 - m11 - m33) * 2.0;
                    q = new Quat((m12 + m21) / s, 0.25 * s, (m23 + m32) / s, (m31 - m13) / s);
                }
                else
                {
                    double s = Math.Sqrt(1.0 + m33 - m11 - m22) * 2.0;
                    q = new Quat((m13 + m31) / s, (m23 + m32) / s, 0.25 * s, (m12 - m21) / s);
                }
                return Normalize(q);
            }

            public static Quat Normalize(Quat q)
            {
                double length = Math.Sqrt(Dot(q, q));
                return length < 1e-12 ? new Quat(0, 0, 0, 1) : new Quat(q.X / length, q.Y / length, q.Z / length, q.W / length);
            }

            public static Quat Slerp(Quat a, Quat b, double t)
            {
                double cos = Dot(a, b);
                if (cos < 0)
                {
                    b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
                    cos = -cos;
                }
                double wa, wb;
                if (cos > 0.9995)
                {
                    wa = 1 - t;
                    wb = t;
                }
                else
                {
                    double angle = Math.Acos(cos);
                    double sin = Math.Sin(angle);
                    wa = Math.Sin((1 - t) * angle) / sin;
                    wb = Math.Sin(t * angle) / sin;
                }
                return Normalize(new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb));
            }
        }
    }
}
