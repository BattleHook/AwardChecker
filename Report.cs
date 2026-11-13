using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace AwardChecker
{
    public sealed class MyLocation
    {
        public string ID { get; set; } = "";
        public bool Found { get; set; }
        public string Name { get; set; } = "";
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public string PostalCode { get; set; } = "";
        public string Country { get; set; } = "";
        public string Locator { get; set; } = "";
        public string Rig { get; set; } = "";
        public string Antenna { get; set; } = "";
        public string Header => !Found
            ? "Location record unavailable for MyQTHID: " + (ID.Length == 0 ? "(missing)" : ID)
            : "MyQTHID: " + ID + " | " + string.Join(" | ",
                new[] { Name, Street.Length + City.Length + PostalCode.Length + Country.Length == 0 ? "Address unavailable" : "",
                    Street, string.Join(" ", new[] { PostalCode, City }.Where(s => s.Length > 0)), Country,
                    "Locator: " + (Locator.Length > 0 ? Locator : "Unavailable") }.Where(s => s.Length > 0));
    }

    public sealed class LocationSection
    {
        public string ID { get; set; } = "";
        public string Header { get; set; } = "";
        public List<QsoResult> Qsos { get; set; } = new List<QsoResult>();
    }

    public static class Reports
    {
        public static List<LocationSection> Sections(IEnumerable<QsoResult> rows) =>
            rows.GroupBy(q => Calls.Key(q.MyQTHID)).OrderBy(g => g.Key)
                .Select(g => new LocationSection
                {
                    ID = g.Key,
                    Header = g.First().Location.Header + " | Station callsign(s): " +
                        string.Join(", ", g.Select(q => q.Station).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
                    Qsos = g.OrderBy(q => q.Utc).ToList()
                }).ToList();

        public static string Value(QsoResult row, string column)
        {
            var property = typeof(QsoResult).GetProperty(column);
            if (property == null) throw new InvalidDataException("Unknown report column: " + column);
            object? value = property.GetValue(row);
            return value is DateTime time ? time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) :
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        public static void WriteHtml(string path, ScoreResult score, Profile profile, IEnumerable<QsoResult> rows, DateTime calculatedUtc)
        {
            var columns = ColumnLayouts.Normalize(profile.Columns).Where(c => c.Visible).ToList();
            string H(string text) => WebUtility.HtmlEncode(text);
            string Cell(string tag, ColumnSetting column, string value)
            {
                string css = column.Width > 0 ? " class=\"sized\"" : column.Name == "Comment" && tag == "td" ? " class=\"comment\"" : "";
                string content = H(value);
                if (column.Width > 0)
                    content = "<div style=\"width:" + Math.Max(1, column.Width - 11).ToString(CultureInfo.InvariantCulture) + "px\">" + content + "</div>";
                return "<" + tag + css + ">" + content + "</" + tag + ">";
            }
            var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>AwardChecker report</title>" +
                "<style>body{font:14px Segoe UI,sans-serif;margin:24px}table{border-collapse:collapse;font-size:12px;table-layout:auto;width:max-content}" +
                "th,td{border:1px solid #bbb;padding:5px;text-align:left;white-space:nowrap}th{background:#e4eff9}" +
                "td.comment{white-space:pre-wrap;overflow-wrap:anywhere;min-width:16em;max-width:32em}" +
                ".sized div{white-space:pre-wrap;overflow-wrap:anywhere}" +
                "section{margin-top:24px}h2{font-size:17px}@media print{thead{display:table-header-group}tr{break-inside:avoid}}</style><body>");
            html.Append("<h1>").Append(H(profile.Name)).Append("</h1><p>Combined award score: ")
                .Append(score.TotalPoints).Append(" | Unique main stations: ").Append(score.UniqueMain)
                .Append(" | Minimum qualification: ")
                .Append(!score.MinimumQualificationConfigured ? "not configured" :
                    score.MinimumQualified ? "met" : "not met (" + score.MinimumPointsNeeded + " points needed)")
                .Append(score.MinimumQualificationConfigured ? " (" + profile.MinimumPoints + " points required)" : "")
                .Append(" | Highest levels: ").Append(H(string.IsNullOrEmpty(score.HighestLevels) ? "None" : score.HighestLevels)).Append("</p><p>")
                .Append(profile.StartUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(" through ").Append(profile.EndUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(" UTC inclusive | Calculated UTC: ").Append(calculatedUtc.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("</p><p>Counting rule: ").Append(H(profile.Counting.ToString()))
                .Append(profile.CountModeGroups ? " (mode groups)" : " (individual modes)")
                .Append(". Score is combined across locations; displayed sections use the selected QSO view.</p>");
            if (score.Levels.Count > 0)
            {
                html.Append("<h2>Award levels</h2><table><thead><tr><th>Category</th><th>Level</th><th>Requirement</th><th>Status</th><th>Points needed</th><th>QSOs needed</th><th>Main stations needed</th></tr></thead><tbody>");
                foreach (var level in score.Levels)
                {
                    string requirement = level.Metric == AwardMetric.Points
                        ? level.RequiredPoints + " points"
                        : level.RequiredQsos + " QSOs";
                    html.Append("<tr><td>").Append(H(level.Category)).Append("</td><td>").Append(H(level.Name))
                        .Append("</td><td>").Append(H(requirement)).Append("</td><td>").Append(H(level.Status))
                        .Append("</td><td>").Append(level.PointsNeeded).Append("</td><td>").Append(level.QsosNeeded)
                        .Append("</td><td>").Append(level.MainStationsNeeded).Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            html.Append("<p>Points: ").Append(H(profile.PointRules)).Append("</p>");
            foreach (var warning in score.Warnings) html.Append("<p>").Append(H(warning)).Append("</p>");
            var sections = Sections(rows);
            if (sections.Count == 0) html.Append("<p>No QSOs in selected view.</p>");
            foreach (var section in sections)
            {
                html.Append("<section><h2>").Append(H(section.Header)).Append("</h2><table><thead><tr>");
                foreach (var column in columns) html.Append(Cell("th", column, column.Name));
                html.Append("</tr></thead><tbody>");
                foreach (var row in section.Qsos)
                {
                    html.Append("<tr>");
                    foreach (var column in columns)
                    {
                        string value = Value(row, column.Name);
                        html.Append(Cell("td", column, value.Length == 0 && column.Name != "Submode" ? "Unavailable" : value));
                    }
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></section>");
            }
            html.Append("<p>Provisional award calculation. MyQTH address/equipment reflects the current referenced record.</p></body></html>");
            File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
        }
    }
}
