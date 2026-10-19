using System.Numerics;

namespace Pigmentor.Core.Numerics;

/// <summary>
/// The one cosine-similarity implementation the whole solution routes through.
/// Cosine was previously copy-pasted inline into every component that ranks
/// vectors (the in-memory store, MMR, the semantic router, the semantic
/// chunker, the anomaly detector). This kernel is that shared primitive: a
/// hardware-accelerated <see cref="Vector{T}"/> fast path with a scalar
/// fallback, so the hot retrieval loop is SIMD-wide on every machine that
/// supports it and still correct on those that do not.
/// </summary>
public static class CosineKernel
{
    /// <summary>
    /// Cosine similarity of two equal-length vectors, in <c>[-1, 1]</c>.
    /// Returns <c>0</c> when the lengths differ or either vector is the zero
    /// vector (an undefined cosine), matching the behavior of the inline
    /// implementations this kernel replaced.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector. Must be the same length as <paramref name="a"/>.</param>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0.0;
        }

        double dot, magA, magB;

        // SIMD fast path: accumulate dot product and both squared magnitudes a
        // full Vector<float> width at a time, then reduce the lanes once at the
        // end. Vector<float>.Count is 8 on AVX2, 16 on AVX-512 — the JIT picks
        // the widest the CPU supports.
        var width = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && a.Length >= width)
        {
            var dotAcc = Vector<float>.Zero;
            var magAAcc = Vector<float>.Zero;
            var magBAcc = Vector<float>.Zero;

            int i = 0;
            for (; i <= a.Length - width; i += width)
            {
                var va = new Vector<float>(a.Slice(i, width));
                var vb = new Vector<float>(b.Slice(i, width));
                dotAcc += va * vb;
                magAAcc += va * va;
                magBAcc += vb * vb;
            }

            dot = Vector.Dot(dotAcc, Vector<float>.One);
            magA = Vector.Dot(magAAcc, Vector<float>.One);
            magB = Vector.Dot(magBAcc, Vector<float>.One);

            // Scalar tail for the elements that did not fill a full vector.
            for (; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
        }
        else
        {
            dot = 0;
            magA = 0;
            magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
        }

        return magA <= 0 || magB <= 0 ? 0.0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
