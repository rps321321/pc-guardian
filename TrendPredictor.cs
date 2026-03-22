using System;
using System.Collections.Generic;
using System.Linq;

namespace PCGuardian
{
    internal static class TrendPredictor
    {
        /// <summary>
        /// Predicts how many days until disk space reaches 0 based on recent history.
        /// Uses linear regression on the last 30 days of daily free space readings.
        /// </summary>
        /// <returns>Days until full, or null if disk is not shrinking or data is too noisy (R² &lt; 0.5).</returns>
        public static float? DaysUntilFull(IReadOnlyList<(DateTime Time, double FreeGb)> history)
        {
            if (history == null || history.Count < 2)
                return null;

            DateTime cutoff = history[history.Count - 1].Time.AddDays(-30);
            var recent = history
                .Where(h => h.Time >= cutoff)
                .OrderBy(h => h.Time)
                .ToList();

            if (recent.Count < 2)
                return null;

            DateTime origin = recent[0].Time;
            float[] x = recent.Select(h => (float)(h.Time - origin).TotalDays).ToArray();
            float[] y = recent.Select(h => (float)h.FreeGb).ToArray();

            var (slope, intercept, rSquared) = LinearRegression(x, y);

            // Not shrinking or too noisy
            if (slope >= 0f || rSquared < 0.5f)
                return null;

            // Days until y = 0: 0 = intercept + slope * days => days = -intercept / slope
            float daysUntilZero = -intercept / slope;
            float currentDay = x[x.Length - 1];

            float remaining = daysUntilZero - currentDay;
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>
        /// Predicts how many months until battery capacity drops below a replacement threshold.
        /// </summary>
        /// <param name="designCapacityMWh">Original design capacity in milliwatt-hours.</param>
        /// <param name="history">Time-series of full charge capacity readings.</param>
        /// <param name="thresholdPct">Capacity fraction below which replacement is recommended (default 60%).</param>
        /// <returns>Months until replacement, or null if insufficient data or battery is not degrading.</returns>
        public static int? MonthsUntilReplacement(
            uint designCapacityMWh,
            IReadOnlyList<(DateTime Time, uint FullChargeMWh)> history,
            float thresholdPct = 0.6f)
        {
            if (history == null || history.Count < 2 || designCapacityMWh == 0)
                return null;

            var sorted = history.OrderBy(h => h.Time).ToList();
            DateTime origin = sorted[0].Time;

            float[] x = sorted.Select(h => (float)(h.Time - origin).TotalDays).ToArray();
            float[] y = sorted.Select(h => (float)h.FullChargeMWh / designCapacityMWh).ToArray();

            var (slope, intercept, rSquared) = LinearRegression(x, y);

            // Battery not degrading or data too noisy
            if (slope >= 0f || rSquared < 0.5f)
                return null;

            // Days until capacity = thresholdPct: thresholdPct = intercept + slope * days
            float daysUntilThreshold = (thresholdPct - intercept) / slope;
            float currentDay = x[x.Length - 1];
            float remainingDays = daysUntilThreshold - currentDay;

            if (remainingDays <= 0f)
                return 0;

            return (int)MathF.Ceiling(remainingDays / 30.44f); // average days per month
        }

        /// <summary>
        /// Projects security posture score decay over the next 30 days assuming no remediation.
        /// </summary>
        /// <param name="currentScore">Current security posture score.</param>
        /// <param name="pendingCriticalUpdates">Number of pending critical updates.</param>
        /// <param name="pendingImportantUpdates">Number of pending important updates.</param>
        /// <param name="daysSinceAvUpdate">Days since the last antivirus definition update.</param>
        /// <returns>List of (day, projected score) for the next 30 days.</returns>
        public static List<(int Day, float ProjectedScore)> ProjectPostureDecay(
            float currentScore,
            int pendingCriticalUpdates,
            int pendingImportantUpdates,
            int daysSinceAvUpdate)
        {
            var projection = new List<(int Day, float ProjectedScore)>(30);

            for (int day = 1; day <= 30; day++)
            {
                int totalOverdueDays = day;

                // Critical updates: 2.0 * log2(1 + daysOverdue/7) per update
                float criticalPenalty = pendingCriticalUpdates
                    * 2.0f
                    * Log2(1f + totalOverdueDays / 7f);

                // Important updates: 1.0 * log2(1 + daysOverdue/14) per update
                float importantPenalty = pendingImportantUpdates
                    * 1.0f
                    * Log2(1f + totalOverdueDays / 14f);

                // AV staleness: 0.5 points per day without update
                float avPenalty = 0.5f * (daysSinceAvUpdate + day);

                float projectedScore = currentScore - criticalPenalty - importantPenalty - avPenalty;
                projection.Add((day, MathF.Max(projectedScore, 0f)));
            }

            return projection;
        }

        /// <summary>
        /// Predicts periodic crashes based on inter-crash interval regularity.
        /// </summary>
        /// <param name="crashTimestamps">Chronological list of crash timestamps.</param>
        /// <returns>
        /// Period in hours and predicted next crash time if crashes are periodic
        /// (coefficient of variation &lt; 0.3). Null values if not enough data or crashes are not periodic.
        /// </returns>
        public static (float? PeriodHours, DateTime? NextCrash) PredictCrashes(
            IReadOnlyList<DateTime> crashTimestamps)
        {
            if (crashTimestamps == null || crashTimestamps.Count < 3)
                return (null, null);

            var sorted = crashTimestamps.OrderBy(t => t).ToList();

            // Compute inter-crash intervals in hours
            var intervals = new float[sorted.Count - 1];
            for (int i = 0; i < intervals.Length; i++)
            {
                intervals[i] = (float)(sorted[i + 1] - sorted[i]).TotalHours;
            }

            float mean = intervals.Average();
            if (mean <= 0f)
                return (null, null);

            float variance = intervals.Select(iv => (iv - mean) * (iv - mean)).Average();
            float stdDev = MathF.Sqrt(variance);
            float cv = stdDev / mean;

            // Crashes are periodic if coefficient of variation < 0.3
            if (cv >= 0.3f)
                return (null, null);

            DateTime lastCrash = sorted[sorted.Count - 1];
            DateTime nextCrash = lastCrash.AddHours(mean);

            return (mean, nextCrash);
        }

        /// <summary>
        /// Ordinary least squares linear regression.
        /// </summary>
        /// <returns>Slope, intercept, and R-squared of the fitted line.</returns>
        public static (float Slope, float Intercept, float RSquared) LinearRegression(float[] x, float[] y)
        {
            if (x.Length != y.Length || x.Length < 2)
                return (0f, 0f, 0f);

            int n = x.Length;
            float sumX = 0f, sumY = 0f, sumXY = 0f, sumX2 = 0f;

            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
                sumXY += x[i] * y[i];
                sumX2 += x[i] * x[i];
            }

            float denom = n * sumX2 - sumX * sumX;
            if (MathF.Abs(denom) < 1e-10f)
                return (0f, sumY / n, 0f);

            float slope = (n * sumXY - sumX * sumY) / denom;
            float intercept = (sumY - slope * sumX) / n;

            // R-squared
            float meanY = sumY / n;
            float ssTot = 0f, ssRes = 0f;

            for (int i = 0; i < n; i++)
            {
                float predicted = intercept + slope * x[i];
                ssRes += (y[i] - predicted) * (y[i] - predicted);
                ssTot += (y[i] - meanY) * (y[i] - meanY);
            }

            float rSquared = ssTot > 0f ? 1f - ssRes / ssTot : 0f;

            return (slope, intercept, rSquared);
        }

        private static float Log2(float value)
        {
            return MathF.Log(value) / MathF.Log(2f);
        }
    }
}
