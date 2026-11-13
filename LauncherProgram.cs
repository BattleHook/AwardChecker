using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace AwardChecker
{
    internal static class LauncherProgram
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                DriverSelectionResult selection = DriverSelector.Select(new WindowsDriverProbe());
                if (!selection.Success)
                {
                    MessageBox.Show(selection.Error, "AwardChecker - Runtime unavailable",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                DriverChoice choice = selection.Choice!;
                if (selection.Warning != null)
                    MessageBox.Show(selection.Warning, "AwardChecker - Runtime selection",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);

                string root = AppDomain.CurrentDomain.BaseDirectory;
                string architectureFolder = choice.Architecture == RuntimeArchitecture.X64 ? "x64" : "x86";
                string executable = Path.Combine(root, architectureFolder, "AwardChecker.App.exe");
                if (!File.Exists(executable))
                    throw new FileNotFoundException(
                        $"The selected {architectureFolder} AwardChecker runtime is missing. Restore the complete application folder.", executable);
                if (!File.Exists(executable + ".config"))
                    throw new FileNotFoundException(
                        $"The selected {architectureFolder} AwardChecker runtime configuration is missing.", executable + ".config");

                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = LaunchSettings.BuildArguments(choice, root),
                    WorkingDirectory = root,
                    UseShellExecute = false
                };
                using (Process? process = Process.Start(start))
                {
                    if (process == null)
                        throw new InvalidOperationException("Windows did not start the selected AwardChecker runtime.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is InvalidOperationException || ex is Win32Exception || ex is ArgumentException)
            {
                MessageBox.Show(ex.Message, "AwardChecker - Startup failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
