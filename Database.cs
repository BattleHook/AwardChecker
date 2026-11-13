using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AwardChecker
{
    public static class Database
    {
        public static List<Qso> Read(string path, DateTime start, DateTime end, string provider,
            string startTime = "00:00", string endTime = "23:59")
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Select an existing DXKeeper database.", path);
            var startUtc = Profile.UtcMinute(start, startTime);
            var endUtc = Profile.UtcMinute(end, endTime);
            if (startUtc > endUtc || end.Date >= new DateTime(9998, 12, 31))
                throw new ArgumentException("Invalid inclusive UTC date/time range.");
            var endExclusiveUtc = endUtc.AddMinutes(1);
            if (!DriverCatalog.IsSupportedProvider(provider))
                throw new ArgumentException("The selected OLE DB provider is not supported.", nameof(provider));
            var builder = new OleDbConnectionStringBuilder
            {
                Provider = provider, DataSource = Path.GetFullPath(path)
            };
            builder["Mode"] = "Read";
            builder["Persist Security Info"] = false;
            using (var connection = new OleDbConnection(builder.ConnectionString))
            {
                connection.Open();
                var schema = connection.GetSchema("Columns", new[] { null, null, "QSOs", null });
                var fields = new HashSet<string>(
                    schema.Rows.Cast<System.Data.DataRow>().Select(r => Convert.ToString(r["COLUMN_NAME"]) ?? ""),
                    StringComparer.OrdinalIgnoreCase);
                string[] required = { "Call", "QSO_Begin", "Band", "Mode" };
                var missing = required.Where(f => !fields.Contains(f)).ToList();
                if (missing.Count > 0)
                    throw new InvalidDataException("Not a compatible DXKeeper QSOs table. Missing: " + string.Join(", ", missing));
                string[] wanted = { "APP_DXKeeper_QSO_NUMBER", "Call", "QSO_Begin", "Band", "Mode",
                    "APP_DXKeeper_SUBMODE", "Freq", "RST_Sent", "RST_Rcvd", "Name", "QTH", "GridSquare", "Comment", "TX_Power",
                    "STATION_CALLSIGN", "Operator", "Contest_ID", "SRX", "SRX_STRING", "STX", "STX_STRING",
                    "QSO_COMPLETE", "MYQTHID" };
                var selected = wanted.Where(fields.Contains).ToList();
                var equipment = ReadEquipment(connection);
                string sql = "SELECT " + string.Join(", ", selected.Select(f => "[" + f + "]")) +
                    " FROM [QSOs] WHERE [QSO_Begin] >= ? AND [QSO_Begin] < ? ORDER BY [QSO_Begin]";
                using (var command = new OleDbCommand(sql, connection))
                {
                    command.Parameters.Add("start", OleDbType.Date).Value = startUtc;
                    command.Parameters.Add("end", OleDbType.Date).Value = endExclusiveUtc;
                    var result = new List<Qso>();
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader == null) throw new InvalidDataException("Database query returned no reader.");
                        while (reader.Read())
                        {
                            string Text(string name) => selected.Contains(name) && !reader.IsDBNull(reader.GetOrdinal(name))
                                ? Convert.ToString(reader[name], CultureInfo.InvariantCulture) ?? "" : "";
                            string qthId = Text("MYQTHID");
                            equipment.TryGetValue(Calls.Key(qthId), out var myEquipment);
                            result.Add(new Qso
                            {
                                Number = int.TryParse(Text("APP_DXKeeper_QSO_NUMBER"), out int n) ? n : result.Count + 1,
                                Call = Text("Call"),
                                Utc = DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("QSO_Begin")), DateTimeKind.Utc),
                                Band = Text("Band"), Mode = Text("Mode"), Submode = Text("APP_DXKeeper_SUBMODE"),
                                Frequency = Text("Freq"), RstSent = Text("RST_Sent"), RstReceived = Text("RST_Rcvd"),
                                Name = Text("Name"), QTH = Text("QTH"), Locator = Text("GridSquare"), Comment = Text("Comment"),
                                Power = Text("TX_Power"), Station = Text("STATION_CALLSIGN"),
                                Operator = Text("Operator"), ContestID = Text("Contest_ID"), SRX = Text("SRX"),
                                SRXString = Text("SRX_STRING"), STX = Text("STX"), STXString = Text("STX_STRING"),
                                Complete = Text("QSO_COMPLETE"), MyQTHID = qthId,
                                Location = myEquipment ?? new MyLocation { ID = qthId },
                                MyRig = myEquipment?.Rig ?? "", MyAntenna = myEquipment?.Antenna ?? ""
                            });
                        }
                    }
                    return result;
                }
            }
        }

        private static Dictionary<string, MyLocation> ReadEquipment(OleDbConnection connection)
        {
            var schema = connection.GetSchema("Columns", new[] { null, null, "QTHs", null });
            var fields = new HashSet<string>(
                schema.Rows.Cast<System.Data.DataRow>().Select(r => Convert.ToString(r["COLUMN_NAME"]) ?? ""),
                StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, MyLocation>();
            if (!fields.Contains("MYQTHID")) return result;
            string[] wanted = { "MYQTHID", "Rig", "ANT", "Name", "StreetAddress", "City", "PostalCode", "Country", "Grid" };
            string select = string.Join(", ", wanted.Select(f => fields.Contains(f) ? "[" + f + "]" : "Null AS [" + f + "]"));
            using (var command = new OleDbCommand("SELECT " + select + " FROM [QTHs]", connection))
            using (var reader = command.ExecuteReader())
            {
                if (reader == null) throw new InvalidDataException("MyQTH equipment query returned no reader.");
                while (reader.Read())
                {
                    string key = Calls.Key(Convert.ToString(reader[0], CultureInfo.InvariantCulture) ?? "");
                    if (key.Length == 0) continue;
                    if (result.ContainsKey(key))
                        throw new InvalidDataException("Duplicate MyQTH ID prevents reliable equipment mapping: " + key);
                    string Text(string field) => Convert.ToString(reader[field], CultureInfo.InvariantCulture) ?? "";
                    result.Add(key, new MyLocation
                    {
                        ID = Text("MYQTHID"), Found = true, Rig = Text("Rig"), Antenna = Text("ANT"),
                        Name = Text("Name"), Street = Text("StreetAddress"), City = Text("City"),
                        PostalCode = Text("PostalCode"), Country = Text("Country"), Locator = Text("Grid")
                    });
                }
            }
            return result;
        }
    }
}
