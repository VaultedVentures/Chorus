using System;

namespace Chorus.Core.WakeWord;

/// <summary>
/// Shared DSP parameters for the CHORUS wake-word matcher. These are the
/// cross-language CONTRACT with tests/wakeword_lib.py — changing one side
/// without the other breaks the packaged templates. See wakeword_lib.py for
/// the reference implementation and the exact formulas.
/// </summary>
public static class WakeWordParams
{
    public const int SampleRate = 16000;
    public const int FftSize = 512;
    public const int WinLen = 400;          // 25 ms
    public const int Hop = 160;             // 10 ms
    public const int NMels = 26;
    public const double FMin = 300.0;
    public const double FMax = 8000.0;      // Nyquist at 16 kHz
    public const int NMfcc = 13;            // c1..c13 (c0 energy dropped)
    public const int MFccDim = NMfcc;

    /// <summary>Hops quieter than this RMS are not match-checked.</summary>
    public const double EnergyFloor = 150.0;

    /// <summary>
    /// A hop louder than this counts as audible for onset detection. Kept
    /// well below <see cref="EnergyFloor"/> so the very start of a phrase
    /// counts — the onset reset must not amputate the phrase's first frames.
    /// </summary>
    public const double OnsetFloor = 60.0;

    /// <summary>
    /// A new utterance starts only after this many consecutive quiet hops
    /// (25 x 10 ms = 250 ms). Shorter dips — e.g. the natural pause between
    /// "hey" and "chorus" — stay inside the current alignment.
    /// </summary>
    public const int MinSilenceHops = 25;

    /// <summary>A match is accepted only if it ended within this many hops (400 ms).</summary>
    public const int FreshnessHops = 40;

    /// <summary>Sakoe-Chiba band (hops either side of the diagonal).</summary>
    public const int DtwBand = 15;

    // Sensitivity mapping (threshold = ThreshLow + s*(ThreshHigh - ThreshLow)):
    //   s=0 (least sensitive) -> 0.30  same-voice, clear speech only
    //   s=1 (most sensitive)  -> 0.40  every measured true positive, some near-misses
    //   s=0.5 (default)       -> 0.35  all true <=0.281, near-misses >=0.356
    public const double ThreshLow = 0.30;
    public const double ThreshHigh = 0.40;

    public static double SensitivityToThreshold(float sensitivity)
    {
        double s = Math.Clamp(sensitivity, 0f, 1f);
        return ThreshLow + s * (ThreshHigh - ThreshLow);
    }

    public static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);

    public static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}

/// <summary>
/// 16 kHz MFCC front-end, byte-for-byte equivalent to the Python reference
/// (tests/wakeword_lib.py): Hamming 25 ms window, 512-pt FFT, 26 sum-normalized
/// mel filters (300-8000 Hz), ln, orthonormal DCT-II, c1..c13, L2 normalize.
/// The unit tests pin this against the Python-computed fixture.
/// </summary>
public sealed class MfccExtractor
{
    private readonly double[] _hamming;
    private readonly double[][] _filterbank; // [26][257]
    private readonly double[] _re = new double[WakeWordParams.FftSize];
    private readonly double[] _im = new double[WakeWordParams.FftSize];

    public MfccExtractor()
    {
        int win = WakeWordParams.WinLen;
        _hamming = new double[win];
        for (int n = 0; n < win; n++)
            _hamming[n] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * n / (win - 1));

        // mel filterbank: triangular filters on the mel scale, sum-normalized
        int nFreqs = WakeWordParams.FftSize / 2 + 1;
        int nMels = WakeWordParams.NMels;
        _filterbank = new double[nMels][];
        double[] fftFreqs = new double[nFreqs];
        for (int j = 0; j < nFreqs; j++)
            fftFreqs[j] = WakeWordParams.FMax * j / (nFreqs - 1); // linspace(0, fs/2, 257)

        double[] melPts = new double[nMels + 2];
        double melMin = WakeWordParams.HzToMel(WakeWordParams.FMin);
        double melMax = WakeWordParams.HzToMel(WakeWordParams.FMax);
        for (int i = 0; i < nMels + 2; i++)
            melPts[i] = melMin + (melMax - melMin) * i / (nMels + 1);
        double[] hzPts = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            hzPts[i] = WakeWordParams.MelToHz(melPts[i]);

        for (int i = 0; i < nMels; i++)
        {
            double lo = hzPts[i], mid = hzPts[i + 1], hi = hzPts[i + 2];
            double[] row = new double[nFreqs];
            double sum = 0.0;
            for (int j = 0; j < nFreqs; j++)
            {
                double f = fftFreqs[j];
                double up = (f - lo) / (mid - lo);
                double dn = (hi - f) / (hi - mid);
                row[j] = Math.Max(0.0, Math.Min(up, dn));
                sum += row[j];
            }
            if (sum > 0.0)
                for (int j = 0; j < nFreqs; j++) row[j] /= sum;
            _filterbank[i] = row;
        }
    }

    /// <summary>
    /// One 400-sample window of 16 kHz int16 PCM -> (13,) L2-normalized
    /// c1..c13 vector. The caller must pass exactly WinLen samples.
    /// </summary>
    public float[] Compute(ReadOnlySpan<short> window)
    {
        if (window.Length != WakeWordParams.WinLen)
            throw new ArgumentException($"window must be {WakeWordParams.WinLen} samples", nameof(window));

        // window * hamming, into the FFT buffers
        for (int i = 0; i < WakeWordParams.WinLen; i++)
        {
            _re[i] = window[i] * _hamming[i];
            _im[i] = 0.0;
        }
        for (int i = WakeWordParams.WinLen; i < WakeWordParams.FftSize; i++)
            _re[i] = _im[i] = 0.0;

        FftRadix2(_re, _im);

        // mel energies: power spectrum (rfft bins 0..256) through the filterbank
        int nFreqs = WakeWordParams.FftSize / 2 + 1;
        Span<double> power = stackalloc double[nFreqs];
        for (int j = 0; j < nFreqs; j++)
            power[j] = _re[j] * _re[j] + _im[j] * _im[j];

        Span<double> mel = stackalloc double[WakeWordParams.NMels];
        for (int i = 0; i < WakeWordParams.NMels; i++)
        {
            double[] row = _filterbank[i];
            double acc = 0.0;
            for (int j = 0; j < nFreqs; j++)
                acc += row[j] * power[j];
            mel[i] = Math.Log(acc + 1e-10);
        }

        // orthonormal DCT-II over the 26 log-mel energies, keep c1..c13
        Span<double> c = stackalloc double[WakeWordParams.NMels];
        DctOrtho(mel, c);

        var mfcc = new float[WakeWordParams.NMfcc];
        double norm = 0.0;
        for (int k = 0; k < WakeWordParams.NMfcc; k++)
        {
            double v = c[k + 1];
            mfcc[k] = (float)v;
            norm += v * v;
        }
        norm = Math.Sqrt(norm);
        if (norm > 1e-8)
            for (int k = 0; k < WakeWordParams.NMfcc; k++)
                mfcc[k] = (float)(mfcc[k] / norm);
        return mfcc;
    }

    /// <summary>RMS of a 400-sample window (matches the Python hop_rms).</summary>
    public static double WindowRms(ReadOnlySpan<short> window)
    {
        double sum = 0.0;
        foreach (short s in window)
            sum += (double)s * s;
        return Math.Sqrt(sum / window.Length);
    }

    private static void DctOrtho(ReadOnlySpan<double> x, Span<double> outC)
    {
        int n = x.Length;
        double invN = 1.0 / n;
        for (int k = 0; k < n; k++)
        {
            double acc = 0.0;
            double phase = Math.PI * k / (2.0 * n);
            for (int i = 0; i < n; i++)
                acc += x[i] * Math.Cos(phase * (2.0 * i + 1));
            outC[k] = acc * (k == 0 ? Math.Sqrt(invN) : Math.Sqrt(2.0 * invN));
        }
    }

    /// <summary>Iterative radix-2 Cooley-Tukey FFT (in place, no normalization).</summary>
    private static void FftRadix2(double[] re, double[] im)
    {
        int n = WakeWordParams.FftSize;
        // bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
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
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                int half = len / 2;
                for (int k = 0; k < half; k++)
                {
                    double uRe = re[i + k], uIm = im[i + k];
                    double vRe = re[i + k + half] * curRe - im[i + k + half] * curIm;
                    double vIm = re[i + k + half] * curIm + im[i + k + half] * curRe;
                    re[i + k] = uRe + vRe;
                    im[i + k] = uIm + vIm;
                    re[i + k + half] = uRe - vRe;
                    im[i + k + half] = uIm - vIm;
                    double nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }
}
