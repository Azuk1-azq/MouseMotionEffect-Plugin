using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using YukkuriMovieMaker.Commons;

namespace MouseMotion;

/// <summary>
/// 軌跡(折れ線)。座標は画面中心を原点とした論理ピクセル。
/// 保存形式は "x,y;x,y;..." の文字列。
/// </summary>
internal sealed class MotionPath
{
    public static readonly MotionPath Empty = new(Array.Empty<Vector2>());

    private readonly Vector2[] points;
    private readonly double[] cumulative; // 各点までの累積長
    private readonly double total;

    public MotionPath(IEnumerable<Vector2> source)
    {
        var list = new List<Vector2>();
        foreach (var p in source)
        {
            // 連続する重複点は除く(0長の線分を作らない)
            if (list.Count == 0 || Vector2.Distance(list[^1], p) > 1e-3f)
                list.Add(p);
        }
        points = list.ToArray();
        cumulative = new double[points.Length];
        for (int i = 1; i < points.Length; i++)
            cumulative[i] = cumulative[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        total = points.Length > 0 ? cumulative[^1] : 0;
    }

    public IReadOnlyList<Vector2> Points => points;
    public double Length => total;
    public bool IsEmpty => points.Length < 2 || total <= 0;

    public static MotionPath Parse(string data)
    {
        if (string.IsNullOrWhiteSpace(data)) return Empty;
        var list = new List<Vector2>();
        foreach (var token in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = token.Split(',');
            if (xy.Length != 2) continue;
            if (float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                float.IsFinite(x) && float.IsFinite(y))
            {
                list.Add(new Vector2(x, y));
            }
        }
        var path = new MotionPath(list);
        return path.IsEmpty ? Empty : path;
    }

    public static string Serialize(IEnumerable<Vector2> pts)
    {
        return string.Join(';', pts.Select(p =>
            p.X.ToString("0.##", CultureInfo.InvariantCulture) + "," +
            p.Y.ToString("0.##", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// 弧長の進行割合 p(0..1)に対応する位置。
    /// Back などで p が範囲外になった場合は、端の線分の向きに延長する。
    /// </summary>
    public Vector2 PositionAt(double p)
    {
        if (IsEmpty) return points.Length > 0 ? points[0] : Vector2.Zero;

        double d = p * total;
        if (d <= 0)
            return points[0] + Direction(0) * (float)d;
        if (d >= total)
            return points[^1] + Direction(points.Length - 2) * (float)(d - total);

        int lo = 0, hi = points.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (cumulative[mid] <= d) lo = mid; else hi = mid;
        }
        double segLen = cumulative[hi] - cumulative[lo];
        float u = (float)((d - cumulative[lo]) / segLen);
        return Vector2.Lerp(points[lo], points[hi], u);
    }

    /// <summary>開始点からの変位。</summary>
    public Vector2 DisplacementAt(double p)
    {
        if (IsEmpty) return Vector2.Zero;
        return PositionAt(p) - points[0];
    }

    private Vector2 Direction(int segment)
    {
        return Vector2.Normalize(points[segment + 1] - points[segment]);
    }
}

internal readonly record struct MotionSample(double TimeMs, Vector2 Position);

/// <summary>
/// 記録(時刻つきの座標列)の保存形式。
///   新形式: "x,y,t;x,y,t;..."  (t は記録開始からのミリ秒)
///   旧形式: "x,y;x,y;..."       (時刻なし。軌跡だけ)
/// </summary>
internal static class MotionRecording
{
    private const int MaxStored = 300;

    public static string Serialize(IReadOnlyList<MotionSample> samples)
    {
        if (samples.Count == 0) return "";

        // 多すぎるときは等間隔に間引く(各点が時刻を持つので、速さの変化は保たれる)
        var list = new List<MotionSample>();
        if (samples.Count <= MaxStored)
        {
            list.AddRange(samples);
        }
        else
        {
            for (int i = 0; i < MaxStored; i++)
            {
                int idx = (int)Math.Round(i * (samples.Count - 1) / (double)(MaxStored - 1));
                list.Add(samples[idx]);
            }
        }

        double t0 = list[0].TimeMs;
        return string.Join(';', list.Select(s =>
            s.Position.X.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            s.Position.Y.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            (s.TimeMs - t0).ToString("0.#", CultureInfo.InvariantCulture)));
    }

    /// <summary>hasTime が false のときは旧形式(時刻なし)。</summary>
    public static List<MotionSample> Parse(string data, out bool hasTime)
    {
        var list = new List<MotionSample>();
        hasTime = false;
        if (string.IsNullOrWhiteSpace(data)) return list;

        bool anyTime = false;
        foreach (var token in data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(',');
            if (parts.Length < 2) continue;
            if (!TryFloat(parts[0], out var x) || !TryFloat(parts[1], out var y)) continue;

            double t = list.Count; // 旧形式では使わない
            if (parts.Length >= 3 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var tt)
                && double.IsFinite(tt))
            {
                t = tt;
                anyTime = true;
            }
            list.Add(new MotionSample(t, new Vector2(x, y)));
        }
        hasTime = anyTime;
        return list;
    }

    private static bool TryFloat(string s, out float v)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && float.IsFinite(v);
}

/// <summary>手ぶれ補正の強さ。</summary>
public enum StabilizationLevel
{
    [Display(Name = "オフ")] Off,
    [Display(Name = "弱")] Weak,
    [Display(Name = "標準")] Normal,
    [Display(Name = "強")] Strong,
    [Display(Name = "最強")] Max,
}

/// <summary>動きを再生する時間の長さ。</summary>
public enum MotionPlayback
{
    [Display(Name = "タイムライン終了まで")] FitItem,
    [Display(Name = "通常速度(記録した時間)")] Realtime,
}

/// <summary>動きの速さの変化(イージング)をどこから取るか。</summary>
public enum MotionEasingSource
{
    [Display(Name = "記録そのまま")] Recorded,
    [Display(Name = "プリセット")] Preset,
}

/// <summary>記録から求めた、軌跡・実際の速さの変化・最も近いプリセット。</summary>
internal sealed class MotionModel
{
    public static readonly MotionModel Empty =
        new(MotionPath.Empty, null, EasingType.Linear, EasingMode.In, Array.Empty<Vector2>(), 0);

    private readonly double[] timing;   // 一様な時刻でのドラッグの進行(0..1)。時刻情報がなければ null
    private readonly double[] slopes;

    public MotionModel(MotionPath path, double[] timing, EasingType fitType, EasingMode fitMode, IReadOnlyList<Vector2> rawPoints, double durationMs)
    {
        DurationMs = durationMs;
        Path = path;
        this.timing = timing;
        slopes = timing != null ? ComputeSlopes(timing) : null;
        FitType = fitType;
        FitMode = fitMode;
        RawPoints = rawPoints;
    }

    /// <summary>手ぶれ補正と簡略化のあとの軌跡。</summary>
    public MotionPath Path { get; }

    /// <summary>補正前の軌跡(表示用)。</summary>
    public IReadOnlyList<Vector2> RawPoints { get; }

    /// <summary>記録の速さの変化に最も近いプリセット。</summary>
    public EasingType FitType { get; }
    public EasingMode FitMode { get; }

    public bool HasTiming => timing != null;

    /// <summary>ドラッグにかかった時間(ミリ秒)。時刻情報がなければ 0。</summary>
    public double DurationMs { get; }

    /// <summary>アイテム内の時刻 t(0..1)における、記録どおりの進行割合(0..1)。</summary>
    public double TimingAt(double t)
    {
        if (timing == null) return Math.Clamp(t, 0, 1);

        int n = timing.Length;
        double x = Math.Clamp(t, 0, 1) * (n - 1);
        int i = Math.Min((int)x, n - 2);
        double u = x - i;
        double u2 = u * u, u3 = u2 * u;

        double h00 = 2 * u3 - 3 * u2 + 1;
        double h10 = u3 - 2 * u2 + u;
        double h01 = -2 * u3 + 3 * u2;
        double h11 = u3 - u2;
        double v = h00 * timing[i] + h10 * slopes[i] + h01 * timing[i + 1] + h11 * slopes[i + 1];
        return Math.Clamp(v, 0, 1);
    }

    // Fritsch–Carlson 法の単調な 3 次エルミート補間(進行が行き過ぎたり戻ったりしない)
    private static double[] ComputeSlopes(double[] g)
    {
        int n = g.Length;
        var delta = new double[n - 1];
        for (int k = 0; k < n - 1; k++) delta[k] = g[k + 1] - g[k];

        var m = new double[n];
        m[0] = delta[0];
        m[n - 1] = delta[n - 2];
        for (int k = 1; k < n - 1; k++)
            m[k] = delta[k - 1] * delta[k] <= 0 ? 0 : (delta[k - 1] + delta[k]) / 2;

        for (int k = 0; k < n - 1; k++)
        {
            if (Math.Abs(delta[k]) < 1e-12)
            {
                m[k] = 0;
                m[k + 1] = 0;
                continue;
            }
            double a = m[k] / delta[k];
            double b = m[k + 1] / delta[k];
            double s = a * a + b * b;
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[k] = tau * a * delta[k];
                m[k + 1] = tau * b * delta[k];
            }
        }
        return m;
    }
}

/// <summary>
/// ドラッグの記録から
///   ・手ぶれ補正した軌跡(折れ線に簡略化)
///   ・実際の速さの変化(時刻ごとの進行)
///   ・それに最も近いプリセットのイージング
/// を求める。
/// </summary>
internal static class MotionBuilder
{
    private const float MinLength = 4f;      // これより短い移動は無視(論理px)
    private const int MaxWaypoints = 24;
    private const int TimingSamples = 96;    // 記録どおりの速さの変化を持つ点の数
    private const int FitSamples = 48;
    private const double ResampleStepMs = 8; // 手ぶれ補正のときの時間刻み

    private static readonly (EasingType Type, EasingMode Mode)[] Candidates = BuildCandidates();

    private static (EasingType, EasingMode)[] BuildCandidates()
    {
        // 先頭の Linear が基準。他は明確に当てはまりが良いときだけ採用する。
        var list = new List<(EasingType, EasingMode)> { (EasingType.Linear, EasingMode.In) };
        var types = new[]
        {
            EasingType.Sine, EasingType.Quad, EasingType.Cubic, EasingType.Quart,
            EasingType.Quint, EasingType.Expo, EasingType.Circ, EasingType.Back,
        };
        var modes = new[] { EasingMode.In, EasingMode.Out, EasingMode.InOut, EasingMode.OutIn };
        foreach (var type in types)
            foreach (var mode in modes)
                list.Add((type, mode));
        return list.ToArray();
    }

    /// <summary>手ぶれ補正のガウスフィルタの標準偏差(ミリ秒)。</summary>
    public static double SigmaMs(StabilizationLevel level) => level switch
    {
        StabilizationLevel.Weak => 25,
        StabilizationLevel.Normal => 60,
        StabilizationLevel.Strong => 110,
        StabilizationLevel.Max => 180,
        _ => 0,
    };

    public static MotionModel Build(string data, StabilizationLevel level)
    {
        var raw = MotionRecording.Parse(data, out bool hasTime);
        return Build(raw, hasTime, level);
    }

    public static MotionModel Build(IReadOnlyList<MotionSample> raw, bool hasTime, StabilizationLevel level)
    {
        if (raw.Count < 2) return MotionModel.Empty;
        var rawPoints = raw.Select(s => s.Position).ToList();

        // 旧形式(軌跡のみ): そのまま折れ線にして、イージングはプリセットに任せる
        if (!hasTime)
        {
            var legacy = new MotionPath(rawPoints);
            return legacy.IsEmpty
                ? MotionModel.Empty
                : new MotionModel(legacy, null, EasingType.Linear, EasingMode.In, rawPoints, 0);
        }

        // 1) 手ぶれ補正
        var series = Stabilize(raw, SigmaMs(level));
        int n = series.Count;
        var pos = new Vector2[n];
        for (int i = 0; i < n; i++) pos[i] = series[i].Position;

        // 2) 幾何: 折れ線に簡略化(残した点の番号を使って、各サンプルの進行を求める)
        var kept = SimplifyIndices(pos);
        var keptIdx = new List<int>();
        var keptPts = new List<Vector2>();
        for (int k = 0; k < kept.Count; k++)
        {
            var p = pos[kept[k]];
            if (keptPts.Count > 0 && Vector2.Distance(keptPts[^1], p) <= 1e-3f) continue;
            keptIdx.Add(kept[k]);
            keptPts.Add(p);
        }

        var path = new MotionPath(keptPts);
        if (path.IsEmpty || path.Length < MinLength) return MotionModel.Empty;

        // 3) 時間: 各サンプルが折れ線のどこまで進んでいたか(同じ線分の上だけで射影するので、行って戻る軌跡でも崩れない)
        int segCount = keptPts.Count - 1;
        var cum = new double[keptPts.Count];
        for (int s = 0; s < segCount; s++)
            cum[s + 1] = cum[s] + Vector2.Distance(keptPts[s], keptPts[s + 1]);
        double total = cum[^1];

        double t0 = series[0].TimeMs;
        double duration = series[^1].TimeMs - t0;
        if (duration < 1)
            return new MotionModel(path, null, EasingType.Linear, EasingMode.In, rawPoints, 0);

        var tau = new double[n];
        var prog = new double[n];
        double runMax = 0;
        int seg = 0;
        for (int i = 0; i < n; i++)
        {
            tau[i] = Math.Clamp((series[i].TimeMs - t0) / duration, 0, 1);

            double arc;
            if (i >= keptIdx[^1])
            {
                arc = total;
            }
            else
            {
                while (seg < segCount - 1 && i > keptIdx[seg + 1]) seg++;
                var a = keptPts[seg];
                var ab = keptPts[seg + 1] - a;
                float len2 = ab.LengthSquared();
                float u = len2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(pos[i] - a, ab) / len2, 0f, 1f);
                arc = cum[seg] + u * (cum[seg + 1] - cum[seg]);
            }
            runMax = Math.Max(runMax, arc / total);
            prog[i] = runMax;
        }

        // 4) 一様な時刻で再サンプリング → 記録どおりの速さの変化
        var g = new double[TimingSamples];
        int j = 0;
        for (int k = 0; k < TimingSamples; k++)
        {
            double x = k / (double)(TimingSamples - 1);
            while (j < n - 2 && tau[j + 1] < x) j++;
            double a = tau[j], b = tau[j + 1];
            double u = b - a <= 1e-9 ? 1 : Math.Clamp((x - a) / (b - a), 0, 1);
            g[k] = prog[j] + (prog[j + 1] - prog[j]) * u;
        }

        // マウスイベントの時刻のばらつきによる細かなガタつきだけ、ごく軽く均す
        g = GaussianSmooth(g, 1.0);

        double g0 = g[0], g1 = g[^1];
        if (g1 - g0 > 1e-6)
        {
            double prev = 0;
            for (int k = 0; k < g.Length; k++)
            {
                double v = Math.Clamp((g[k] - g0) / (g1 - g0), 0, 1);
                prev = Math.Max(prev, v);
                g[k] = prev;
            }
        }
        else
        {
            for (int k = 0; k < g.Length; k++) g[k] = k / (double)(g.Length - 1);
        }

        // 5) 最も近いプリセット
        var (type, mode) = FitPreset(g);

        return new MotionModel(path, g, type, mode, rawPoints, duration);
    }

    private static (EasingType, EasingMode) FitPreset(double[] g)
    {
        var bestType = EasingType.Linear;
        var bestMode = EasingMode.In;
        double best = double.MaxValue;

        // 48 点に取り直して比較
        var target = new double[FitSamples];
        for (int k = 0; k < FitSamples; k++)
        {
            double x = k / (double)(FitSamples - 1) * (g.Length - 1);
            int i = Math.Min((int)x, g.Length - 2);
            target[k] = g[i] + (g[i + 1] - g[i]) * (x - i);
        }

        foreach (var (type, mode) in Candidates)
        {
            double cost = 0;
            for (int k = 0; k < FitSamples; k++)
            {
                double x = k / (double)(FitSamples - 1);
                double diff = target[k] - Easing.GetValue(type, mode, x);
                cost += diff * diff;
            }
            cost /= FitSamples;

            // 先頭(Linear)以外は、これまでの最良より明確に良い場合のみ採用
            if (best == double.MaxValue || cost < best * 0.9 - 1e-6)
            {
                best = cost;
                bestType = type;
                bestMode = mode;
            }
        }
        return (bestType, bestMode);
    }

    // ---- 手ぶれ補正 ----

    /// <summary>
    /// 時間方向のローパス(ガウスフィルタ)。手の細かな震えを取り除く。
    /// 始点と終点は動かさない。速さの変化もなめらかになる。
    /// </summary>
    private static List<MotionSample> Stabilize(IReadOnlyList<MotionSample> raw, double sigmaMs)
    {
        double t0 = raw[0].TimeMs;
        double duration = raw[^1].TimeMs - t0;
        if (sigmaMs <= 0 || duration < 1) return raw.ToList();

        // 一定の時間刻みに取り直す
        int n = Math.Clamp((int)Math.Ceiling(duration / ResampleStepMs) + 1, 2, 4000);
        double dt = duration / (n - 1);
        var xs = new double[n];
        var ys = new double[n];
        int j = 0;
        for (int k = 0; k < n; k++)
        {
            double t = k * dt;
            while (j < raw.Count - 2 && raw[j + 1].TimeMs - t0 < t) j++;
            double a = raw[j].TimeMs - t0, b = raw[j + 1].TimeMs - t0;
            double u = b - a <= 1e-9 ? 1 : Math.Clamp((t - a) / (b - a), 0, 1);
            xs[k] = raw[j].Position.X + (raw[j + 1].Position.X - raw[j].Position.X) * u;
            ys[k] = raw[j].Position.Y + (raw[j + 1].Position.Y - raw[j].Position.Y) * u;
        }

        var sx = GaussianSmooth(xs, sigmaMs / dt);
        var sy = GaussianSmooth(ys, sigmaMs / dt);

        var result = new List<MotionSample>(n);
        for (int k = 0; k < n; k++)
            result.Add(new MotionSample(t0 + k * dt, new Vector2((float)sx[k], (float)sy[k])));
        return result;
    }

    /// <summary>
    /// ガウスフィルタ。両端は端点まわりの点対称に延長するので、端点はそのまま保たれる。
    /// sigma は点の間隔を 1 とした標準偏差。
    /// </summary>
    private static double[] GaussianSmooth(double[] v, double sigma)
    {
        int n = v.Length;
        if (n < 3 || sigma <= 1e-6) return (double[])v.Clone();

        int radius = Math.Min((int)Math.Ceiling(3 * sigma), n - 1);
        var w = new double[radius + 1];
        double sum = 0;
        for (int k = 0; k <= radius; k++)
        {
            w[k] = Math.Exp(-0.5 * k * k / (sigma * sigma));
            sum += k == 0 ? w[k] : 2 * w[k];
        }
        for (int k = 0; k <= radius; k++) w[k] /= sum;

        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            double acc = 0;
            for (int k = -radius; k <= radius; k++)
            {
                int idx = i + k;
                double value;
                if (idx < 0) value = 2 * v[0] - v[-idx];
                else if (idx > n - 1) value = 2 * v[n - 1] - v[2 * (n - 1) - idx];
                else value = v[idx];
                acc += w[Math.Abs(k)] * value;
            }
            result[i] = acc;
        }
        return result;
    }

    // ---- 折れ線の簡略化 (Ramer–Douglas–Peucker) ----

    private static List<int> SimplifyIndices(Vector2[] pts)
    {
        if (pts.Length <= 2) return Enumerable.Range(0, pts.Length).ToList();

        float eps = 3f;
        List<int> result = null;
        for (int i = 0; i < 12; i++)
        {
            result = Rdp(pts, eps);
            if (result.Count <= MaxWaypoints) break;
            eps *= 1.6f;
        }
        return result;
    }

    private static List<int> Rdp(Vector2[] pts, float eps)
    {
        int n = pts.Length;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;

        var stack = new Stack<(int, int)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float maxDist = 0;
            int index = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > maxDist) { maxDist = d; index = i; }
            }
            if (index >= 0 && maxDist > eps)
            {
                keep[index] = true;
                stack.Push((a, index));
                stack.Push((index, b));
            }
        }

        var res = new List<int>();
        for (int i = 0; i < n; i++)
            if (keep[i]) res.Add(i);
        return res;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }
}
