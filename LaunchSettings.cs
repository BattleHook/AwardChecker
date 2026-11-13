using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AwardChecker
{
    public sealed class LaunchSettings
    {
        public string ProviderName { get; }
        public RuntimeArchitecture Architecture { get; }
        public string DataRoot { get; }

        private LaunchSettings(string providerName, RuntimeArchitecture architecture, string dataRoot)
        {
            ProviderName = providerName;
            Architecture = architecture;
            DataRoot = dataRoot;
        }

        public static LaunchSettings Parse(string[] args)
        {
            if (args == null || args.Length != 6)
                throw new ArgumentException("The selected runtime must be started by AwardChecker.exe with a validated provider and data root.");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i += 2)
            {
                if (args[i] != "--provider" && args[i] != "--architecture" && args[i] != "--data-root")
                    throw new ArgumentException("Unknown runtime startup argument: " + args[i]);
                if (string.IsNullOrWhiteSpace(args[i + 1]) || values.ContainsKey(args[i]))
                    throw new ArgumentException("Missing or repeated runtime startup argument: " + args[i]);
                values.Add(args[i], args[i + 1]);
            }
            if (values.Count != 3 || !values.ContainsKey("--provider") ||
                !values.ContainsKey("--architecture") || !values.ContainsKey("--data-root"))
                throw new ArgumentException("The selected runtime startup arguments are incomplete.");

            string provider = values["--provider"];
            if (!DriverCatalog.IsSupportedProvider(provider))
                throw new ArgumentException("Unsupported OLE DB provider passed to AwardChecker: " + provider);
            if (!Enum.TryParse(values["--architecture"], true, out RuntimeArchitecture architecture) ||
                (architecture != RuntimeArchitecture.X86 && architecture != RuntimeArchitecture.X64))
                throw new ArgumentException("The selected runtime architecture is invalid.");
            if (architecture == RuntimeArchitecture.X64 && string.Equals(provider, DriverCatalog.Jet40, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Microsoft Jet 4.0 is supported only in the x86 runtime.");
            string root = Path.GetFullPath(values["--data-root"]);
            if (!Directory.Exists(root))
                throw new ArgumentException("AwardChecker's data root does not exist: " + root);
            return new LaunchSettings(provider, architecture, root);
        }

        public static string BuildArguments(DriverChoice choice, string dataRoot)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            if (!DriverCatalog.IsSupportedProvider(choice.ProviderName))
                throw new ArgumentException("Unsupported OLE DB provider.", nameof(choice));
            if (choice.Architecture == RuntimeArchitecture.X64 &&
                string.Equals(choice.ProviderName, DriverCatalog.Jet40, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Microsoft Jet 4.0 is supported only in the x86 runtime.", nameof(choice));
            string root = Path.GetFullPath(dataRoot);
            return "--provider " + Quote(choice.ProviderName) +
                " --architecture " + Quote(choice.Architecture.ToString()) +
                " --data-root " + Quote(root);
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2).Append('"');
            return result.ToString();
        }
    }
}
