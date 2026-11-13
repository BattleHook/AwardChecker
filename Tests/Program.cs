using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.Windows.Forms;
using AwardChecker;

internal static class Tests
{
    private static int passed;
    private static readonly DateTime Day = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Test("Runtime selection prefers x64 ACE, then x86 ACE, then x86 Jet", () =>
            {
                var probe = new FakeDriverProbe(true);
                probe.SetRuntime(RuntimeArchitecture.X64, true);
                probe.SetRuntime(RuntimeArchitecture.X86, true);
                probe.SetProvider(DriverCatalog.Ace16, RuntimeArchitecture.X64, false);
                probe.SetProvider(DriverCatalog.Ace12, RuntimeArchitecture.X64, true);
                probe.SetProvider(DriverCatalog.Ace16, RuntimeArchitecture.X86, true);
                DriverSelectionResult result = DriverSelector.Select(probe);
                Equal(true, result.Success);
                Equal(RuntimeArchitecture.X64, result.Choice!.Architecture);
                Equal(DriverCatalog.Ace12, result.Choice.ProviderName);
                Equal(null, result.Warning);
            });
            Test("Runtime selection falls back to matching x86 ACE with an explicit warning", () =>
            {
                var probe = new FakeDriverProbe(true);
                probe.SetRuntime(RuntimeArchitecture.X64, false);
                probe.SetRuntime(RuntimeArchitecture.X86, true);
                probe.SetProvider(DriverCatalog.Ace16, RuntimeArchitecture.X64, true);
                probe.SetProvider(DriverCatalog.Ace16, RuntimeArchitecture.X86, false);
                probe.SetProvider(DriverCatalog.Ace12, RuntimeArchitecture.X86, true);
                DriverSelectionResult result = DriverSelector.Select(probe);
                Equal(true, result.Success);
                Equal(RuntimeArchitecture.X86, result.Choice!.Architecture);
                Equal(DriverCatalog.Ace12, result.Choice.ProviderName);
                Equal(true, result.Warning!.Contains("64-bit provider/runtime path was unavailable"));
                Equal(true, result.Warning.Contains(".NET Framework 4.8 x64 runtime is not registered"));
            });
            Test("Runtime selection uses Jet only as the warned x86 fallback and reports missing prerequisites", () =>
            {
                var fallback = new FakeDriverProbe(false);
                fallback.SetRuntime(RuntimeArchitecture.X86, true);
                fallback.SetProvider(DriverCatalog.Jet40, RuntimeArchitecture.X86, true);
                DriverSelectionResult jet = DriverSelector.Select(fallback);
                Equal(true, jet.Success);
                Equal(DriverCatalog.Jet40, jet.Choice!.ProviderName);
                Equal(RuntimeArchitecture.X86, jet.Choice.Architecture);
                Equal(true, jet.Warning!.Contains("legacy 32-bit Microsoft Jet 4.0"));
                Equal(false, fallback.CheckedArchitectures.Contains(RuntimeArchitecture.X64));

                var missing = new FakeDriverProbe(true);
                DriverSelectionResult unavailable = DriverSelector.Select(missing);
                Equal(false, unavailable.Success);
                Equal(true, unavailable.Error.Contains("No database was opened"));
                Equal(true, unavailable.Error.Contains(".NET Framework 4.8"));
                Equal(true, unavailable.Error.Contains(DriverCatalog.Ace16));
            });
            Test("Provider registration uses matching registry view, CLSID, DLL path and PE architecture", () =>
            {
                WithTempDirectory(dir =>
                {
                    string dll = Path.Combine(dir, "synthetic-ace.dll");
                    WriteSyntheticPe(dll, 0x8664);
                    var registry = new MemoryRegistry();
                    var clsid = Guid.NewGuid();
                    registry.Set(RegistryHive.LocalMachine, RegistryView.Registry64,
                        @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", "534296");
                    registry.Set(RegistryHive.ClassesRoot, RegistryView.Registry64,
                        DriverCatalog.Ace16 + @"\CLSID", null, clsid.ToString("B"));
                    registry.Set(RegistryHive.ClassesRoot, RegistryView.Registry64,
                        @"CLSID\" + clsid.ToString("B") + @"\InprocServer32", null, dll);
                    var probe = new WindowsDriverProbe(registry, new FilePeArchitectureReader(), true);
                    Equal(true, probe.CheckFramework48(RuntimeArchitecture.X64).Available);
                    Equal(true, probe.CheckOleDbProvider(DriverCatalog.Ace16, RuntimeArchitecture.X64).Available);
                    ProviderCheck wrongView = probe.CheckOleDbProvider(DriverCatalog.Ace16, RuntimeArchitecture.X86);
                    Equal(false, wrongView.Available);
                    registry.Set(RegistryHive.ClassesRoot, RegistryView.Registry32,
                        DriverCatalog.Ace16 + @"\CLSID", null, clsid.ToString("B"));
                    registry.Set(RegistryHive.ClassesRoot, RegistryView.Registry32,
                        @"CLSID\" + clsid.ToString("B") + @"\InprocServer32", null, dll);
                    ProviderCheck wrongDll = probe.CheckOleDbProvider(DriverCatalog.Ace16, RuntimeArchitecture.X86);
                    Equal(false, wrongDll.Available);
                    Equal(true, wrongDll.Detail.Contains("DLL is x64"));
                });
            });
            Test("Child runtime arguments preserve data root and reject incompatible selection", () =>
            {
                WithTempDirectory(dir =>
                {
                    string root = Path.Combine(dir, "AwardChecker data");
                    Directory.CreateDirectory(root);
                    var choice = new DriverChoice(DriverCatalog.Ace12, RuntimeArchitecture.X64, "synthetic.dll");
                    string commandLine = LaunchSettings.BuildArguments(choice, root);
                    Equal(true, commandLine.Contains("--data-root \"" + root + "\""));
                    LaunchSettings settings = LaunchSettings.Parse(new[]
                    {
                        "--provider", DriverCatalog.Ace12, "--architecture", "x64", "--data-root", root
                    });
                    Equal(DriverCatalog.Ace12, settings.ProviderName);
                    Equal(RuntimeArchitecture.X64, settings.Architecture);
                    Equal(Path.GetFullPath(root), settings.DataRoot);
                    RuntimeFolders.ConfigureApplicationRoot(settings.DataRoot);
                    Equal(Path.Combine(root, "Profiles"), RuntimeFolders.Current.Profiles);
                    RuntimeFolders.ClearConfiguredApplicationRoot();
                    Throws<ArgumentException>(() => LaunchSettings.Parse(new[]
                    {
                        "--provider", DriverCatalog.Jet40, "--architecture", "x64", "--data-root", root
                    }));
                    Throws<ArgumentException>(() => LaunchSettings.Parse(new string[0]));
                });
            });
            Test("Startup has no award template or bundled station list", () =>
            {
                using (var form = new MainForm(DriverCatalog.Jet40))
                {
                    foreach (string field in new[] { "mainCalls", "memberCalls", "source", "notes" })
                        Equal("", ((TextBox)typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)).Text);
                    var levels = (DataGridView)typeof(MainForm).GetField("levelGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
                    Equal(0, levels.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow));
                    var read = typeof(MainForm).GetMethod("ReadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    try { read.Invoke(form, null); throw new Exception("An empty station list must not be scoreable."); }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
                    {
                        Equal(true, ex.InnerException.Message.Contains("Add at least one main or member station"));
                    }
                    var station = (TextBox)typeof(MainForm).GetField("mainCalls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
                    station.Text = "TST1A";
                    var profile = (Profile)read.Invoke(form, null);
                    Equal("20M", string.Join(",", profile.Bands));
                    Equal("CW", string.Join(",", profile.Modes.Where(mode => mode.Enabled).Select(mode => mode.Mode)));
                    Equal(3, Scorer.Score(profile, new[] { Q("TST1A", DateTime.SpecifyKind(DateTime.Today.AddHours(12), DateTimeKind.Utc)) }).TotalPoints);
                    typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
                }
                Equal(0, typeof(Profile).Assembly.GetManifestResourceNames().Length);
            });
            Test("UTC time range includes Begin and the entire End minute, not adjacent QSOs", () =>
            {
                var p = Basic(); p.Start = Day.Date; p.End = Day.Date;
                p.StartTime = "12:30"; p.EndTime = "13:45";
                Equal(DateTimeKind.Utc, p.StartUtc.Kind);
                Equal(Day.Date.AddHours(12).AddMinutes(30), p.StartUtc);
                Equal(Day.Date.AddHours(13).AddMinutes(46), p.EndExclusiveUtc);
                var result = Scorer.Score(p, new[]
                {
                    Q("MAIN", p.StartUtc.AddTicks(-1)), Q("MAIN", p.StartUtc),
                    Q("MAIN", p.EndExclusiveUtc.AddTicks(-1)), Q("MAIN", p.EndExclusiveUtc)
                });
                Equal(2, result.Qsos.Count);
                Equal(p.StartUtc, result.Qsos.First().Utc);
                Equal(p.EndExclusiveUtc.AddTicks(-1), result.Qsos.Last().Utc);
                p.StartTime = "13:45";
                Equal(1, Scorer.Score(p, new[] { Q("MAIN", p.EndUtc), Q("MAIN", p.EndExclusiveUtc) }).Qsos.Count);
            });
            Test("UTC default whole-day and overnight ranges retain daily reset behavior", () =>
            {
                var p = Basic(); p.Start = Day.Date; p.End = Day.Date;
                Equal("00:00", p.StartTime); Equal("23:59", p.EndTime);
                Equal(Day.Date.AddDays(1), p.EndExclusiveUtc);
                Equal(2, Scorer.Score(p, new[]
                {
                    Q("MAIN", p.StartUtc), Q("MAIN", p.EndExclusiveUtc.AddTicks(-1)),
                    Q("MAIN", p.EndExclusiveUtc)
                }).Qsos.Count);
                p.End = p.Start.AddDays(1); p.StartTime = "23:30"; p.EndTime = "00:15";
                p.Counting = CountingRule.StationPerDay;
                var result = Scorer.Score(p, new[]
                {
                    Q("MAIN", p.StartUtc), Q("MAIN", p.End.Date),
                    Q("MAIN", p.EndExclusiveUtc)
                });
                Equal(2, result.Qsos.Count(q => q.Status == "Counted"));
            });
            Test("UTC times reject malformed and reversed same-day boundaries", () =>
            {
                foreach (string time in new[] { "", "24:00", "12:60", "9:00", "12:00:01", "-01:00" })
                {
                    var p = Basic(); p.StartTime = time;
                    Throws<InvalidDataException>(() => p.Validate());
                    p = Basic(); p.EndTime = time;
                    Throws<InvalidDataException>(() => p.Validate());
                }
                var reversed = Basic(); reversed.End = reversed.Start;
                reversed.StartTime = "13:00"; reversed.EndTime = "12:59";
                Throws<InvalidDataException>(() => reversed.Validate());
            });
            Test("UTC profile times round-trip and legacy XML retains whole-day defaults", () =>
            {
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "times.award.xml");
                    var p = Basic(); p.StartTime = "06:15"; p.EndTime = "18:40";
                    Profiles.Save(p, path);
                    var loaded = Profiles.Load(path);
                    Equal("06:15", loaded.StartTime); Equal("18:40", loaded.EndTime);
                    Equal(p.StartUtc, loaded.StartUtc); Equal(p.EndExclusiveUtc, loaded.EndExclusiveUtc);
                    var xml = System.Xml.Linq.XDocument.Load(path);
                    xml.Root!.Element("StartTime")!.Remove();
                    xml.Root.Element("EndTime")!.Remove();
                    xml.Save(path);
                    loaded = Profiles.Load(path);
                    Equal("00:00", loaded.StartTime); Equal("23:59", loaded.EndTime);
                    Equal(p.End.Date.AddDays(1), loaded.EndExclusiveUtc);
                });
            });
            Test("Rule checker imports UTC times, defaults omitted times and flags reversed ranges", () =>
            {
                var result = RuleTextImporter.Parse("2. Event Start / End Dates\n" +
                    "Begin: 1 November 2026 12:30 UTC\nEnd: 1 November 2026 13:45 UTC");
                Equal("12:30", result.Draft.StartTime); Equal("13:45", result.Draft.EndTime);
                Equal(true, result.ReviewText.Contains("2026-11-01 12:30 through 2026-11-01 13:45 UTC"));
                Equal(false, result.ReviewText.Contains("whole UTC dates"));
                result = RuleTextImporter.Parse("2. Event Start / End Dates\n" +
                    "Start: 1 November 2026 UTC\nEnd: 2 November 2026 UTC");
                Equal("00:00", result.Draft.StartTime); Equal("23:59", result.Draft.EndTime);
                Equal(2, result.Findings.Count(f => f.Detail.Contains("time was specified")));
                result = RuleTextImporter.Parse("2. Event Start / End Dates\n" +
                    "Start: 1 November 2026 13:00 UTC\nEnd: 1 November 2026 12:59 UTC");
                Equal(true, result.Findings.Any(f => f.Detail.Contains("after End")));
                result = RuleTextImporter.Parse("2. Event Start / End Dates\n" +
                    "Start: 1 November 2026 24:00 UTC\nEnd: 1 November 2026 13:00 UTC");
                Equal(true, result.Findings.Any(f => f.Detail.Contains("Could not parse")));
                result = RuleTextImporter.Parse("2. Event Start / End Dates\n" +
                    "Start: 1 November 2026 12:00 UTC\nStart: 1 November 2026 13:00 UTC");
                Equal("12:00", result.Draft.StartTime);
                Equal(true, result.Findings.Any(f => f.Detail.Contains("Multiple")));
            });
            Test("CSV and HTML report metadata preserve UTC Begin and End times", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = Basic(); p.StartTime = "06:15"; p.EndTime = "18:40";
                    var score = Scorer.Score(p, new[] { Q("MAIN", Day) });
                    string csv = Path.Combine(dir, "times.csv"), html = Path.Combine(dir, "times.html");
                    Csv.Write(csv, score.Qsos, p, Day);
                    Reports.WriteHtml(html, score, p, score.Qsos, Day);
                    foreach (string path in new[] { csv, html })
                    {
                        string text = File.ReadAllText(path);
                        Equal(true, text.Contains("2026-11-01 06:15"));
                        Equal(true, text.Contains("2026-11-30 18:40"));
                    }
                });
            });
            Test("Synthetic numbered rules draft Hunter scoring and flag limitations", () =>
            {
                string rules = @"**1. Purpose**
This synthetic parser fixture describes a contest titled “SYNTHETIC CAFÉ”.
**2. Event Start / End Dates**
Start: 28 October 2026 00:00 UTC
End: 11 November 2026 23:59 UTC
**3. QSO Requirements**
One QSO per band and mode per day is allowed.
**4. Bands**
HF: 80 / 40 / 30 / 20 / 17 / 15 / 12 / 10 metres
VHF/UHF: optional – for local participation
**5. Modes**
CW
SSB
DIGI (FT8, FT4, RTTY, PSK)
FM (VHF/UHF)
**6. Scoring / QSO**
The award requires a minimum of 50 points.
Scoring System:
CW:
Each QSO on each band → 10 points
SSB:
Each QSO on each band → 5 points
DIGI:
FT8 / FT4 → 2 points
RTTY / PSK → 5 points
FM (VHF/UHF):
Each QSO → 3 points
**9. Overall Ranking**
Rankings are based on total points and total QSOs.
**11. Special Prizes**
Activator awards.
**12. Achievement Levels**
Hunter – All Bands
Mixed Mode
500 points → Bronze
750 points → Silver
2000 points → Gold
CW
750 → Bronze
1000 → Silver
2000 → Gold
SSB
300 → Bronze
700 → Silver
1000 → Gold
DIGI
300 → Bronze
600 → Silver
1000 → Gold
Activator – All Bands
(CW / SSB / DIGI combined)
300 QSO → Bronze
500 QSO → Silver
1000 QSO → Gold";
                RuleImportResult imported = RuleTextImporter.Parse(rules);
                Profile p = imported.Draft;
                Equal("SYNTHETIC CAFÉ", p.Name);
                Equal(new DateTime(2026, 10, 28), p.Start);
                Equal(new DateTime(2026, 11, 11), p.End);
                Equal(50, p.MinimumPoints);
                Equal("80M,40M,30M,20M,17M,15M,12M,10M", string.Join(",", p.Bands));
                Equal(CountingRule.StationBandModePerDay, p.Counting);
                Equal(false, p.CountModeGroups);
                Equal(PointScoring.Mode, p.Scoring);
                Equal("CW=10,FM=3,FT4=2,FT8=2,PSK=5,RTTY=5,SSB=5",
                    string.Join(",", p.Modes.Where(m => m.Points.HasValue).OrderBy(m => m.Mode).Select(m => m.Mode + "=" + m.Points)));
                Equal(false, p.Modes.Single(m => m.Mode == "FM").Enabled);
                Equal(15, p.Levels.Count);
                Equal("Mixed Mode,CW,SSB,DIGI,Activator / All Bands",
                    string.Join(",", p.Levels.Select(l => l.Category).Distinct()));
                Equal("500,750,2000", string.Join(",", p.Levels.Where(l => l.Category == "Mixed Mode").Select(l => l.Points)));
                Equal("300,600,1000", string.Join(",", p.Levels.Where(l => l.Category == "DIGI").Select(l => l.Points)));
                Equal("300,500,1000", string.Join(",", p.Levels.Where(l => l.Category == "Activator / All Bands").Select(l => l.Qsos)));
                Equal(0, p.MainCalls.Count);
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("No special-event station callsigns")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("VHF/UHF is marked optional")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("PSK was imported as the literal mode name")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("Activator QSO thresholds")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("Ranking and tie-break")));
                Equal(false, imported.Findings.Any(f => f.Detail.Contains("cannot fit a single mixed-mode profile")));
                p.MainCalls.Add("MAIN");
                p.Validate();
                var score = Scorer.Score(p, new[]
                {
                    Q("MAIN", new DateTime(2026, 10, 28), "80M", "CW"),
                    Q("MAIN", new DateTime(2026, 10, 28, 1, 0, 0), "80M", "CW")
                });
                Equal(10, score.TotalPoints);
                Equal("Excluded", score.Qsos[1].Status);
                Equal(40, score.MinimumPointsNeeded);
                Equal(false, score.MinimumQualified);
            });
            Test("Synthetic prose rules import boundaries, rates, counting and the award station", () =>
            {
                string rules = @"Synthetic November Activity
2. Start/end date of the award
From 00.00 UTC of November 1, 2026 to 23.59 UTC of November 30, 2026.
4. Bands
HF: 80-40-30-20-17-15-12-10 mt
5. Modes
CW, SSB, DIGI (FT8, FT4)
6. Scores/QSOs
To obtain the TST1A AWARD, a minimum of 100 points is required.
CW: 10 points for each CW QSO on each band.
Example TST1A special station: CW on 40m, 20m and 10m; 30 points in total.
SSB: 5 points for each SSB QSO on each band.
Example TST1A special station: LSB on 80m and USB on 15m; 10 points in total.
DIGI: 2 points for each FT8, FT4 on each band.
7. QSO Credit
Each special station can be worked once per day on each band and mode.
9. Overall Score Lists
The ranking is calculated based on highest score.";
                RuleImportResult imported = RuleTextImporter.Parse(rules);
                Profile p = imported.Draft;
                Equal("Synthetic November Activity", p.Name);
                Equal(new DateTime(2026, 11, 1), p.Start);
                Equal(new DateTime(2026, 11, 30), p.End);
                Equal("00:00", p.StartTime); Equal("23:59", p.EndTime);
                Equal(100, p.MinimumPoints);
                Equal("80M,40M,30M,20M,17M,15M,12M,10M", string.Join(",", p.Bands));
                Equal(CountingRule.StationBandModePerDay, p.Counting);
                Equal(PointScoring.Mode, p.Scoring);
                Equal("CW=10,FT4=2,FT8=2,SSB=5",
                    string.Join(",", p.Modes.Where(m => m.Points.HasValue).OrderBy(m => m.Mode).Select(m => m.Mode + "=" + m.Points)));
                Equal(true, p.Modes.Where(m => m.Points.HasValue).All(m => m.Enabled));
                Equal("TST1A", string.Join(",", p.MainCalls));
                Equal(false, imported.Findings.Any(f => f.Detail.Contains("No per-mode point values")));
                Equal(false, imported.Findings.Any(f => f.Detail.Contains("No supported duplicate-counting")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("Event boundaries interpreted as 2026-11-01 00:00 through 2026-11-30 23:59")));
            });
            Test("AI-assist prompt round-trip imports a strict structured answer and rejects bad tokens", () =>
            {
                string prompt = AiRuleAssist.BuildPrompt("Some rules body");
                Equal(true, prompt.Contains(AiRuleAssist.Marker));
                Equal(true, prompt.Contains("STATION_BAND_MODE_PER_DAY"));
                Equal(true, prompt.Contains("Some rules body"));
                Throws<ArgumentException>(() => AiRuleAssist.BuildPrompt("  "));
                string answer = @"Here is the extraction you asked for:
```
AWARDSCORER-IMPORT: 1
AWARD: Synthetic November Activity
START_UTC: 2026-11-01 00:00
END_UTC: 2026-11-30 23:59
MINIMUM_POINTS: 100
COUNTING: STATION_BAND_MODE_PER_DAY
BANDS: 80M 40M 30M 20M 17M 15M 12M 10M
MODE: CW = 10
MODE: SSB = 5
MODE: FT8 = 2
MODE: FT4 = 2
MODE: DIGI = 2
MODE: RTTY = banana
MAIN_STATIONS: TST1A NOTACALL
LEVEL: Mixed Mode | Bronze | POINTS = 300
LEVEL: Activator | Gold | QSOS = 1000
BANDS: 23X
UNRECOGNIZED: QSY requests by band or mode are forbidden.
```
Let me know if you need anything else!";
                Equal(true, AiRuleAssist.LooksLikeStructuredAnswer(answer));
                Equal(false, AiRuleAssist.LooksLikeStructuredAnswer("plain rules text"));
                RuleImportResult imported = AiRuleAssist.ParseStructured(answer);
                Profile p = imported.Draft;
                Equal("Synthetic November Activity", p.Name);
                Equal(new DateTime(2026, 11, 1), p.Start); Equal("00:00", p.StartTime);
                Equal(new DateTime(2026, 11, 30), p.End); Equal("23:59", p.EndTime);
                Equal(100, p.MinimumPoints);
                Equal(CountingRule.StationBandModePerDay, p.Counting);
                Equal("80M,40M,30M,20M,17M,15M,12M,10M", string.Join(",", p.Bands));
                Equal("CW=10,FT4=2,FT8=2,SSB=5",
                    string.Join(",", p.Modes.Where(m => m.Points.HasValue).OrderBy(m => m.Mode).Select(m => m.Mode + "=" + m.Points)));
                Equal("TST1A", string.Join(",", p.MainCalls));
                Equal(2, p.Levels.Count);
                Equal(1000, p.Levels.Single(l => l.Category == "Activator").Qsos);
                Equal(PointScoring.Mode, p.Scoring);
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("MODE: DIGI must be expanded")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("MODE line was rejected") && f.Detail.Contains("banana")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("rejected as a callsign: NOTACALL")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("Band token was rejected") && f.Detail.Contains("23X")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("QSY requests")));
                Equal(true, imported.Findings.Any(f => f.Detail.Contains("operator-supplied AI answer")));
                RuleImportResult sparse = AiRuleAssist.ParseStructured(
                    "AWARDSCORER-IMPORT: 1\nAWARD: UNKNOWN\nSTART_UTC: UNKNOWN\nEND_UTC: 2026-11-30 23:59\nCOUNTING: ONCE_PER_WEEK");
                Equal(true, sparse.Findings.Any(f => f.Detail.Contains("START_UTC as UNKNOWN")));
                Equal(true, sparse.Findings.Any(f => f.Detail.Contains("Start and/or end UTC boundaries are missing")));
                Equal(true, sparse.Findings.Any(f => f.Detail.Contains("COUNTING token is not one of the supported values: ONCE_PER_WEEK")));
                Equal(true, sparse.Findings.Any(f => f.Detail.Contains("No valid MODE lines")));
                Throws<ArgumentException>(() => AiRuleAssist.ParseStructured(" "));
            });
            Test("Rule files read locally from TXT or Markdown with unsupported extensions rejected", () =>
            {
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "award-rules.md");
                    string text = "## 1. Purpose\r\nSynthetic contest titled “SYNTHETIC CAFÉ”.\r\nScoring: FT8 → 2 points.";
                    File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                    Equal(text, RuleTextImporter.ReadFile(path));
                    Equal("SYNTHETIC CAFÉ", RuleTextImporter.Parse(RuleTextImporter.ReadFile(path)).Draft.Name);
                    string unsupported = Path.Combine(dir, "award-rules.pdf");
                    File.WriteAllText(unsupported, "not parsed");
                    Throws<InvalidDataException>(() => RuleTextImporter.ReadFile(unsupported));
                });
                Test("Unnumbered anniversary rules import monthly individual-mode rates and classes", () =>
                {
                    CheckMonthlyDraft(MonthlyRules);
                    CheckMonthlyDraft(MonthlyRules.Replace("\r", "").Replace("\n", " "));
                });
                Test("Calendar-month counting resets at UTC month/year boundaries, not after 30 days", () =>
                {
                    var p = RuleTextImporter.Parse(MonthlyRules).Draft;
                    var contacts = new[]
                    {
                        Q("TST1A", new DateTime(2026, 4, 30, 23, 59, 59), "20M", "CW"),
                        Q("TST1A", new DateTime(2026, 5, 1), "20M", "CW"),
                        Q("TST1A", new DateTime(2026, 5, 31), "20M", "CW"),
                        Q("TST1A", new DateTime(2026, 5, 2), "40M", "CW"),
                        Q("TST1A", new DateTime(2026, 5, 3), "20M", "FT8"),
                        Q("TST1A", new DateTime(2026, 5, 3), "20M", "FT4"),
                        Q("OTHER1A", new DateTime(2026, 5, 1), "20M", "CW")
                    };
                    var score = Scorer.Score(p, contacts.Reverse());
                    Equal(17, score.TotalPoints);
                    Equal(5, score.Qsos.Count(q => q.Status == "Counted"));
                    Equal(true, score.Qsos.Single(q => q.Utc == new DateTime(2026, 5, 31)).Reason.Contains("Duplicate"));
                    p.Start = new DateTime(2026, 12, 1); p.End = new DateTime(2027, 12, 31);
                    Equal(15, Scorer.Score(p, new[]
                    {
                        Q("TST1A", new DateTime(2026, 12, 31, 23, 59, 59)),
                        Q("TST1A", new DateTime(2027, 1, 1)),
                        Q("TST1A", new DateTime(2027, 12, 1))
                    }).TotalPoints);
                });
                Test("Monthly counting preserves grouping choice, station separation and XML/report contracts", () =>
                {
                    var p = RuleTextImporter.Parse(MonthlyRules).Draft;
                    p.MemberCalls.Add("MEMBER");
                    p.CountModeGroups = true;
                    Equal(2, Scorer.Score(p, new[]
                    {
                        Q("TST1A", new DateTime(2026, 5, 1), "20M", "FT8"),
                        Q("TST1A", new DateTime(2026, 5, 2), "20M", "FT4"),
                        Q("MEMBER", new DateTime(2026, 5, 2), "20M", "FT4")
                    }).TotalPoints);
                    WithTempDirectory(dir =>
                    {
                        string path = Path.Combine(dir, "monthly.award.xml");
                        Profiles.Save(p, path);
                        var loaded = Profiles.Load(path);
                        Equal(CountingRule.StationBandModePerMonth, loaded.Counting);
                        Equal(true, loaded.CountModeGroups);
                        Equal(true, File.ReadAllText(path).Contains("StationBandModePerMonth"));
                        var score = Scorer.Score(loaded, new[] { Q("TST1A", new DateTime(2026, 5, 1)) });
                        string html = Path.Combine(dir, "monthly.html");
                        Reports.WriteHtml(html, score, loaded, score.Qsos, Day);
                        Equal(true, File.ReadAllText(html).Contains("StationBandModePerMonth"));
                    });
                    Equal(5, (int)CountingRule.StationBandModePerDay);
                    Equal(6, (int)CountingRule.StationBandModePerMonth);
                });
                Test("Monthly review rows do not consume a counted key; effective submodes stay distinct", () =>
                {
                    var p = RuleTextImporter.Parse(MonthlyRules).Draft;
                    var incomplete = Q("TST1A", new DateTime(2026, 4, 1));
                    incomplete.Complete = "P";
                    var unknown = Q("TST1A", new DateTime(2026, 4, 2));
                    unknown.Submode = "UNKNOWN";
                    var ft4 = Q("TST1A", new DateTime(2026, 4, 3), "20M", "MFSK");
                    ft4.Submode = "FT4";
                    var score = Scorer.Score(p, new[]
                    {
                        incomplete, unknown, ft4,
                        Q("TST1A", new DateTime(2026, 4, 4)),
                        Q("TST1A", new DateTime(2026, 4, 4), "20M", "FT8")
                    });
                    Equal(7, score.TotalPoints);
                    Equal(2, score.Qsos.Count(q => q.Status == "Review"));
                });
            });
            Test("Minimum qualification is explicit, validated and backward compatible", () =>
            {
                var p = Basic();
                p.MinimumPoints = 5;
                var score = Scorer.Score(p, new[] { Q("MAIN", Day) });
                Equal(3, score.TotalPoints);
                Equal(5, score.MinimumPointsRequired);
                Equal(true, score.MinimumQualificationConfigured);
                Equal(false, score.MinimumQualified);
                Equal(2, score.MinimumPointsNeeded);
                p.MinimumPoints = 1000001;
                Throws<InvalidDataException>(() => p.Validate());
                p.MinimumPoints = 0;
                Equal(false, Scorer.Score(p, new[] { Q("MAIN", Day) }).MinimumQualificationConfigured);
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "legacy-minimum.award.xml");
                    Profiles.Save(p, path);
                    string xml = File.ReadAllText(path).Replace("<MinimumPoints>0</MinimumPoints>", "");
                    File.WriteAllText(path, xml);
                    Equal(0, Profiles.Load(path).MinimumPoints);
                });
            });
            Test("Rule import only returns a draft after explicit apply", () =>
            {
                using (var dialog = new RuleImportDialog())
                {
                    dialog.StartPosition = FormStartPosition.Manual;
                    dialog.Location = new Point(-20000, -20000);
                    dialog.ShowInTaskbar = false;
                    dialog.Show(); Application.DoEvents();
                    var input = (TextBox)typeof(RuleImportDialog).GetField("pastedRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
                    var apply = (Button)typeof(RuleImportDialog).GetField("apply", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
                    Equal(false, apply.Enabled);
                    input.Text = "1. Purpose\r\nContest titled “Local review test”.";
                    typeof(RuleImportDialog).GetMethod("Analyze", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
                    Equal(true, apply.Enabled);
                    Equal(null, dialog.Draft);
                    apply.PerformClick();
                    Equal(DialogResult.OK, dialog.DialogResult);
                    Equal("Local review test", dialog.Draft!.Name);
                }
            });
            Test("Invalid list data is actionable and handled as a user error", () =>
            {
                var handler = typeof(MainForm).GetMethod("IsUserError", BindingFlags.Static | BindingFlags.NonPublic)!;
                Equal(true, (bool)handler.Invoke(null, new object[] { new InvalidDataException("invalid input") }));
                Equal(false, (bool)handler.Invoke(null, new object[] { new NullReferenceException() }));
                try
                {
                    Calls.Parse("MAIN; FT8: 2", "Main station list");
                    throw new Exception("Expected list validation to fail.");
                }
                catch (InvalidDataException ex)
                {
                    Equal(true, ex.Message.Contains("Main station list"));
                    Equal(true, ex.Message.Contains("\"FT8:\""));
                }
                Equal("MAIN,MEMBER", string.Join(",", Calls.Parse(" main; MEMBER,MAIN ")));
                Throws<InvalidDataException>(() => Calls.Parse("IQ\u00D8PG", "Main station list"));
                Equal("IQ0PG", Calls.Parse("IQ0PG")[0]);
            });
            Test("Mode-based points override categories for main and member contacts", () =>
            {
                var p = ModeProfile();
                var contacts = p.Modes.SelectMany((m, i) => new[]
                {
                    Q("MAIN", Day.AddMinutes(i), "20M", m.Mode),
                    Q("MEMBER", Day.AddMinutes(i), "20M", m.Mode)
                }).ToList();
                var score = Scorer.Score(p, contacts);
                Equal(p.Modes.Count * 2, score.Qsos.Count(q => q.Status == "Counted"));
                Equal(p.Modes.Sum(m => m.Points!.Value) * 2, score.TotalPoints);
                foreach (var row in score.Qsos)
                {
                    Equal(p.Modes.Single(m => m.Mode == row.Mode).Points!.Value, row.Points);
                    Equal(true, row.Reason.Contains("Mode " + row.Mode));
                }
            });
            Test("Mode points use submode without guessing or changing eligibility", () =>
            {
                var p = ModeProfile();
                var usb = Q("MAIN", Day, "20M", "SSB"); usb.Submode = "USB";
                var ft4 = Q("MAIN", Day.AddMinutes(1), "20M", "MFSK"); ft4.Submode = "FT4";
                var unknown = Q("MAIN", Day.AddMinutes(2)); unknown.Submode = "UNKNOWN";
                var unlisted = Q("OTHER", Day.AddMinutes(3), "20M", "CW");
                var incomplete = Q("MEMBER", Day.AddMinutes(4)); incomplete.Complete = "N";
                var score = Scorer.Score(p, new[] { usb, ft4, unknown, unlisted, incomplete });
                Equal(9, score.TotalPoints);
                Equal("Review", score.Qsos[2].Status); Equal("Excluded", score.Qsos[3].Status);
                Equal("Review", score.Qsos[4].Status);
            });
            Test("Mode points do not change duplicate grouping or choose highest points", () =>
            {
                var p = ModeProfile(); p.CountModeGroups = true;
                p.Modes.Single(m => m.Mode == "CW").Group = "DIGI";
                var score = Scorer.Score(p, new[]
                {
                    Q("MAIN", Day, "20M", "FT8"), Q("MAIN", Day.AddMinutes(1), "20M", "CW"),
                    Q("MAIN", Day.AddMinutes(2), "20M", "FT4")
                });
                Equal(2, score.TotalPoints); Equal(1, score.Qsos.Count(q => q.Status == "Counted"));
                p.CountModeGroups = false;
                Equal(14, Scorer.Score(p, new[]
                {
                    Q("MAIN", Day, "20M", "FT8"), Q("MAIN", Day.AddMinutes(1), "20M", "CW"),
                    Q("MAIN", Day.AddMinutes(2), "20M", "FT4")
                }).TotalPoints);
            });
            Test("Mode-based validation rejects missing, negative and excessive points", () =>
            {
                var p = ModeProfile(); p.Modes[0].Points = null;
                Throws<InvalidDataException>(() => p.Validate());
                p.Modes[0].Enabled = false; p.Validate();
                p.Modes[0].Points = -1; Throws<InvalidDataException>(() => p.Validate());
                p.Modes[0].Points = 1000001; Throws<InvalidDataException>(() => p.Validate());
                p.Modes[0].Points = 0; p.Modes[0].Enabled = true; p.Validate();
                Equal(0, Scorer.Score(p, new[] { Q("MAIN", Day, "20M", p.Modes[0].Mode) }).TotalPoints);
                p.Scoring = (PointScoring)99; Throws<InvalidDataException>(() => p.Validate());
                Equal(null, ModeMapping.SuggestedPoints("UNSPECIFIED"));
            });
            Test("Legacy profiles remain category-based; mode rates and basis round-trip", () =>
            {
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "points.award.xml");
                    var p = ModeProfile(); Profiles.Save(p, path);
                    var copy = Profiles.Load(path);
                    Equal(PointScoring.Mode, copy.Scoring); Equal(10, copy.Modes.Single(m => m.Mode == "CW").Points!.Value);
                    Equal(10, Scorer.Score(copy, new[] { Q("MAIN", Day) }).TotalPoints);
                    string xml = File.ReadAllText(path).Replace("<Scoring>Mode</Scoring>", "");
                    File.WriteAllText(path, xml);
                    var legacy = Profiles.Load(path);
                    Equal(PointScoring.Category, legacy.Scoring);
                    Equal(3, Scorer.Score(legacy, new[] { Q("MAIN", Day) }).TotalPoints);
                    Equal(PointScoring.Category, new Profile().Scoring);
                });
            });
            Test("HTML and CSV expose the point basis and mode rates", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = ModeProfile(); p.MinimumPoints = 15;
                    var score = Scorer.Score(p, new[] { Q("MAIN", Day) });
                    string html = Path.Combine(dir, "mode.html"), csv = Path.Combine(dir, "mode.csv");
                    Reports.WriteHtml(html, score, p, score.Qsos, Day); Csv.Write(csv, score.Qsos, p, Day);
                    Equal(true, File.ReadAllText(html).Contains("Mode-based:"));
                    Equal(true, File.ReadAllText(html).Contains("CW=10"));
                    Equal(true, File.ReadAllText(html).Contains("Minimum qualification: not met (5 points needed) (15 points required)"));
                    Equal(true, File.ReadAllText(csv).Contains("\"PointScoring\",\"PointRules\""));
                    Equal(true, File.ReadAllText(csv).Contains("\"MinimumQualificationPoints\""));
                    Equal(true, File.ReadAllText(csv).Contains("CW=10"));
                });
            });
            Test("Inclusive end date and exclusive following midnight", () =>
            {
                var p = Basic();
                var score = Scorer.Score(p, new[]
                {
                    Q("MAIN", Day.AddDays(-1)), Q("MAIN", new DateTime(2026, 11, 30, 23, 59, 59)),
                    Q("MAIN", new DateTime(2026, 12, 1)), Q("MEMBER", new DateTime(2026, 11, 1))
                });
                Equal(2, score.Qsos.Count); Equal(4, score.TotalPoints);
            });
            foreach (CountingRule rule in Enum.GetValues(typeof(CountingRule)))
                Test("Counting rule " + rule, () =>
                {
                    var p = Basic(); p.Counting = rule;
                    var contacts = new[]
                    {
                        Q("MAIN", Day), Q("MAIN", Day.AddMinutes(1)),
                        Q("MAIN", Day.AddDays(1)), Q("MAIN", Day.AddMinutes(2), "40M"),
                        Q("MAIN", Day.AddMinutes(3), "20M", "SSB")
                    };
                    int expected = rule == CountingRule.StationPerPeriod ? 1 :
                        rule == CountingRule.StationPerDay || rule == CountingRule.StationBandPerPeriod ? 2 :
                        rule == CountingRule.StationBandModePerDay ? 4 : 3;
                    Equal(expected * 3, Scorer.Score(p, contacts).TotalPoints);
                });
            Test("FT8 and FT4 count together; CW and bands separately", () =>
            {
                var p = Basic();
                var score = Scorer.Score(p, new[]
                {
                    Q("MAIN", Day, "20M", "FT8"), Q("MAIN", Day.AddMinutes(1), "20M", "FT4"),
                    Q("MAIN", Day.AddMinutes(2), "20M", "CW"), Q("MAIN", Day.AddMinutes(3), "40M", "FT4")
                });
                Equal(9, score.TotalPoints); Equal(1, score.UniqueMain);
            });
            Test("Individual mode counting separates FT8 and FT4", () =>
            {
                var p = Basic(); p.CountModeGroups = false;
                Equal(6, Scorer.Score(p, new[] { Q("MAIN", Day, "20M", "FT8"), Q("MAIN", Day, "20M", "FT4") }).TotalPoints);
            });
            Test("Mode submode takes precedence", () =>
            {
                var q = Q("MAIN", Day, "20M", "MFSK"); q.Submode = "FT4";
                Equal("DIGI", Scorer.Score(Basic(), new[] { q }).Qsos.Single().ModeGroup);
                q.Submode = "UNKNOWN";
                Equal("Review", Scorer.Score(Basic(), new[] { q }).Qsos.Single().Status);
            });
            Test("UTC daily reset crosses midnight", () =>
            {
                var p = Basic(); p.Counting = CountingRule.StationPerDay;
                Equal(6, Scorer.Score(p, new[]
                {
                    Q("MAIN", new DateTime(2026, 11, 1, 23, 59, 59)),
                    Q("MAIN", new DateTime(2026, 11, 2, 0, 0, 0))
                }).TotalPoints);
            });
            Test("Main category wins overlap with warning", () =>
            {
                var p = Basic(); p.MemberCalls.Add("MAIN");
                var r = Scorer.Score(p, new[] { Q("MAIN", Day) });
                Equal(3, r.TotalPoints); Equal("Main", r.Qsos.Single().Category);
                Equal(1, r.Warnings.Count); Equal(2, r.Stations.Count);
            });
            Test("Case and whitespace normalization, exact calls only", () =>
            {
                var p = Basic();
                var r = Scorer.Score(p, new[] { Q(" main ", Day), Q("MAIN/P", Day) });
                Equal(3, r.TotalPoints); Equal("Excluded", r.Qsos.Last().Status);
            });
            Test("Band exclusion and unknown mode review", () =>
            {
                var r = Scorer.Score(Basic(), new[] { Q("MAIN", Day, "10M"), Q("MAIN", Day, "20M", "UNKNOWN") });
                Equal(0, r.TotalPoints); Equal("Excluded", r.Qsos[0].Status); Equal("Review", r.Qsos[1].Status);
            });
            Test("Unchecked modes do not score", () =>
            {
                var p = Basic(); p.Modes.Single(m => m.Mode == "CW").Enabled = false;
                Equal(0, Scorer.Score(p, new[] { Q("MAIN", Day) }).TotalPoints);
            });
            Test("Incomplete QSO held for review, does not consume duplicate key", () =>
            {
                var q = Q("MAIN", Day); q.Complete = "N";
                var r = Scorer.Score(Basic(), new[] { q, Q("MAIN", Day.AddMinutes(1)) });
                Equal(3, r.TotalPoints); Equal("Review", r.Qsos.First().Status);
            });
            Test("Missing band, mode and completion held for review", () =>
            {
                var missing = Q("MAIN", Day); missing.Complete = "";
                Equal(3, Scorer.Score(Basic(), new[] { Q("MAIN", Day, ""), Q("MAIN", Day, "20M", ""), missing })
                    .Qsos.Count(q => q.Status == "Review"));
            });
            Test("Station identity filtering", () =>
            {
                var p = Basic(); p.OwnStation = "OWN";
                var good = Q("MAIN", Day); good.Station = "own";
                var other = Q("MEMBER", Day); other.Station = "OTHER";
                var missing = Q("MAIN", Day);
                var r = Scorer.Score(p, new[] { good, other, missing });
                Equal(3, r.TotalPoints); Equal(1, r.Qsos.Count(q => q.Status == "Review"));
                Equal(1, r.Qsos.Count(q => q.Status == "Excluded"));
            });
            Test("Earliest QSO wins regardless of input order", () =>
            {
                var r = Scorer.Score(Basic(), new[] { Q("MAIN", Day.AddHours(1)), Q("MAIN", Day) });
                Equal(Day, r.Qsos.Single(q => q.Status == "Counted").Utc);
            });
            Test("Points and unique main thresholds are AND conditions", () =>
            {
                var p = Basic();
                p.MainCalls.Add("SECOND");
                p.Levels.Add(new AwardLevel { Name = "Target", Points = 3, MainStations = 2 });
                var r = Scorer.Score(p, new[] { Q("MAIN", Day), Q("MEMBER", Day), Q("MAIN", Day, "40M") });
                Equal(7, r.TotalPoints); Equal(false, r.Levels.Single().Qualified);
                Equal(1, r.Levels.Single().MainStationsNeeded); Equal(0, r.Levels.Single().PointsNeeded);
                r = Scorer.Score(p, new[] { Q("MAIN", Day), Q("SECOND", Day) });
                Equal("Target", r.HighestLevel);
            });
            Test("Zero-point main still counts as a unique contacted station", () =>
            {
                var p = Basic(); p.MainPoints = 0;
                var r = Scorer.Score(p, new[] { Q("MAIN", Day) });
                Equal(0, r.TotalPoints); Equal(1, r.UniqueMain);
            });
            Test("Synthetic multi-station Bronze threshold", () =>
            {
                var p = SyntheticProfile();
                var qs = p.MainCalls.Take(5).SelectMany(c => new[] { Q(c, Day, "20M", "SSB"), Q(c, Day, "40M", "SSB") });
                var r = Scorer.Score(p, qs); Equal(30, r.TotalPoints); Equal("Bronze", r.HighestLevel);
            });
            Test("Category levels sort, score independently and leave Activator unevaluated", () =>
            {
                var p = ModeProfile();
                p.Levels = new List<AwardLevel>
                {
                    new AwardLevel { Name = "Bronze", Category = "DIGI", Points = 2 },
                    new AwardLevel { Name = "Silver", Category = "Mixed Mode", Points = 20 },
                    new AwardLevel { Name = "Bronze", Category = "CW", Points = 10 },
                    new AwardLevel { Name = "Bronze", Category = "Mixed Mode", Points = 17 },
                    new AwardLevel { Name = "Bronze", Category = "SSB", Points = 7 },
                    new AwardLevel { Name = "Bronze", Category = "Activator / All Bands", Metric = AwardMetric.Qsos, Qsos = 300 }
                };
                p.Validate();
                Equal("Mixed Mode:Bronze,Mixed Mode:Silver,CW:Bronze,SSB:Bronze,DIGI:Bronze,Activator / All Bands:Bronze",
                    string.Join(",", p.Levels.Select(l => l.Category + ":" + l.Name)));
                var r = Scorer.Score(p, new[]
                {
                    Q("MAIN", Day, "20M", "CW"), Q("MAIN", Day.AddMinutes(1), "20M", "SSB"),
                    Q("MAIN", Day.AddMinutes(2), "20M", "FT8")
                });
                Equal(19, r.TotalPoints);
                Equal(true, r.Levels.Single(l => l.Category == "Mixed Mode" && l.Name == "Bronze").Qualified);
                Equal(false, r.Levels.Single(l => l.Category == "Mixed Mode" && l.Name == "Silver").Qualified);
                Equal(true, r.Levels.Single(l => l.Category == "CW").Qualified);
                Equal(true, r.Levels.Single(l => l.Category == "SSB").Qualified);
                Equal(true, r.Levels.Single(l => l.Category == "DIGI").Qualified);
                var activator = r.Levels.Single(l => l.Category == "Activator / All Bands");
                Equal(false, activator.Evaluated);
                Equal("Not evaluated (Activator log required)", activator.Status);
                Equal("Mixed Mode Bronze; CW Bronze; SSB Bronze; DIGI Bronze", r.HighestLevels);
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "category-levels.html");
                    Reports.WriteHtml(path, r, p, r.Qsos, Day);
                    string html = File.ReadAllText(path);
                    Equal(true, html.Contains("CW"));
                    Equal(true, html.Contains("Activator / All Bands"));
                    Equal(true, html.Contains("Not evaluated (Activator log required)"));
                    Equal(true, html.Contains("Highest levels: Mixed Mode Bronze; CW Bronze; SSB Bronze; DIGI Bronze"));
                });
            });
            Test("Legacy profiles default levels to Mixed Mode and retain separate minimum points", () =>
            {
                WithTempDirectory(dir =>
                {
                    string path = Path.Combine(dir, "legacy-categories.award.xml");
                    var p = Basic();
                    p.MinimumPoints = 50;
                    p.Levels = new List<AwardLevel>
                    {
                        new AwardLevel { Name = "BASIC", Points = 50 },
                        new AwardLevel { Name = "BRONZE", Points = 500 },
                        new AwardLevel { Name = "SILVER", Points = 750 },
                        new AwardLevel { Name = "GOLD", Points = 2000 }
                    };
                    Profiles.Save(p, path);
                    string xml = File.ReadAllText(path)
                        .Replace("<Category>Mixed Mode</Category>", "")
                        .Replace("<Metric>Points</Metric>", "")
                        .Replace("<Qsos>0</Qsos>", "");
                    File.WriteAllText(path, xml);
                    var legacy = Profiles.Load(path);
                    Equal(50, legacy.MinimumPoints);
                    Equal(true, legacy.Levels.All(l =>
                        l.Category == "Mixed Mode" && l.Metric == AwardMetric.Points && l.Qsos == 0));
                    Profiles.Save(legacy, path);
                    Equal(4, Profiles.Load(path).Levels.Count);
                });
            });
            Test("Sort levels automatically and reject impossible thresholds or duplicate mode mapping", () =>
            {
                var p = Basic(); p.Levels.Add(new AwardLevel { Name = "Bad", MainStations = 2 });
                Throws<InvalidDataException>(() => p.Validate());
                p = Basic(); p.Levels.Add(new AwardLevel { Name = "Higher", Points = 10 });
                p.Levels.Add(new AwardLevel { Name = "Lower", Points = 1 });
                p.Validate();
                Equal("Lower,Higher", string.Join(",", p.Levels.Select(l => l.Name)));
                p = Basic(); p.Modes.Add(new ModeMapping { Mode = "CW", Group = "OTHER" });
                Throws<InvalidDataException>(() => p.Validate());
            });
            Test("Reject reversed dates, empty band/mode eligibility and invalid call text", () =>
            {
                var p = Basic(); p.End = p.Start.AddDays(-1);
                Throws<InvalidDataException>(() => p.Validate());
                p = Basic(); p.Bands.Clear(); Throws<InvalidDataException>(() => p.Validate());
                p = Basic(); p.Modes.Clear(); Throws<InvalidDataException>(() => p.Validate());
                Throws<InvalidDataException>(() => Calls.Parse("CALL 'SQL'"));
            });
            Test("Profile round-trip and save-as preserve modes, groups and timestamps", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = Basic(); p.Modes.Add(new ModeMapping { Enabled = false, Mode = "CUSTOM", Group = "MYGROUP" });
                    var first = Path.Combine(dir, "first.award.xml");
                    var second = Path.Combine(dir, "second.award.xml");
                    Profiles.Save(p, first);
                    var loaded = Profiles.Load(first);
                    Equal(false, loaded.Modes.Last().Enabled); Equal("MYGROUP", loaded.Modes.Last().Group);
                    Equal(true, loaded.SavedUtc > DateTime.UtcNow.AddMinutes(-1));
                    loaded.Name = "Copy"; Profiles.Save(loaded, second);
                    Equal("Test", Profiles.Load(first).Name); Equal("Copy", Profiles.Load(second).Name);
                    loaded.Name = "Replace"; Profiles.Save(loaded, second);
                    Equal("Replace", Profiles.Load(second).Name);
                });
            });
            Test("Profile rejects DTD and unsupported versions", () =>
            {
                WithTempDirectory(dir =>
                {
                    var path = Path.Combine(dir, "bad.xml");
                    File.WriteAllText(path, "<!DOCTYPE Profile [<!ENTITY x SYSTEM 'file:///no'>]><Profile/>");
                    Throws<InvalidOperationException>(() => Profiles.Load(path));
                    var p = Basic(); p.Version = 99;
                    Throws<InvalidDataException>(() => Profiles.Save(p, path));
                });
            });
            Test("CSV quoting, formula safety and profile metadata", () =>
            {
                Equal("\"'=SUM(1)\"", Csv.Cell("=SUM(1)")); Equal("\"a,\"\"b\"", Csv.Cell("a,\"b"));
                WithTempDirectory(dir =>
                {
                    var path = Path.Combine(dir, "qsos.csv");
                    var p = Basic();
                    var q = Q("MAIN", Day);
                    q.ContestID = "DEMO-CONTEST"; q.SRX = "12"; q.SRXString = "Received exchange";
                    q.STX = "34"; q.STXString = "Sent exchange";
                    Csv.Write(path, Scorer.Score(p, new[] { q }).Qsos, p, Day);
                    string[] lines = File.ReadAllLines(path);
                    Equal(2, lines.Length); Equal(true, lines[0].Contains("EndInclusiveUTC"));
                    Equal(true, lines[1].Contains("\"MAIN\"")); Equal(false, lines[0].Contains("ContactedRig"));
                    Equal(false, lines[0].Contains("QsoNumber")); Equal(true, lines[0].Contains("MyQTHID"));
                    foreach (string column in new[] { "ContestID", "SRX", "SRXString", "STX", "STXString" })
                        Equal(true, lines[0].Contains("\"" + column + "\""));
                    foreach (string value in new[] { "DEMO-CONTEST", "12", "Received exchange", "34", "Sent exchange" })
                        Equal(true, lines[1].Contains("\"" + value + "\""));
                });
            });
            Test("Result grids support sorting", () =>
            {
                var list = new SortableList<StationResult>(new List<StationResult>
                {
                    new StationResult { Call = "Z", Points = 1 }, new StationResult { Call = "A", Points = 3 }
                });
                ((IBindingList)list).ApplySort(TypeDescriptor.GetProperties(typeof(StationResult))["Call"], ListSortDirection.Ascending);
                Equal("A", list.First().Call);
            });
            Test("QSO details preserve own equipment and logged contact fields", () =>
            {
                var q = Q("MAIN", Day); q.MyRig = "My rig";
                q.MyAntenna = "Dipole"; q.MyQTHID = "HOME"; q.Power = "100";
                q.Name = "Example"; q.QTH = "Sample City"; q.Locator = "JO00AA"; q.Comment = "Hello";
                q.RstSent = "-10"; q.RstReceived = "-12"; q.Frequency = "14.074";
                q.ContestID = "DEMO-CONTEST"; q.SRX = "12"; q.SRXString = "Received exchange";
                q.STX = "34"; q.STXString = "Sent exchange";
                var row = Scorer.Score(Basic(), new[] { q }).Qsos.Single();
                Equal("My rig", row.MyRig);
                Equal("Dipole", row.MyAntenna); Equal("HOME", row.MyQTHID);
                Equal(q.Name, row.Name); Equal(q.QTH, row.QTH); Equal(q.Locator, row.Locator); Equal(q.Comment, row.Comment);
                Equal("100", row.PowerWatts); Equal("-10", row.RstSent); Equal("-12", row.RstReceived);
                Equal("DEMO-CONTEST", row.ContestID); Equal("12", row.SRX); Equal("Received exchange", row.SRXString);
                Equal("34", row.STX); Equal("Sent exchange", row.STXString);
            });
            Test("SSB parent and explicitly grouped USB/LSB modes/submodes count together", () =>
            {
                var p = SyntheticProfile();
                var call = p.MainCalls.First();
                var usb = Q(call, Day.AddMinutes(1), "20M", "SSB"); usb.Submode = "USB";
                var lsb = Q(call, Day.AddMinutes(2), "20M", "SSB"); lsb.Submode = "LSB";
                var r = Scorer.Score(p, new[] { Q(call, Day, "20M", "SSB"), usb, lsb,
                    Q(call, Day.AddMinutes(3), "20M", "USB"), Q(call, Day.AddMinutes(4), "20M", "LSB") });
                Equal(3, r.TotalPoints); Equal(1, r.UniqueMain);
                Equal(true, r.Qsos.All(q => q.ModeGroup == "SSB"));
                Equal(4, r.Qsos.Count(q => q.Status == "Excluded"));
            });
            Test("Column defaults match result fields; Category immediately before Points", () =>
            {
                var columns = ColumnLayouts.AppDefault();
                var properties = TypeDescriptor.GetProperties(typeof(QsoResult)).Cast<PropertyDescriptor>()
                    .Where(p => p.IsBrowsable).Select(p => p.Name).OrderBy(n => n);
                Equal(string.Join(",", properties), string.Join(",", columns.Select(c => c.Name).OrderBy(n => n)));
                Equal(columns.FindIndex(c => c.Name == "Category") + 1, columns.FindIndex(c => c.Name == "Points"));
                foreach (string column in new[] { "ContestID", "SRX", "SRXString", "STX", "STXString" })
                    Equal(true, columns.Any(c => c.Name == column));
                Equal(false, columns.Any(c => c.Name == "ContactedRig" || c.Name == "QsoNumber"));
            });
            Test("Profile-specific layout survives save/load and does not affect app defaults", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = Basic(); p.Columns = ColumnLayouts.AppDefault();
                    var column = p.Columns.Single(c => c.Name == "Comment");
                    column.Visible = false; column.Width = 210; p.Columns.Remove(column); p.Columns.Insert(0, column);
                    var path = Path.Combine(dir, "layout.award.xml"); Profiles.Save(p, path);
                    var copy = Profiles.Load(path);
                    Equal("Comment", copy.Columns.First().Name); Equal(false, copy.Columns.First().Visible);
                    Equal(210, copy.Columns.First().Width);
                    Equal("Call", ColumnLayouts.AppDefault().First().Name);
                    Equal("Call", ColumnLayouts.Normalize(new List<ColumnSetting>()).First().Name);
                });
            });
            Test("Award-independent user default save/restore does not alter other profiles", () =>
            {
                WithTempDirectory(dir =>
                {
                    var a = Basic(); a.Columns = ColumnLayouts.AppDefault();
                    var b = Basic(); b.Columns = ColumnLayouts.AppDefault();
                    a.Columns.Reverse(); a.Columns[0].Visible = false;
                    a.Columns[0].Width = 175;
                    string path = Path.Combine(dir, "column-default.xml");
                    ColumnLayouts.SaveDefault(path, a.Columns);
                    a.Columns = ColumnLayouts.AppDefault();
                    var restored = ColumnLayouts.LoadDefault(path);
                    Equal("Reason", restored[0].Name); Equal(false, restored[0].Visible);
                    Equal(175, restored[0].Width); Equal(0, b.Columns[0].Width);
                    Equal("Call", b.Columns[0].Name);
                    Equal("Reason", ColumnLayouts.LoadDefault(path)[0].Name);
                    ColumnLayouts.SaveDefault(path, b.Columns);
                    Equal("Call", ColumnLayouts.LoadDefault(path)[0].Name);
                });
            });
            Test("Reject empty visibility, duplicated and unknown columns", () =>
            {
                var settings = ColumnLayouts.AppDefault(); settings.ForEach(c => c.Visible = false);
                Throws<InvalidDataException>(() => ColumnLayouts.Normalize(settings));
                settings = ColumnLayouts.AppDefault(); settings.Add(new ColumnSetting { Name = "Call" });
                Throws<InvalidDataException>(() => ColumnLayouts.Normalize(settings));
                Throws<InvalidDataException>(() => ColumnLayouts.Normalize(new[] { new ColumnSetting { Name = "Unknown" } }));
                Throws<InvalidDataException>(() => ColumnLayouts.Normalize(new[] { new ColumnSetting { Name = "Call", Width = -1 } }));
                Throws<InvalidDataException>(() => ColumnLayouts.Normalize(new[] { new ColumnSetting { Name = "Call", Width = 65537 } }));
            });
            Test("Location sections retain global duplicate rules and flag missing records", () =>
            {
                var a = Q("MAIN", Day); a.MyQTHID = "HOME";
                a.Location = new MyLocation { ID = "HOME", Found = true, Street = "Example Street" };
                var b = Q("MAIN", Day.AddMinutes(1)); b.MyQTHID = "FIELD";
                b.Location = new MyLocation { ID = "FIELD" };
                var r = Scorer.Score(Basic(), new[] { a, b });
                Equal(3, r.TotalPoints); Equal(1, r.UniqueMain);
                var sections = Reports.Sections(r.Qsos);
                Equal(2, sections.Count); Equal(1, sections.Sum(s => s.Qsos.Count(q => q.Status == "Counted")));
                Equal(true, sections.Single(s => s.ID == "HOME").Header.Contains("Example Street"));
                Equal(true, sections.Single(s => s.ID == "FIELD").Header.Contains("unavailable"));
            });
            Test("Sectioned HTML escapes logged data and respects visible column order", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = Basic(); p.Columns = ColumnLayouts.AppDefault();
                    p.Columns.ForEach(c => c.Visible = new[] { "Call", "ContestID", "SRX", "SRXString", "STX", "STXString", "Comment" }.Contains(c.Name));
                    var q = Q("MAIN", Day); q.Comment = "<script>alert(1)</script>";
                    q.ContestID = "DEMO-CONTEST"; q.SRX = "12"; q.SRXString = "Received exchange";
                    q.STX = "34"; q.STXString = "Sent exchange";
                    q.Location = new MyLocation { ID = "HOME", Found = true, Street = "Street & Road" }; q.MyQTHID = "HOME";
                    var r = Scorer.Score(p, new[] { q }); string path = Path.Combine(dir, "report.html");
                    Reports.WriteHtml(path, r, p, r.Qsos, Day);
                    string html = File.ReadAllText(path);
                    Equal(true, html.Contains("Combined award score: 3")); Equal(true, html.Contains("Street &amp; Road"));
                    Equal(false, html.Contains("<script>")); Equal(true, html.Contains("&lt;script&gt;"));
                    Equal(false, html.Contains("<th>Name</th>"));
                    Equal(true, html.IndexOf("<th>Call</th>", StringComparison.Ordinal) < html.IndexOf("<th>Comment</th>", StringComparison.Ordinal));
                    foreach (string column in new[] { "ContestID", "SRX", "SRXString", "STX", "STXString" })
                        Equal(true, html.Contains("<th>" + column + "</th>"));
                    foreach (string value in new[] { "DEMO-CONTEST", "12", "Received exchange", "34", "Sent exchange" })
                        Equal(true, html.Contains("<td>" + value + "</td>"));
                    Equal(true, html.Contains("white-space:nowrap"));
                    Equal(true, html.Contains("table-layout:auto;width:max-content"));
                    Equal(true, html.Contains("td.comment{white-space:pre-wrap;"));
                    Equal(true, html.Contains("<td class=\"comment\">&lt;script&gt;"));
                });
            });
            Test("HTML uses saved widths and leaves absent Submode empty", () =>
            {
                WithTempDirectory(dir =>
                {
                    var p = Basic(); p.Columns = ColumnLayouts.AppDefault();
                    p.Columns.ForEach(c => c.Visible = c.Name == "Submode" || c.Name == "Comment");
                    p.Columns.Single(c => c.Name == "Comment").Width = 160;
                    var q = Q("MAIN", Day); q.Comment = new string('X', 300);
                    var result = Scorer.Score(p, new[] { q });
                    string path = Path.Combine(dir, "widths.html");
                    Reports.WriteHtml(path, result, p, result.Qsos, Day);
                    string html = File.ReadAllText(path);
                    Equal(true, html.Contains("<th>Submode</th>"));
                    Equal(true, html.Contains("<tr><td></td><td class=\"sized\"><div style=\"width:149px\">"));
                    Equal(true, html.Contains("overflow-wrap:anywhere"));
                });
            });
            Test("Portable runtime folders stay beside the executable, independent of working directory", () =>
            {
                string root = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                Equal(Path.Combine(root, "Profiles"), RuntimeFolders.Current.Profiles);
                Equal(Path.Combine(root, "Reports"), RuntimeFolders.Current.Reports);
                Equal(Path.Combine(root, "Defaults", "User-Columns.xml"), ColumnLayouts.UserDefaultPath);
                WithTempDirectory(dir =>
                {
                    var folders = new RuntimeFolders(dir);
                    RuntimeFolders.Ensure(folders.Profiles);
                    RuntimeFolders.Ensure(folders.Reports);
                    try
                    {
                        var profile = Basic();
                        string path = Path.Combine(folders.Profiles, "example.award.xml");
                        Profiles.Save(profile, path);
                        ColumnLayouts.SaveDefault(folders.UserColumns, profile.Columns);
                        Equal("Test", Profiles.Load(path).Name);
                        Equal(ColumnLayouts.AppDefault().Count, ColumnLayouts.LoadDefault(folders.UserColumns).Count);
                        Equal(true, Directory.Exists(folders.Reports));
                    }
                    finally
                    {
                        foreach (string folder in new[] { folders.Profiles, folders.Defaults, folders.Reports })
                        {
                            if (!Directory.Exists(folder)) continue;
                            foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
                            Directory.Delete(folder);
                        }
                    }
                });
            });
            Test("Last database preference survives restart, replacement and missing database", () =>
            {
                WithTempDirectory(dir =>
                {
                    string settings = Path.Combine(dir, "Last-Database.txt");
                    string first = Path.Combine(dir, "first.mdb");
                    string second = Path.Combine(dir, "second.mdb");
                    Equal("", DatabasePreference.Load(settings));
                    File.WriteAllText(first, "synthetic path fixture, not a database");
                    File.WriteAllText(second, "synthetic path fixture, not a database");
                    DatabasePreference.Save(settings, first);
                    Equal(first, DatabasePreference.Load(settings));
                    DatabasePreference.Save(settings, second);
                    Equal(second, DatabasePreference.Load(settings));
                    File.Delete(second);
                    Equal(second, DatabasePreference.Load(settings));
                    Throws<FileNotFoundException>(() => DatabasePreference.Save(settings, second));
                    File.WriteAllText(settings, "relative.mdb");
                    Throws<InvalidDataException>(() => DatabasePreference.Load(settings));
                    Equal(Path.Combine(RuntimeFolders.Current.Defaults, "Last-Database.txt"), RuntimeFolders.Current.LastDatabase);
                });
            });
            Test("Missing database reports an error", () =>
                Throws<FileNotFoundException>(() => Database.Read("not-present.mdb", Day, Day, DriverCatalog.Jet40)));

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--database" && i + 1 < args.Length)
                {
                    string path = args[++i];
                    Test("Actual selected DXKeeper database: read-only field mapping and scoring", () =>
                    {
                        var p = Basic();
                        var qsos = Database.Read(path, p.Start, p.End, DriverCatalog.Jet40);
                        Equal(true, qsos.Count > 0);
                        Equal(true, qsos.All(q => q.Utc >= p.Start && q.Utc < p.End.AddDays(1)));
                        Equal(true, qsos.All(q => q.Utc.Kind == DateTimeKind.Utc));
                        var r = Scorer.Score(p, qsos);
                        Console.WriteLine("  Database rows in period: " + qsos.Count + "; listed rows: " +
                            r.Qsos.Count(q => q.Category != "Unlisted") + "; review rows: " + r.Qsos.Count(q => q.Status == "Review"));
                        Console.WriteLine("  Mapped detail values present: frequency=" + qsos.Count(q => q.Frequency.Length > 0) +
                            ", RST sent=" + qsos.Count(q => q.RstSent.Length > 0) +
                            ", RST received=" + qsos.Count(q => q.RstReceived.Length > 0) +
                            ", name=" + qsos.Count(q => q.Name.Length > 0) +
                            ", QTH=" + qsos.Count(q => q.QTH.Length > 0) +
                            ", locator=" + qsos.Count(q => q.Locator.Length > 0) +
                            ", comment=" + qsos.Count(q => q.Comment.Length > 0) +
                            ", TX power=" + qsos.Count(q => q.Power.Length > 0) +
                            ", own rig=" + qsos.Count(q => q.MyRig.Length > 0) +
                            ", own antenna=" + qsos.Count(q => q.MyAntenna.Length > 0) +
                            ", location records=" + qsos.Count(q => q.Location.Found));
                        Equal(true, qsos.Any(q => q.Location.Found));
                        Equal(true, qsos.Any(q => q.Locator.Length > 0));
                    });
                }
                else if (args[i] == "--preview" && i + 1 < args.Length)
                {
                    string dir = args[++i];
                    Test("Rendered desktop preview and UI round-trip", () => Preview(dir));
                }
                else if (args[i] == "--rule-import-preview" && i + 1 < args.Length)
                {
                    string path = args[++i];
                    Test("Rendered pasted-rule import preview", () => RuleImportPreview(path));
                }
                else if (args[i] == "--utc-time-preview" && i + 1 < args.Length)
                {
                    string dir = args[++i];
                    Test("Rendered UTC time controls and profile round-trip", () => UtcTimePreview(dir));
                }
                else if (args[i] == "--award-rules-preview" && i + 2 < args.Length)
                {
                    string rules = RuleTextImporter.ReadFile(args[++i]);
                    string directory = args[++i];
                    Test("Supplied anniversary rules file and applied editor previews", () =>
                    {
                        CheckMonthlyDraft(rules);
                        AwardRulesPreview(rules, directory);
                    });
                }
                else if (args[i] == "--summary-preview" && i + 1 < args.Length)
                {
                    string path = args[++i];
                    Test("Rendered scoring summary preview", () => SummaryPreview(path));
                }
                else if (args[i] == "--tab-font-preview" && i + 1 < args.Length)
                {
                    string directory = args[++i];
                    Test("Active 11pt green / inactive 9pt black bold tabs preserve contents at default/minimum sizes", () =>
                        TabFontPreview(directory));
                }
                else if (args[i] == "--level-editor-preview" && i + 1 < args.Length)
                {
                    string path = args[++i];
                    Test("Rendered category award-level editor preview", () => LevelEditorPreview(path));
                }
                else if (args[i] == "--profile-readonly" && i + 1 < args.Length)
                {
                    string path = args[++i];
                    Test("Read-only profile compatibility check", () =>
                    {
                        var profile = Profiles.Load(path);
                        Equal(50, profile.MinimumPoints);
                        Equal("BASIC,BRONZE,SILVER,GOLD", string.Join(",", profile.Levels.Select(level => level.Name)));
                        Equal(true, profile.Levels.All(level =>
                            level.Category == "Mixed Mode" && level.Metric == AwardMetric.Points));
                    });
                }
                else throw new ArgumentException("Expected --database <path>, --preview <folder>, --rule-import-preview <file>, --summary-preview <file>, --level-editor-preview <file> or --profile-readonly <file>.");
            }
            Console.WriteLine("PASS: " + passed + " tests.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
    }

    private static Profile ModeProfile()
    {
        var p = Basic(); p.Scoring = PointScoring.Mode; p.CountModeGroups = false;
        p.Modes = Calls.Parse("FT8 FT4 FT2 RTTY RTTYR PSK PSK31 PSK63 PSK125 SSB USB LSB CW")
            .Select(m => new ModeMapping { Mode = m, Group = m == "USB" || m == "LSB" ? "SSB" :
                m == "CW" || m == "SSB" ? m : "DIGI", Points = ModeMapping.SuggestedPoints(m) }).ToList();
        return p;
    }

    private static Profile Basic() => new Profile
    {
        Name = "Test", Start = new DateTime(2026, 11, 1), End = new DateTime(2026, 11, 30),
        MainCalls = new List<string> { "MAIN" }, MemberCalls = new List<string> { "MEMBER" },
        Bands = new List<string> { "20M", "40M" }, Modes = new List<ModeMapping>
        {
            new ModeMapping { Mode = "CW", Group = "CW" }, new ModeMapping { Mode = "SSB", Group = "SSB" },
            new ModeMapping { Mode = "FT8", Group = "DIGI" }, new ModeMapping { Mode = "FT4", Group = "DIGI" }
        }
    };
    private static Profile SyntheticProfile()
    {
        var profile = Basic();
        profile.Name = "Synthetic activity";
        profile.MainCalls = Enumerable.Range(1, 16).Select(i => "TEST" + i + "A").ToList();
        profile.MemberCalls = Enumerable.Range(1, 242).Select(i => "DEMO" + i + "B").ToList();
        profile.Modes.Add(new ModeMapping { Mode = "USB", Group = "SSB" });
        profile.Modes.Add(new ModeMapping { Mode = "LSB", Group = "SSB" });
        profile.Levels = new List<AwardLevel>
        {
            new AwardLevel { Name = "Bronze", Points = 30, MainStations = 5 },
            new AwardLevel { Name = "Silver", Points = 60, MainStations = 8 }
        };
        return profile;
    }

    private const string MonthlyRules = @"Synthetic Monthly Activity
Fixture special anniversary call sign TST1A.
DURATION
From April 1 to December 31, 2026.
BANDS
Fixture band range: all amateur radio bands from 160m to 6m.
MODES
CW, SSB, RTTY, FT (FT8/FT4).
LIMITATIONS
Fixture credit: only once per month for each unique band and mode combination.
SCORING
1 point for each FT mode QSO
2 points for each SSB mode QSO
3 points for each RTTY mode QSO
5 points for each CW mode QSO
AWARD CLASSES
1.Bronze Class: 12 points
2.Silver Class: 25 points
3.Gold Class: 45 points
4. Platinum Class: 75 points";

    private static void CheckMonthlyDraft(string rules)
    {
        var result = RuleTextImporter.Parse(rules);
        var p = result.Draft;
        Equal(new DateTime(2026, 4, 1), p.Start);
        Equal(new DateTime(2026, 12, 31), p.End);
        Equal("00:00", p.StartTime); Equal("23:59", p.EndTime);
        Equal("TST1A", string.Join(",", p.MainCalls));
        Equal("160M,80M,60M,40M,30M,20M,17M,15M,12M,10M,6M", string.Join(",", p.Bands));
        Equal("CW=5,FT4=1,FT8=1,RTTY=3,SSB=2",
            string.Join(",", p.Modes.Where(m => m.Enabled).Select(m => m.Mode + "=" + m.Points)));
        Equal(PointScoring.Mode, p.Scoring);
        Equal(false, p.CountModeGroups);
        Equal(CountingRule.StationBandModePerMonth, p.Counting);
        Equal("12,25,45,75", string.Join(",", p.Levels.Select(l => l.Points)));
        Equal(true, p.Levels.All(l => l.Category == "Mixed Mode" &&
            l.Metric == AwardMetric.Points && l.MainStations == 0));
        Equal(0, p.MinimumPoints);
        Equal(true, result.Findings.Any(f => f.Detail.Contains("Confirm UTC interpretation")));
        p.Validate();
    }
    private static Qso Q(string call, DateTime utc, string band = "20M", string mode = "CW") =>
        new Qso { Call = call, Utc = utc, Band = band, Mode = mode, Complete = "Y" };
    private static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Test(string title, Action run)
    {
        Console.Write("TEST " + title + " ... "); run(); passed++; Console.WriteLine("PASS");
    }
    private static void WithTempDirectory(Action<string> run)
    {
        string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { run(dir); }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Preview(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            Capture(form, Path.Combine(directory, "preview-startup.png"));
            form.Size = new Size(1100, 780);
            Application.DoEvents();
            Capture(form, Path.Combine(directory, "preview-startup-minimum.png"));
            form.Size = new Size(1280, 900);
            var read = typeof(MainForm).GetMethod("ReadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var applyCategorizedProfile = typeof(MainForm).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var categorized = SyntheticProfile();
            categorized.MinimumPoints = 50;
            categorized.Levels = new List<AwardLevel>
            {
                new AwardLevel { Name = "Bronze", Category = "DIGI", Points = 300 },
                new AwardLevel { Name = "Bronze", Category = "CW", Points = 750 },
                new AwardLevel { Name = "Bronze", Category = "Mixed Mode", Points = 500 },
                new AwardLevel { Name = "Bronze", Category = "SSB", Points = 300 },
                new AwardLevel { Name = "Bronze", Category = "Activator / All Bands", Metric = AwardMetric.Qsos, Qsos = 300 }
            };
            applyCategorizedProfile.Invoke(form, new object?[] { categorized, null });
            var levelsGrid = (DataGridView)typeof(MainForm).GetField("levelGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var categoryCwRow = levelsGrid.Rows.Cast<DataGridViewRow>()
                .Single(row => !row.IsNewRow && ((AwardLevel)row.DataBoundItem).Category == "CW");
            levelsGrid.CurrentCell = categoryCwRow.Cells["Category"];
            categoryCwRow.Cells["Category"].Value = "Activator / All Bands";
            levelsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            levelsGrid.EndEdit(); Application.DoEvents();
            var changedLevel = (AwardLevel)categoryCwRow.DataBoundItem;
            Equal(AwardMetric.Qsos, changedLevel.Metric);
            Equal(0, changedLevel.Points);
            Equal(0, changedLevel.MainStations);
            applyCategorizedProfile.Invoke(form, new object?[] { categorized, null });
            var profile = (Profile)read.Invoke(form, null);
            Equal(16, profile.MainCalls.Count); Equal(242, profile.MemberCalls.Count);
            var tabs = (TabControl)typeof(MainForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(true, levelsGrid.Columns.Contains("Category"));
            Equal(true, levelsGrid.Columns.Contains("Metric"));
            Equal(true, levelsGrid.Columns.Contains("Points"));
            Equal(true, levelsGrid.Columns.Contains("Qsos"));
            Equal("Mixed Mode,CW,SSB,DIGI,Activator / All Bands",
                string.Join(",", profile.Levels.Select(l => l.Category)));
            for (int page = 0; page < 3; page++)
            {
                tabs.SelectedIndex = page; Application.DoEvents();
                Capture(form, Path.Combine(directory, "preview-" + (page + 1) + ".png"));
            }
            var qsos = profile.MainCalls.Take(5).SelectMany(c => new[]
            {
                new Qso { Call = c, Utc = Day, Band = "20M", Mode = "SSB", Frequency = "14.250", RstSent = "59",
                    RstReceived = "59", Complete = "Y", Power = "100", Station = "DEMO", MyRig = "Example rig",
                    Name = "Example operator", QTH = "Sample City", Locator = "JO00AA", Comment = "Synthetic preview",
                    MyQTHID = "HOME", Location = new MyLocation { ID = "HOME", Found = true, Name = "Demo station",
                        Street = "123 Example Street", City = "Sample City", PostalCode = "0000 AA", Country = "Example country", Locator = "JO00BB" } },
                new Qso { Call = c, Utc = Day.AddDays(1), Band = "40M", Mode = "FT8", Frequency = "7.074",
                    RstSent = "-10", RstReceived = "-12", Complete = "Y", Power = "50", Station = "DEMO",
                    MyQTHID = "FIELD", Location = new MyLocation { ID = "FIELD" } }
            }).ToList();
            qsos.Add(new Qso { Call = profile.MainCalls[0], Utc = Day.AddHours(1), Band = "20M", Mode = "SSB", Complete = "Y" });
            typeof(MainForm).GetMethod("DisplayResults", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object[] { Scorer.Score(profile, qsos), profile, Day });
            var summary = (Label)typeof(MainForm).GetField("summary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(true, summary.Text.Contains("2 bands"));
            Equal(true, summary.Text.Contains("2 mode groups"));
            Equal(true, summary.Text.Contains("Highest levels: None"));
            var qualificationGrid = (DataGridView)typeof(MainForm).GetField("qualificationGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(true, qualificationGrid.Rows.Cast<DataGridViewRow>().Any(row =>
                row.DataBoundItem is LevelResult level && level.Category == "Activator / All Bands" &&
                level.Status == "Not evaluated (Activator log required)"));
            var sectionPicker = (ComboBox)typeof(MainForm).GetField("locationSection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal("All MyQTHIDs", Convert.ToString(sectionPicker.SelectedItem));
            var combinedGrid = (DataGridView)typeof(MainForm).GetField("qsoGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(qsos.Count, combinedGrid.Rows.Count);
            Equal(DataGridViewAutoSizeColumnsMode.None, combinedGrid.AutoSizeColumnsMode);
            Equal(true, combinedGrid.AllowUserToResizeColumns);
            Application.DoEvents(); Capture(form, Path.Combine(directory, "preview-all-locations.png"));
            var reportButton = (Button)typeof(MainForm).GetField("exportReport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(1, reportButton.Parent.Controls.OfType<Button>().Select(button => button.Height).Distinct().Count());
            var stationsGrid = (DataGridView)typeof(MainForm).GetField("stationGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var stationFilter = (CheckBox)typeof(MainForm).GetField("workedStationsOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var resultTabs = (TabControl)stationsGrid.Parent.Parent;
            resultTabs.SelectedIndex = 1;
            Equal(258, stationsGrid.Rows.Count);
            stationFilter.Checked = true;
            Equal(5, stationsGrid.Rows.Count);
            Equal(true, stationsGrid.Rows.Cast<DataGridViewRow>().All(row => ((StationResult)row.DataBoundItem).CountedQsos > 0));
            Application.DoEvents();
            var formatting = new DataGridViewCellFormattingEventArgs(0, 1, "Example", typeof(string), new DataGridViewCellStyle());
            typeof(DataGridView).GetMethod("OnCellFormatting", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(DataGridViewCellFormattingEventArgs) }, null)!
                .Invoke(stationsGrid, new object[] { formatting });
            Equal(Color.FromArgb(220, 244, 224), formatting.CellStyle.BackColor);
            Capture(form, Path.Combine(directory, "preview-station-filter.png"));
            stationFilter.Checked = false;
            Equal(258, stationsGrid.Rows.Count);
            resultTabs.SelectedIndex = 0;
            sectionPicker.SelectedIndex = sectionPicker.Items.IndexOf("MyQTHID: HOME");
            Application.DoEvents();
            Capture(form, Path.Combine(directory, "preview-4.png"));
            using (var dialog = new ColumnsDialog(profile.Columns))
            {
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-20000, -20000);
                dialog.ShowInTaskbar = false; dialog.Show(form); Application.DoEvents();
                Capture(dialog, Path.Combine(directory, "preview-columns.png"));
                var choices = (CheckedListBox)typeof(ColumnsDialog).GetField("choices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
                var wndProc = typeof(ColumnCheckList).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Click(int x, int row, int message = 0x0201)
                {
                    var rect = choices.GetItemRectangle(row);
                    var msg = Message.Create(choices.Handle, message, new IntPtr(1),
                        new IntPtr(((rect.Top + rect.Height / 2) << 16) | x));
                    object[] values = { msg };
                    wndProc.Invoke(choices, values);
                    msg = Message.Create(choices.Handle, 0x0202, IntPtr.Zero,
                        new IntPtr(((rect.Top + rect.Height / 2) << 16) | x));
                    wndProc.Invoke(choices, new object[] { msg });
                    Application.DoEvents();
                }
                Click(65, 1);
                Equal(1, choices.SelectedIndex); Equal(true, choices.GetItemChecked(1));
                Click(65, 1); Equal(true, choices.GetItemChecked(1));
                Click(6, 1); Equal(false, choices.GetItemChecked(1));
                Click(6, 1); Equal(true, choices.GetItemChecked(1));
                Click(65, 1, 0x0203); Equal(true, choices.GetItemChecked(1));
                choices.SelectedIndex = 0;
                choices.SetItemChecked(0, false);
                var move = typeof(ColumnsDialog).GetMethod("MoveColumn", BindingFlags.Instance | BindingFlags.NonPublic)!;
                move.Invoke(dialog, new object[] { 1 });
                Equal("Call", (string)choices.Items[1]); Equal(false, choices.GetItemChecked(1));
                move.Invoke(dialog, new object[] { -1 });
                Equal("Call", (string)choices.Items[0]); Equal(false, choices.GetItemChecked(0));
                choices.SetItemChecked(0, true);
                var readColumns = typeof(ColumnsDialog).GetMethod("ReadColumns", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Equal(ColumnLayouts.AppDefault().Count, ((List<ColumnSetting>)readColumns.Invoke(dialog, null)).Count);
                dialog.Close();
            }
            Equal("MyQTHID: HOME", Convert.ToString(sectionPicker.SelectedItem));
            var qsoGrid = (DataGridView)typeof(MainForm).GetField("qsoGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(false, qsoGrid.Columns.Contains("QsoNumber")); Equal(false, qsoGrid.Columns.Contains("ContactedRig"));
            Equal(qsoGrid.Columns["Category"].DisplayIndex + 1, qsoGrid.Columns["Points"].DisplayIndex);
            sectionPicker.SelectedIndex = sectionPicker.Items.IndexOf("MyQTHID: FIELD");
            Application.DoEvents(); Capture(form, Path.Combine(directory, "preview-missing-location.png"));
            sectionPicker.SelectedIndex = sectionPicker.Items.IndexOf("MyQTHID: HOME");
            Application.DoEvents();
            Reports.WriteHtml(Path.Combine(directory, "preview-report.html"), Scorer.Score(profile, qsos), profile,
                Scorer.Score(profile, qsos).Qsos, Day);
            Equal(false, (bool)typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
            combinedGrid.Columns["Comment"].Width = 220;
            Application.DoEvents();
            Equal(true, (bool)typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
            Equal(220, ((Profile)read.Invoke(form, null)).Columns.Single(c => c.Name == "Comment").Width);
            sectionPicker.SelectedIndex = 0;
            Application.DoEvents();
            Equal(220, combinedGrid.Columns["Comment"].Width);
            using (var widthDialog = new ColumnsDialog(((Profile)read.Invoke(form, null)).Columns))
            {
                var readColumns = typeof(ColumnsDialog).GetMethod("ReadColumns", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Equal(220, ((List<ColumnSetting>)readColumns.Invoke(widthDialog, null)).Single(c => c.Name == "Comment").Width);
            }
            Equal("", Convert.ToString(combinedGrid.Rows[0].Cells["Submode"].FormattedValue));
            Reports.WriteHtml(Path.Combine(directory, "preview-widths.html"), Scorer.Score(profile, qsos),
                (Profile)read.Invoke(form, null), Scorer.Score(profile, qsos).Qsos, Day);
            Capture(form, Path.Combine(directory, "preview-adjustable-widths.png"));
            var profileName = (TextBox)typeof(MainForm).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var export = (Button)typeof(MainForm).GetField("exportQsos", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal("AwardChecker v1.0.5", form.Text);
            Equal(true, export.Enabled);
            profileName.Text += " edited";
            Equal("AwardChecker v1.0.5", form.Text);
            Equal(false, export.Enabled);
            Equal(true, (bool)typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
            var applyProfile = typeof(MainForm).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            applyProfile.Invoke(form, new object?[] { ModeProfile(), null });
            Equal("AwardChecker v1.0.5", form.Text);
            tabs.SelectedIndex = 2; Application.DoEvents();
            var modeGrid = (DataGridView)typeof(MainForm).GetField("modeGrid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var pointSelector = (ComboBox)typeof(MainForm).GetField("pointScoring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var mainPoints = (NumericUpDown)typeof(MainForm).GetField("mainPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            Equal(true, modeGrid.Columns["Points"].Visible); Equal(false, mainPoints.Enabled);
            var fromUi = (Profile)read.Invoke(form, null);
            Equal(PointScoring.Mode, fromUi.Scoring);
            Equal(2, fromUi.Modes.Single(m => m.Mode == "FT2").Points!.Value);
            Equal(10, Scorer.Score(fromUi, new[] { Q("MAIN", Day) }).TotalPoints);
            var cwRow = modeGrid.Rows.Cast<DataGridViewRow>().Single(r => !r.IsNewRow && ((ModeRow)r.DataBoundItem).Mode == "CW");
            cwRow.Cells["Points"].Value = 12; modeGrid.EndEdit();
            Equal(12, ((Profile)read.Invoke(form, null)).Modes.Single(m => m.Mode == "CW").Points!.Value);
            cwRow.Cells["Points"].Value = 10; modeGrid.EndEdit();
            Capture(form, Path.Combine(directory, "preview-mode-points.png"));
            var modeScore = Scorer.Score(fromUi, fromUi.Modes.Where(m => m.Enabled)
                .Select((m, i) => Q("MAIN", Day.AddMinutes(i), "20M", m.Mode)));
            Reports.WriteHtml(Path.Combine(directory, "preview-mode-points.html"), modeScore, fromUi, modeScore.Qsos, Day);
            pointSelector.SelectedIndex = 0; Application.DoEvents();
            Equal(false, modeGrid.Columns["Points"].Visible); Equal(true, mainPoints.Enabled);
            Equal(3, Scorer.Score((Profile)read.Invoke(form, null), new[] { Q("MAIN", Day) }).TotalPoints);
            pointSelector.SelectedIndex = 1; Application.DoEvents();
            Equal(10, Scorer.Score((Profile)read.Invoke(form, null), new[] { Q("MAIN", Day) }).TotalPoints);
            applyProfile.Invoke(form, new object?[] { SyntheticProfile(), null });
            Equal("AwardChecker v1.0.5", form.Text);
            Equal(0, pointSelector.SelectedIndex); Equal(false, modeGrid.Columns["Points"].Visible);
            applyProfile.Invoke(form, new object?[] { SyntheticProfile(), "synthetic.award.xml" });
            Equal("AwardChecker v1.0.5 - Synthetic activity", form.Text);
            profileName.Text = "Saved profile title";
            Equal("AwardChecker v1.0.5 - Synthetic activity", form.Text);
            WithTempDirectory(dir =>
            {
                string path = Path.Combine(dir, "saved-title.award.xml");
                var saved = (Profile)read.Invoke(form, null);
                Profiles.Save(saved, path);
                typeof(MainForm).GetMethod("MarkProfileSaved", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { saved, path });
            });
            Equal("AwardChecker v1.0.5 - Saved profile title", form.Text);
            tabs.SelectedIndex = 0;
            Application.DoEvents();
            Capture(form, Path.Combine(directory, "preview-saved-profile-title.png"));
            applyProfile.Invoke(form, new object?[] { new Profile(), null });
            Equal("AwardChecker v1.0.5", form.Text);
            Application.DoEvents();
            Capture(form, Path.Combine(directory, "preview-profile-title.png"));
            // Only synthetic fixture edits exist in this test-owned window.
            typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
            form.Close();
        }
    }

    private static void RuleImportPreview(string path)
    {
        using (var dialog = new RuleImportDialog())
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-20000, -20000);
            dialog.ShowInTaskbar = false;
            var input = (TextBox)typeof(RuleImportDialog).GetField("pastedRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
            input.Text = "1. Purpose\r\nSynthetic contest titled “SYNTHETIC CAFÉ”.\r\n" +
                "2. Event Start / End Dates\r\nStart: 28 October 2026 06:15 UTC\r\nEnd: 11 November 2026 18:40 UTC\r\n" +
                "3. QSO Requirements\r\nOne QSO per band and mode per day.\r\n" +
                "4. Bands\r\nHF: 80 / 40 / 30 / 20 / 17 / 15 / 12 / 10 metres\r\nVHF/UHF: optional – for local participation\r\n" +
                "5. Modes\r\nCW\r\nSSB\r\nDIGI (FT8, FT4, RTTY, PSK)\r\nFM (VHF/UHF)\r\n" +
                "6. Scoring / QSO\r\nMinimum of 50 points.\r\nScoring System:\r\n" +
                "CW:\r\nEach QSO on each band → 10 points\r\nSSB:\r\nEach QSO on each band → 5 points\r\n" +
                "DIGI:\r\nFT8 / FT4 → 2 points\r\nRTTY / PSK → 5 points\r\n" +
                "FM (VHF/UHF):\r\nEach QSO → 3 points\r\n" +
                "9. Overall Ranking\r\nRankings use total points and total QSOs.\r\n" +
                "11. Special Prizes\r\nActivator awards.\r\n" +
                "12. Achievement Levels\r\nHunter – All Bands\r\nMixed Mode\r\n" +
                "500 points → Bronze\r\n750 points → Silver\r\n2000 points → Gold";
            typeof(RuleImportDialog).GetMethod("Analyze", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
            input.SelectionStart = 0;
            input.SelectionLength = 0;
            dialog.Show(); Application.DoEvents();
            Capture(dialog, path);
            dialog.Close();
        }
    }

    private static void AwardRulesPreview(string rules, string directory)
    {
        Directory.CreateDirectory(directory);
        Profile draft;
        using (var dialog = new RuleImportDialog())
        {
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-20000, -20000);
            dialog.ShowInTaskbar = false;
            var input = (TextBox)typeof(RuleImportDialog).GetField("pastedRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
            input.Text = rules;
            typeof(RuleImportDialog).GetMethod("Analyze", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
            dialog.Show(); Application.DoEvents();
            Capture(dialog, Path.Combine(directory, "import-review.png"));
            var button = (Button)typeof(RuleImportDialog).GetField("apply", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog);
            button.PerformClick();
            draft = dialog.Draft ?? throw new Exception("Apply draft did not return the analyzed profile.");
            Equal("Synthetic Monthly Activity", draft.Name);
        }
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var apply = typeof(MainForm).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var read = typeof(MainForm).GetMethod("ReadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            apply.Invoke(form, new object?[] { draft, null });
            var profile = (Profile)read.Invoke(form, null);
            Equal(draft.Start, profile.Start); Equal(draft.End, profile.End);
            Equal(CountingRule.StationBandModePerMonth, profile.Counting);
            Equal(false, profile.CountModeGroups);
            Equal("TST1A", string.Join(",", profile.MainCalls));
            Equal("12,25,45,75", string.Join(",", profile.Levels.Select(l => l.Points)));
            Equal(PointScoring.Mode, profile.Scoring);
            Equal("CW=5,FT4=1,FT8=1,RTTY=3,SSB=2", string.Join(",",
                profile.Modes.Where(m => m.Enabled).OrderBy(m => m.Mode).Select(m => m.Mode + "=" + m.Points)));
            var tabs = (TabControl)typeof(MainForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            foreach (var size in new[] { new Size(1280, 900), new Size(1100, 780) })
            {
                form.Size = size; Application.DoEvents();
                tabs.SelectedIndex = 0; Application.DoEvents();
                Capture(form, Path.Combine(directory, "applied-profile-" + size.Width + ".png"));
                tabs.SelectedIndex = 2; Application.DoEvents();
                Capture(form, Path.Combine(directory, "applied-modes-" + size.Width + ".png"));
            }
            typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
            form.Close();
        }
    }

    private static void UtcTimePreview(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var read = typeof(MainForm).GetMethod("ReadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var apply = typeof(MainForm).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var p = (Profile)read.Invoke(form, null);
            Equal("00:00", p.StartTime); Equal("23:59", p.EndTime);
            Capture(form, Path.Combine(directory, "utc-default.png"));
            p.StartTime = "06:15"; p.EndTime = "18:40";
            apply.Invoke(form, new object?[] { p, null });
            p = (Profile)read.Invoke(form, null);
            Equal("06:15", p.StartTime); Equal("18:40", p.EndTime);
            foreach (string field in new[] { "startTime", "endTime" })
            {
                var picker = (DateTimePicker)typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
                Equal("HH:mm", picker.CustomFormat); Equal(true, picker.ShowUpDown);
                Equal(true, picker.Width >= 70);
            }
            Capture(form, Path.Combine(directory, "utc-custom.png"));
            form.Size = form.MinimumSize; Application.DoEvents();
            Capture(form, Path.Combine(directory, "utc-minimum.png"));
            var endPicker = (DateTimePicker)typeof(MainForm).GetField("endTime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            endPicker.Value = endPicker.Value.AddMinutes(1);
            Equal("18:41", ((Profile)read.Invoke(form, null)).EndTime);
            Equal(true, (bool)typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
            form.Close();
        }
    }

    private static void LevelEditorPreview(string path)
    {
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var profile = SyntheticProfile();
            profile.MinimumPoints = 50;
            profile.Levels = new List<AwardLevel>
            {
                new AwardLevel { Name = "Gold", Category = "DIGI", Points = 1000 },
                new AwardLevel { Name = "Bronze", Category = "Mixed Mode", Points = 500 },
                new AwardLevel { Name = "Bronze", Category = "CW", Points = 750 },
                new AwardLevel { Name = "Bronze", Category = "SSB", Points = 300 },
                new AwardLevel { Name = "Bronze", Category = "Activator / All Bands", Metric = AwardMetric.Qsos, Qsos = 300 }
            };
            typeof(MainForm).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object?[] { profile, null });
            var read = typeof(MainForm).GetMethod("ReadProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var applied = (Profile)read.Invoke(form, null);
            Equal("Mixed Mode,CW,SSB,DIGI,Activator / All Bands",
                string.Join(",", applied.Levels.Select(level => level.Category)));
            var tabs = (TabControl)typeof(MainForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            tabs.SelectedIndex = 0;
            Application.DoEvents();
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            Capture(form, path);
            typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
            form.Close();
        }
    }

    private static void SummaryPreview(string path)
    {
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var profile = Basic();
            profile.Bands.AddRange(new[] { "6M", "80M" });
            var qsos = new[]
            {
                Q("MAIN", Day),
                new Qso { Call = "MAIN", Utc = Day.AddMinutes(1), Band = "40M",
                    Mode = "MFSK", Submode = "FT8", Complete = "Y" },
                new Qso { Call = "MEMBER", Utc = Day.AddMinutes(2), Band = "40M",
                    Mode = "MFSK", Submode = "FT4", Complete = "Y" },
                Q("MAIN", Day.AddMinutes(3)),
                Q("UNLISTED", Day, "6M", "SSB"),
                new Qso { Call = "MAIN", Utc = Day, Band = "80M", Mode = "SSB", Complete = "N" }
            };
            var display = typeof(MainForm).GetMethod("DisplayResults", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var summary = (Label)typeof(MainForm).GetField("summary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            foreach (bool grouped in new[] { true, false })
            {
                profile.CountModeGroups = grouped;
                string modeText = grouped ? "2 mode groups" : "3 modes";
                foreach (Size size in new[] { new Size(1280, 900), new Size(1100, 780) })
                {
                    form.Size = size;
                    display.Invoke(form, new object[] { Scorer.Score(profile, qsos), profile, Day });
                    Equal(true, summary.Text.Contains("3 counted QSOs   |   2 bands   |   " + modeText + "\r\n"));
                    Application.DoEvents();
                    string previewPath = grouped && size.Width == 1280 ? path :
                        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) +
                            (grouped ? "-groups-" : "-modes-") + size.Width + Path.GetExtension(path));
                    Capture(form, previewPath);
                }
                display.Invoke(form, new object[] { Scorer.Score(profile, new Qso[0]), profile, Day });
                Equal(true, summary.Text.Contains("0 counted QSOs   |   0 bands   |   0 " +
                    (grouped ? "mode groups" : "modes") + "\r\n"));
            }
            typeof(MainForm).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, false);
            form.Close();
        }
    }

    private static void TabFontPreview(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var form = new MainForm(DriverCatalog.Jet40))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.ShowInTaskbar = false;
            form.Show(); Application.DoEvents();
            var mainTabs = (TabControl)typeof(MainForm).GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form);
            var pending = new Stack<Control>();
            var tabControls = new List<TabControl>();
            pending.Push(form);
            while (pending.Count > 0)
            {
                var control = pending.Pop();
                if (control is TabControl tabControl) tabControls.Add(tabControl);
                foreach (Control child in control.Controls) pending.Push(child);
            }
            Equal(2, tabControls.Count);
            foreach (var tabControl in tabControls)
            {
                Equal(11F, tabControl.Font.SizeInPoints);
                Equal(true, tabControl.Font.Bold);
                Equal(TabDrawMode.OwnerDrawFixed, tabControl.DrawMode);
                foreach (TabPage page in tabControl.TabPages)
                {
                    Equal(9F, page.Font.SizeInPoints);
                    Equal(false, page.Font.Bold);
                }
            }
            foreach (var size in new[] { new Size(1280, 900), form.MinimumSize })
            {
                form.Size = size; Application.DoEvents();
                foreach (var tabControl in tabControls)
                    for (int i = 0; i < tabControl.TabCount; i++)
                    {
                        Rectangle rect = tabControl.GetTabRect(i);
                        Equal(true, rect.Left >= 0 && rect.Right <= tabControl.ClientSize.Width);
                        Equal(true, rect.Top >= 0 && rect.Bottom <= tabControl.ClientSize.Height);
                    }
                for (int i = 0; i < mainTabs.TabCount; i++)
                {
                    mainTabs.SelectedIndex = i; Application.DoEvents();
                    Capture(form, Path.Combine(directory, "main-" + (i + 1) + "-" + size.Width + ".png"));
                }
                var results = tabControls.Single(t => t != mainTabs);
                for (int i = 0; i < results.TabCount; i++)
                {
                    results.SelectedIndex = i; Application.DoEvents();
                    Capture(form, Path.Combine(directory, "result-" + (i + 1) + "-" + size.Width + ".png"));
                }
            }
            form.Close();
        }
    }

    private static void WriteSyntheticPe(string path, ushort machine)
    {
        var image = new byte[256];
        BitConverter.GetBytes((ushort)0x5A4D).CopyTo(image, 0);
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        BitConverter.GetBytes(0x00004550u).CopyTo(image, 0x80);
        BitConverter.GetBytes(machine).CopyTo(image, 0x84);
        File.WriteAllBytes(path, image);
    }

    private sealed class MemoryRegistry : IRegistryValueReader
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void Set(RegistryHive hive, RegistryView view, string keyPath, string? valueName, string value)
        {
            values[Key(hive, view, keyPath, valueName)] = value;
        }

        public RegistryValueRead ReadValue(RegistryHive hive, RegistryView view, string keyPath, string? valueName) =>
            values.TryGetValue(Key(hive, view, keyPath, valueName), out string? value)
                ? new RegistryValueRead(value) : new RegistryValueRead(null);

        private static string Key(RegistryHive hive, RegistryView view, string keyPath, string? valueName) =>
            hive + "|" + view + "|" + keyPath + "|" + (valueName ?? "(default)");
    }

    private sealed class FakeDriverProbe : IDriverProbe
    {
        private readonly Dictionary<RuntimeArchitecture, bool> runtimes = new Dictionary<RuntimeArchitecture, bool>();
        private readonly Dictionary<string, bool> providers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public bool Is64BitOperatingSystem { get; }
        public List<RuntimeArchitecture> CheckedArchitectures { get; } = new List<RuntimeArchitecture>();

        public FakeDriverProbe(bool is64BitOperatingSystem)
        {
            Is64BitOperatingSystem = is64BitOperatingSystem;
        }

        public void SetRuntime(RuntimeArchitecture architecture, bool available) => runtimes[architecture] = available;

        public void SetProvider(string providerName, RuntimeArchitecture architecture, bool available) =>
            providers[providerName + "|" + architecture] = available;

        public RuntimeCheck CheckFramework48(RuntimeArchitecture architecture)
        {
            CheckedArchitectures.Add(architecture);
            bool available = runtimes.TryGetValue(architecture, out bool value) && value;
            string label = architecture == RuntimeArchitecture.X64 ? "x64" : "x86";
            return new RuntimeCheck(available, available ? 534296 : null,
                available
                    ? $".NET Framework 4.8 {label} runtime is available."
                    : $".NET Framework 4.8 {label} runtime is not registered.");
        }

        public ProviderCheck CheckOleDbProvider(string providerName, RuntimeArchitecture architecture)
        {
            bool available = providers.TryGetValue(providerName + "|" + architecture, out bool value) && value;
            return new ProviderCheck(available, available ? "synthetic-" + architecture + ".dll" : null,
                available
                    ? providerName + " " + architecture + " provider is available."
                    : providerName + " " + architecture + " provider is not registered.");
        }
    }

    private static void Capture(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
