using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexInputUsage
{
    internal sealed class SessionModelSummary
    {
        internal string Name;
        internal int Calls;
        internal double CallSharePct;
        internal long TotalTokens, UncachedInputTokens, CachedInputTokens, ReasoningTokens, ReplyOutputTokens;
        internal double? AvgTps, AvgTtftMs, CacheHitPct;
    }

    internal struct TrendSample
    {
        internal int Turn;
        internal double Value;
        internal string Model;

        internal TrendSample(int turn, double value, string model)
        {
            Turn = turn;
            Value = value;
            Model = model;
        }
    }

    internal struct TrendEstimate
    {
        internal double X, Value, Low, High;
    }

    internal static class SessionAnalytics
    {
        internal static string ModelName(TurnSummary turn, SessionMetricsSnapshot snapshot)
        {
            string model = MetricFormatter.FormatModelName(string.IsNullOrWhiteSpace(turn.Model) ? snapshot.ModelName : turn.Model);
            return model.Length == 0 ? "未知模型" : model;
        }

        internal static SessionModelSummary[] BuildSessionModelSummaries(SessionMetricsSnapshot snapshot)
        {
            return snapshot.Turns.GroupBy(turn => ModelName(turn, snapshot), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    long input = 0, output = 0, activeMs = 0, ttft = 0;
                    int ttftCount = 0;
                    var summary = new SessionModelSummary { Name = group.Key };
                    foreach (var turn in group)
                    {
                        summary.Calls++;
                        summary.TotalTokens += turn.TotalTokens;
                        summary.UncachedInputTokens += Math.Max(0, turn.InputTokens - turn.CachedInputTokens);
                        summary.CachedInputTokens += turn.CachedInputTokens;
                        summary.ReasoningTokens += turn.ReasoningOutputTokens;
                        summary.ReplyOutputTokens += Math.Max(0, turn.OutputTokens - turn.ReasoningOutputTokens);
                        input += turn.InputTokens;
                        if (turn.OutputTokens > 0 && turn.ModelActiveMs >= 200)
                        {
                            output += turn.OutputTokens;
                            activeMs += turn.ModelActiveMs;
                        }
                        if (turn.TtftMs >= 0)
                        {
                            ttft += turn.TtftMs.Value;
                            ttftCount++;
                        }
                    }
                    summary.CallSharePct = 100.0 * summary.Calls / snapshot.Turns.Count;
                    summary.AvgTps = RolloutSessionTracker.ComputeAverageTps(output, activeMs);
                    if (ttftCount > 0) summary.AvgTtftMs = (double)ttft / ttftCount;
                    if (input > 0) summary.CacheHitPct = summary.CachedInputTokens * 100.0 / input;
                    return summary;
                })
                .OrderByDescending(model => model.TotalTokens).ThenByDescending(model => model.Calls)
                .ToArray();
        }

        internal static TrendEstimate[] ComputeTrendFit(IEnumerable<TrendSample> samples, double maximum)
        {
            var points = samples.Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value))
                .OrderBy(p => p.Turn).ToArray();
            int n = points.Length;
            if (n == 0) return new TrendEstimate[0];

            double first = points[0].Turn, range = points[n - 1].Turn - first;
            double mean = points.Average(p => p.Value);
            if (n == 1 || range == 0) return new[] { Estimate(first, mean, 0, maximum) };

            // Normalize the turn axis so the solve is stable for large or sparse turn IDs.
            var x = points.Select(p => (p.Turn - first) / range).ToArray();
            var y = points.Select(p => p.Value).ToArray();
            bool local = n >= 6;
            var weights = new double[n];
            var result = new TrendEstimate[41];
            for (int i = 0; i < result.Length; i++)
            {
                double at = (double)i / (result.Length - 1);
                double bandwidth = 1;
                if (local)
                {
                    var distances = x.Select(value => Math.Abs(value - at)).OrderBy(distance => distance).ToArray();
                    bandwidth = Math.Max(1e-8, distances[(int)Math.Ceiling(n * 0.5) - 1]);
                }

                double sumW = 0, sumW2 = 0, sumX = 0, sumY = 0;
                for (int j = 0; j < n; j++)
                {
                    double d = (x[j] - at) / bandwidth;
                    double w = local ? Math.Exp(-0.5 * d * d) : 1;
                    weights[j] = w;
                    sumW += w;
                    sumW2 += w * w;
                    sumX += w * x[j];
                    sumY += w * y[j];
                }
                double center = sumX / sumW, level = sumY / sumW, xx = 0, xy = 0;
                for (int j = 0; j < n; j++)
                {
                    double dx = x[j] - center;
                    xx += weights[j] * dx * dx;
                    xy += weights[j] * dx * (y[j] - level);
                }
                double slope = xx > 0 ? xy / xx : 0;
                double prediction = level + slope * (at - center);
                double residual = 0;
                for (int j = 0; j < n; j++)
                {
                    double error = y[j] - level - slope * (x[j] - center);
                    residual += weights[j] * error * error;
                }

                double margin = 0;
                if (local)
                {
                    // Effective sample size corrects the weighted residual variance for two fit parameters.
                    double effective = sumW * sumW / sumW2;
                    if (effective > 2)
                        margin = 1.96 * Math.Sqrt(residual / sumW * effective / (effective - 2));
                }
                else if (n > 2)
                {
                    double critical = new[] { 12.706204736, 4.302652730, 3.182446305 }[n - 3];
                    double dx = at - center;
                    margin = critical * Math.Sqrt(residual / (n - 2) * (1.0 / n + dx * dx / xx));
                }
                // Two points determine the line but leave no residual degrees of freedom.
                result[i] = Estimate(first + at * range, prediction, margin, maximum);
            }
            return result;
        }

        private static TrendEstimate Estimate(double x, double value, double margin, double maximum)
        {
            return new TrendEstimate
            {
                X = x, Value = Clip(value, maximum),
                Low = Clip(value - margin, maximum), High = Clip(value + margin, maximum)
            };
        }

        private static double Clip(double value, double maximum)
        {
            if (double.IsNaN(value) || double.IsNegativeInfinity(value)) return 0;
            return Math.Max(0, Math.Min(maximum, value));
        }
    }
}
