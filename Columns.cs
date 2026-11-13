using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

namespace AwardChecker
{
    public sealed class RuntimeFolders
    {
        private static string? applicationRoot;
        public static RuntimeFolders Current => new RuntimeFolders(applicationRoot ?? AppDomain.CurrentDomain.BaseDirectory);
        public string Profiles { get; }
        public string Defaults { get; }
        public string Reports { get; }
        public string UserColumns => Path.Combine(Defaults, "User-Columns.xml");
        public string LastDatabase => Path.Combine(Defaults, "Last-Database.txt");

        public RuntimeFolders(string applicationDirectory)
        {
            string root = Path.GetFullPath(applicationDirectory);
            Profiles = Path.Combine(root, "Profiles");
            Defaults = Path.Combine(root, "Defaults");
            Reports = Path.Combine(root, "Reports");
        }

        public static void ConfigureApplicationRoot(string applicationDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectory) || !Path.IsPathRooted(applicationDirectory))
                throw new ArgumentException("AwardChecker's data root must be an absolute path.", nameof(applicationDirectory));
            string root = Path.GetFullPath(applicationDirectory);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("AwardChecker's data root does not exist: " + root);
            applicationRoot = root;
        }

        public static void ClearConfiguredApplicationRoot() => applicationRoot = null;

        public static string Ensure(string directory)
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static class DatabasePreference
    {
        public static string Load(string settingsPath)
        {
            if (!File.Exists(settingsPath)) return "";
            string path = File.ReadAllText(settingsPath).Trim();
            if (path.Length == 0 || !Path.IsPathRooted(path) || path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidDataException("Invalid remembered database path in " + settingsPath);
            return Path.GetFullPath(path);
        }

        public static void Save(string settingsPath, string databasePath)
        {
            string path = Path.GetFullPath(databasePath);
            if (!File.Exists(path)) throw new FileNotFoundException("Cannot remember a database that does not exist.", path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            string temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, path);
                if (File.Exists(settingsPath)) File.Replace(temp, settingsPath, null);
                else File.Move(temp, settingsPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public sealed class ColumnSetting
    {
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public int Width { get; set; }
        public override string ToString() => Name;
    }

    public sealed class ColumnDefault
    {
        public int Version { get; set; } = 1;
        public List<ColumnSetting> Columns { get; set; } = new List<ColumnSetting>();
    }

    public static class ColumnLayouts
    {
        public static string UserDefaultPath => RuntimeFolders.Current.UserColumns;

        public static List<ColumnSetting> AppDefault() => new[]
        {
            "Call", "Name", "QTH", "Locator", "Utc", "Band", "Mode", "Submode", "ModeGroup", "FrequencyMHz",
            "RstSent", "RstReceived", "MyRig", "MyAntenna", "MyQTHID", "PowerWatts", "Station", "Operator",
            "ContestID", "SRX", "SRXString", "STX", "STXString", "Comment", "Category", "Points", "Status", "Reason"
        }.Select(n => new ColumnSetting { Name = n }).ToList();

        public static List<ColumnSetting> Normalize(IEnumerable<ColumnSetting> settings)
        {
            var defaults = AppDefault();
            var known = new HashSet<string>(defaults.Select(c => c.Name));
            var result = new List<ColumnSetting>();
            var used = new HashSet<string>();
            foreach (var column in settings)
            {
                if (!known.Contains(column.Name) || !used.Add(column.Name))
                    throw new InvalidDataException("Unknown or duplicate QSO column: " + column.Name);
                if (column.Width < 0 || column.Width > 65536)
                    throw new InvalidDataException("Invalid QSO column width: " + column.Name);
                result.Add(new ColumnSetting { Name = column.Name, Visible = column.Visible, Width = column.Width });
            }
            if (result.Count > 0 && !result.Any(c => c.Visible))
                throw new InvalidDataException("Keep at least one QSO column visible.");
            result.AddRange(defaults.Where(c => !used.Contains(c.Name)));
            return result;
        }

        public static List<ColumnSetting> LoadDefault(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                var saved = (ColumnDefault?)new XmlSerializer(typeof(ColumnDefault)).Deserialize(reader);
                if (saved == null || saved.Version != 1) throw new InvalidDataException("Unsupported column default file.");
                return Normalize(saved.Columns);
            }
        }

        public static void SaveDefault(string path, IEnumerable<ColumnSetting> columns)
        {
            var saved = new ColumnDefault { Columns = Normalize(columns) };
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temp))
                    new XmlSerializer(typeof(ColumnDefault)).Serialize(writer, saved);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public class ColumnCheckList : CheckedListBox
    {
        protected override void WndProc(ref Message message)
        {
            bool mouseDown = message.Msg == 0x0201 || message.Msg == 0x0203;
            if (!mouseDown) { base.WndProc(ref message); return; }
            int x = unchecked((short)(message.LParam.ToInt64() & 0xffff));
            int y = unchecked((short)((message.LParam.ToInt64() >> 16) & 0xffff));
            int index = IndexFromPoint(x, y);
            using (var graphics = CreateGraphics())
            {
                int glyphWidth = CheckBoxRenderer.GetGlyphSize(graphics,
                    System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
                if (index >= 0 && x >= GetItemRectangle(index).Left + glyphWidth + 3)
                {
                    Focus();
                    SelectedIndex = index;
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }

    public sealed class ColumnsDialog : Form
    {
        private readonly CheckedListBox choices = new ColumnCheckList { Dock = DockStyle.Fill, CheckOnClick = true };
        private readonly Label message = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(6) };
        private readonly Dictionary<string, int> widths = new Dictionary<string, int>();
        public List<ColumnSetting> SelectedColumns { get; private set; }

        public ColumnsDialog(IEnumerable<ColumnSetting> current)
        {
            Text = "AwardChecker - QSO column order and visibility";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(600, 650);
            MinimumSize = new Size(580, 500);
            Font = new Font("Segoe UI", 9);
            SelectedColumns = ColumnLayouts.Normalize(current);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 205, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8) };
            Add(buttons, "Move up", () => MoveColumn(-1));
            Add(buttons, "Move down", () => MoveColumn(1));
            Add(buttons, "Save as user default", () =>
            {
                ColumnLayouts.SaveDefault(ColumnLayouts.UserDefaultPath, ReadColumns());
                message.Text = "Award-independent user default saved. Other profiles are unchanged.";
            });
            Add(buttons, "Restore user default", () =>
            {
                Fill(ColumnLayouts.LoadDefault(ColumnLayouts.UserDefaultPath));
                message.Text = "User default loaded into this editor. Apply to use it for this profile.";
            });
            Add(buttons, "Restore app default", () =>
            {
                Fill(ColumnLayouts.AppDefault());
                message.Text = "App default loaded. Your saved user default is unchanged.";
            });
            Add(buttons, "Apply to profile", () =>
            {
                SelectedColumns = ReadColumns();
                DialogResult = DialogResult.OK;
            });
            var cancel = new Button { Text = "Cancel", Width = 185, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(cancel);
            CancelButton = cancel;
            var caption = new Label { Dock = DockStyle.Top, Height = 55, Padding = new Padding(6),
                Text = "Click a checkbox to toggle; click a name to select for Move up/down.\r\nApply saves this layout with the award profile when you Save the profile." };
            Controls.Add(choices); Controls.Add(buttons); Controls.Add(message); Controls.Add(caption);
            Fill(SelectedColumns);
        }

        private void Add(Control parent, string title, Action action)
        {
            var button = new Button { Text = title, Width = 185, Height = 32 };
            button.Click += (_, __) =>
            {
                try { action(); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException ||
                    ex is InvalidOperationException || ex is XmlException || ex is ArgumentException)
                {
                    message.Text = "Failed: " + ex.Message;
                    MessageBox.Show(this, ex.Message, "AwardChecker - Column settings failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            parent.Controls.Add(button);
        }

        private void Fill(IEnumerable<ColumnSetting> columns)
        {
            var layout = ColumnLayouts.Normalize(columns);
            widths.Clear();
            choices.Items.Clear();
            foreach (var column in layout)
            {
                widths.Add(column.Name, column.Width);
                choices.Items.Add(column.Name, column.Visible);
            }
            if (choices.Items.Count > 0) choices.SelectedIndex = 0;
        }

        private List<ColumnSetting> ReadColumns() => ColumnLayouts.Normalize(
            choices.Items.Cast<string>().Select((name, i) => new ColumnSetting
                { Name = name, Visible = choices.GetItemChecked(i), Width = widths[name] }));

        private void MoveColumn(int delta)
        {
            int index = choices.SelectedIndex, target = index + delta;
            if (index < 0 || target < 0 || target >= choices.Items.Count) return;
            object item = choices.Items[index];
            bool enabled = choices.GetItemChecked(index);
            choices.Items.RemoveAt(index); choices.Items.Insert(target, item);
            choices.SetItemChecked(target, enabled); choices.SelectedIndex = target;
        }
    }
}
