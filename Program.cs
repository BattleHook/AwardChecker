using System;
using System.IO;
using System.Windows.Forms;

namespace AwardChecker
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                LaunchSettings launch = LaunchSettings.Parse(args);
                RuntimeArchitecture processArchitecture = Environment.Is64BitProcess
                    ? RuntimeArchitecture.X64 : RuntimeArchitecture.X86;
                if (launch.Architecture != processArchitecture)
                    throw new InvalidOperationException(
                        $"The launcher selected {launch.Architecture}, but this process is {processArchitecture}.");
                RuntimeFolders.ConfigureApplicationRoot(launch.DataRoot);
                Application.Run(new MainForm(launch.ProviderName));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, "AwardChecker - Startup failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
