using System;

namespace MeltySynth
{
    /// <summary>
    /// net48 has no <c>System.MathF</c>. This shim provides the single-precision helpers MeltySynth uses,
    /// resolved by simple name from within the MeltySynth namespace (so the upstream source is unmodified).
    /// </summary>
    internal static class MathF
    {
        public const float PI = (float)Math.PI;

        public static float Abs(float x) => Math.Abs(x);
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Log(float x) => (float)Math.Log(x);
        public static float Log(float x, float newBase) => (float)Math.Log(x, newBase);
        public static float Log10(float x) => (float)Math.Log10(x);
        public static float Round(float x) => (float)Math.Round(x);

        // net48 also lacks MathF.Clamp; the upstream `MathF.Clamp(...)` calls are redirected here.
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        public static long Clamp(long v, long min, long max) => v < min ? min : (v > max ? max : v);
    }
}
