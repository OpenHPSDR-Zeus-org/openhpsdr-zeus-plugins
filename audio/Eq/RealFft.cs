// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tiny managed radix-2 complex FFT used by the EQ plugin's live
// spectrum analyser. We keep this in-tree so the plugin has no
// third-party DSP dependency — the SDK rule is "managed only,
// no native deps for v1 chain blocks", and the operator can read
// the algorithm in 50 lines.
//
// 2048-point complex FFT cost on M-series silicon: ~50 µs.
// We call it twice per ~21 ms block (input + output spectrum)
// for ~0.5 % CPU — negligible.

namespace Openhpsdr.Zeus.Samples.Eq;

/// <summary>
/// In-place radix-2 Cooley-Tukey FFT for power-of-two lengths.
/// Forward transform only (we never need inverse for a spectrum
/// analyser). Caller supplies <paramref name="re"/> + <paramref name="im"/>
/// span pair; on return the same spans hold the frequency-domain
/// coefficients. No allocation; designed to be called from the
/// realtime audio thread.
/// </summary>
internal static class RealFft
{
    /// <summary>
    /// Run a forward FFT in place. <paramref name="re"/> and
    /// <paramref name="im"/> must each be exactly <paramref name="n"/>
    /// elements, and <paramref name="n"/> must be a positive power of two.
    /// </summary>
    public static void Forward(Span<float> re, Span<float> im, int n)
    {
        if (re.Length != n || im.Length != n)
            throw new ArgumentException($"re/im spans must be length n={n}");
        if (n <= 0 || (n & (n - 1)) != 0)
            throw new ArgumentException($"n must be a positive power of two, got {n}");

        // Bit-reversal permutation.
        int j = 0;
        for (int i = 1; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Butterflies. For each stage len = 2, 4, 8, ..., n: combine
        // pairs of half-len blocks using rotating twiddle factors.
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            float angleStep = -MathF.PI / half;  // forward (negative)
            float wRe = MathF.Cos(angleStep);
            float wIm = MathF.Sin(angleStep);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f;
                float curIm = 0f;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k;
                    int b = a + half;
                    // t = w * (re[b] + j im[b])
                    float tRe = curRe * re[b] - curIm * im[b];
                    float tIm = curRe * im[b] + curIm * re[b];
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    // w *= e^{angleStep i}
                    float nextRe = curRe * wRe - curIm * wIm;
                    float nextIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                    curIm = nextIm;
                }
            }
        }
    }
}
