using System;
using System.Collections.Generic;
using System.Linq;

namespace N_m3u8DL_CLI
{
    public static class AdDetectStrategies
    {
        // Default ad keywords to look for in URLs
        private static readonly string[] DefaultAdKeywords = new string[]
        {
            "adjump", "ad-", "/ad/", "_ad_", "advert",
            "commercial", "preroll", "midroll", "postroll",
            "adserv", "adserver", "adplayer",
            "doubleclick", "googlesyndication"
        };

        /// <summary>
        /// Strategy 1: Domain/Path Difference
        /// Compare domain and path patterns across DISCONTINUITY groups.
        /// Groups with different domains or path patterns are likely ads.
        /// </summary>
        public static Dictionary<long, StrategyResult> DomainPathDifference(List<SegmentInfo> segments)
        {
            var results = new Dictionary<long, StrategyResult>();

            if (segments.Count == 0) return results;

            // Group segments by GroupIndex
            var groups = segments.GroupBy(s => s.GroupIndex).ToList();
            if (groups.Count <= 1) return results;

            // Find the dominant group (largest by total duration, likely the main content)
            var largestGroup = groups.OrderByDescending(g => g.Sum(s => s.Duration)).First();
            string dominantDomain = largestGroup.First().Domain;

            // Get the common path prefix of the dominant group
            var dominantPaths = largestGroup.Select(s => s.Path).ToList();
            string dominantPathPrefix = GetCommonPathPrefix(dominantPaths);

            foreach (var seg in segments)
            {
                double score = 0;
                string reason = "";

                // Domain difference - strong signal
                if (!string.IsNullOrEmpty(seg.Domain) && !string.IsNullOrEmpty(dominantDomain)
                    && seg.Domain != dominantDomain)
                {
                    score = 0.8;
                    reason = "域名不同: " + seg.Domain + " vs " + dominantDomain;
                }
                // Path difference - moderate signal
                else if (!string.IsNullOrEmpty(seg.Path) && !string.IsNullOrEmpty(dominantPathPrefix)
                    && !seg.Path.StartsWith(dominantPathPrefix))
                {
                    score = 0.6;
                    string diffPath = seg.Path.Length > 40 ? seg.Path.Substring(0, 40) + "..." : seg.Path;
                    reason = "路径不同: " + diffPath;
                }

                if (score > 0)
                {
                    results[seg.Index] = new StrategyResult { Score = score, Reason = reason };
                }
            }

            return results;
        }

        /// <summary>
        /// Strategy 2: Duration Anomaly
        /// Find DISCONTINUITY groups where all segments share the same fixed duration,
        /// which differs from content segments' durations.
        /// </summary>
        public static Dictionary<long, StrategyResult> DurationAnomaly(List<SegmentInfo> segments)
        {
            var results = new Dictionary<long, StrategyResult>();

            if (segments.Count == 0) return results;

            var groups = segments.GroupBy(s => s.GroupIndex).ToList();
            if (groups.Count <= 1) return results;

            // Calculate duration statistics per group
            var groupStats = new Dictionary<int, GroupDurationStats>();
            foreach (var group in groups)
            {
                var durations = group.Select(s => s.Duration).ToList();
                groupStats[group.Key] = new GroupDurationStats
                {
                    Count = durations.Count,
                    Mean = durations.Average(),
                    IsUniform = durations.All(d => Math.Abs(d - durations[0]) < 0.001),
                    UniformDuration = durations[0],
                    TotalDuration = durations.Sum()
                };
            }

            // Find the main content group (largest by total duration)
            var mainGroup = groupStats.OrderByDescending(kv => kv.Value.TotalDuration).First();

            foreach (var kv in groupStats)
            {
                // Skip the main content group
                if (kv.Key == mainGroup.Key) continue;

                var stats = kv.Value;

                // Suspicious if: uniform duration, reasonable ad duration (3-120s)
                if (stats.IsUniform && stats.TotalDuration >= 3.0 && stats.TotalDuration <= 120.0)
                {
                    double diffRatio = Math.Abs(stats.UniformDuration - mainGroup.Value.Mean) / Math.Max(mainGroup.Value.Mean, 0.001);

                    double score = 0;
                    string reason = "";

                    if (diffRatio > 0.3)
                    {
                        score = 0.7;
                        reason = string.Format("固定时长{0:F1}s, 主体约{1:F1}s", stats.UniformDuration, mainGroup.Value.Mean);
                    }
                    else if (diffRatio > 0.1)
                    {
                        score = 0.4;
                        reason = string.Format("相近固定时长{0:F1}s", stats.UniformDuration);
                    }

                    if (score > 0)
                    {
                        foreach (var seg in groups.First(g => g.Key == kv.Key))
                        {
                            results[seg.Index] = new StrategyResult { Score = score, Reason = reason };
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Strategy 3: CUE/SCTE-35 Marker Detection
        /// Segments between CUE-OUT and CUE-IN tags are likely ads.
        /// </summary>
        public static Dictionary<long, StrategyResult> CUEMarkerDetection(List<SegmentInfo> segments)
        {
            var results = new Dictionary<long, StrategyResult>();

            foreach (var seg in segments)
            {
                if (seg.IsInCueOut)
                {
                    double score = 0.9;
                    string reason = seg.CueOutDuration > 0
                        ? string.Format("CUE-OUT标记区间(预计{0:F0}s)", seg.CueOutDuration)
                        : "CUE-OUT标记区间";
                    results[seg.Index] = new StrategyResult { Score = score, Reason = reason };
                }
            }

            return results;
        }

        /// <summary>
        /// Strategy 4: URL Keyword Matching
        /// Look for ad-related keywords in segment URLs.
        /// </summary>
        public static Dictionary<long, StrategyResult> URLKeywordMatch(List<SegmentInfo> segments, string customKeywords)
        {
            var results = new Dictionary<long, StrategyResult>();

            // Combine default and custom keywords
            var keywords = new List<string>(DefaultAdKeywords);
            if (!string.IsNullOrEmpty(customKeywords))
            {
                foreach (string kw in customKeywords.Split(','))
                {
                    string trimmed = kw.Trim().ToLower();
                    if (!string.IsNullOrEmpty(trimmed) && !keywords.Contains(trimmed))
                        keywords.Add(trimmed);
                }
            }

            foreach (var seg in segments)
            {
                if (string.IsNullOrEmpty(seg.Url)) continue;

                string urlLower = seg.Url.ToLower();
                double maxScore = 0;
                string matchedKeyword = "";

                foreach (string kw in keywords)
                {
                    if (urlLower.Contains(kw))
                    {
                        double score = 0;

                        if (kw == "adjump") score = 0.9;
                        else if (kw == "/ad/" || kw == "ad-") score = 0.85;
                        else if (kw.Contains("adserver") || kw.Contains("adserv")) score = 0.8;
                        else if (kw == "commercial" || kw == "preroll") score = 0.85;
                        else if (kw == "midroll" || kw == "postroll") score = 0.8;
                        else if (kw.Contains("doubleclick") || kw.Contains("googlesyndication")) score = 0.9;
                        else score = 0.6;

                        if (score > maxScore)
                        {
                            maxScore = score;
                            matchedKeyword = kw;
                        }
                    }
                }

                if (maxScore > 0)
                {
                    results[seg.Index] = new StrategyResult
                    {
                        Score = maxScore,
                        Reason = "URL包含关键词: " + matchedKeyword
                    };
                }
            }

            return results;
        }

        /// <summary>
        /// Strategy 5: Duration Consistency
        /// If a group has very consistent durations while others vary, it might be ads.
        /// Ad segments typically have identical durations (e.g., all 3.0s or all 6.0s).
        /// </summary>
        public static Dictionary<long, StrategyResult> DurationConsistency(List<SegmentInfo> segments)
        {
            var results = new Dictionary<long, StrategyResult>();

            if (segments.Count == 0) return results;

            var groups = segments.GroupBy(s => s.GroupIndex).ToList();
            if (groups.Count <= 1) return results;

            // Calculate duration variance per group
            var groupVariances = new Dictionary<int, double>();
            var groupCounts = new Dictionary<int, int>();

            foreach (var group in groups)
            {
                var durations = group.Select(s => s.Duration).ToList();
                if (durations.Count < 2) continue;

                double mean = durations.Average();
                double variance = durations.Sum(d => Math.Pow(d - mean, 2)) / durations.Count;
                groupVariances[group.Key] = variance;
                groupCounts[group.Key] = durations.Count;
            }

            if (groupVariances.Count < 2) return results;

            double varianceThreshold = 0.001;

            foreach (var kv in groupVariances)
            {
                // This group has uniform durations
                if (kv.Value < varianceThreshold && groupCounts[kv.Key] >= 2)
                {
                    // Check if other groups have non-uniform durations
                    bool othersAreVaried = groupVariances.Any(v => v.Key != kv.Key && v.Value > varianceThreshold);

                    if (othersAreVaried)
                    {
                        double score = 0.5;
                        string reason = string.Format("分片时长高度一致(方差:{0:F6})", kv.Value);

                        foreach (var seg in groups.First(g => g.Key == kv.Key))
                        {
                            results[seg.Index] = new StrategyResult { Score = score, Reason = reason };
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Get common path prefix from a list of URL paths
        /// </summary>
        private static string GetCommonPathPrefix(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return "";

            string prefix = paths[0];
            foreach (string path in paths)
            {
                int commonLen = 0;
                int minLen = Math.Min(prefix.Length, path.Length);
                while (commonLen < minLen && prefix[commonLen] == path[commonLen])
                    commonLen++;

                prefix = prefix.Substring(0, commonLen);

                if (string.IsNullOrEmpty(prefix)) break;
            }

            // Trim to last '/' for a clean path prefix
            int lastSlash = prefix.LastIndexOf('/');
            if (lastSlash > 0)
                prefix = prefix.Substring(0, lastSlash + 1);

            return prefix;
        }

        // Helper class for duration statistics
        private class GroupDurationStats
        {
            public int Count { get; set; }
            public double Mean { get; set; }
            public bool IsUniform { get; set; }
            public double UniformDuration { get; set; }
            public double TotalDuration { get; set; }
        }
    }
}
