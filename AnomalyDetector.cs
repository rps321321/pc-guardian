namespace PCGuardian;

// ── Result type ─────────────────────────────────────────────────────────────

internal sealed record AnomalyResult(
    string Metric,
    float Value,
    float Expected,
    float ZScore,
    bool IsAnomaly,
    string Description);

// ── EWMA anomaly detection engine ───────────────────────────────────────────

internal sealed class AnomalyDetector
{
    const float AnomalyZThreshold = 3.0f;
    const int   HourlyBufferSize  = 72;
    const float DriftSlopeThreshold = 0.005f; // 0.5 % per hour

    readonly Dictionary<string, MetricTracker> _trackers = new();
    readonly object _lock = new();

    static readonly string[] TrackedMetrics =
        ["CPU", "GPU", "RAM", "DiskIO", "NetworkIO"];

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Feed a new sample for a metric and get anomaly assessment.</summary>
    public AnomalyResult Update(string metric, float value)
    {
        lock (_lock)
        {
            var tracker = GetOrCreateTracker(metric);
            int hour = DateTime.Now.Hour;

            var (zScore, isAnomaly) = tracker.Update(value);
            tracker.UpdateHourlyProfile(hour, value);

            float expected = tracker.Ewma;
            string description = BuildDescription(metric, value, expected, zScore, isAnomaly);

            var result = new AnomalyResult(metric, value, expected, zScore, isAnomaly, description);

            if (isAnomaly)
                tracker.LastAnomaly = result;
            else
                tracker.LastAnomaly = null;

            return result;
        }
    }

    /// <summary>Check if a specific metric is currently flagged as anomalous.</summary>
    public bool IsAnomalous(string metric)
    {
        lock (_lock)
        {
            return _trackers.TryGetValue(metric, out var tracker)
                && tracker.LastAnomaly is not null;
        }
    }

    /// <summary>Get all metrics currently flagged as anomalous.</summary>
    public List<AnomalyResult> GetActiveAnomalies()
    {
        lock (_lock)
        {
            var anomalies = new List<AnomalyResult>();
            foreach (var tracker in _trackers.Values)
            {
                if (tracker.LastAnomaly is not null)
                    anomalies.Add(tracker.LastAnomaly);
            }
            return anomalies;
        }
    }

    /// <summary>Detect slow upward drift via linear regression on hourly means.</summary>
    public bool IsMetricDrifting(string metric)
    {
        lock (_lock)
        {
            return _trackers.TryGetValue(metric, out var tracker)
                && tracker.ComputeDriftSlope() > DriftSlopeThreshold;
        }
    }

    /// <summary>Get the drift rate (slope of hourly means) for a metric.</summary>
    public float GetDriftRate(string metric)
    {
        lock (_lock)
        {
            return _trackers.TryGetValue(metric, out var tracker)
                ? tracker.ComputeDriftSlope()
                : 0f;
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────

    MetricTracker GetOrCreateTracker(string metric)
    {
        if (!_trackers.TryGetValue(metric, out var tracker))
        {
            tracker = new MetricTracker();
            _trackers[metric] = tracker;
        }
        return tracker;
    }

    static string BuildDescription(
        string metric, float value, float expected, float zScore, bool isAnomaly)
    {
        if (!isAnomaly)
            return $"{metric} is within normal range";

        float pctDiff = expected > 0.0001f
            ? MathF.Abs(value - expected) / expected * 100f
            : MathF.Abs(value - expected) * 100f;

        string direction = value > expected ? "above" : "below";
        return $"{metric} is {pctDiff:F0}% {direction} your usual baseline";
    }

    // ── Per-metric tracker ──────────────────────────────────────────────────

    sealed class MetricTracker
    {
        float _ewma;
        float _ewmaVariance;
        bool  _initialized;
        float _alpha = 0.1f;

        // Per-hour-of-day baseline (24 slots, Welford's algorithm)
        readonly float[] _hourlyMean     = new float[24];
        readonly float[] _hourlyVariance = new float[24];
        readonly int[]   _hourlyCount    = new int[24];

        // Circular buffer of last 72 hourly means for drift detection
        readonly float[] _hourlyMeanBuffer = new float[HourlyBufferSize];
        int  _bufferIndex;
        int  _bufferCount;
        int  _lastRecordedHour = -1;

        public float Ewma => _ewma;
        public AnomalyResult? LastAnomaly { get; set; }

        /// <summary>
        /// Update EWMA, compute z-score, and check against hourly profile.
        /// </summary>
        public (float zScore, bool isAnomaly) Update(float value)
        {
            if (!_initialized)
            {
                _ewma = value;
                _ewmaVariance = 0f;
                _initialized = true;
                return (0f, false);
            }

            // Update EWMA mean
            float delta = value - _ewma;
            _ewma += _alpha * delta;

            // Update EWMA variance (exponentially weighted)
            float deviation = delta * delta;
            _ewmaVariance = (1f - _alpha) * (_ewmaVariance + _alpha * deviation);

            // Compute z-score against EWMA
            float stdDev = MathF.Sqrt(_ewmaVariance);
            float zScore = stdDev > 0.0001f ? delta / stdDev : 0f;

            bool isEwmaAnomaly = MathF.Abs(zScore) > AnomalyZThreshold;

            // Check against hourly profile for secondary confirmation
            int hour = DateTime.Now.Hour;
            bool isHourlyAnomaly = IsHourlyAnomaly(hour, value);

            // Anomaly if EWMA flags it, or if hourly profile flags it
            // (with enough hourly data to be meaningful)
            bool isAnomaly = isEwmaAnomaly || isHourlyAnomaly;

            return (zScore, isAnomaly);
        }

        /// <summary>
        /// Update per-hour-of-day baseline using Welford's online algorithm.
        /// </summary>
        public void UpdateHourlyProfile(int hour, float value)
        {
            _hourlyCount[hour]++;
            int n = _hourlyCount[hour];
            float oldMean = _hourlyMean[hour];
            float delta = value - oldMean;
            float newMean = oldMean + delta / n;
            _hourlyMean[hour] = newMean;
            _hourlyVariance[hour] += delta * (value - newMean);

            // Record hourly mean snapshot in circular buffer once per new hour
            if (hour != _lastRecordedHour)
            {
                _hourlyMeanBuffer[_bufferIndex] = _hourlyMean[hour];
                _bufferIndex = (_bufferIndex + 1) % HourlyBufferSize;
                if (_bufferCount < HourlyBufferSize) _bufferCount++;
                _lastRecordedHour = hour;
            }
        }

        /// <summary>
        /// Simple linear regression on the circular buffer of hourly means.
        /// Returns slope (rate of change per hour).
        /// </summary>
        public float ComputeDriftSlope()
        {
            if (_bufferCount < 6) return 0f; // need enough points

            // Read buffer in chronological order
            int start = _bufferCount < HourlyBufferSize
                ? 0
                : _bufferIndex; // oldest element when buffer is full

            float sumX = 0f, sumY = 0f, sumXY = 0f, sumX2 = 0f;
            int n = _bufferCount;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % HourlyBufferSize;
                float x = i;
                float y = _hourlyMeanBuffer[idx];
                sumX  += x;
                sumY  += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            float denom = n * sumX2 - sumX * sumX;
            if (MathF.Abs(denom) < 0.0001f) return 0f;

            float slope = (n * sumXY - sumX * sumY) / denom;
            return slope;
        }

        bool IsHourlyAnomaly(int hour, float value)
        {
            int n = _hourlyCount[hour];
            if (n < 10) return false; // not enough data for hourly baseline

            float mean = _hourlyMean[hour];
            // Welford's stores sum of squared deviations; divide by n for variance
            float variance = _hourlyVariance[hour] / n;
            float stdDev = MathF.Sqrt(variance);

            if (stdDev < 0.0001f) return false;

            float z = MathF.Abs(value - mean) / stdDev;
            return z > AnomalyZThreshold;
        }
    }
}
