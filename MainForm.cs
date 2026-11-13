using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AwardChecker
{
    public sealed class ModeRow
    {
        public bool Enabled { get; set; }
        public string Mode { get; set; } = "";
        public string Group { get; set; } = "";
        public int? Points { get; set; }
    }

    public sealed class MainForm : Form
    {
        private readonly TextBox name = new TextBox(), database = new TextBox(), ownStation = new TextBox(),
            source = new TextBox(), notes = new TextBox();
        private readonly TextBox mainCalls = CallBox(), memberCalls = CallBox();
        private readonly DateTimePicker start = DateBox(), end = DateBox();
        private readonly DateTimePicker startTime = TimeBox(), endTime = TimeBox();
        private readonly NumericUpDown mainPoints = PointsBox(), memberPoints = PointsBox(), minimumPoints = PointsBox();
        private readonly ComboBox counting = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox pointScoring = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 235 };
        private readonly CheckBox grouped = new CheckBox { Text = "Count by mode group (otherwise by individual mode)", AutoSize = true };
        private readonly CheckedListBox bands = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, MultiColumn = true, ColumnWidth = 100 };
        private readonly DataGridView modeGrid = Grid(false), levelGrid = Grid(false),
            qsoGrid = Grid(true), stationGrid = Grid(true), qualificationGrid = Grid(true);
        private readonly Label summary = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Select a database, review the profile, then Read and score.", Padding = new Padding(12) };
        private readonly TextBox warnings = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(8, 4, 8, 0) };
        private readonly Button scoreButton = new Button { Text = "Read and score", AutoSize = true };
        private readonly Button exportQsos = new Button { Text = "Export QSO CSV", AutoSize = true, Enabled = false };
        private readonly Button exportStations = new Button { Text = "Export station CSV", AutoSize = true, Enabled = false };
        private readonly Button exportLevels = new Button { Text = "Export award CSV", AutoSize = true, Enabled = false };
        private readonly Button exportReport = new Button { Text = "Sectioned report...", AutoSize = true, Enabled = false };
        private readonly ComboBox locationSection = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly CheckBox workedStationsOnly = new CheckBox
        { Text = "Only stations with counted QSOs (> 0)  |  Green = counted QSOs", Dock = DockStyle.Top, Height = 32 };
        private readonly Label locationHeader = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(6) };
        private readonly ComboBox view = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly Panel content = new Panel { Dock = DockStyle.Fill };
        private ScoreResult? results;
        private Profile? scoredProfile;
        private readonly string databaseProvider;
        private string? profilePath;
        private DateTime scoredUtc;
        private bool loading, dirty;
        private List<ColumnSetting> columnLayout = ColumnLayouts.AppDefault();
        private List<LocationSection> sections = new List<LocationSection>();

        public MainForm(string databaseProvider)
        {
            if (!DriverCatalog.IsSupportedProvider(databaseProvider))
                throw new ArgumentException("A validated OLE DB provider is required.", nameof(databaseProvider));
            this.databaseProvider = databaseProvider;
            UpdateWindowTitle(null);
            Font = new Font("Segoe UI", 9F);
            tabs.Font = new Font(Font.FontFamily, Font.SizeInPoints + 2F, FontStyle.Bold);
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem += DrawTabHeader;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(1100, 780);
            Size = new Size(1280, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 250);
            var header = new Label
            {
                Text = "AwardChecker   |   Read-only DXKeeper award analysis",
                Dock = DockStyle.Top, Height = 52, ForeColor = Color.White,
                BackColor = Color.FromArgb(25, 49, 75), Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                Padding = new Padding(16, 11, 0, 0)
            };
            Controls.Add(content); Controls.Add(status); Controls.Add(header);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(layout);
            var toolbar = Flow();
            AddButton(toolbar, "New profile", () => ReplaceProfile(EmptyProfile()));
            AddButton(toolbar, "Import rules text...", ImportRulesText);
            AddButton(toolbar, "Load profile...", LoadProfile);
            AddButton(toolbar, "Save", () => SaveProfile(false));
            AddButton(toolbar, "Save as...", () => SaveProfile(true));
            toolbar.Controls.Add(new Label { Text = "Profiles are local XML files; database records are never edited.", AutoSize = true, Margin = new Padding(12, 9, 0, 0) });
            layout.Controls.Add(toolbar, 0, 0);
            var dbRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
            dbRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            dbRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            dbRow.Controls.Add(new Label { Text = "DXKeeper database", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            database.Dock = DockStyle.Fill;
            dbRow.Controls.Add(database, 1, 0);
            var browse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
            browse.Click += (_, __) => Guard(() =>
            {
                using (var picker = new OpenFileDialog { Title = "AwardChecker - Select DXKeeper database (read-only)", Filter = "DXKeeper database (*.mdb)|*.mdb", CheckFileExists = true })
                    if (picker.ShowDialog(this) == DialogResult.OK) database.Text = picker.FileName;
            });
            dbRow.Controls.Add(browse, 2, 0);
            scoreButton.Dock = DockStyle.Fill;
            scoreButton.Click += async (_, __) => await ReadAndScore();
            dbRow.Controls.Add(scoreButton, 3, 0);
            layout.Controls.Add(dbRow, 0, 1);
            layout.Controls.Add(tabs, 0, 2);
            BuildProfileTab(); BuildStationTab(); BuildEligibilityTab(); BuildResultsTab();
            counting.DataSource = new[]
            {
                "Once per station / period", "Once per station / UTC day",
                "Once per station / band / period", "Once per station / band / UTC day",
                "Once per station / band / mode or group / period",
                "Once per station / band / mode or group / UTC day",
                "Once per station / band / mode or group / UTC month"
            };
            view.Items.AddRange(new object[] { "Listed-station QSOs", "Counted QSOs only", "Needs review only", "All period QSOs" });
            view.SelectedIndex = 0;
            view.SelectedIndexChanged += (_, __) => RefreshQsoView();
            database.TextChanged += (_, __) => InvalidateResults();
            foreach (var box in new[] { name, ownStation, source, notes, mainCalls, memberCalls })
                box.TextChanged += (_, __) => Changed();
            start.ValueChanged += (_, __) => Changed();
            end.ValueChanged += (_, __) => Changed();
            startTime.ValueChanged += (_, __) => Changed();
            endTime.ValueChanged += (_, __) => Changed();
            mainPoints.ValueChanged += (_, __) => Changed();
            memberPoints.ValueChanged += (_, __) => Changed();
            minimumPoints.ValueChanged += (_, __) => Changed();
            counting.SelectedIndexChanged += (_, __) => Changed();
            pointScoring.SelectedIndexChanged += (_, __) => { UpdatePointScoring(); Changed(); };
            grouped.CheckedChanged += (_, __) => Changed();
            bands.ItemCheck += (_, __) => Changed();
            foreach (var grid in new[] { modeGrid, levelGrid })
            {
                grid.CellValueChanged += (_, e) =>
                {
                    if (e.RowIndex >= 0 && e.RowIndex < grid.Rows.Count && !grid.Rows[e.RowIndex].IsNewRow) Changed();
                };
                grid.UserDeletedRow += (_, __) => Changed();
                grid.CurrentCellDirtyStateChanged += (_, __) =>
                {
                    if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                };
                grid.DataError += (_, e) =>
                {
                    e.ThrowException = false;
                    MessageBox.Show(this, "Invalid cell value. Points and station thresholds must be whole numbers.",
                        "AwardChecker - Invalid rule value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
            }
            FormClosing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
            ApplyProfile(EmptyProfile(), null);
            dirty = false;
            Shown += (_, __) => Guard(() =>
            {
                string path = DatabasePreference.Load(RuntimeFolders.Current.LastDatabase);
                if (path.Length == 0) return;
                database.Text = path;
                status.Text = File.Exists(path)
                    ? "Last database restored. Click Read and score to read it; no automatic database access."
                    : "Remembered database is unavailable. Use Browse to choose an existing database.";
            });
        }

        private static Profile EmptyProfile() => new Profile
        {
            Bands = new List<string> { "20M" },
            Modes = new List<ModeMapping> { new ModeMapping { Mode = "CW", Group = "CW" } }
        };

        private static TextBox CallBox() => new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 10F) };
        private static DateTimePicker DateBox() => new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Dock = DockStyle.Fill };
        private static DateTimePicker TimeBox() => new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm",
            ShowUpDown = true, Dock = DockStyle.Fill
        };

        private static Control DateTimeBoxes(DateTimePicker date, DateTimePicker time)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            row.Controls.Add(date, 0, 0);
            row.Controls.Add(time, 1, 0);
            return row;
        }
        private static NumericUpDown PointsBox() => new NumericUpDown { Minimum = 0, Maximum = 1000000, Dock = DockStyle.Fill };
        private static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 3, 0, 0), AutoScroll = true };
        private static DataGridView Grid(bool readOnly) => new DataGridView
        {
            Dock = DockStyle.Fill, AutoGenerateColumns = true, ReadOnly = readOnly,
            AllowUserToAddRows = !readOnly, AllowUserToDeleteRows = !readOnly,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            RowHeadersVisible = !readOnly, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(240, 245, 250) }
        };
        private TabPage Page(string title)
        {
            var page = new TabPage(title) { Padding = new Padding(10), BackColor = Color.White, Font = Font };
            tabs.TabPages.Add(page);
            return page;
        }
        private static void DrawTabHeader(object? sender, DrawItemEventArgs e)
        {
            if (!(sender is TabControl control) || e.Index < 0) return;
            bool active = control.SelectedIndex == e.Index;
            using (var background = new SolidBrush(active ? Color.White : SystemColors.Control))
                e.Graphics.FillRectangle(background, e.Bounds);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (active)
                TextRenderer.DrawText(e.Graphics, control.TabPages[e.Index].Text, control.Font,
                    e.Bounds, Color.Green, flags);
            else
                using (var inactiveFont = new Font(control.Font.FontFamily,
                    control.Font.SizeInPoints - 2F, control.Font.Style))
                    TextRenderer.DrawText(e.Graphics, control.TabPages[e.Index].Text, inactiveFont,
                        e.Bounds, Color.Black, flags);
            if (active && control.Focused)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -3, -3));
        }
        private void AddButton(Control parent, string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 29 };
            button.Click += (_, __) => Guard(action);
            parent.Controls.Add(button);
        }
        private static void Field(TableLayoutPanel table, string label, Control control, int row)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            control.Dock = DockStyle.Fill;
            table.Controls.Add(control, 1, row);
        }

        private void BuildProfileTab()
        {
            levelGrid.AutoGenerateColumns = false;
            levelGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Name", DataPropertyName = "Name", HeaderText = "Level",
                MinimumWidth = 55, FillWeight = 56
            });
            levelGrid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "Category", DataPropertyName = "Category", HeaderText = "Category",
                DataSource = new[] { "Mixed Mode", "CW", "SSB", "DIGI", "Activator / All Bands" },
                MinimumWidth = 150, FillWeight = 155
            });
            levelGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Metric", DataPropertyName = "Metric", HeaderText = "Metric", ReadOnly = true,
                MinimumWidth = 52, FillWeight = 52
            });
            levelGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Points", DataPropertyName = "Points", HeaderText = "Hunter points",
                MinimumWidth = 72, FillWeight = 72
            });
            levelGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Qsos", DataPropertyName = "Qsos", HeaderText = "Activator QSOs",
                MinimumWidth = 77, FillWeight = 77
            });
            levelGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "MainStations", DataPropertyName = "MainStations", HeaderText = "Unique main stations",
                MinimumWidth = 95, FillWeight = 95
            });
            levelGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            levelGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            levelGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            levelGrid.CellValueChanged += (_, e) =>
            {
                if (loading || e.RowIndex < 0 || e.ColumnIndex < 0 ||
                    levelGrid.Columns[e.ColumnIndex].Name != "Category" ||
                    levelGrid.Rows[e.RowIndex].IsNewRow ||
                    !(levelGrid.Rows[e.RowIndex].DataBoundItem is AwardLevel level))
                    return;
                if (level.Category == "Activator / All Bands")
                {
                    level.Metric = AwardMetric.Qsos;
                    level.Points = 0;
                    level.MainStations = 0;
                }
                else
                {
                    level.Metric = AwardMetric.Points;
                    level.Qsos = 0;
                }
                levelGrid.Refresh();
            };
            var page = Page("1  Profile and award levels");
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 10, Padding = new Padding(0, 0, 15, 0) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Field(fields, "Profile name", name, 0);
            Field(fields, "Begin (UTC)", DateTimeBoxes(start, startTime), 1);
            Field(fields, "End (UTC, inclusive)", DateTimeBoxes(end, endTime), 2);
            Field(fields, "Minimum points to qualify", minimumPoints, 3);
            Field(fields, "Counting rule", counting, 4);
            Field(fields, "Mode counting", grouped, 5);
            Field(fields, "Own station (optional)", ownStation, 6);
            Field(fields, "Official rules URL", source, 7);
            notes.Multiline = true; notes.ScrollBars = ScrollBars.Vertical;
            fields.Controls.Add(new Label { Text = "Notes / interpretations", AutoSize = true }, 0, 8);
            fields.Controls.Add(notes, 1, 8); notes.Dock = DockStyle.Fill;
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var explanation = new Label
            {
                Text = "UTC times: Begin 00:00, End 23:59 by default. The final minute is included.\r\nDaily limits reset at 00:00 UTC.\r\n" +
                    "Monthly limits reset on the first day at 00:00 UTC.\r\n" +
                    "Exact callsign matching: no automatic suffix stripping.\r\n" +
                    "QSO_COMPLETE must be Y; other values are held for review.",
                Dock = DockStyle.Fill
            };
            fields.Controls.Add(explanation, 0, 9); fields.SetColumnSpan(explanation, 2);
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            var levelsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            levelsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            levelsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            levelsPanel.Controls.Add(new Label
            {
                Text = "AWARD LEVELS\r\nHunter levels use category points; Activator uses QSOs. Hunter also requires unique main stations.\r\nRows sort by category and threshold when read or saved.",
                Dock = DockStyle.Fill
            }, 0, 0);
            levelsPanel.Controls.Add(levelGrid, 0, 1);
            split.Controls.Add(fields, 0, 0); split.Controls.Add(levelsPanel, 1, 0);
            page.Controls.Add(split);
        }

        private void BuildStationTab()
        {
            var page = Page("2  Main and member stations");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.Controls.Add(StationPanel("MAIN STATIONS", mainCalls, mainPoints), 0, 0);
            layout.Controls.Add(StationPanel("MEMBER STATIONS (OPTIONAL)", memberCalls, memberPoints), 1, 0);
            page.Controls.Add(layout);
        }

        private Control StationPanel(string title, TextBox calls, NumericUpDown points)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(5) };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            panel.Controls.Add(new Label { Text = title, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            var options = Flow();
            options.Controls.Add(new Label { Text = "Points per counted QSO", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            points.Dock = DockStyle.None; points.Width = 85;
            options.Controls.Add(points);
            AddButton(options, "Import TXT (replace)...", () =>
            {
                using (var picker = new OpenFileDialog { Title = "AwardChecker - Import callsign list (replace)", Filter = "Callsign lists (*.txt)|*.txt|All files (*.*)|*.*" })
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    {
                        var parsed = Calls.Parse(File.ReadAllText(picker.FileName));
                        calls.Text = string.Join(Environment.NewLine, parsed);
                    }
            });
            panel.Controls.Add(options, 0, 1); panel.Controls.Add(calls, 0, 2);
            panel.Controls.Add(new Label { Text = "One call per line, or whitespace/comma separated.\r\nOverlap: MAIN takes precedence; warning shown after scoring.", Dock = DockStyle.Fill }, 0, 3);
            return panel;
        }

        private void BuildEligibilityTab()
        {
            var page = Page("3  Bands, modes and groups");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            var bandPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            bandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            bandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            bandPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bandPanel.Controls.Add(new Label { Text = "APPLICABLE BANDS\r\nOnly checked bands are eligible.", Dock = DockStyle.Fill }, 0, 0);
            var buttons = Flow();
            AddButton(buttons, "Select all", () => { for (int i = 0; i < bands.Items.Count; i++) bands.SetItemChecked(i, true); });
            AddButton(buttons, "Clear all", () => { for (int i = 0; i < bands.Items.Count; i++) bands.SetItemChecked(i, false); });
            AddButton(buttons, "Add band...", AddBand);
            bandPanel.Controls.Add(buttons, 0, 1); bandPanel.Controls.Add(bands, 0, 2);
            var modePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12, 0, 0, 0) };
            modePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
            modePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            modePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            modePanel.Controls.Add(new Label
            {
                Text = "APPLICABLE MODES, GROUPS AND POINTS\r\nSubmode takes priority. Unselected/unmapped modes need review.\r\nMode-based points replace category points for all eligible listed stations.\r\nEdit Points for every enabled mode. Groups/repeat limits remain independent.",
                Dock = DockStyle.Fill
            }, 0, 0);
            var modeButtons = Flow();
            modeButtons.WrapContents = true;
            pointScoring.Items.AddRange(new object[] { "Category-based points (Main / Member)", "Mode-based points" });
            modeButtons.Controls.Add(pointScoring);
            AddButton(modeButtons, "Select all modes", () => SetAllModes(true));
            AddButton(modeButtons, "Clear all modes", () => SetAllModes(false));
            modePanel.Controls.Add(modeButtons, 0, 1);
            modePanel.Controls.Add(modeGrid, 0, 2);
            layout.Controls.Add(bandPanel, 0, 0); layout.Controls.Add(modePanel, 1, 0);
            page.Controls.Add(layout);
        }

        private void UpdatePointScoring()
        {
            bool byMode = pointScoring.SelectedIndex == (int)PointScoring.Mode;
            mainPoints.Enabled = memberPoints.Enabled = !byMode;
            if (modeGrid.Columns.Contains("Points"))
            {
                modeGrid.Columns["Points"].Visible = byMode;
                modeGrid.Columns["Points"].HeaderText = "Points per QSO";
            }
        }

        private void AddBand()
        {
            using (var dialog = new Form { Text = "AwardChecker - Add ADIF band token", Size = new Size(350, 150), StartPosition = FormStartPosition.CenterParent })
            {
                var input = new TextBox { Dock = DockStyle.Top };
                var ok = new Button { Text = "Add", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
                dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var tokens = Calls.Parse(input.Text);
                if (tokens.Count != 1) throw new InvalidDataException("Enter one band token, for example 20M.");
                int index = bands.Items.IndexOf(tokens[0]);
                if (index >= 0) bands.SetItemChecked(index, true);
                else bands.Items.Add(tokens[0], true);
                Changed();
            }
        }
        private void SetAllModes(bool enabled)
        {
            modeGrid.EndEdit();
            var rows = (BindingList<ModeRow>)modeGrid.DataSource;
            foreach (var row in rows) row.Enabled = enabled;
            modeGrid.Refresh(); Changed();
        }

        private void BuildResultsTab()
        {
            var page = Page("4  Scores and QSO details");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            summary.BackColor = Color.FromArgb(228, 239, 249);
            summary.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            layout.Controls.Add(summary, 0, 0);
            var actions = Flow();
            actions.Controls.Add(view); actions.Controls.Add(exportQsos); actions.Controls.Add(exportStations); actions.Controls.Add(exportLevels);
            AddButton(actions, "QSO columns...", ChooseColumns);
            exportQsos.Click += (_, __) => Guard(() => Export(CurrentQsos(), "qsos"));
            exportStations.Click += (_, __) => Guard(() => Export(CurrentStations(), "stations"));
            exportLevels.Click += (_, __) => Guard(() => Export(results!.Levels, "award-levels"));
            layout.Controls.Add(actions, 0, 1);
            actions.Controls.Add(exportReport);
            foreach (var button in actions.Controls.OfType<Button>())
            {
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.MinimumSize = new Size(0, 29);
                button.Height = 29;
            }
            exportReport.Click += (_, __) => Guard(ExportReport);
            var resultTabs = new TabControl { Dock = DockStyle.Fill, Font = tabs.Font,
                DrawMode = TabDrawMode.OwnerDrawFixed };
            resultTabs.DrawItem += DrawTabHeader;
            foreach (var pair in new[] { Tuple.Create("QSO details", qsoGrid), Tuple.Create("Station scores", stationGrid), Tuple.Create("Award qualification", qualificationGrid) })
            {
                var tab = new TabPage(pair.Item1) { Font = Font };
                if (pair.Item2 == qsoGrid)
                {
                    var qsoPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
                    qsoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                    qsoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                    qsoPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    qsoPanel.Controls.Add(locationSection, 0, 0);
                    qsoPanel.Controls.Add(locationHeader, 0, 1);
                    qsoPanel.Controls.Add(qsoGrid, 0, 2);
                    tab.Controls.Add(qsoPanel);
                    locationSection.SelectedIndexChanged += (_, __) => BindSection();
                }
                else if (pair.Item2 == stationGrid)
                {
                    tab.Controls.Add(stationGrid);
                    tab.Controls.Add(workedStationsOnly);
                    workedStationsOnly.CheckedChanged += (_, __) => RefreshStations();
                    stationGrid.CellFormatting += (_, e) =>
                    {
                        if (e.RowIndex >= 0 && stationGrid.Rows[e.RowIndex].DataBoundItem is StationResult station &&
                            station.CountedQsos > 0) e.CellStyle.BackColor = Color.FromArgb(220, 244, 224);
                    };
                }
                else tab.Controls.Add(pair.Item2);
                resultTabs.TabPages.Add(tab);
            }
            layout.Controls.Add(resultTabs, 0, 2); layout.Controls.Add(warnings, 0, 3);
            page.Controls.Add(layout);
        }

        private Profile ReadProfile()
        {
            if (!ValidateChildren() || !modeGrid.EndEdit() || !levelGrid.EndEdit())
                throw new InvalidDataException("Finish or correct the active rule cell.");
            var p = new Profile
            {
                Name = name.Text.Trim(), Start = start.Value.Date, End = end.Value.Date,
                StartTime = startTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                EndTime = endTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                RuleSource = source.Text.Trim(), Notes = notes.Text, OwnStation = ownStation.Text,
                MainPoints = (int)mainPoints.Value, MemberPoints = (int)memberPoints.Value,
                MinimumPoints = (int)minimumPoints.Value,
                Scoring = (PointScoring)pointScoring.SelectedIndex,
                MainCalls = Calls.Parse(mainCalls.Text, "Main station list"), MemberCalls = Calls.Parse(memberCalls.Text, "Member station list"),
                Counting = (CountingRule)counting.SelectedIndex, CountModeGroups = grouped.Checked,
                Bands = bands.CheckedItems.Cast<string>().ToList(),
                Modes = ((BindingList<ModeRow>)modeGrid.DataSource)
                    .Where(r => r.Enabled || !string.IsNullOrWhiteSpace(r.Mode))
                    .Select(r => new ModeMapping { Enabled = r.Enabled, Mode = r.Mode, Group = r.Group, Points = r.Points }).ToList(),
                Columns = ColumnLayouts.Normalize(columnLayout),
                Levels = Profile.SortLevels(((BindingList<AwardLevel>)levelGrid.DataSource)
                    .Select(l => new AwardLevel
                    {
                        Name = l.Name, Category = l.Category, Metric = l.Metric,
                        Points = l.Points, Qsos = l.Qsos, MainStations = l.MainStations
                    }))
            };
            p.Validate();
            levelGrid.DataSource = new BindingList<AwardLevel>(p.Levels.Select(l =>
                new AwardLevel
                {
                    Name = l.Name, Category = l.Category, Metric = l.Metric,
                    Points = l.Points, Qsos = l.Qsos, MainStations = l.MainStations
                }).ToList());
            return p;
        }

        private void ApplyProfile(Profile p, string? path)
        {
            loading = true;
            try
            {
                profilePath = path;
                columnLayout = ColumnLayouts.Normalize(p.Columns);
                name.Text = p.Name; start.Value = p.Start; end.Value = p.End;
                startTime.Value = p.StartUtc; endTime.Value = p.EndUtc;
                ownStation.Text = p.OwnStation; source.Text = p.RuleSource; notes.Text = p.Notes;
                mainCalls.Text = string.Join(Environment.NewLine, p.MainCalls);
                memberCalls.Text = string.Join(Environment.NewLine, p.MemberCalls);
                mainPoints.Value = p.MainPoints; memberPoints.Value = p.MemberPoints;
                minimumPoints.Value = p.MinimumPoints;
                pointScoring.SelectedIndex = (int)p.Scoring;
                counting.SelectedIndex = (int)p.Counting; grouped.Checked = p.CountModeGroups;
                bands.Items.Clear();
                foreach (string band in Calls.Parse("2190M 630M 560M 160M 80M 60M 40M 30M 20M 17M 15M 12M 10M 8M 6M 5M 4M " +
                    "2M 1.25M 70CM 33CM 23CM 13CM 9CM 6CM 3CM 1.25CM 6MM 4MM 2.5MM 2MM 1MM SUBMM").Union(p.Bands))
                    bands.Items.Add(band, p.Bands.Contains(band));
                var mappings = p.Modes.ToDictionary(m => Calls.Key(m.Mode));
                var allModes = Calls.Parse("CW SSB USB LSB FT8 FT4 RTTY RTTYR PSK PSK31 PSK63 PSK125 MFSK MFSK16 MFSK32 " +
                    "JT65 JT9 JT4 Q65 JS8 FST4 FST4W OLIVIA CONTESTI DOMINO DOMINOEX THOR THROB " +
                    "HELL HELL80 FELDHELL FMHELL PSKHELL HFSK PACKET PKT AMTOR AMTORFEC PACTOR " +
                    "ARDOP VARA WINMOR DIGITALVOICE DSTAR C4FM DMR FREEDV MSK144 FSK441 ISCAT QRA64 " +
                    "WSPR ROS SSTV FAX CHIP CLO COHERENTOP ASCI MT63 LENTUS OPERA AM FM FT2").Union(mappings.Keys);
                modeGrid.DataSource = new BindingList<ModeRow>(allModes.Select(m => new ModeRow
                {
                    Enabled = mappings.TryGetValue(m, out var mapping) && mapping.Enabled, Mode = m,
                    Group = mappings.TryGetValue(m, out mapping) ? mapping.Group : m == "USB" || m == "LSB" ? "SSB" : m,
                    Points = mappings.TryGetValue(m, out mapping) && mapping.Points.HasValue ? mapping.Points : ModeMapping.SuggestedPoints(m)
                }).ToList());
                UpdatePointScoring();
                levelGrid.DataSource = new BindingList<AwardLevel>(Profile.SortLevels(p.Levels).Select(l =>
                    new AwardLevel
                    {
                        Name = l.Name, Category = l.Category, Metric = l.Metric,
                        Points = l.Points, Qsos = l.Qsos, MainStations = l.MainStations
                    }).ToList());
                dirty = false;
            }
            finally { loading = false; }
            UpdateWindowTitle(path == null ? null : p.Name);
            InvalidateResults();
            status.Text = path == null ? "Unsaved profile. Read-only database access. No automatic refresh." :
                "Loaded: " + path + " | Saved UTC: " + p.SavedUtc.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void Changed()
        {
            if (loading) return;
            dirty = true;
            InvalidateResults();
            status.Text = "Profile has unsaved changes. Save / Save as to keep this configuration.";
        }
        private void InvalidateResults()
        {
            if (loading) return;
            results = null; scoredProfile = null;
            qsoGrid.DataSource = null; stationGrid.DataSource = null; qualificationGrid.DataSource = null;
            exportQsos.Enabled = exportStations.Enabled = exportLevels.Enabled = exportReport.Enabled = false;
            sections.Clear(); locationSection.Items.Clear(); locationHeader.Text = "";
            summary.Text = "Results not calculated or stale. Click Read and score to refresh.";
            warnings.Text = "";
        }
        private bool ConfirmDiscard() => !dirty || MessageBox.Show(this,
            "Discard unsaved profile changes? Use Save first if you want to keep them.",
            "AwardChecker - Unsaved profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        private void ReplaceProfile(Profile p)
        {
            if (!ConfirmDiscard()) return;
            ApplyProfile(p, null); dirty = true;
        }
        private void ImportRulesText()
        {
            using (var dialog = new RuleImportDialog())
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Draft != null)
                    ReplaceProfile(dialog.Draft);
        }
        private void LoadProfile()
        {
            if (!ConfirmDiscard()) return;
            using (var picker = new OpenFileDialog { Title = "AwardChecker - Load profile",
                InitialDirectory = RuntimeFolders.Ensure(RuntimeFolders.Current.Profiles),
                Filter = "AwardChecker profile (*.award.xml)|*.award.xml|XML files (*.xml)|*.xml" })
                if (picker.ShowDialog(this) == DialogResult.OK)
                    ApplyProfile(Profiles.Load(picker.FileName), picker.FileName);
        }
        private void SaveProfile(bool saveAs)
        {
            var p = ReadProfile();
            string? path = profilePath;
            if (saveAs || path == null)
            {
                using (var picker = new SaveFileDialog { Title = "AwardChecker - Save profile", Filter = "AwardChecker profile (*.award.xml)|*.award.xml", DefaultExt = "award.xml",
                    InitialDirectory = RuntimeFolders.Ensure(RuntimeFolders.Current.Profiles),
                    FileName = SafeName(p.Name) + ".award.xml" })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    path = picker.FileName;
                }
            }
            if (path == null) throw new InvalidDataException("Choose a profile file to save.");
            Profiles.Save(p, path);
            MarkProfileSaved(p, path);
        }
        private void MarkProfileSaved(Profile profile, string path)
        {
            profilePath = path;
            dirty = false;
            UpdateWindowTitle(profile.Name);
            status.Text = "Saved: " + path + " | UTC " + profile.SavedUtc.ToString("yyyy-MM-dd HH:mm:ss");
        }
        private void UpdateWindowTitle(string? profileName)
        {
            string? savedProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName!.Trim();
            Version? version = GetType().Assembly.GetName().Version;
            string versionText = version?.ToString(3) ?? "1.0.0";
            Text = "AwardChecker v" + versionText +
                (savedProfileName == null ? "" : " - " + savedProfileName);
        }
        private async Task ReadAndScore()
        {
            try
            {
                var p = ReadProfile();
                string path = database.Text.Trim();
                if (!File.Exists(path)) throw new FileNotFoundException("Choose an existing DXKeeper .mdb database.");
                InvalidateResults();
                content.Enabled = false; UseWaitCursor = true;
                status.Text = "Reading database in Mode=Read, then calculating scores...";
                var result = await Task.Run(() => Scorer.Score(p,
                    Database.Read(path, p.Start, p.End, databaseProvider, p.StartTime, p.EndTime)));
                DisplayResults(result, p, DateTime.UtcNow);
                DatabasePreference.Save(RuntimeFolders.Current.LastDatabase, path);
            }
            catch (Exception ex) when (IsUserError(ex)) { ShowError(ex); }
            finally { content.Enabled = true; UseWaitCursor = false; }
        }

        private void DisplayResults(ScoreResult result, Profile p, DateTime calculatedUtc)
        {
                results = result; scoredProfile = p; scoredUtc = calculatedUtc;
                string minimumStatus = !result.MinimumQualificationConfigured
                    ? "Minimum qualification not configured"
                    : result.MinimumQualified
                        ? "Minimum qualification met"
                        : "Minimum qualification not met; " + result.MinimumPointsNeeded + " points needed";
                string highestLevels = string.IsNullOrEmpty(result.HighestLevels) ? "None" : result.HighestLevels;
                var countedQsos = result.Qsos.Where(q => q.Status == "Counted").ToList();
                int bandCount = countedQsos.Select(q => q.Band).Distinct().Count();
                int modeCount = countedQsos.Select(q => p.CountModeGroups ? q.ModeGroup :
                    q.Submode.Length > 0 ? q.Submode : q.Mode).Distinct().Count();
                string modeLabel = p.CountModeGroups ? "mode groups" : "modes";
                summary.Text = $"{p.Name}   |   {result.TotalPoints} points   |   {result.UniqueMain} unique main stations   |   {minimumStatus}   |   Highest levels: {highestLevels}\r\n" +
                    $"{p.StartUtc:yyyy-MM-dd HH:mm} through {p.EndUtc:yyyy-MM-dd HH:mm} UTC   |   {countedQsos.Count} counted QSOs   |   " +
                    $"{bandCount} bands   |   {modeCount} {modeLabel}\r\n" +
                    $"Snapshot {scoredUtc:yyyy-MM-dd HH:mm:ss} UTC";
                warnings.Text = string.Join(Environment.NewLine, result.Warnings.Concat(new[]
                {
                    "Provisional calculation: exact callsign matching; check eligibility, mode selections and all Review rows. Missing detail values display as unavailable."
                }));
                RefreshStations();
                qualificationGrid.DataSource = new SortableList<LevelResult>(result.Levels);
                RefreshQsoView();
                exportQsos.Enabled = exportStations.Enabled = exportLevels.Enabled = exportReport.Enabled = true;
                tabs.SelectedIndex = 3;
                status.Text = "Read-only snapshot complete. Refresh explicitly after DXKeeper changes.";
        }

        private List<QsoResult> CurrentQsos()
        {
            if (results == null) return new List<QsoResult>();
            IEnumerable<QsoResult> rows = results.Qsos;
            switch (view.SelectedIndex)
            {
                case 0: rows = rows.Where(q => q.Category != "Unlisted"); break;
                case 1: rows = rows.Where(q => q.Status == "Counted"); break;
                case 2: rows = rows.Where(q => q.Status == "Review"); break;
            }
            return rows.ToList();
        }
        private List<StationResult> CurrentStations() => results == null ? new List<StationResult>() :
            results.Stations.Where(s => !workedStationsOnly.Checked || s.CountedQsos > 0).ToList();

        private void RefreshStations()
        {
            stationGrid.DataSource = new SortableList<StationResult>(CurrentStations());
        }

        private void RefreshQsoView()
        {
            if (results == null) return;
            string? selected = locationSection.SelectedItem as string;
            sections = Reports.Sections(CurrentQsos());
            locationSection.Items.Clear();
            locationSection.Items.Add("All MyQTHIDs");
            foreach (var section in sections)
                locationSection.Items.Add("MyQTHID: " + (section.ID.Length == 0 ? "(missing)" : section.ID));
            int previous = selected == null ? -1 : locationSection.Items.IndexOf(selected);
            locationSection.SelectedIndex = previous >= 0 ? previous : 0;
        }

        private bool bindingColumns;

        private void RememberColumnWidth(object? sender, DataGridViewColumnEventArgs e)
        {
            if (bindingColumns || results == null) return;
            var setting = columnLayout.SingleOrDefault(c => c.Name == e.Column.DataPropertyName);
            if (setting == null || setting.Width == e.Column.Width) return;
            setting.Width = e.Column.Width;
            if (scoredProfile != null) scoredProfile.Columns = ColumnLayouts.Normalize(columnLayout);
            dirty = true;
            status.Text = "Column width changed for the table and report. Save the profile to retain it.";
        }

        private void BindSection()
        {
            bindingColumns = true;
            try
            {
            int selected = locationSection.SelectedIndex;
            var section = selected > 0 && selected <= sections.Count ? sections[selected - 1] : null;
            bool all = selected == 0;
            locationHeader.Text = all
                ? "All MyQTHIDs - combined QSO list. Select a location for its address header. Combined score is unchanged."
                : section?.Header ?? "No QSOs in selected view. Combined score above is unchanged.";
            qsoGrid.DataSource = new SortableList<QsoResult>(all ? CurrentQsos() : section?.Qsos ?? new List<QsoResult>());
            qsoGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            qsoGrid.AllowUserToResizeColumns = true;
            foreach (var column in columnLayout)
            {
                if (!qsoGrid.Columns.Contains(column.Name)) continue;
                var gridColumn = qsoGrid.Columns[column.Name];
                gridColumn.DisplayIndex = columnLayout.IndexOf(column);
                gridColumn.Visible = column.Visible;
                gridColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                if (column.Width > 0) gridColumn.Width = Math.Max(gridColumn.MinimumWidth, column.Width);
                else qsoGrid.AutoResizeColumn(gridColumn.Index, DataGridViewAutoSizeColumnMode.DisplayedCells);
            }
            if (qsoGrid.Columns.Contains("Utc")) qsoGrid.Columns["Utc"].DefaultCellStyle.Format = "yyyy-MM-dd HH:mm:ss";
            foreach (DataGridViewColumn column in qsoGrid.Columns)
                column.DefaultCellStyle.NullValue = column.DataPropertyName == "Submode" ? "" : "Unavailable";
            foreach (DataGridViewRow row in qsoGrid.Rows)
            {
                var item = (QsoResult)row.DataBoundItem;
                if (item.Status == "Review") row.DefaultCellStyle.BackColor = Color.LemonChiffon;
            }
            qsoGrid.CellFormatting -= MissingDetail;
            qsoGrid.CellFormatting += MissingDetail;
            qsoGrid.ColumnWidthChanged -= RememberColumnWidth;
            qsoGrid.ColumnWidthChanged += RememberColumnWidth;
            }
            finally { bindingColumns = false; }
        }
        private void MissingDetail(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            string field = qsoGrid.Columns[e.ColumnIndex].DataPropertyName;
            if (new[] { "Name", "QTH", "Locator", "Comment", "FrequencyMHz", "RstSent", "RstReceived", "MyRig", "MyAntenna", "MyQTHID", "PowerWatts", "Station", "Operator",
                "ContestID", "SRX", "SRXString", "STX", "STXString" }.Contains(field) &&
                string.IsNullOrEmpty(Convert.ToString(e.Value)))
            { e.Value = "Unavailable"; e.FormattingApplied = true; }
        }

        private void ChooseColumns()
        {
            using (var dialog = new ColumnsDialog(columnLayout))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                columnLayout = ColumnLayouts.Normalize(dialog.SelectedColumns);
                dirty = true;
                if (scoredProfile != null) scoredProfile.Columns = ColumnLayouts.Normalize(columnLayout);
                if (results != null) BindSection();
                status.Text = "Column layout applied. Save the profile to retain it. Scoring is unchanged.";
            }
        }

        private void ExportReport()
        {
            if (scoredProfile == null || results == null) throw new InvalidOperationException("Calculate fresh results first.");
            using (var picker = new SaveFileDialog { Title = "AwardChecker - Export sectioned location report",
                InitialDirectory = RuntimeFolders.Ensure(RuntimeFolders.Current.Reports),
                Filter = "HTML report (*.html)|*.html", FileName = SafeName(scoredProfile.Name) + "-report.html" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                Reports.WriteHtml(picker.FileName, results, scoredProfile, CurrentQsos(), scoredUtc);
                status.Text = "Exported sectioned report: " + picker.FileName;
            }
        }

        private void Export<T>(IEnumerable<T> rows, string kind)
        {
            if (scoredProfile == null) throw new InvalidOperationException("Calculate fresh results first.");
            using (var picker = new SaveFileDialog { Title = "AwardChecker - Export " + kind + " CSV",
                InitialDirectory = RuntimeFolders.Ensure(RuntimeFolders.Current.Reports),
                Filter = "CSV (*.csv)|*.csv", FileName = SafeName(scoredProfile.Name) + "-" + kind + ".csv" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                Csv.Write(picker.FileName, rows, scoredProfile, scoredUtc);
                status.Text = "Exported: " + picker.FileName;
            }
        }
        private static string SafeName(string text) => string.Concat(text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        private static bool IsUserError(Exception ex) => ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException ||
            ex is InvalidOperationException || ex is ArgumentException || ex is System.Data.OleDb.OleDbException ||
            ex is System.Xml.XmlException || ex is OverflowException;
        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) when (IsUserError(ex)) { ShowError(ex); }
        }
        private void ShowError(Exception ex)
        {
            status.Text = "Failed: " + ex.Message;
            MessageBox.Show(this, ex.Message + (ex.InnerException == null ? "" : "\r\n" + ex.InnerException.Message),
                "AwardChecker - action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static class Csv
    {
        public static string Cell(object? value)
        {
            string text = value is DateTime time ? time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) :
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (value is string && text.TrimStart().Length > 0 && "=+-@".Contains(text.TrimStart()[0]))
                text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        public static void Write<T>(string path, IEnumerable<T> rows, Profile profile, DateTime calculatedUtc)
        {
            var properties = typeof(T) == typeof(QsoResult)
                ? ColumnLayouts.Normalize(profile.Columns).Select(c => typeof(T).GetProperty(c.Name)!).ToArray()
                : typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public);
            string[] context = { profile.Name, profile.StartUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                profile.EndUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                profile.MinimumPoints.ToString(CultureInfo.InvariantCulture), profile.Counting.ToString(),
                profile.CountModeGroups ? "Groups" : "Individual modes",
                calculatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                string.Join("; ", profile.Modes.Where(m => m.Enabled).Select(m => m.Mode + "=" + m.Group)),
                profile.Scoring.ToString(), profile.PointRules };
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(string.Join(",", new[] { "Profile", "StartUTC", "EndInclusiveUTC", "MinimumQualificationPoints",
                    "CountingRule", "ModeCounting", "CalculatedUTC", "ModeMappings", "PointScoring", "PointRules" }
                    .Concat(properties.Select(p => p.Name)).Select(Cell)));
                foreach (var row in rows)
                    writer.WriteLine(string.Join(",", context.Select(Cell).Concat(properties.Select(p => Cell(p.GetValue(row))))));
            }

        }
    }

    public sealed class SortableList<T> : BindingList<T>
    {
        private bool sorted;
        private PropertyDescriptor? sortProperty;
        private ListSortDirection sortDirection;
        public SortableList(List<T> items) : base(items) { }
        protected override bool SupportsSortingCore => true;
        protected override bool IsSortedCore => sorted;
        protected override PropertyDescriptor? SortPropertyCore => sortProperty;
        protected override ListSortDirection SortDirectionCore => sortDirection;
        protected override void ApplySortCore(PropertyDescriptor property, ListSortDirection direction)
        {
            var list = (List<T>)Items;
            list.Sort((a, b) =>
            {
                int comparison = System.Collections.Comparer.DefaultInvariant.Compare(property.GetValue(a), property.GetValue(b));
                return direction == ListSortDirection.Ascending ? comparison : -comparison;
            });
            sorted = true; sortProperty = property; sortDirection = direction;
            ResetBindings();
        }
        protected override void RemoveSortCore() { sorted = false; sortProperty = null; }
    }
}
