using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AwardChecker
{
    /// <summary>
    /// Operator-driven AI assistance: builds a copy-paste prompt for ANY external
    /// AI chat (Gemini, ChatGPT, Copilot, ...) and strictly imports the structured
    /// answer the operator pastes back. AwardChecker itself never contacts a network;
    /// the operator chooses whether and where to paste the rules text.
    /// </summary>
    public static class AiRuleAssist
    {
        public const string Marker = "AWARDSCORER-IMPORT: 1";

        private static readonly Dictionary<string, CountingRule> CountingTokens =
            new Dictionary<string, CountingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["STATION_PER_PERIOD"] = CountingRule.StationPerPeriod,
                ["STATION_PER_DAY"] = CountingRule.StationPerDay,
                ["STATION_BAND_PER_PERIOD"] = CountingRule.StationBandPerPeriod,
                ["STATION_BAND_PER_DAY"] = CountingRule.StationBandPerDay,
                ["STATION_BAND_MODE_PER_PERIOD"] = CountingRule.StationBandModePerPeriod,
                ["STATION_BAND_MODE_PER_DAY"] = CountingRule.StationBandModePerDay,
                ["STATION_BAND_MODE_PER_MONTH"] = CountingRule.StationBandModePerMonth
            };

        public static bool LooksLikeStructuredAnswer(string text) =>
            !string.IsNullOrWhiteSpace(text) &&
            text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0;

        public static string BuildPrompt(string rulesText)
        {
            if (string.IsNullOrWhiteSpace(rulesText))
                throw new ArgumentException("Paste the award rules first.", nameof(rulesText));
            var prompt = new StringBuilder();
            prompt.AppendLine("You are converting amateur-radio award rules into a strict machine format.");
            prompt.AppendLine("Read the award rules at the end of this message and answer with ONLY the");
            prompt.AppendLine("following lines - no explanations, no markdown, no code fences:");
            prompt.AppendLine();
            prompt.AppendLine(Marker);
            prompt.AppendLine("AWARD: <award name>");
            prompt.AppendLine("START_UTC: <yyyy-MM-dd HH:mm>   (event start, UTC)");
            prompt.AppendLine("END_UTC: <yyyy-MM-dd HH:mm>     (event end, UTC, final minute inclusive)");
            prompt.AppendLine("MINIMUM_POINTS: <integer>       (basic qualification score; 0 if none)");
            prompt.AppendLine("COUNTING: <token>               (exactly one of:");
            prompt.AppendLine("  STATION_PER_PERIOD        = each station counts once for the whole event");
            prompt.AppendLine("  STATION_PER_DAY           = each station counts once per UTC day");
            prompt.AppendLine("  STATION_BAND_PER_PERIOD   = once per station+band for the whole event");
            prompt.AppendLine("  STATION_BAND_PER_DAY      = once per station+band per UTC day");
            prompt.AppendLine("  STATION_BAND_MODE_PER_PERIOD = once per station+band+mode for the whole event");
            prompt.AppendLine("  STATION_BAND_MODE_PER_DAY = once per station+band+mode per UTC day");
            prompt.AppendLine("  STATION_BAND_MODE_PER_MONTH = once per station+band+mode per calendar month)");
            prompt.AppendLine("BANDS: <eligible bands, e.g. 80M 40M 20M 17M 15M 12M 10M>");
            prompt.AppendLine("MODE: <mode> = <points per QSO>   (one line per eligible mode, e.g. MODE: CW = 10;");
            prompt.AppendLine("  use real logged modes such as CW, SSB, FT8, FT4, RTTY - expand DIGI to its submodes)");
            prompt.AppendLine("MAIN_STATIONS: <special-event callsigns separated by spaces; empty if not listed>");
            prompt.AppendLine("LEVEL: <category> | <level name> | POINTS = <n>   (one line per achievement level;");
            prompt.AppendLine("  use QSOS = <n> instead of POINTS for QSO-count levels; omit if the award has none)");
            prompt.AppendLine("UNRECOGNIZED: <one line per rule you could not express in this format>");
            prompt.AppendLine();
            prompt.AppendLine("Dates, times and duplicate counting MUST come from the rules text; do not guess.");
            prompt.AppendLine("If the rules do not state a value, write the line with UNKNOWN as its value.");
            prompt.AppendLine();
            prompt.AppendLine("===== AWARD RULES TEXT =====");
            prompt.AppendLine(rulesText.Trim());
            return prompt.ToString();
        }

        public static RuleImportResult ParseStructured(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
                throw new ArgumentException("Paste the AI answer first.", nameof(answer));
            var result = new RuleImportResult();
            var profile = new Profile
            {
                Name = "Imported AI answer",
                MainCalls = new List<string>(),
                MemberCalls = new List<string>(),
                Bands = new List<string>(),
                Modes = new List<ModeMapping>(),
                Levels = new List<AwardLevel>()
            };
            result.Draft = profile;
            bool sawStart = false, sawEnd = false, sawCounting = false, sawMinimum = false;
            int ignored = 0;
            foreach (string raw in answer.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) ||
                    line.Equals(Marker, StringComparison.OrdinalIgnoreCase))
                    continue;
                Match keyed = Regex.Match(line, @"^(?<key>[A-Z_]+)\s*:\s*(?<value>.*)$");
                if (!keyed.Success)
                {
                    ignored++;
                    continue;
                }
                string key = keyed.Groups["key"].Value.ToUpperInvariant();
                string value = keyed.Groups["value"].Value.Trim();
                bool unknown = value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) || value.Length == 0;
                switch (key)
                {
                    case "AWARD":
                        if (!unknown) profile.Name = value.Length <= 160 ? value : value.Substring(0, 160);
                        else Review(result, "The AI did not provide an award name.");
                        break;
                    case "START_UTC":
                    case "END_UTC":
                        bool isStart = key == "START_UTC";
                        if (unknown)
                        {
                            Review(result, "The AI marked " + key + " as UNKNOWN; enter it manually from the rules.");
                            break;
                        }
                        if (!DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime parsed))
                        {
                            Review(result, key + " is not in the required yyyy-MM-dd HH:mm format: " + value);
                            break;
                        }
                        if (isStart)
                        {
                            profile.Start = parsed.Date;
                            profile.StartTime = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
                            sawStart = true;
                        }
                        else
                        {
                            profile.End = parsed.Date;
                            profile.EndTime = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
                            sawEnd = true;
                        }
                        break;
                    case "MINIMUM_POINTS":
                        if (!unknown && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int minimum) &&
                            minimum <= 9999999)
                        {
                            profile.MinimumPoints = minimum;
                            sawMinimum = true;
                        }
                        else
                            Review(result, "MINIMUM_POINTS is missing or not a plain integer: " + value);
                        break;
                    case "COUNTING":
                        if (CountingTokens.TryGetValue(value, out CountingRule rule))
                        {
                            profile.Counting = rule;
                            profile.CountModeGroups = false;
                            sawCounting = true;
                        }
                        else
                            Review(result, "COUNTING token is not one of the supported values: " + value);
                        break;
                    case "BANDS":
                        if (unknown) { Review(result, "The AI did not provide eligible bands."); break; }
                        foreach (string token in Regex.Split(value, @"[\s,;]+").Where(t => t.Length > 0))
                        {
                            string band = token.ToUpperInvariant();
                            if (Regex.IsMatch(band, @"^\d{1,3}(\.\d+)?(M|CM)$"))
                                profile.Bands.Add(band);
                            else
                                Review(result, "Band token was rejected (expected forms like 80M, 70CM): " + token);
                        }
                        break;
                    case "MODE":
                        Match mode = Regex.Match(value,
                            @"^(?<name>[A-Z0-9]{1,10})\s*=\s*(?<points>\d{1,7})\s*(?<disabled>DISABLED)?$",
                            RegexOptions.IgnoreCase);
                        if (!mode.Success)
                        {
                            Review(result, "MODE line was rejected (expected 'MODE: CW = 10'): " + value);
                            break;
                        }
                        string name = Calls.Key(mode.Groups["name"].Value);
                        if (name == "DIGI")
                        {
                            Review(result, "MODE: DIGI must be expanded to real logged submodes (FT8, FT4, ...); it was skipped.");
                            break;
                        }
                        profile.Modes.Add(new ModeMapping
                        {
                            Mode = name,
                            Group = name == "CW" ? "CW" : name == "SSB" || name == "USB" || name == "LSB"
                                ? "SSB" : name == "FM" ? "FM" : "DIGI",
                            Points = int.Parse(mode.Groups["points"].Value, CultureInfo.InvariantCulture),
                            Enabled = !mode.Groups["disabled"].Success
                        });
                        break;
                    case "MAIN_STATIONS":
                        if (unknown) break;
                        foreach (string token in Regex.Split(value, @"[\s,;]+").Where(t => t.Length > 0))
                        {
                            if (Regex.IsMatch(token, @"^[A-Z0-9/]{3,12}$", RegexOptions.IgnoreCase) &&
                                Regex.IsMatch(token, @"\d") && Regex.IsMatch(token, @"[A-Za-z]"))
                                profile.MainCalls.Add(Calls.Key(token));
                            else
                                Review(result, "Main-station token was rejected as a callsign: " + token);
                        }
                        break;
                    case "LEVEL":
                        Match level = Regex.Match(value,
                            @"^(?<category>[^|]+)\|(?<name>[^|]+)\|\s*(?<metric>POINTS|QSOS)\s*=\s*(?<value>\d{1,7})\s*$",
                            RegexOptions.IgnoreCase);
                        if (!level.Success)
                        {
                            Review(result, "LEVEL line was rejected (expected 'LEVEL: <category> | <name> | POINTS = 300'): " + value);
                            break;
                        }
                        bool qsoMetric = level.Groups["metric"].Value.Equals("QSOS", StringComparison.OrdinalIgnoreCase);
                        int metricValue = int.Parse(level.Groups["value"].Value, CultureInfo.InvariantCulture);
                        profile.Levels.Add(new AwardLevel
                        {
                            Category = level.Groups["category"].Value.Trim(),
                            Name = level.Groups["name"].Value.Trim(),
                            Metric = qsoMetric ? AwardMetric.Qsos : AwardMetric.Points,
                            Points = qsoMetric ? 0 : metricValue,
                            Qsos = qsoMetric ? metricValue : 0,
                            MainStations = 0
                        });
                        break;
                    case "UNRECOGNIZED":
                        if (!unknown)
                            Review(result, "The AI could not express this rule; handle it manually: " + value);
                        break;
                    default:
                        ignored++;
                        break;
                }
            }
            profile.Bands = profile.Bands.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.MainCalls = profile.MainCalls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            profile.Levels = Profile.SortLevels(profile.Levels);
            profile.Scoring = profile.Modes.Any(m => m.Points.HasValue) ? PointScoring.Mode : PointScoring.Category;
            profile.CountModeGroups = false;
            if (!sawStart || !sawEnd)
                Review(result, "Start and/or end UTC boundaries are missing; enter them manually before scoring.");
            else if (profile.Start > profile.End)
                Review(result, "The imported Begin date/time is after End; correct the UTC range before scoring.");
            if (!sawCounting)
                Review(result, "No valid COUNTING token was imported; review the duplicate-counting rule.");
            if (!sawMinimum)
                Review(result, "No minimum qualification score was imported; the draft uses 0 until you enter one.");
            if (profile.Modes.Count == 0)
                Review(result, "No valid MODE lines were imported; scoring rules must be entered manually.");
            if (profile.MainCalls.Count == 0)
                Review(result, "No special-event station callsigns were imported. Add the official main-station list on tab 2 before scoring.");
            else
                Review(result, "Imported special-event callsigns: " + string.Join(", ", profile.MainCalls) +
                    ". Confirm the eligible station list on tab 2.");
            if (ignored > 0)
                Review(result, ignored + " line(s) of the pasted answer were not in the required format and were ignored.");
            result.Findings.Add(new RuleImportFinding
            {
                Status = "INFO",
                Detail = "Values came from an operator-supplied AI answer and were strictly validated locally. " +
                    "Verify every value against the official rules; AwardChecker itself sent nothing to a network."
            });
            profile.Notes = "Draft imported from an operator-supplied AI answer. Review items:\r\n" +
                string.Join("\r\n", result.Findings.Select(f => f.Status + ": " + f.Detail));
            return result;
        }

        private static void Review(RuleImportResult result, string detail) =>
            result.Findings.Add(new RuleImportFinding { Status = "REVIEW", Detail = detail });
    }
}
