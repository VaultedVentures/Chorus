using System;

namespace Chorus.Core.WakeWord;

/// <summary>
/// Incremental banded DTW of one template against a stream (mirrors
/// tests/wakeword_lib.py OnlineDtw exactly).
///
/// One call to <see cref="Step"/> per 10 ms hop. The returned value is the
/// cost of the best alignment of the FULL template ending at the current hop,
/// normalized by template length — <see cref="float.PositiveInfinity"/> when
/// the current hop is outside the Sakoe-Chiba band (the template cannot have
/// finished yet, or the rate difference is implausible). The band is what
/// stops a PARTIAL phrase from stretching to fit the whole template and
/// false-triggering mid-utterance.
/// </summary>
public sealed class OnlineDtw
{
    private readonly float[][] _template; // [frame][13]
    private readonly int _n;
    private readonly int _band;
    private float[]? _prev; // previous column (inf-padded to template length)
    private int _j = -1;
    private float _endCost = float.PositiveInfinity;

    public OnlineDtw(float[][] template, int band = WakeWordParams.DtwBand)
    {
        _template = template;
        _n = template.Length;
        _band = band;
    }

    public int Length => _n;

    public void Reset()
    {
        _prev = null;
        _j = -1;
        _endCost = float.PositiveInfinity;
    }

    /// <summary>
    /// Advance one hop with the stream's MFCC vector; returns the normalized
    /// end cost (possibly +inf while the template cannot have finished).
    /// </summary>
    public float Step(ReadOnlySpan<float> x)
    {
        int j = _j + 1;
        int lo = Math.Max(0, j - _band);
        int hi = Math.Min(_n - 1, j + _band);

        if (lo > hi)
        {
            // the template can no longer end within the band — the phrase, if
            // it was said, is long past. Restart fresh so a LATER occurrence
            // can still match (the all-inf columns are equivalent anyway).
            Reset();
            return Step(x);
        }

        var cur = new float[_n];
        Array.Fill(cur, float.PositiveInfinity);

        if (_prev is null)
        {
            // first stream column: only i==0 (within band) is reachable
            cur[0] = Dist(_template[0], x);
            for (int i = 1; i <= hi; i++)
                cur[i] = Dist(_template[i], x) + cur[i - 1];
        }
        else
        {
            var prev = _prev;
            for (int i = lo; i <= hi; i++)
            {
                float best = float.PositiveInfinity;
                if (i > 0 && prev[i - 1] < best) best = prev[i - 1];   // both advance
                if (prev[i] < best) best = prev[i];                     // stream waits (slow speech)
                if (i > 0 && cur[i - 1] < best) best = cur[i - 1];      // template waits (fast speech)
                cur[i] = Dist(_template[i], x) + best;
            }
        }

        _prev = cur;
        _j = j;
        _endCost = Math.Abs((_n - 1) - j) <= _band
            ? cur[_n - 1] / _n
            : float.PositiveInfinity;
        return _endCost;
    }

    /// <summary>Number of hops consumed (including the current one).</summary>
    public int Hops => _j + 1;

    /// <summary>Euclidean distance between two L2-normalized MFCC vectors (0..2).</summary>
    public static float Dist(float[] a, ReadOnlySpan<float> b)
    {
        double acc = 0.0;
        for (int k = 0; k < WakeWordParams.NMfcc; k++)
        {
            double d = a[k] - b[k];
            acc += d * d;
        }
        return (float)Math.Sqrt(acc);
    }
}
