using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace N_m3u8DL_CLI
{
    public class SegmentInfo
    {
        public long Index { get; set; }
        public double Duration { get; set; }
        public string Url { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; }
        public int GroupIndex { get; set; }
        public bool IsInCueOut { get; set; }
        public double CueOutDuration { get; set; }
    }

    public class StrategyResult
    {
        public double Score { get; set; }
        public string Reason { get; set; }
    }

    public class AdDetectResult
    {
        public long SegmentIndex { get; set; }
        public bool IsAd { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> StrategyScores { get; set; }
        public Dictionary<string, string> StrategyReasons { get; set; }
    }

    public class AdDetectionReport
    {
        public List<SegmentInfo> AllSegments { get; set; }
        public List<AdDetectResult> Results { get; set; }
        public int TotalSegments { get; set; }
        public int AdSegmentCount { get; set; }
        public double TotalAdDuration { get; set; }
        public double TotalDuration { get; set; }
        public double Threshold { get; set; }
        public List<long> AdSegmentIndices { get; set; }
    }

    public class AdDetector
    {
        private double threshold = 0.6;
        private string customKeywords = "";

        public double Threshold { get => threshold; set => threshold = value; }
        public string CustomKeywords { get => customKeywords; set => customKeywords = value; }

        public AdDetectionReport Detect(JArray parts)
        {
            List<SegmentInfo> segments = ExtractSegmentInfo(parts);

            // Strategy weights
            var weights = new Dictionary<string, double>
            {
                { "DomainPath", 0.25 },
                { "DurationAnomaly", 0.20 },
                { "CUEMarker", 0.30 },
                { "URLKeyword", 0.15 },
                { "DurationConsistency", 0.10 }
            };

            // Run all strategies
            var domainPathResults = AdDetectStrategies.DomainPathDifference(segments);
            var durationAnomalyResults = AdDetectStrategies.DurationAnomaly(segments);
            var cueResults = AdDetectStrategies.CUEMarkerDetection(segments);
            var keywordResults = AdDetectStrategies.URLKeywordMatch(segments, customKeywords);
            var consistencyResults = AdDetectStrategies.DurationConsistency(segments);

            // Map strategy name to results
            var allStrategies = new Dictionary<string, Dictionary<long, StrategyResult>>
            {
                { "DomainPath", domainPathResults },
                { "DurationAnomaly", durationAnomalyResults },
                { "CUEMarker", cueResults },
                { "URLKeyword", keywordResults },
                { "DurationConsistency", consistencyResults }
            };

            // Combine results for each segment
            List<AdDetectResult> results = new List<AdDetectResult>();

            foreach (var seg in segments)
            {
                var result = new AdDetectResult
                {
                    SegmentIndex = seg.Index,
                    StrategyScores = new Dictionary<string, double>(),
                    StrategyReasons = new Dictionary<string, string>()
                };

                double weightedSum = 0;
                double totalWeight = 0;

                foreach (var strategy in allStrategies)
                {
                    string name = strategy.Key;
                    var strategyResults = strategy.Value;

                    if (strategyResults.ContainsKey(seg.Index))
                    {
                        result.StrategyScores[name] = strategyResults[seg.Index].Score;
                        result.StrategyReasons[name] = strategyResults[seg.Index].Reason;
                        weightedSum += strategyResults[seg.Index].Score * weights[name];
                        totalWeight += weights[name];
                    }
                }

                result.Confidence = totalWeight > 0 ? weightedSum / totalWeight : 0;
                result.IsAd = result.Confidence >= threshold;

                results.Add(result);
            }

            // Build report
            var adIndices = results.Where(r => r.IsAd).Select(r => r.SegmentIndex).ToList();
            double totalAdDur = segments.Where(s => adIndices.Contains(s.Index)).Sum(s => s.Duration);
            double totalDur = segments.Sum(s => s.Duration);

            return new AdDetectionReport
            {
                AllSegments = segments,
                Results = results,
                TotalSegments = segments.Count,
                AdSegmentCount = adIndices.Count,
                TotalAdDuration = totalAdDur,
                TotalDuration = totalDur,
                Threshold = threshold,
                AdSegmentIndices = adIndices
            };
        }

        private List<SegmentInfo> ExtractSegmentInfo(JArray parts)
        {
            List<SegmentInfo> segments = new List<SegmentInfo>();
            int groupIndex = 0;

            foreach (JArray part in parts)
            {
                foreach (var seg in part)
                {
                    string url = seg["segUri"] != null ? seg["segUri"].ToString() : "";
                    Uri uri = null;
                    try { if (!string.IsNullOrEmpty(url)) uri = new Uri(url); } catch (Exception) { }

                    var info = new SegmentInfo
                    {
                        Index = seg["index"] != null ? seg["index"].Value<long>() : 0,
                        Duration = seg["duration"] != null ? seg["duration"].Value<double>() : 0,
                        Url = url,
                        Domain = uri != null ? uri.Host : "",
                        Path = uri != null ? uri.AbsolutePath : "",
                        GroupIndex = groupIndex,
                        IsInCueOut = seg["isInCueOut"] != null && seg["isInCueOut"].Value<bool>(),
                        CueOutDuration = seg["cueOutDuration"] != null ? seg["cueOutDuration"].Value<double>() : 0
                    };

                    segments.Add(info);
                }
                groupIndex++;
            }

            return segments;
        }

        /// <summary>
        /// Remove ad segments from parts JArray
        /// </summary>
        public static JArray RemoveAdSegments(JArray parts, List<long> adIndices)
        {
            JArray newParts = new JArray();
            foreach (JArray part in parts)
            {
                JArray newPart = new JArray();
                foreach (var seg in part)
                {
                    long idx = seg["index"] != null ? seg["index"].Value<long>() : -1;
                    if (!adIndices.Contains(idx))
                    {
                        newPart.Add(seg);
                    }
                }
                if (newPart.Count > 0)
                    newParts.Add(newPart);
            }
            return newParts;
        }

        /// <summary>
        /// Print detection report to console
        /// </summary>
        public static void PrintReport(AdDetectionReport report)
        {
            Console.WriteLine();
            LOGGER.PrintLine("====== 广告检测报告 ======", LOGGER.Warning);
            LOGGER.PrintLine("总分片数: " + report.TotalSegments);
            LOGGER.PrintLine("检测到广告分片: " + report.AdSegmentCount);
            LOGGER.PrintLine(string.Format("广告总时长: {0:F1}s / {1:F1}s", report.TotalAdDuration, report.TotalDuration));
            LOGGER.PrintLine(string.Format("置信度阈值: {0:F2}", report.Threshold));
            Console.WriteLine();

            if (report.AdSegmentCount > 0)
            {
                LOGGER.PrintLine("--- 广告分片详情 ---");
                var adResults = report.Results.Where(r => r.IsAd).ToList();
                foreach (var r in adResults)
                {
                    var seg = report.AllSegments.FirstOrDefault(s => s.Index == r.SegmentIndex);
                    if (seg != null)
                    {
                        Console.WriteLine(string.Format("  分片 #{0} [时长:{1:F1}s] [置信度:{2:F2}]", r.SegmentIndex, seg.Duration, r.Confidence));
                        foreach (var kv in r.StrategyScores)
                        {
                            string reason = r.StrategyReasons.ContainsKey(kv.Key) ? r.StrategyReasons[kv.Key] : "";
                            Console.WriteLine(string.Format("    {0}: {1:F2} ({2})", kv.Key, kv.Value, reason));
                        }
                    }
                }
            }
            else
            {
                LOGGER.PrintLine("未检测到广告分片。");
            }

            Console.WriteLine();
            LOGGER.PrintLine("========================", LOGGER.Warning);
        }

        /// <summary>
        /// Write detection report to JSON file
        /// </summary>
        public static void WriteReportToFile(AdDetectionReport report, string filePath)
        {
            JObject json = new JObject();
            json["totalSegments"] = report.TotalSegments;
            json["adSegmentCount"] = report.AdSegmentCount;
            json["totalAdDuration"] = report.TotalAdDuration;
            json["totalDuration"] = report.TotalDuration;
            json["threshold"] = report.Threshold;

            JArray adSegs = new JArray();
            foreach (var r in report.Results.Where(r => r.IsAd))
            {
                var seg = report.AllSegments.FirstOrDefault(s => s.Index == r.SegmentIndex);
                JObject obj = new JObject();
                obj["index"] = r.SegmentIndex;
                obj["confidence"] = r.Confidence;
                if (seg != null)
                {
                    obj["duration"] = seg.Duration;
                    obj["url"] = seg.Url;
                    obj["groupIndex"] = seg.GroupIndex;
                }
                JObject scores = new JObject();
                foreach (var kv in r.StrategyScores)
                    scores[kv.Key] = kv.Value;
                obj["strategyScores"] = scores;
                JObject reasons = new JObject();
                foreach (var kv in r.StrategyReasons)
                    reasons[kv.Key] = kv.Value;
                obj["strategyReasons"] = reasons;
                adSegs.Add(obj);
            }
            json["adSegments"] = adSegs;

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(filePath, json.ToString());
        }
    }
}
