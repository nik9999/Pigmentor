namespace Pigmentor.Evaluation;

/// <summary>
/// A two-sided confidence interval on a real-valued statistic, expressed as a
/// closed <c>[Lower, Upper]</c> range. A delta interval whose bounds straddle
/// zero is statistically indistinguishable from "no change."
/// </summary>
public readonly record struct ConfidenceInterval(double Lower, double Upper)
{
    /// <summary>The width of the interval (<see cref="Upper"/> − <see cref="Lower"/>).</summary>
    public double Width => Upper - Lower;

    /// <summary><see langword="true"/> when zero lies inside the interval — i.e. the effect is not significant.</summary>
    public bool ContainsZero => Lower <= 0 && Upper >= 0;
}

/// <summary>
/// The outcome of comparing a candidate pipeline against a baseline on the
/// same seed set: the observed mean delta, its confidence interval, and a
/// verdict on whether the delta is a real regression / improvement or noise.
/// </summary>
/// <param name="Delta">Observed mean difference (candidate − baseline).</param>
/// <param name="Interval">Confidence interval on <paramref name="Delta"/>.</param>
/// <param name="IsSignificant"><see langword="true"/> when the interval excludes zero.</param>
public readonly record struct DeltaVerdict(double Delta, ConfidenceInterval Interval, bool IsSignificant)
{
    /// <summary>
    /// <see langword="true"/> when the delta is a significant regression in the
    /// "wrong" direction — the interval lies entirely below zero (the candidate
    /// scored lower than the baseline). This is the condition a promotion gate
    /// blocks on: "block if the delta's CI excludes zero in the wrong direction."
    /// </summary>
    public bool IsSignificantRegression => IsSignificant && Interval.Upper < 0;
}

/// <summary>
/// The 2×2 contingency of pass/fail outcomes when the same seed set is scored
/// by two pipeline versions. The diagonals (both pass / both fail) carry no
/// information about a change; McNemar's test looks only at the discordant
/// off-diagonal counts.
/// </summary>
/// <param name="BaselinePassCandidateFail">Items the baseline passed but the candidate failed (regressions).</param>
/// <param name="BaselineFailCandidatePass">Items the baseline failed but the candidate passed (fixes).</param>
/// <param name="Statistic">The continuity-corrected McNemar chi-square statistic (1 d.o.f.).</param>
/// <param name="PValue">The two-sided p-value for <paramref name="Statistic"/>.</param>
public readonly record struct McNemarResult(
    int BaselinePassCandidateFail,
    int BaselineFailCandidatePass,
    double Statistic,
    double PValue)
{
    /// <summary>The total number of discordant pairs (the only ones that count).</summary>
    public int DiscordantPairs => BaselinePassCandidateFail + BaselineFailCandidatePass;

    /// <summary>
    /// <see langword="true"/> when the flip pattern is significant at the given
    /// alpha (default 0.05) — i.e. the change in pass-rate is unlikely to be a
    /// coincidence of which items happened to flip.
    /// </summary>
    public bool IsSignificant(double alpha = 0.05) => PValue < alpha;
}

/// <summary>
/// A small, dependency-free toolkit for asking "is this metric delta real, or
/// is it inside the noise band of a 30–80-item seed set?" — the question
/// Chapter 20 raises about gating on a fixed "down &gt;2 points" threshold.
///
/// <para>
/// Three tools, each pure and deterministic:
/// <list type="bullet">
///   <item><see cref="WilsonInterval"/> — a confidence interval for a
///   <em>proportion</em> (pass-rate, recall, precision). Well-behaved at small
///   <c>n</c> and near 0/1, unlike the normal approximation.</item>
///   <item><see cref="PairedBootstrapDelta"/> — a confidence interval for the
///   <em>mean delta</em> between two pipelines scored on the same items, by
///   resampling the per-item differences. Seeded, so a CI run reproduces.</item>
///   <item><see cref="McNemar"/> — a paired test for <em>pass/fail flips</em>
///   between two pipelines, which uses only the discordant pairs.</item>
/// </list>
/// </para>
/// </summary>
public static class StatisticalSignificance
{
    /// <summary>
    /// The Wilson score interval for a binomial proportion <c>successes / total</c>.
    /// Preferred over the Wald (normal-approximation) interval because it stays
    /// inside <c>[0, 1]</c> and remains sensible for small samples and extreme
    /// rates. <paramref name="z"/> is the standard-normal critical value
    /// (1.96 ≈ 95%, 1.645 ≈ 90%, 2.576 ≈ 99%).
    /// </summary>
    /// <param name="successes">Number of successes (0 ≤ successes ≤ total).</param>
    /// <param name="total">Number of trials (&gt; 0).</param>
    /// <param name="z">Standard-normal critical value for the desired confidence.</param>
    /// <returns>The lower and upper bounds, each clamped to <c>[0, 1]</c>.</returns>
    public static ConfidenceInterval WilsonInterval(int successes, int total, double z = 1.96)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(total);
        if (successes > total)
        {
            throw new ArgumentOutOfRangeException(nameof(successes),
                "successes cannot exceed total.");
        }

        double n = total;
        double phat = successes / n;
        double z2 = z * z;
        double denom = 1 + z2 / n;
        double center = (phat + z2 / (2 * n)) / denom;
        double margin = z * Math.Sqrt(phat * (1 - phat) / n + z2 / (4 * n * n)) / denom;

        return new ConfidenceInterval(
            Lower: Math.Clamp(center - margin, 0, 1),
            Upper: Math.Clamp(center + margin, 0, 1));
    }

    /// <summary>
    /// A paired bootstrap confidence interval for the mean of a single set of
    /// scores (e.g. faithfulness scores for one pipeline). Resamples the scores
    /// with replacement <paramref name="iterations"/> times and returns the
    /// percentile interval of the resampled means.
    /// </summary>
    /// <param name="scores">The per-item scores.</param>
    /// <param name="seed">RNG seed — pass a fixed value for reproducible CIs.</param>
    /// <param name="iterations">Number of bootstrap resamples (default 2000).</param>
    /// <param name="confidence">Two-sided confidence level in (0, 1) (default 0.95).</param>
    public static ConfidenceInterval BootstrapMean(
        IReadOnlyList<double> scores,
        int seed,
        int iterations = 2000,
        double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count == 0)
        {
            throw new ArgumentException("scores must not be empty.", nameof(scores));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        ValidateConfidence(confidence);

        var rng = new Random(seed);
        var means = new double[iterations];
        int n = scores.Count;
        for (int b = 0; b < iterations; b++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += scores[rng.Next(n)];
            }
            means[b] = sum / n;
        }
        return Percentile(means, confidence);
    }

    /// <summary>
    /// A paired bootstrap confidence interval for the <em>mean delta</em>
    /// (<paramref name="candidate"/> − <paramref name="baseline"/>) between two
    /// pipeline versions scored on the <em>same</em> seed set. Because the two
    /// score lists are paired item-for-item, the per-item difference cancels
    /// the shared per-item difficulty, giving a much tighter interval than two
    /// independent-sample CIs — the reason a paired test is the right tool when
    /// comparing versions on a fixed seed set.
    /// </summary>
    /// <param name="baseline">Baseline scores, one per seed item.</param>
    /// <param name="candidate">Candidate scores, aligned with <paramref name="baseline"/>.</param>
    /// <param name="seed">RNG seed — pass a fixed value for reproducible CIs.</param>
    /// <param name="iterations">Number of bootstrap resamples (default 2000).</param>
    /// <param name="confidence">Two-sided confidence level in (0, 1) (default 0.95).</param>
    /// <returns>
    /// The observed mean delta, its bootstrap CI, and whether the CI excludes zero.
    /// </returns>
    public static DeltaVerdict PairedBootstrapDelta(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        int seed,
        int iterations = 2000,
        double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (baseline.Count != candidate.Count)
        {
            throw new ArgumentException(
                "baseline and candidate must be paired (same length).", nameof(candidate));
        }
        if (baseline.Count == 0)
        {
            throw new ArgumentException("score sets must not be empty.", nameof(baseline));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        ValidateConfidence(confidence);

        int n = baseline.Count;
        var diffs = new double[n];
        double observedSum = 0;
        for (int i = 0; i < n; i++)
        {
            diffs[i] = candidate[i] - baseline[i];
            observedSum += diffs[i];
        }
        double observedDelta = observedSum / n;

        var rng = new Random(seed);
        var means = new double[iterations];
        for (int b = 0; b < iterations; b++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += diffs[rng.Next(n)];
            }
            means[b] = sum / n;
        }

        var interval = Percentile(means, confidence);
        return new DeltaVerdict(observedDelta, interval, IsSignificant: !interval.ContainsZero);
    }

    /// <summary>
    /// McNemar's test for paired pass/fail outcomes. Given, for each seed item,
    /// whether the baseline and the candidate passed, it counts the discordant
    /// pairs (one passed, the other failed) and reports the chi-square statistic
    /// (with the continuity correction, valid for small samples) and its
    /// one-degree-of-freedom p-value.
    /// </summary>
    /// <param name="baselinePass">Per-item pass flag for the baseline.</param>
    /// <param name="candidatePass">Per-item pass flag for the candidate (aligned with the baseline).</param>
    public static McNemarResult McNemar(
        IReadOnlyList<bool> baselinePass,
        IReadOnlyList<bool> candidatePass)
    {
        ArgumentNullException.ThrowIfNull(baselinePass);
        ArgumentNullException.ThrowIfNull(candidatePass);
        if (baselinePass.Count != candidatePass.Count)
        {
            throw new ArgumentException(
                "baseline and candidate must be paired (same length).", nameof(candidatePass));
        }

        int b = 0; // baseline pass, candidate fail
        int c = 0; // baseline fail, candidate pass
        for (int i = 0; i < baselinePass.Count; i++)
        {
            if (baselinePass[i] && !candidatePass[i])
            {
                b++;
            }
            else if (!baselinePass[i] && candidatePass[i])
            {
                c++;
            }
        }

        int discordant = b + c;
        double statistic;
        double pValue;
        if (discordant == 0)
        {
            // No flips at all — there is nothing to distinguish the two versions.
            statistic = 0;
            pValue = 1.0;
        }
        else
        {
            // Continuity-corrected McNemar chi-square, 1 d.o.f.
            double diff = Math.Abs(b - c) - 1.0;
            statistic = diff <= 0 ? 0 : diff * diff / discordant;
            pValue = ChiSquaredSurvival1Df(statistic);
        }

        return new McNemarResult(b, c, statistic, pValue);
    }

    // --- helpers --------------------------------------------------------------

    private static void ValidateConfidence(double confidence)
    {
        if (confidence is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence),
                "confidence must be in the open interval (0, 1).");
        }
    }

    private static ConfidenceInterval Percentile(double[] samples, double confidence)
    {
        Array.Sort(samples);
        double alpha = 1 - confidence;
        double lowerQ = alpha / 2;
        double upperQ = 1 - alpha / 2;
        return new ConfidenceInterval(
            Lower: Quantile(samples, lowerQ),
            Upper: Quantile(samples, upperQ));
    }

    // Linear-interpolation quantile over a pre-sorted array.
    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi)
        {
            return sorted[lo];
        }
        double frac = pos - lo;
        return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
    }

    // Survival function (upper tail) of the chi-square distribution with 1 d.o.f.
    // For 1 d.o.f., P(X > x) = erfc(sqrt(x / 2)). Uses a standard rational
    // approximation of erfc (Abramowitz & Stegun 7.1.26), good to ~1e-7 — far
    // more precision than a gate decision needs.
    private static double ChiSquaredSurvival1Df(double x)
    {
        if (x <= 0)
        {
            return 1.0;
        }
        return Erfc(Math.Sqrt(x / 2.0));
    }

    private static double Erfc(double x)
    {
        // erfc(x) for x >= 0 via A&S 7.1.26. erfc(-x) = 2 - erfc(x), but all
        // call sites here pass x >= 0.
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double y = t * (0.254829592
            + t * (-0.284496736
            + t * (1.421413741
            + t * (-1.453152027
            + t * 1.061405429))));
        return y * Math.Exp(-x * x);
    }
}
