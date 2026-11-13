using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace AwardChecker
{
    public enum CountingRule
    {
        StationPerPeriod, StationPerDay, StationBandPerPeriod,
        StationBandPerDay, StationBandModePerPeriod, StationBandModePerDay,
        StationBandModePerMonth
    }

    public enum PointScoring { Category, Mode }
    public enum AwardMetric { Points, Qsos }

    public sealed class ModeMapping
    {
        public bool Enabled { get; set; } = true;
        public string Mode { get; set; } = "";
        public string Group { get; set; } = "";
        public int? Points { get; set; }

        public static int? SuggestedPoints(string mode)
        {
            switch (Calls.Key(mode))
            {
                case "FT8": case "FT4": case "FT2": return 2;
                case "RTTY": case "RTTYR": case "PSK": case "PSK31": case "PSK63": case "PSK125": return 4;
                case "SSB": case "USB": case "LSB": return 7;
                case "CW": return 10;
                default: return null;
            }
        }
    }

    public sealed class AwardLevel
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "Mixed Mode";
        public AwardMetric Metric { get; set; } = AwardMetric.Points;
        public int Points { get; set; }
        public int Qsos { get; set; }
        public int MainStations { get; set; }
    }

    public sealed class Profile
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = "New award";
        public DateTime Start { get; set; } = DateTime.Today;
        public DateTime End { get; set; } = DateTime.Today;
        public string StartTime { get; set; } = "00:00";
        public string EndTime { get; set; } = "23:59";
        [XmlIgnore]
        public DateTime StartUtc => UtcMinute(Start, StartTime);
        [XmlIgnore]
        public DateTime EndUtc => UtcMinute(End, EndTime);
        [XmlIgnore]
        public DateTime EndExclusiveUtc => EndUtc.AddMinutes(1);
        public DateTime SavedUtc { get; set; }
        public string RuleSource { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<string> MainCalls { get; set; } = new List<string>();
        public List<string> MemberCalls { get; set; } = new List<string>();
        public int MainPoints { get; set; } = 3;
        public int MemberPoints { get; set; } = 1;
        public int MinimumPoints { get; set; }
        public PointScoring Scoring { get; set; } = PointScoring.Category;
        public CountingRule Counting { get; set; } = CountingRule.StationBandModePerPeriod;
        public bool CountModeGroups { get; set; } = true;
        public List<string> Bands { get; set; } = new List<string>();
        public List<ModeMapping> Modes { get; set; } = new List<ModeMapping>();
        public List<AwardLevel> Levels { get; set; } = new List<AwardLevel>();
        public string OwnStation { get; set; } = "";
        public List<ColumnSetting> Columns { get; set; } = new List<ColumnSetting>();

        public static DateTime UtcMinute(DateTime date, string time)
        {
            if (!TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out var parsed) ||
                parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
                throw new InvalidDataException("Enter a UTC time in HH:mm format between 00:00 and 23:59.");
            return DateTime.SpecifyKind(date.Date.Add(parsed), DateTimeKind.Utc);
        }

        public static List<AwardLevel> SortLevels(IEnumerable<AwardLevel> levels) =>
            levels.OrderBy(level => LevelCategoryOrder(level.Category))
                .ThenBy(level => level.Metric)
                .ThenBy(level => level.Metric == AwardMetric.Points ? level.Points : level.Qsos)
                .ThenBy(level => level.MainStations)
                .ThenBy(level => Calls.Key(level.Name), StringComparer.Ordinal)
                .ToList();

        private static int LevelCategoryOrder(string category)
        {
            switch (category)
            {
                case "Mixed Mode": return 0;
                case "CW": return 1;
                case "SSB": return 2;
                case "DIGI": return 3;
                case "Activator / All Bands": return 4;
                default: return int.MaxValue;
            }
        }

        public void Validate()
        {
            if (Version != 1) throw new InvalidDataException("Unsupported profile version.");
            if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("Enter a profile name.");
            if (Start.Date < new DateTime(1753, 1, 1) || End.Date >= new DateTime(9998, 12, 31) ||
                StartUtc > EndUtc)
                throw new InvalidDataException("Enter a valid inclusive UTC date/time range; Begin must not be after End.");
            if (!Enum.IsDefined(typeof(CountingRule), Counting))
                throw new InvalidDataException("Unsupported counting rule.");
            if (!Enum.IsDefined(typeof(PointScoring), Scoring))
                throw new InvalidDataException("Unsupported point-scoring basis.");
            if (MainPoints < 0 || MemberPoints < 0 || MinimumPoints < 0 ||
                MainPoints > 1000000 || MemberPoints > 1000000 || MinimumPoints > 1000000)
                throw new InvalidDataException("Points must be between 0 and 1,000,000.");
            MainCalls = Calls.Parse(string.Join("\n", MainCalls));
            MemberCalls = Calls.Parse(string.Join("\n", MemberCalls));
            if (MainCalls.Count + MemberCalls.Count == 0)
                throw new InvalidDataException("Add at least one main or member station.");
            Bands = Bands.Select(Calls.Key).Distinct().ToList();
            if (Bands.Count == 0 || Bands.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Select at least one valid band.");
            if (!Modes.Any(m => m.Enabled)) throw new InvalidDataException("Select at least one mode.");
            var modeNames = new HashSet<string>();
            foreach (var m in Modes)
            {
                m.Mode = Calls.Key(m.Mode);
                m.Group = Calls.Key(m.Group);
                if (m.Mode.Length == 0 || m.Group.Length == 0 || !modeNames.Add(m.Mode))
                    throw new InvalidDataException("Each selected mode needs exactly one nonempty group.");
                if (m.Points < 0 || m.Points > 1000000)
                    throw new InvalidDataException("Mode points must be between 0 and 1,000,000: " + m.Mode);
                if (Scoring == PointScoring.Mode && m.Enabled && !m.Points.HasValue)
                    throw new InvalidDataException("Enter points for every enabled mode in mode-based scoring: " + m.Mode);
            }
            if (Levels.Any(l => string.IsNullOrWhiteSpace(l.Name) || string.IsNullOrWhiteSpace(l.Category) ||
                                l.Points < 0 || l.Qsos < 0 || l.MainStations < 0 ||
                                l.MainStations > MainCalls.Count || !Enum.IsDefined(typeof(AwardMetric), l.Metric)))
                throw new InvalidDataException("Award levels need a name, nonnegative thresholds and an achievable main-station count.");
            foreach (var level in Levels)
            {
                level.Category = level.Category.Trim();
                if (LevelCategoryOrder(level.Category) == int.MaxValue)
                    throw new InvalidDataException("Unsupported award level category: " + level.Category);
                bool activator = level.Category == "Activator / All Bands";
                if (activator != (level.Metric == AwardMetric.Qsos) ||
                    (level.Metric == AwardMetric.Points && level.Qsos != 0) ||
                    (level.Metric == AwardMetric.Qsos && (level.Points != 0 || level.MainStations != 0)))
                    throw new InvalidDataException("Hunter levels use points; Activator / All Bands levels use QSO counts.");
            }
            if (Levels.GroupBy(l => Calls.Key(l.Category) + "|" + l.Metric + "|" + Calls.Key(l.Name)).Any(g => g.Count() > 1))
                throw new InvalidDataException("Award level names must be unique within each category and metric.");
            Levels = SortLevels(Levels);
            foreach (var categoryLevels in Levels.GroupBy(l => l.Category + "|" + l.Metric))
                for (int i = 1; i < categoryLevels.Count(); i++)
                {
                    var previous = categoryLevels.ElementAt(i - 1);
                    var current = categoryLevels.ElementAt(i);
                    if (current.MainStations < previous.MainStations)
                        throw new InvalidDataException("Unique main-station requirements must not decrease within a category.");
                }
            OwnStation = Calls.Key(OwnStation);
            Columns = ColumnLayouts.Normalize(Columns);
        }

        [XmlIgnore]
        public string PointRules => Scoring == PointScoring.Category
            ? "Category-based: Main=" + MainPoints.ToString(CultureInfo.InvariantCulture) +
              "; Member=" + MemberPoints.ToString(CultureInfo.InvariantCulture)
            : "Mode-based: " + string.Join("; ", Modes.Where(m => m.Enabled).Select(m =>
                m.Mode + "=" + (m.Points.HasValue ? m.Points.Value.ToString(CultureInfo.InvariantCulture) : "missing")));

    }

    public static class Calls
    {
        public static string Key(string value) => (value ?? "").Trim().ToUpperInvariant();
        public static List<string> Parse(string text, string field = "List")
        {
            var items = Regex.Split(text.Trim(), @"[\s,;]+").Where(s => s.Length > 0)
                .Select(Key).Distinct().ToList();
            string? invalid = items.FirstOrDefault(s => !Regex.IsMatch(s, @"^[A-Z0-9/.\-]+$"));
            if (invalid != null)
                throw new InvalidDataException(field + ": invalid token \"" + invalid +
                    "\". Enter callsigns/band tokens only, separated by whitespace, commas or semicolons; omit headings and rule descriptions.");
            return items;
        }
    }

    public static class Profiles
    {
        public static Profile Load(string path)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings))
            {
                var p = (Profile?)new XmlSerializer(typeof(Profile)).Deserialize(reader);
                if (p == null) throw new InvalidDataException("The profile is empty.");
                p.Validate();
                return p;
            }
        }

        public static void Save(Profile p, string path)
        {
            p.Validate();
            p.SavedUtc = DateTime.UtcNow;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
                    new XmlSerializer(typeof(Profile)).Serialize(writer, p);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class Qso
    {
        public int Number { get; set; }
        public DateTime Utc { get; set; }
        public string Call { get; set; } = "";
        public string Band { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Submode { get; set; } = "";
        public string Frequency { get; set; } = "";
        public string RstSent { get; set; } = "";
        public string RstReceived { get; set; } = "";
        public string Name { get; set; } = "";
        public string QTH { get; set; } = "";
        public string Locator { get; set; } = "";
        public string Comment { get; set; } = "";
        public string MyRig { get; set; } = "";
        public string MyAntenna { get; set; } = "";
        public string MyQTHID { get; set; } = "";
        public MyLocation Location { get; set; } = new MyLocation();
        public string Power { get; set; } = "";
        public string Station { get; set; } = "";
        public string Operator { get; set; } = "";
        public string ContestID { get; set; } = "";
        public string SRX { get; set; } = "";
        public string SRXString { get; set; } = "";
        public string STX { get; set; } = "";
        public string STXString { get; set; } = "";
        public string Complete { get; set; } = "";
    }

    public sealed class QsoResult
    {
        public string Call { get; set; } = "";
        public string Name { get; set; } = "";
        public string QTH { get; set; } = "";
        public string Locator { get; set; } = "";
        public DateTime Utc { get; set; }
        public string Band { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Submode { get; set; } = "";
        public string ModeGroup { get; set; } = "";
        public string FrequencyMHz { get; set; } = "";
        public string RstSent { get; set; } = "";
        public string RstReceived { get; set; } = "";
        public string MyRig { get; set; } = "";
        public string MyAntenna { get; set; } = "";
        public string MyQTHID { get; set; } = "";
        public string PowerWatts { get; set; } = "";
        public string Station { get; set; } = "";
        public string Operator { get; set; } = "";
        public string ContestID { get; set; } = "";
        public string SRX { get; set; } = "";
        public string SRXString { get; set; } = "";
        public string STX { get; set; } = "";
        public string STXString { get; set; } = "";
        public string Comment { get; set; } = "";
        public string Category { get; set; } = "";
        public int Points { get; set; }
        public string Status { get; set; } = "";
        public string Reason { get; set; } = "";
        [Browsable(false)]
        public MyLocation Location { get; set; } = new MyLocation();
    }

    public sealed class StationResult
    {
        public string Call { get; set; } = "";
        public string Category { get; set; } = "";
        public int CountedQsos { get; set; }
        public int Points { get; set; }
        public int ReviewQsos { get; set; }
    }

    public sealed class LevelResult
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public AwardMetric Metric { get; set; }
        public int RequiredPoints { get; set; }
        public int RequiredQsos { get; set; }
        public int RequiredMainStations { get; set; }
        public int PointsNeeded { get; set; }
        public int QsosNeeded { get; set; }
        public int MainStationsNeeded { get; set; }
        public bool Evaluated { get; set; }
        public bool Qualified { get; set; }
        public string Status => !Evaluated ? "Not evaluated (Activator log required)" : Qualified ? "Qualified" : "Not qualified";
    }

    public sealed class ScoreResult
    {
        public List<QsoResult> Qsos { get; } = new List<QsoResult>();
        public List<StationResult> Stations { get; } = new List<StationResult>();
        public List<LevelResult> Levels { get; } = new List<LevelResult>();
        public List<string> Warnings { get; } = new List<string>();
        public int TotalPoints => Qsos.Sum(q => q.Points);
        public int UniqueMain => Qsos.Where(q => q.Category == "Main" && q.Status == "Counted")
            .Select(q => q.Call).Distinct().Count();
        public int MinimumPointsRequired { get; set; }
        public bool MinimumQualificationConfigured => MinimumPointsRequired > 0;
        public bool MinimumQualified => TotalPoints >= MinimumPointsRequired;
        public int MinimumPointsNeeded => Math.Max(0, MinimumPointsRequired - TotalPoints);
        public string HighestLevel => Levels.LastOrDefault(l => l.Qualified && l.Evaluated)?.Name ?? "None";
        public string HighestLevels => string.Join("; ", Levels.Where(l => l.Qualified && l.Evaluated)
            .GroupBy(l => l.Category).Select(group => group.Key + " " + group.Last().Name));
    }

    public static class Scorer
    {
        public static ScoreResult Score(Profile profile, IEnumerable<Qso> qsos)
        {
            profile.Validate();
            var result = new ScoreResult { MinimumPointsRequired = profile.MinimumPoints };
            var main = new HashSet<string>(profile.MainCalls);
            var members = new HashSet<string>(profile.MemberCalls);
            var overlap = main.Intersect(members).OrderBy(c => c).ToList();
            if (overlap.Count > 0)
                result.Warnings.Add("Main takes precedence for overlapping calls: " + string.Join(", ", overlap));
            var modes = profile.Modes.Where(m => m.Enabled).ToDictionary(m => m.Mode);
            var seen = new HashSet<string>();
            var startUtc = profile.StartUtc;
            var endExclusiveUtc = profile.EndExclusiveUtc;
            foreach (var q in qsos.OrderBy(q => q.Utc).ThenBy(q => q.Number))
            {
                string call = Calls.Key(q.Call);
                if (q.Utc < startUtc || q.Utc >= endExclusiveUtc) continue;
                bool isMain = main.Contains(call);
                bool isMember = members.Contains(call);
                var row = new QsoResult
                {
                    Call = call, Utc = q.Utc, Name = q.Name, QTH = q.QTH, Locator = q.Locator, Comment = q.Comment,
                    Category = isMain ? "Main" : isMember ? "Member" : "Unlisted",
                    Band = Calls.Key(q.Band), Mode = Calls.Key(q.Mode), Submode = Calls.Key(q.Submode),
                    FrequencyMHz = q.Frequency, RstSent = q.RstSent, RstReceived = q.RstReceived,
                    MyRig = q.MyRig, MyAntenna = q.MyAntenna, MyQTHID = q.MyQTHID, Location = q.Location,
                    PowerWatts = q.Power, Station = q.Station, Operator = q.Operator,
                    ContestID = q.ContestID, SRX = q.SRX, SRXString = q.SRXString, STX = q.STX, STXString = q.STXString
                };
                result.Qsos.Add(row);
                if (!isMain && !isMember)
                {
                    row.Status = "Excluded";
                    row.Reason = "Callsign not listed (exact matching; review any suffix variants).";
                    continue;
                }
                if (profile.OwnStation.Length > 0 && Calls.Key(q.Station) != profile.OwnStation)
                {
                    row.Status = string.IsNullOrWhiteSpace(q.Station) ? "Review" : "Excluded";
                    row.Reason = "Station identity missing or does not match profile.";
                    continue;
                }
                if (row.Band.Length == 0 || row.Mode.Length == 0)
                {
                    row.Status = "Review"; row.Reason = "Missing band or mode."; continue;
                }
                if (!profile.Bands.Contains(row.Band))
                {
                    row.Status = "Excluded"; row.Reason = "Band not selected."; continue;
                }
                // A recorded submode is more specific than the parent mode; never silently fall back.
                string effectiveMode = row.Submode.Length > 0 ? row.Submode : row.Mode;
                if (!modes.TryGetValue(effectiveMode, out var mapping))
                {
                    row.Status = "Review";
                    row.Reason = "Mode/submode not selected or unmapped: " + effectiveMode;
                    continue;
                }
                string group = mapping.Group;
                row.ModeGroup = group;
                if (Calls.Key(q.Complete) != "Y")
                {
                    row.Status = "Review";
                    row.Reason = "QSO_COMPLETE is not Y; two-way contact needs review.";
                    continue;
                }
                string key = call;
                if (profile.Counting == CountingRule.StationBandPerPeriod ||
                    profile.Counting == CountingRule.StationBandPerDay ||
                    profile.Counting == CountingRule.StationBandModePerPeriod ||
                    profile.Counting == CountingRule.StationBandModePerDay ||
                    profile.Counting == CountingRule.StationBandModePerMonth)
                    key += "|" + row.Band;
                if (profile.Counting == CountingRule.StationBandModePerPeriod ||
                    profile.Counting == CountingRule.StationBandModePerDay ||
                    profile.Counting == CountingRule.StationBandModePerMonth)
                    key += "|" + (profile.CountModeGroups ? group : effectiveMode);
                if (profile.Counting == CountingRule.StationPerDay ||
                    profile.Counting == CountingRule.StationBandPerDay ||
                    profile.Counting == CountingRule.StationBandModePerDay)
                    key += "|" + q.Utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (profile.Counting == CountingRule.StationBandModePerMonth)
                    key += "|" + q.Utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (!seen.Add(key))
                {
                    row.Status = "Excluded"; row.Reason = "Duplicate under selected counting rule."; continue;
                }
                row.Status = "Counted";
                row.Points = profile.Scoring == PointScoring.Mode ? mapping.Points!.Value :
                    isMain ? profile.MainPoints : profile.MemberPoints;
                row.Reason = "First eligible QSO for counting key." + (profile.Scoring == PointScoring.Mode
                    ? " Mode " + effectiveMode + " awards " + row.Points.ToString(CultureInfo.InvariantCulture) + " points." : "");
            }
            foreach (string call in main.Union(members).OrderBy(c => c))
            {
                var rows = result.Qsos.Where(q => q.Call == call).ToList();
                result.Stations.Add(new StationResult
                {
                    Call = call, Category = main.Contains(call) ? "Main" : "Member",
                    CountedQsos = rows.Count(q => q.Status == "Counted"),
                    Points = rows.Sum(q => q.Points), ReviewQsos = rows.Count(q => q.Status == "Review")
                });
            }
            foreach (var level in profile.Levels)
            {
                bool evaluated = level.Category != "Activator / All Bands";
                var categoryQsos = result.Qsos.Where(q => q.Status == "Counted" &&
                    (level.Category == "Mixed Mode" || q.ModeGroup == level.Category)).ToList();
                int points = categoryQsos.Sum(q => q.Points);
                int mainStations = categoryQsos.Where(q => q.Category == "Main").Select(q => q.Call).Distinct().Count();
                int qsoCount = categoryQsos.Count;
                bool qualified = evaluated && (level.Metric == AwardMetric.Points
                    ? points >= level.Points && mainStations >= level.MainStations
                    : qsoCount >= level.Qsos);
                result.Levels.Add(new LevelResult
                {
                    Name = level.Name, Category = level.Category, Metric = level.Metric,
                    RequiredPoints = level.Points, RequiredQsos = level.Qsos,
                    RequiredMainStations = level.MainStations,
                    PointsNeeded = Math.Max(0, level.Points - points),
                    QsosNeeded = Math.Max(0, level.Qsos - qsoCount),
                    MainStationsNeeded = Math.Max(0, level.MainStations - mainStations),
                    Evaluated = evaluated, Qualified = qualified
                });
            }
            int reviews = result.Qsos.Count(q => q.Status == "Review");
            if (reviews > 0) result.Warnings.Add(reviews + " QSO(s) require review and were not scored.");
            return result;
        }
    }
}
