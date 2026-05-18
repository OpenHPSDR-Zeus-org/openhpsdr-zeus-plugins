// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus voice audio chain v2 — 10-Band Parametric EQ.
// Copyright (C) 2025-2026 KB2UKA and contributors.
//
// 10 cascaded biquad peaking filters per the Audio EQ Cookbook
// (Bristow-Johnson, https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html).
// Each band: frequency (Hz), gain (dB), Q. Coefficients re-derived
// only when the operator touches a parameter; per-sample inner loop
// is just 5 multiplies + 4 adds per band.
//
// v0.2.0 additions on top of the v0.1.0 cascade:
//   * Input + Output gain stages (-24 dB … +24 dB each, default 0 dB).
//     Bypass-still-applies-gain semantics so the plugin can be used
//     as a pure gain stage with the EQ cascade off.
//   * Live FFT spectrum analyser. Maintains running 2048-sample
//     buffers of input + output, Hann-windows and FFTs every 1024
//     new samples (50 % overlap), log-bins to 256 bins over
//     20 Hz – 20 kHz. Bin arrays are read by GET /api/plugins/{id}/spectrum.

namespace Openhpsdr.Zeus.Samples.Eq;

/// <summary>
/// 10-band cascaded parametric EQ. All 10 bands are peaking filters;
/// operators get the most flexible shape. HP/LP shelves can ride at
/// bands 1 and 10 by dialing Q low and pulling gain up/down, or by
/// extending this DSP to support filter-type-per-band in a later
/// release.
///
/// Realtime contract identical to <c>CompressorDsp</c>: no allocate,
/// no lock, no IO inside <see cref="Process"/>.
/// </summary>
public sealed class EqDsp
{
    public const int BandCount = 10;

    // Default band frequencies — voice-focused logarithmic spread from
    // 80 Hz (low-mids / chest) to 6 kHz (presence / "air"). Operator can
    // move each band freely; these are just initial positions.
    public static readonly float[] DefaultFrequencies =
    {
        80f, 150f, 300f, 500f, 800f, 1200f, 1800f, 2700f, 4000f, 6000f,
    };

    // Per-band state.
    public sealed class Band
    {
        public float FrequencyHz = 1000f;
        public float GainDb      = 0f;
        public float Q           = 1.0f;

        // Biquad coefficients (recomputed when params change).
        public float B0, B1, B2, A1, A2;
        // Direct-form II transposed state (one delay per band — minimal
        // numerical denormal risk vs Direct-Form-I in float32).
        public float Z1, Z2;
    }

    public readonly Band[] Bands = new Band[BandCount];

    public bool Bypass { get; set; } = false;

    /// <summary>
    /// Input gain, applied to incoming samples BEFORE the band cascade.
    /// Range clamped to [-24, +24] dB on assignment. Default 0 dB.
    /// The IN meter (<see cref="LastInputPeakDb"/>) reads the
    /// post-input-gain signal so the operator sees what the DSP
    /// actually receives — not the raw mic level upstream.
    /// </summary>
    public float InputGainDb
    {
        get => _inputGainDb;
        set => _inputGainDb = MathF.Max(-24f, MathF.Min(24f, value));
    }
    private float _inputGainDb = 0f;

    /// <summary>
    /// Output gain, applied AFTER the band cascade (or after bypass
    /// fast-path). Range clamped to [-24, +24] dB. Default 0 dB. The
    /// OUT meter reads the post-output-gain signal so it shows what
    /// hits the next plugin in the chain.
    /// </summary>
    public float OutputGainDb
    {
        get => _outputGainDb;
        set => _outputGainDb = MathF.Max(-24f, MathF.Min(24f, value));
    }
    private float _outputGainDb = 0f;

    // Last-block readbacks for metering (input peak, output peak).
    public float LastInputPeakDb  { get; private set; } = MinDb;
    public float LastOutputPeakDb { get; private set; } = MinDb;

    private float _sampleRate = 48000f;
    private bool _coefDirty = true;

    // -------------------------------------------------------------------
    // Live spectrum analyser state.
    //
    // We keep two N=2048 ring buffers (input + output), accumulate new
    // samples into them, and every HOP=1024 new samples trigger a Hann-
    // windowed FFT. Magnitudes are converted to dB and log-binned to
    // BinCount=256 bins covering 20 Hz – 20 kHz. The bin arrays are
    // read by the REST handler; we deliberately don't lock — single
    // writer (audio thread), single reader (request thread), a torn
    // read shows at most one block of visual flicker.
    // -------------------------------------------------------------------

    public const int FftSize = 2048;
    public const int FftHop  = 1024;
    public const int BinCount = 256;
    public const float SpectrumFMinHz = 20f;
    public const float SpectrumFMaxHz = 20_000f;
    public const float SpectrumDbFloor = -120f;

    private readonly float[] _inHistory  = new float[FftSize];
    private readonly float[] _outHistory = new float[FftSize];
    private int _inHistoryPos = 0;   // write cursor, modulo FftSize
    private int _outHistoryPos = 0;
    private int _samplesSinceLastFft = 0;

    // Pre-computed Hann window (built lazily on first Initialize so a
    // ctor-time computation isn't required).
    private readonly float[] _hann = new float[FftSize];

    // Pre-allocated FFT workspaces — the realtime loop must not GC.
    private readonly float[] _fftRe = new float[FftSize];
    private readonly float[] _fftIm = new float[FftSize];

    // Log-bin → FFT-bin mapping. binToFftLow[k] is the first FFT bin
    // index that falls into log-bin k; binToFftHigh[k] is the
    // exclusive upper bound. Pre-computed at Initialize time.
    private readonly int[] _binToFftLow  = new int[BinCount];
    private readonly int[] _binToFftHigh = new int[BinCount];

    /// <summary>
    /// Last computed input-side spectrum, in dB per bin (BinCount bins
    /// covering 20 Hz – 20 kHz log-spaced). <see cref="MinDb"/>-floor
    /// when no signal. Realtime audio thread updates this; REST
    /// handler reads it without locking.
    /// </summary>
    public readonly float[] LastInputSpectrumDb  = new float[BinCount];

    /// <summary>Last computed output-side spectrum, dB per bin. See <see cref="LastInputSpectrumDb"/>.</summary>
    public readonly float[] LastOutputSpectrumDb = new float[BinCount];

    public EqDsp()
    {
        for (int i = 0; i < BandCount; i++)
            Bands[i] = new Band { FrequencyHz = DefaultFrequencies[i], GainDb = 0f, Q = 1.0f };

        // Floor the spectrum arrays so an immediate GET before any audio
        // arrives doesn't return uninitialised noise.
        for (int i = 0; i < BinCount; i++)
        {
            LastInputSpectrumDb[i]  = SpectrumDbFloor;
            LastOutputSpectrumDb[i] = SpectrumDbFloor;
        }
    }

    public void Initialize(int sampleRateHz)
    {
        if (sampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "sample rate must be positive");
        _sampleRate = sampleRateHz;
        _coefDirty = true;
        BuildHannWindow();
        BuildLogBinMap();
        Reset();
    }

    /// <summary>Zero the per-band delay state. Safe to call from the audio thread.</summary>
    public void Reset()
    {
        for (int i = 0; i < BandCount; i++)
        {
            Bands[i].Z1 = 0f;
            Bands[i].Z2 = 0f;
        }
        Array.Clear(_inHistory);
        Array.Clear(_outHistory);
        _inHistoryPos = 0;
        _outHistoryPos = 0;
        _samplesSinceLastFft = 0;
        LastInputPeakDb = MinDb;
        LastOutputPeakDb = MinDb;
    }

    /// <summary>Mark coefficients dirty — recomputed at the start of the next Process block.</summary>
    public void MarkParamsDirty() => _coefDirty = true;

    private void BuildHannWindow()
    {
        // Hann: w[n] = 0.5 * (1 - cos(2π n / (N-1)))
        for (int n = 0; n < FftSize; n++)
        {
            _hann[n] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * n / (FftSize - 1)));
        }
    }

    private void BuildLogBinMap()
    {
        // Map BinCount log-spaced output bins to the FFT's linear bins.
        // FFT bin k corresponds to frequency k * fs / FftSize. We want
        // log-spaced bin EDGES from SpectrumFMinHz to SpectrumFMaxHz,
        // then each output-bin averages the FFT magnitudes that fall
        // inside its edge pair.
        float fNyquist = _sampleRate * 0.5f;
        float fMax = MathF.Min(SpectrumFMaxHz, fNyquist);
        float fMin = SpectrumFMinHz;
        float lnLo = MathF.Log(fMin);
        float lnHi = MathF.Log(fMax);
        float binWidthHz = _sampleRate / FftSize;
        for (int k = 0; k < BinCount; k++)
        {
            float t0 = (float)k / BinCount;
            float t1 = (float)(k + 1) / BinCount;
            float fLow  = MathF.Exp(lnLo + t0 * (lnHi - lnLo));
            float fHigh = MathF.Exp(lnLo + t1 * (lnHi - lnLo));
            int kLow  = (int)MathF.Floor(fLow / binWidthHz);
            int kHigh = (int)MathF.Ceiling(fHigh / binWidthHz);
            if (kLow < 1) kLow = 1;                      // skip DC
            if (kHigh > FftSize / 2) kHigh = FftSize / 2;
            if (kHigh <= kLow) kHigh = kLow + 1;
            _binToFftLow[k] = kLow;
            _binToFftHigh[k] = kHigh;
        }
    }

    /// <summary>
    /// Recompute peaking-EQ biquad coefficients for all 10 bands using
    /// the standard Audio EQ Cookbook formulas:
    ///   A   = √(10^(gainDb/20))
    ///   ω0  = 2π · f0 / fs
    ///   α   = sin(ω0) / (2Q)
    ///   b0  = 1 + α·A     b1 = -2·cos(ω0)     b2 = 1 - α·A
    ///   a0  = 1 + α/A     a1 = -2·cos(ω0)     a2 = 1 - α/A
    ///   (then normalise everything by a0)
    /// </summary>
    private void RecomputeCoefficients()
    {
        for (int i = 0; i < BandCount; i++)
        {
            var b = Bands[i];
            float f0 = MathF.Max(10f, MathF.Min(_sampleRate * 0.45f, b.FrequencyHz));
            float gainDb = b.GainDb;
            float q = MathF.Max(0.1f, b.Q);

            float A = MathF.Pow(10f, gainDb / 40f);  // sqrt(10^(gainDb/20))
            float w0 = 2f * MathF.PI * f0 / _sampleRate;
            float cosW0 = MathF.Cos(w0);
            float alpha = MathF.Sin(w0) / (2f * q);

            float b0 = 1f + alpha * A;
            float b1 = -2f * cosW0;
            float b2 = 1f - alpha * A;
            float a0 = 1f + alpha / A;
            float a1 = -2f * cosW0;
            float a2 = 1f - alpha / A;

            // Normalise by a0.
            float inv = 1f / a0;
            b.B0 = b0 * inv;
            b.B1 = b1 * inv;
            b.B2 = b2 * inv;
            b.A1 = a1 * inv;
            b.A2 = a2 * inv;
        }
        _coefDirty = false;
    }

    /// <summary>
    /// Process one block. <paramref name="input"/> and <paramref name="output"/>
    /// MUST be the same length. In-place processing acceptable.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("input and output spans must be the same length");
        if (input.Length == 0) return;

        // Convert gain dB → linear once per block; applied per sample
        // below. Negligible MathF.Pow cost amortised over a whole block.
        float inGainLin  = DbToLinear(_inputGainDb);
        float outGainLin = DbToLinear(_outputGainDb);

        if (Bypass)
        {
            // Bypass fast-path: skip the band cascade BUT still apply
            // input + output gain so the plugin can be used as a pure
            // gain stage when bypassed. Meters reflect post-gain
            // signal on both ends.
            float ipeak = MinDb;
            float opeak = MinDb;
            for (int n = 0; n < input.Length; n++)
            {
                float x = input[n] * inGainLin;
                float y = x * outGainLin;
                output[n] = y;
                float xa = LinearToDb(MathF.Abs(x));
                float ya = LinearToDb(MathF.Abs(y));
                if (xa > ipeak) ipeak = xa;
                if (ya > opeak) opeak = ya;
                AccumulateForFft(x, y);
            }
            LastInputPeakDb = ipeak;
            LastOutputPeakDb = opeak;
            MaybeComputeSpectra();
            return;
        }

        if (_coefDirty) RecomputeCoefficients();

        float inputPeakDb = MinDb;
        float outputPeakDb = MinDb;

        // Process sample-by-sample, cascading all 10 bands. Direct-Form-II
        // Transposed structure per band — two state vars (Z1, Z2), four
        // multiplies + two adds for the input path, two multiplies + two
        // adds for the feedback path = ~6 mul-adds per band per sample.
        // 10 bands × ~6 ops × 1024 samples = ~60k ops per P1 TX block,
        // well under the per-tick CPU budget.
        for (int n = 0; n < input.Length; n++)
        {
            // Input gain stage. xMeter = post-input-gain sample feeds
            // the IN meter and the input-side FFT history.
            float xMeter = input[n] * inGainLin;
            float xAbsDb = LinearToDb(MathF.Abs(xMeter));
            if (xAbsDb > inputPeakDb) inputPeakDb = xAbsDb;

            float y = xMeter;
            for (int i = 0; i < BandCount; i++)
            {
                var b = Bands[i];
                // Transposed Direct Form II:
                //   y[n] = b0 · x[n] + Z1
                //   Z1   = b1 · x[n] - a1 · y[n] + Z2
                //   Z2   = b2 · x[n] - a2 · y[n]
                float yi = b.B0 * y + b.Z1;
                b.Z1 = b.B1 * y - b.A1 * yi + b.Z2;
                b.Z2 = b.B2 * y - b.A2 * yi;
                y = yi;
            }

            // Output gain stage. yMeter feeds the OUT meter and the
            // output-side FFT history.
            float yMeter = y * outGainLin;
            output[n] = yMeter;
            float yAbsDb = LinearToDb(MathF.Abs(yMeter));
            if (yAbsDb > outputPeakDb) outputPeakDb = yAbsDb;

            AccumulateForFft(xMeter, yMeter);
        }

        LastInputPeakDb = inputPeakDb;
        LastOutputPeakDb = outputPeakDb;
        MaybeComputeSpectra();
    }

    /// <summary>
    /// Append one (input, output) sample pair to the ring history
    /// buffers. Branch-free pointer increment via masked wrap (FftSize
    /// is a power of two; the runtime const lets the JIT fold the
    /// modulo into an AND).
    /// </summary>
    private void AccumulateForFft(float xIn, float yOut)
    {
        _inHistory[_inHistoryPos] = xIn;
        _outHistory[_outHistoryPos] = yOut;
        _inHistoryPos = (_inHistoryPos + 1) & (FftSize - 1);
        _outHistoryPos = (_outHistoryPos + 1) & (FftSize - 1);
        _samplesSinceLastFft++;
    }

    /// <summary>
    /// If we've accumulated at least <see cref="FftHop"/> new samples
    /// since the last FFT, run both input + output FFTs and refresh
    /// the public spectrum arrays. Called once per Process block (NOT
    /// per sample) so the cost is amortised over hundreds of samples.
    /// </summary>
    private void MaybeComputeSpectra()
    {
        if (_samplesSinceLastFft < FftHop) return;
        _samplesSinceLastFft = 0;
        ComputeOneSpectrum(_inHistory,  _inHistoryPos,  LastInputSpectrumDb);
        ComputeOneSpectrum(_outHistory, _outHistoryPos, LastOutputSpectrumDb);
    }

    /// <summary>
    /// Compute one Hann-windowed FFT from a ring history buffer and
    /// log-bin it into the supplied dB-bin destination array. The
    /// ring's current write cursor is the start of the "oldest" sample
    /// in the FFT window; the FFT walks N samples forward from there
    /// (wrapping modulo N).
    /// </summary>
    private void ComputeOneSpectrum(float[] history, int writeCursor, float[] dbBins)
    {
        // Pack the most-recent FftSize samples into _fftRe with the
        // Hann window applied. Most-recent means: the LAST sample in
        // the FFT window is at index (writeCursor - 1) mod FftSize;
        // the FIRST is at writeCursor. Walk N forward from writeCursor.
        int mask = FftSize - 1;
        for (int i = 0; i < FftSize; i++)
        {
            int idx = (writeCursor + i) & mask;
            _fftRe[i] = history[idx] * _hann[i];
            _fftIm[i] = 0f;
        }

        RealFft.Forward(_fftRe, _fftIm, FftSize);

        // Bin magnitudes (avg over the FFT-bin range that falls in
        // each output bin) and convert to dB.
        // Magnitude scaling: divide by N/2 so a pure sine at full scale
        // shows ≈ 0 dBFS. Hann coherent gain ≈ 0.5; we compensate by
        // dividing by (N/2 * 0.5) = N/4. Close enough for visualisation.
        float magScale = 4f / FftSize;
        for (int k = 0; k < BinCount; k++)
        {
            int kLow = _binToFftLow[k];
            int kHigh = _binToFftHigh[k];
            float sumMag = 0f;
            int count = 0;
            for (int kk = kLow; kk < kHigh; kk++)
            {
                float re = _fftRe[kk];
                float im = _fftIm[kk];
                float mag = MathF.Sqrt(re * re + im * im);
                sumMag += mag;
                count++;
            }
            float avgMag = count > 0 ? (sumMag / count) * magScale : 0f;
            float db = avgMag > 1e-10f
                ? 20f * MathF.Log10(avgMag)
                : SpectrumDbFloor;
            if (db < SpectrumDbFloor) db = SpectrumDbFloor;
            dbBins[k] = db;
        }
    }

    // -----------------------------------------------------------------------
    // dB helpers — same shape as CompressorDsp.
    // -----------------------------------------------------------------------
    internal const float MinDb = -200f;

    internal static float LinearToDb(float linear)
    {
        if (linear <= 1e-10f) return MinDb;
        return MathF.Log(linear) * 8.685889638065035f; // 20 / ln(10)
    }

    internal static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}
