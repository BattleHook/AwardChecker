using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AwardChecker
{
    public sealed class RuleImportDialog : Form
    {
        private readonly TextBox pastedRules = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9F)
        };
        private readonly TextBox review = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9F)
        };
        private readonly Button apply = new Button { Text = "Apply draft to editor", AutoSize = true, Enabled = false };
        private RuleImportResult? parsed;

        public Profile? Draft { get; private set; }

        public RuleImportDialog()
        {
            Text = "AwardChecker - Import award rules (local review)";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(850, 650);
            Size = new Size(1000, 760);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.Controls.Add(new Label
            {
                Text = "Paste rules or open a .txt/.md file, then Analyze. Or: Copy AI prompt, paste it into your own AI chat "
                    + "(Gemini, ChatGPT, ...), paste the AI's answer back here and Analyze - the answer is strictly validated locally.",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            layout.Controls.Add(pastedRules, 0, 1);
            layout.Controls.Add(new Label { Text = "Draft interpretation and review items", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            layout.Controls.Add(review, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var analyze = new Button { Text = "Analyze rules", AutoSize = true };
            var copyPrompt = new Button { Text = "Copy AI prompt", AutoSize = true };
            var openFile = new Button { Text = "Open rules file...", AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(apply);
            buttons.Controls.Add(analyze);
            buttons.Controls.Add(copyPrompt);
            buttons.Controls.Add(openFile);
            layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout);
            AcceptButton = analyze;
            CancelButton = cancel;

            analyze.Click += (_, __) => Analyze();
            copyPrompt.Click += (_, __) => CopyAiPrompt();
            openFile.Click += (_, __) => OpenRulesFile();
            apply.Click += (_, __) =>
            {
                if (parsed == null) return;
                Draft = parsed.Draft;
                DialogResult = DialogResult.OK;
                Close();
            };
            pastedRules.TextChanged += (_, __) =>
            {
                parsed = null;
                apply.Enabled = false;
                review.Clear();
            };
        }

        private void Analyze()
        {
            try
            {
                parsed = AiRuleAssist.LooksLikeStructuredAnswer(pastedRules.Text)
                    ? AiRuleAssist.ParseStructured(pastedRules.Text)
                    : RuleTextImporter.Parse(pastedRules.Text);
                review.Text = parsed.ReviewText;
                apply.Enabled = true;
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(this, ex.Message, "AwardChecker - Rules required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CopyAiPrompt()
        {
            try
            {
                Clipboard.SetText(AiRuleAssist.BuildPrompt(pastedRules.Text));
                review.Text = "AI prompt copied to the clipboard." + Environment.NewLine +
                    "1. Paste it into your own AI chat (Gemini, ChatGPT, Copilot, ...)." + Environment.NewLine +
                    "2. Copy the AI's complete answer." + Environment.NewLine +
                    "3. Paste the answer into the box above and click Analyze rules." + Environment.NewLine +
                    "AwardChecker sends nothing itself; you decide what leaves this PC.";
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(this, ex.Message, "AwardChecker - Rules required", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OpenRulesFile()
        {
            using (var picker = new OpenFileDialog
            {
                Title = "AwardChecker - Open plain-text award rules",
                Filter = "Rules text (*.txt;*.md)|*.txt;*.md|Text files (*.txt)|*.txt|Markdown files (*.md)|*.md",
                CheckFileExists = true
            })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    pastedRules.Text = RuleTextImporter.ReadFile(picker.FileName);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    MessageBox.Show(this, ex.Message, "AwardChecker - Could not read rules file",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
