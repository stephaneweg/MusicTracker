using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MeltySynth
{
    internal static class ArrayMath
    {
        public static void MultiplyAdd(float a, float[] x, float[] destination)
        {
            /*
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
            }
            */

            // Force Span<T> selection (not ReadOnlySpan<T>): on the .NET 10 compiler, passing a
            // T[] directly to MemoryMarshal.Cast now binds the ReadOnlySpan overload, which yields
            // a readonly indexer — writing back to `vd[i]` then fails CS8331.
            Span<float> spx = x;
            Span<float> spd = destination;
            var vx = MemoryMarshal.Cast<float, Vector<float>>(spx);
            var vd = MemoryMarshal.Cast<float, Vector<float>>(spd);

            var count = 0;

            for (var i = 0; i < vd.Length; i++)
            {
                // .NET 8+ made Vector<T> a readonly struct; the C# 12+/.NET 10 compiler now treats
                // Span<Vector<float>>.this[]'s ref return as a readonly-variable location for the
                // purpose of the += operator (CS8331). Copy the sum into a local first, then write
                // the local back through the indexer — this compiles on all targets.
                var sum = vd[i] + a * vx[i];
                vd[i] = sum;
                count += Vector<float>.Count;
            }

            for (var i = count; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
            }
        }

        public static void MultiplyAdd(float a, float step, float[] x, float[] destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
                a += step;
            }
        }
    }
}
