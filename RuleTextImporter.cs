using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AwardChecker
{
    public sealed class RuleImportFinding
    {
        public string Status { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public sealed class RuleImportResult
    {
        public Profile Draft { get; set; } = new Profile();
        public List<RuleImportFinding> Findings { get; } = new List<RuleImportFinding>();

        public string ReviewText
        {
            get
            {
                var text = new StringBuilder();
                text.AppendLine("DRAFT PROFILE");
                text.AppendLine("Name: " + Draft.Name);
                text.AppendLine("Event: " + Draft.Start.ToString("yyyy-MM-dd") + " " + Draft.StartTime +
                    " through " + Draft.End.ToString("yyyy-MM-dd") + " " + Draft.EndTime + " UTC (final minute inclusive)");
                text.AppendLine("Basic Hunter qualification: " + Draft.MinimumPoints + " points (separate from achievement levels)");
                text.AppendLine("Counting: " + Draft.Counting + (Draft.CountModeGroups ? " (mode groups)" : " (individual modes)"));
                text.AppendLine("Main stations: " + string.Join(", ", Draft.MainCalls));
                text.AppendLine("Bands: " + string.Join(", ", Draft.Bands));
                text.AppendLine("Points: " + string.Join("; ", Draft.Modes.Where(m => m.Points.HasValue)
                    .Select(m => m.Mode + "=" + m.Points.GetValueOrDefault() + " (" + (m.Enabled ? "enabled" : "disabled") + ")")));
                text.AppendLine("Achievement levels: " + string.Join("; ", Draft.Levels.Select(l =>
                    l.Category + " " + l.Name + "=" +
                    (l.Metric == AwardMetric.Points ? l.Points + " points" : l.Qsos + " QSOs"))));
                text.AppendLine();
                text.AppendLine("REVIEW ITEMS");
                foreach (var finding in Findings)
                    text.AppendLine(finding.Status + ": " + finding.Detail);
                return text.ToString();
            }
        }
    }

    public static class RuleTextImporter
    {
        private sealed class Section
        {
            public string Heading { get; set; } = "";
            public List<string> Lines { get; } = new List<string>();
        }

        private static readonly Regex NumberedHeading = new Regex(@"^\s*\d+\.\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex Points = new Regex(@"\b(\d{1,7})\s*points?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ModesInText = new Regex(
            @"\b(CW|SSB|FM|FT[0-9A-Z]+|RTTYR?|PSK[0-9]*|MFSK[0-9]*|JT[0-9]+|USB|LSB|AM)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static RuleImportResult Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Paste the award rules first.", nameof(input));

            var result = new RuleImportResult();
            string normalized = input.Replace("**", "").Replace("__", "");
            normalized = Regex.Replace(normalized,
                @"(?<!\S)(LOGS AND SCORING|AWARD CLASSES|ELIGIBILITY|DURATION|BANDS|MODES|EXCHANGE|LIMITATIONS|SCORING|PRIZES|RANKING|E-QSL|QSL|CONTACTS)(?!\S)",
                "\n$1\n");
            normalized = Regex.Replace(normalized, @"(?=•\s*\d|\b\d+\s+points?\s+for\s+each|\b\d+\.\s*(?:Bronze|Silver|Gold|Platinum)\s+Class:)", "\n",
                RegexOptions.IgnoreCase);
            List<string> lines = normalized
                .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(CleanLine)
                .Where(line => line.Length > 0).ToList();
            List<Section> sections = SplitSections(lines);
            string allText = string.Join("\n", lines);
            int minimumPoints = ExtractMinimumPoints(allText);
            var profile = new Profile
            {
                Name = ExtractTitle(allText) ?? (lines[0].Length <= 160 ? lines[0] : "Imported award rules"),
                MainCalls = new List<string>(),
                MemberCalls = new List<string>(),
                Bands = new List<string>(),
                Modes = new List<ModeMapping>(),
                Levels = new List<AwardLevel>(),
                MinimumPoints = minimumPoints
            };
            result.Draft = profile;
            if (!Regex.IsMatch(allText, @"\bminimum\s+of\s+\d{1,7}\s+points?\b", RegexOptions.IgnoreCase))
                AddFinding(result, "REVIEW", "A minimum qualification score was not recognized; the draft uses 0 until you enter one.");

            ParseDates(GetSection(sections, "Event Start") ?? GetSection(sections, "Duration")
                ?? GetSectionContaining(sections, "date", "period", "start/end"), profile, result);
            ParseBands(GetSection(sections, "Bands") ?? GetSectionContaining(sections, "band"), profile, result);
            ParseModes(GetSection(sections, "Modes") ?? GetSectionContaining(sections, "mode"),
                GetSection(sections, "Scoring") ?? GetSectionContaining(sections, "scor", "point"),
                allText, profile, result);
            ParseCounting(allText, profile, result);
            ParseLevels(GetSection(sections, "Achievement Levels") ?? GetSection(sections, "Award Classes"), profile, result);
            ParseUnsupportedRules(allText, result);
            foreach (Match match in Regex.Matches(allText,
                @"\bspecial\s+(?:anniversary|event)\s+call\s*sign\s+([A-Z0-9/]+)\b", RegexOptions.IgnoreCase))
                profile.MainCalls.AddRange(Calls.Parse(match.Groups[1].Value));
            foreach (Match match in Regex.Matches(allText,
                @"\b(?:obtain|achieve|request|receive)\s+the\s+([A-Z0-9]{3,12})\s+AWARD\b", RegexOptions.IgnoreCase))
                if (LooksLikeCallsign(match.Groups[1].Value))
                    profile.MainCalls.Add(Calls.Key(match.Groups[1].Value));
            foreach (string line in lines)
            {
                if (!Regex.IsMatch(line, @"\bspecial\b[\s\S]{0,40}\bstation", RegexOptions.IgnoreCase))
                    continue;
                foreach (Match token in Regex.Matches(line, @"(?<![A-Z0-9/])([A-Z0-9]{3,12})(?![A-Z0-9/])"))
                    if (LooksLikeCallsign(token.Groups[1].Value))
                        profile.MainCalls.Add(Calls.Key(token.Groups[1].Value));
            }
            profile.MainCalls = profile.MainCalls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (profile.MainCalls.Count == 0)
                AddFinding(result, "REVIEW", "No special-event station callsigns were found. Add the official main-station list on tab 2 before scoring.");
            else
                AddFinding(result, "REVIEW", "Explicit special-event callsign imported: " +
                    string.Join(", ", profile.MainCalls) + ". Confirm the eligible station list on tab 2.");
            AddFinding(result, "INFO", "Only explicit, locally recognized rule patterns were imported. The pasted text was not sent to an AI service or network.");
            profile.Notes = "Draft imported locally from pasted award rules. Review items:\r\n" +
                string.Join("\r\n", result.Findings.Select(f => f.Status + ": " + f.Detail));
            return result;
        }

        public static string ReadFile(string path)
        {
            string extension = Path.GetExtension(path);
            if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a plain-text .txt or Markdown .md rules file.");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string CleanLine(string line)
        {
            string clean = line.Trim();
            while (clean.StartsWith("#", StringComparison.Ordinal))
                clean = clean.Substring(1).TrimStart();
            if (clean.StartsWith("•", StringComparison.Ordinal) || clean.StartsWith("-", StringComparison.Ordinal))
                return clean.Substring(1).Trim();
            if (clean.StartsWith("o ", StringComparison.OrdinalIgnoreCase))
                return clean.Substring(2).Trim();
            return clean;
        }

        private static List<Section> SplitSections(List<string> lines)
        {
            var sections = new List<Section>();
            Section? current = null;
            foreach (string line in lines)
            {
                Match heading = NumberedHeading.Match(line);
                if (heading.Success && !Regex.IsMatch(heading.Groups[1].Value,
                    @"^(Bronze|Silver|Gold|Platinum)\s+Class:", RegexOptions.IgnoreCase))
                {
                    current = new Section { Heading = heading.Groups[1].Value };
                    sections.Add(current);
                }
                else if (Regex.IsMatch(line,
                    @"^(ELIGIBILITY|DURATION|BANDS|MODES|EXCHANGE|LIMITATIONS|SCORING|AWARD CLASSES|PRIZES|RANKING|E-QSL|QSL|LOGS AND SCORING|CONTACTS)$"))
                {
                    current = new Section { Heading = line };
                    sections.Add(current);
                }
                else if (current != null) current.Lines.Add(line);
            }
            return sections;
        }

        private static Section? GetSection(IEnumerable<Section> sections, string prefix) =>
            sections.FirstOrDefault(s => s.Heading.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static Section? GetSectionContaining(IEnumerable<Section> sections, params string[] needles) =>
            sections.FirstOrDefault(s => needles.Any(n =>
                s.Heading.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0));

        private static string? ExtractTitle(string text)
        {
            Match match = Regex.Match(text, @"\btitled\s+[“""](?<title>[^”""]+)[”""]", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["title"].Value.Trim() : null;
        }

        private static int ExtractMinimumPoints(string text)
        {
            Match match = Regex.Match(text, @"\bminimum\s+of\s+(\d{1,7})\s+points?\b", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int points)
                ? points : 0;
        }

        private static readonly Regex UtcBoundaryPhrase = new Regex(
            @"(?:(?<time>\d{1,2}[.:]\d{2})\s*(?:UTC|z)?\s*(?:of|on)\s+)?" +
            @"(?:(?<month>January|February|March|April|May|June|July|August|September|October|November|December)" +
            @"\s+(?<day>\d{1,2})(?:st|nd|rd|th)?|(?<day2>\d{1,2})(?:st|nd|rd|th)?\s+" +
            @"(?<month2>January|February|March|April|May|June|July|August|September|October|November|December))" +
            @"\s*,?\s*(?<year>\d{4})(?:\s*(?:at\s+)?(?<time2>\d{1,2}[.:]\d{2})\s*(?:UTC|z)?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void ParseDates(Section? section, Profile profile, RuleImportResult result)
        {
            if (section == null)
            {
                AddFinding(result, "REVIEW", "Event dates were not recognized; enter both UTC dates manually.");
                return;
            }
            DateTime? start = null, end = null;
            string sectionText = string.Join(" ", section.Lines);
            var boundaries = new List<(DateTime Date, string? Time)>();
            foreach (Match phrase in UtcBoundaryPhrase.Matches(sectionText))
            {
                string month = phrase.Groups["month"].Success ? phrase.Groups["month"].Value : phrase.Groups["month2"].Value;
                string day = phrase.Groups["day"].Success ? phrase.Groups["day"].Value : phrase.Groups["day2"].Value;
                if (!DateTime.TryParseExact(month + " " + day + " " + phrase.Groups["year"].Value, "MMMM d yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    continue;
                string? time = phrase.Groups["time"].Success ? phrase.Groups["time"].Value
                    : phrase.Groups["time2"].Success ? phrase.Groups["time2"].Value : null;
                boundaries.Add((date, time?.Replace('.', ':')));
            }
            if (boundaries.Count >= 2)
            {
                var first = boundaries[0];
                var last = boundaries[boundaries.Count - 1];
                profile.StartTime = NormalizeTime(first.Time, "00:00");
                profile.EndTime = NormalizeTime(last.Time, "23:59");
                start = first.Date.Add(TimeSpan.ParseExact(profile.StartTime, "hh\\:mm", CultureInfo.InvariantCulture));
                end = last.Date.Add(TimeSpan.ParseExact(profile.EndTime, "hh\\:mm", CultureInfo.InvariantCulture));
                AddFinding(result, "REVIEW", "Event boundaries interpreted as " +
                    first.Date.ToString("yyyy-MM-dd") + " " + profile.StartTime + " through " +
                    last.Date.ToString("yyyy-MM-dd") + " " + profile.EndTime +
                    " UTC" + (first.Time == null || last.Time == null ? "; missing times use 00:00/23:59" : "") +
                    ". Confirm against the official rules.");
                if (boundaries.Count > 2)
                    AddFinding(result, "REVIEW", "More than two dates were found in the date section; the first and last were used. Confirm the range.");
            }
            Match duration = Regex.Match(sectionText,
                @"\bfrom\s+(?<start>[A-Za-z]+\s+\d{1,2})(?:,\s*(?<startYear>\d{4}))?\s+to\s+(?<end>[A-Za-z]+\s+\d{1,2}),?\s+(?<endYear>\d{4})\b",
                RegexOptions.IgnoreCase);
            if (duration.Success && !start.HasValue && !end.HasValue)
            {
                string startYear = duration.Groups["startYear"].Success
                    ? duration.Groups["startYear"].Value : duration.Groups["endYear"].Value;
                if (DateTime.TryParseExact(duration.Groups["start"].Value + " " + startYear, "MMMM d yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime first) &&
                    DateTime.TryParseExact(duration.Groups["end"].Value + " " + duration.Groups["endYear"].Value,
                        "MMMM d yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime last))
                {
                    start = first;
                    end = last.AddHours(23).AddMinutes(59);
                    AddFinding(result, "REVIEW", "Duration dates imported; omitted times/timezone use 00:00 through 23:59 UTC. Confirm UTC interpretation.");
                }
            }
            foreach (string line in section.Lines)
            {
                Match match = Regex.Match(line, @"^\s*(Start|Begin|End)\s*:\s*(\d{1,2}\s+[A-Za-z]+\s+\d{4})(?:\s+(\d{2}:\d{2}))?\s*UTC\b",
                    RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                bool isStart = !match.Groups[1].Value.Equals("End", StringComparison.OrdinalIgnoreCase);
                string time = match.Groups[3].Success ? match.Groups[3].Value : isStart ? "00:00" : "23:59";
                string dateTime = match.Groups[2].Value + " " + time;
                if (!DateTime.TryParseExact(dateTime, new[] { "d MMMM yyyy HH:mm", "dd MMMM yyyy HH:mm" },
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                {
                    AddFinding(result, "REVIEW", "Could not parse the " + match.Groups[1].Value + " UTC date/time: " + dateTime);
                    continue;
                }
                if (!match.Groups[3].Success)
                    AddFinding(result, "REVIEW", "No " + match.Groups[1].Value + " time was specified; the draft uses " + time + " UTC.");
                if ((isStart ? start : end).HasValue)
                {
                    AddFinding(result, "REVIEW", "Multiple " + match.Groups[1].Value + " UTC boundaries were found; the first is retained. Confirm the range.");
                    continue;
                }
                if (isStart)
                {
                    start = parsed;
                    profile.StartTime = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
                }
                else
                {
                    end = parsed;
                    profile.EndTime = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
                }
            }
            if (start.HasValue) profile.Start = start.Value.Date;
            else AddFinding(result, "REVIEW", "Start date was not recognized; the displayed date is a placeholder.");
            if (end.HasValue) profile.End = end.Value.Date;
            else AddFinding(result, "REVIEW", "End date was not recognized; the displayed date is a placeholder.");
            if (start.HasValue && end.HasValue && start.Value > end.Value)
                AddFinding(result, "REVIEW", "The parsed Begin date/time is after End; correct the UTC range before scoring.");
        }

        private static bool LooksLikeCallsign(string token)
        {
            // Prefix with a digit inside and a letter suffix.
            return Regex.IsMatch(token, @"^[A-Z]{1,3}[0-9]{1,4}[A-Z]{1,6}$", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(token, @"^(FT[0-9]|JT[0-9]|PSK[0-9]|MFSK[0-9]|RTTY|A4)", RegexOptions.IgnoreCase);
        }

        private static string NormalizeTime(string? time, string fallback)
        {
            if (string.IsNullOrEmpty(time))
                return fallback;
            string[] parts = time!.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                hours > 23 || minutes > 59)
                return fallback;
            return hours.ToString("00", CultureInfo.InvariantCulture) + ":" +
                minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        private static void ParseBands(Section? section, Profile profile, RuleImportResult result)
        {
            if (section == null)
            {
                AddFinding(result, "REVIEW", "Band eligibility was not recognized; select eligible bands manually.");
                return;
            }
            string hfLine = section.Lines.FirstOrDefault(line => line.StartsWith("HF", StringComparison.OrdinalIgnoreCase)) ?? "";
            Match range = Regex.Match(hfLine, @"\bHF\s*:\s*(.*)", RegexOptions.IgnoreCase);
            if (range.Success)
                profile.Bands = Regex.Matches(range.Groups[1].Value, @"(?<![A-Z0-9])(\d{1,3}(?:\.\d+)?)(?![A-Z0-9])")
                    .Cast<Match>().Select(m => m.Groups[1].Value + "M").Distinct().ToList();
            if (profile.Bands.Count == 0 && Regex.IsMatch(string.Join(" ", section.Lines),
                @"\ball\s+amateur\s+radio\s+bands\s+from\s+160\s*m\s+to\s+6\s*m\b", RegexOptions.IgnoreCase))
            {
                profile.Bands = new List<string> { "160M", "80M", "60M", "40M", "30M", "20M",
                    "17M", "15M", "12M", "10M", "6M" };
                AddFinding(result, "REVIEW", "160m-to-6m range expanded to common amateur bands. Review any additional locally permitted bands before scoring.");
            }
            if (profile.Bands.Count == 0)
                AddFinding(result, "REVIEW", "HF band list was not recognized; select eligible bands manually.");
            if (section.Lines.Any(line => line.IndexOf("VHF/UHF", StringComparison.OrdinalIgnoreCase) >= 0 &&
                line.IndexOf("optional", StringComparison.OrdinalIgnoreCase) >= 0))
                AddFinding(result, "REVIEW", "VHF/UHF is marked optional and was omitted. Confirm whether to add those bands.");
        }

        private static void ParseModes(Section? modeSection, Section? scoringSection, string allText,
            Profile profile, RuleImportResult result)
        {
            if (modeSection == null)
            {
                AddFinding(result, "REVIEW", "Eligible modes were not recognized; configure modes manually.");
                return;
            }
            var eligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in modeSection.Lines)
                foreach (Match match in ModesInText.Matches(line))
                    if (!Calls.Key(match.Value).Equals("DIGI", StringComparison.OrdinalIgnoreCase))
                        eligible.Add(Calls.Key(match.Value));

            var rates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? category = null;
            if (scoringSection != null)
            {
                foreach (string line in scoringSection.Lines)
                {
                    if (Regex.IsMatch(line, @"^\s*(?:Example|e\.?g\.?|Esempio)\b", RegexOptions.IgnoreCase))
                        continue;
                    Match heading = Regex.Match(line, @"^\s*(CW|SSB|DIGI|FM)(?:\s*\([^)]*\))?\s*:\s*$", RegexOptions.IgnoreCase);
                    if (heading.Success)
                    {
                        category = Calls.Key(heading.Groups[1].Value);
                        continue;
                    }
                    Match pointsMatch = Points.Match(line);
                    if (!pointsMatch.Success || !int.TryParse(pointsMatch.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out int points)) continue;
                    List<string> namedModes = ModesInText.Matches(line).Cast<Match>()
                        .Select(m => Calls.Key(m.Value)).Distinct().ToList();
                    if (namedModes.Count == 0 && category != null && category != "DIGI")
                        namedModes.Add(category);
                    if (category == "DIGI" && namedModes.Count == 0)
                        AddFinding(result, "REVIEW", "A DIGI points rule has no explicit submode names: " + line);
                    foreach (string mode in namedModes) rates[mode] = points;
                    if (Regex.IsMatch(line, @"\bFT\s+mode\s+QSO\b", RegexOptions.IgnoreCase))
                        foreach (string mode in eligible.Where(m => m == "FT8" || m == "FT4"))
                            rates[mode] = points;
                }
            }
            if (rates.Count == 0)
                AddFinding(result, "REVIEW", "No per-mode point values were recognized; scoring rules must be entered manually.");

            bool optionalVhf = Regex.IsMatch(allText,
                @"(?:VHF\s*/\s*UHF[\s\S]{0,80}\boptional\b|\boptional\b[\s\S]{0,80}VHF\s*/\s*UHF)",
                RegexOptions.IgnoreCase);
            foreach (string mode in eligible.OrderBy(m => m, StringComparer.Ordinal))
            {
                bool hasRate = rates.TryGetValue(mode, out int points);
                string group = mode == "CW" ? "CW" : mode == "SSB" || mode == "USB" || mode == "LSB"
                    ? "SSB" : mode == "FM" ? "FM" : "DIGI";
                bool enabled = hasRate && !(mode == "FM" && optionalVhf);
                profile.Modes.Add(new ModeMapping
                {
                    Mode = mode, Group = group, Points = hasRate ? points : (int?)null, Enabled = enabled
                });
                if (!hasRate)
                    AddFinding(result, "REVIEW", mode + " is listed as eligible but has no recognized point rate; it was left disabled.");
            }
            foreach (string mode in rates.Keys.Except(eligible, StringComparer.OrdinalIgnoreCase))
                AddFinding(result, "REVIEW", mode + " has a point value but was not found in the eligible-modes section; it was not enabled.");
            if (eligible.Contains("PSK"))
                AddFinding(result, "REVIEW", "PSK was imported as the literal mode name. Confirm whether logged PSK submodes such as PSK31 should also be enabled.");
            if (optionalVhf && eligible.Contains("FM") && rates.ContainsKey("FM"))
                AddFinding(result, "REVIEW", "FM has a parsed 3-point rate but is disabled because its VHF/UHF section is optional.");
            profile.Scoring = rates.Count > 0 ? PointScoring.Mode : PointScoring.Category;
            profile.CountModeGroups = false;
        }

        private static void ParseCounting(string text, Profile profile, RuleImportResult result)
        {
            if (Regex.IsMatch(text,
                @"\bonly\s+once\s+per\s+month\s+for\s+each\s+unique\s+band\s+and\s+mode\s+combination\b", RegexOptions.IgnoreCase))
            {
                profile.Counting = CountingRule.StationBandModePerMonth;
                profile.CountModeGroups = false;
                AddFinding(result, "INFO", "Duplicate limit mapped to one QSO per station/band/individual mode/calendar month; resets at 00:00 UTC on the first day, not after 30 days.");
            }
            else if (Regex.IsMatch(text, @"one\s+QSO\s+per\s+band\s+and\s+mode\s+per\s+(?:UTC\s+)?day", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\bonce\s+(?:per|a|each)\s+(?:UTC\s+)?day\s+(?:on|in|for)?\s*(?:each|every|per)\s+band\s+and\s+(?:on\s+each\s+)?mode\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\bonce\s+per\s+band\s+and\s+mode\s+(?:per|each)\s+(?:UTC\s+)?day\b", RegexOptions.IgnoreCase))
            {
                profile.Counting = CountingRule.StationBandModePerDay;
                profile.CountModeGroups = false;
                AddFinding(result, "INFO", "Duplicate limit mapped to one QSO per station/band/individual mode/UTC day; later duplicates score zero.");
            }
            else if (Regex.IsMatch(text, @"\bonce\s+(?:per|on|for)\s+each\s+band\s+and\s+mode\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(text, @"\bonce\s+per\s+band\s+and\s+mode\b", RegexOptions.IgnoreCase))
            {
                profile.Counting = CountingRule.StationBandModePerPeriod;
                profile.CountModeGroups = false;
                AddFinding(result, "INFO", "Duplicate limit mapped to one QSO per station/band/individual mode for the whole event period.");
            }
            else if (Regex.IsMatch(text, @"\bonce\s+(?:per|a|each)\s+(?:UTC\s+)?day\b", RegexOptions.IgnoreCase))
            {
                profile.Counting = CountingRule.StationPerDay;
                profile.CountModeGroups = false;
                AddFinding(result, "INFO", "Duplicate limit mapped to one QSO per station per UTC day on any band/mode; confirm this matches the rules.");
            }
            else
                AddFinding(result, "REVIEW", "No supported duplicate-counting rule was recognized; review the counting rule.");
        }

        private static void ParseLevels(Section? section, Profile profile, RuleImportResult result)
        {
            if (section == null)
            {
                AddFinding(result, "REVIEW", "Achievement thresholds were not recognized.");
                return;
            }
            bool hunter = false, activator = false;
            string? category = null;
            var threshold = new Regex(
                @"^\s*(?<value>\d{1,7})\s*(?<unit>points?|QSOs?)?\s*(?:→|->|=>)\s*(?<name>Bronze|Silver|Gold|Platinum)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (string line in section.Lines)
            {
                Match awardClass = Regex.Match(line,
                    @"^\s*\d+\.\s*(?<name>Bronze|Silver|Gold|Platinum)\s+Class:\s*(?<value>\d{1,7})\s+points?\b",
                    RegexOptions.IgnoreCase);
                if (section.Heading.Equals("AWARD CLASSES", StringComparison.OrdinalIgnoreCase) &&
                    awardClass.Success)
                {
                    profile.Levels.Add(new AwardLevel
                    {
                        Name = awardClass.Groups["name"].Value,
                        Points = int.Parse(awardClass.Groups["value"].Value, CultureInfo.InvariantCulture)
                    });
                    continue;
                }
                string heading = Regex.Replace(line, @"[\s:]+$", "");
                if (Regex.IsMatch(heading, @"^Hunter\b", RegexOptions.IgnoreCase))
                {
                    hunter = true; activator = false; category = null;
                    continue;
                }
                if (Regex.IsMatch(heading, @"^Activator\b", RegexOptions.IgnoreCase))
                {
                    hunter = false; activator = true; category = "Activator / All Bands";
                    continue;
                }
                if (heading.Equals("Mixed Mode", StringComparison.OrdinalIgnoreCase) && hunter)
                {
                    category = "Mixed Mode";
                    continue;
                }
                if (hunter && Regex.IsMatch(heading, @"^(CW|SSB|DIGI)$", RegexOptions.IgnoreCase))
                {
                    category = Calls.Key(heading);
                    continue;
                }
                if (category == null) continue;
                Match match = threshold.Match(line);
                if (!match.Success ||
                    !int.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    continue;
                string unit = match.Groups["unit"].Value;
                bool qsoMetric = activator;
                if ((qsoMetric && !Regex.IsMatch(unit, @"^QSOs?$", RegexOptions.IgnoreCase)) ||
                    (!qsoMetric && Regex.IsMatch(unit, @"^QSOs?$", RegexOptions.IgnoreCase)))
                    continue;
                profile.Levels.Add(new AwardLevel
                {
                    Name = Calls.Key(match.Groups["name"].Value),
                    Category = category,
                    Metric = qsoMetric ? AwardMetric.Qsos : AwardMetric.Points,
                    Points = qsoMetric ? 0 : value,
                    Qsos = qsoMetric ? value : 0,
                    MainStations = 0
                });
            }
            profile.Levels = Profile.SortLevels(profile.Levels);
            if (profile.Levels.Count == 0)
                AddFinding(result, "REVIEW", "Achievement thresholds were not recognized.");
        }

        private static void ParseUnsupportedRules(string text, RuleImportResult result)
        {
            if (Regex.IsMatch(text, @"\bActivator\b", RegexOptions.IgnoreCase))
                AddFinding(result, "REVIEW", "Activator QSO thresholds are imported but cannot be evaluated from a Hunter log; activator-specific input is required.");
            if (Regex.IsMatch(text, @"\bRanking\b|\btie.?break", RegexOptions.IgnoreCase))
                AddFinding(result, "REVIEW", "Ranking and tie-break rules are not represented in the current scoring profile.");
        }

        private static void AddFinding(RuleImportResult result, string status, string detail) =>
            result.Findings.Add(new RuleImportFinding { Status = status, Detail = detail });
    }
}
