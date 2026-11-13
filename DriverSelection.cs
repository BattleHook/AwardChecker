using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Security;

namespace AwardChecker
{
    public enum RuntimeArchitecture
    {
        X86,
        X64
    }

    public sealed class RegistryValueRead
    {
        public string? Value { get; }
        public string? Error { get; }

        public RegistryValueRead(string? value, string? error = null)
        {
            Value = value;
            Error = error;
        }
    }

    public interface IRegistryValueReader
    {
        RegistryValueRead ReadValue(RegistryHive hive, RegistryView view, string keyPath, string? valueName);
    }

    public interface IPeArchitectureReader
    {
        bool TryGetArchitecture(string path, out RuntimeArchitecture architecture, out string error);
    }

    public sealed class RuntimeCheck
    {
        public bool Available { get; }
        public int? Release { get; }
        public string Detail { get; }

        public RuntimeCheck(bool available, int? release, string detail)
        {
            Available = available;
            Release = release;
            Detail = detail;
        }
    }

    public sealed class ProviderCheck
    {
        public bool Available { get; }
        public string? DllPath { get; }
        public string Detail { get; }

        public ProviderCheck(bool available, string? dllPath, string detail)
        {
            Available = available;
            DllPath = dllPath;
            Detail = detail;
        }
    }

    public interface IDriverProbe
    {
        bool Is64BitOperatingSystem { get; }
        RuntimeCheck CheckFramework48(RuntimeArchitecture architecture);
        ProviderCheck CheckOleDbProvider(string providerName, RuntimeArchitecture architecture);
    }

    public sealed class WindowsDriverProbe : IDriverProbe
    {
        public const int MinimumFramework48Release = 528040;
        private const string FrameworkKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";
        private readonly IRegistryValueReader registry;
        private readonly IPeArchitectureReader peReader;

        public bool Is64BitOperatingSystem { get; }

        public WindowsDriverProbe(
            IRegistryValueReader? registry = null,
            IPeArchitectureReader? peReader = null,
            bool? is64BitOperatingSystem = null)
        {
            this.registry = registry ?? new WindowsRegistryValueReader();
            this.peReader = peReader ?? new FilePeArchitectureReader();
            Is64BitOperatingSystem = is64BitOperatingSystem ?? Environment.Is64BitOperatingSystem;
        }

        public RuntimeCheck CheckFramework48(RuntimeArchitecture architecture)
        {
            if (architecture == RuntimeArchitecture.X64 && !Is64BitOperatingSystem)
                return new RuntimeCheck(false, null, ".NET Framework 4.8 x64 is unavailable on 32-bit Windows.");

            var view = RegistryViewFor(architecture);
            RegistryValueRead value = registry.ReadValue(RegistryHive.LocalMachine, view, FrameworkKey, "Release");
            string label = architecture == RuntimeArchitecture.X64 ? "64-bit" : "32-bit";
            if (value.Error != null)
                return new RuntimeCheck(false, null, $".NET Framework 4.8 {label} check failed: {value.Error}");
            if (value.Value == null)
                return new RuntimeCheck(false, null, $".NET Framework 4.8 {label} runtime is not registered.");
            if (!int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int release))
                return new RuntimeCheck(false, null, $".NET Framework {label} Release registry value is invalid: {value.Value}");
            if (release < MinimumFramework48Release)
                return new RuntimeCheck(false, release,
                    $".NET Framework 4.8 {label} runtime is missing (Release {release}; at least {MinimumFramework48Release} is required).");
            return new RuntimeCheck(true, release, $".NET Framework 4.8 {label} runtime is available (Release {release}).");
        }

        public ProviderCheck CheckOleDbProvider(string providerName, RuntimeArchitecture architecture)
        {
            if (!DriverCatalog.IsSupportedProvider(providerName))
                return new ProviderCheck(false, null, $"{providerName} is not a supported provider.");
            if (architecture == RuntimeArchitecture.X64 && !Is64BitOperatingSystem)
                return new ProviderCheck(false, null, $"{providerName} x64 is unavailable on 32-bit Windows.");

            RegistryView view = RegistryViewFor(architecture);
            string label = architecture == RuntimeArchitecture.X64 ? "64-bit" : "32-bit";
            RegistryValueRead clsidValue = registry.ReadValue(RegistryHive.ClassesRoot, view,
                providerName + @"\CLSID", null);
            if (clsidValue.Error != null)
                return new ProviderCheck(false, null, $"{providerName} {label} registration could not be read: {clsidValue.Error}");
            if (!Guid.TryParse(clsidValue.Value, out Guid clsid))
                return new ProviderCheck(false, null, $"{providerName} {label} ProgID/CLSID registration is missing or invalid.");

            RegistryValueRead serverValue = registry.ReadValue(RegistryHive.ClassesRoot, view,
                @"CLSID\" + clsid.ToString("B") + @"\InprocServer32", null);
            if (serverValue.Error != null)
                return new ProviderCheck(false, null, $"{providerName} {label} CLSID registration could not be read: {serverValue.Error}");
            if (serverValue.Value == null || string.IsNullOrWhiteSpace(serverValue.Value))
                return new ProviderCheck(false, null, $"{providerName} {label} CLSID has no InprocServer32 DLL registration.");

            string dllPath = Environment.ExpandEnvironmentVariables(serverValue.Value.Trim().Trim('"'));
            if (!Path.IsPathRooted(dllPath))
                return new ProviderCheck(false, dllPath, $"{providerName} {label} DLL registration is not an absolute path: {dllPath}");
            if (!File.Exists(dllPath))
                return new ProviderCheck(false, dllPath, $"{providerName} {label} registered DLL is missing: {dllPath}");
            if (!peReader.TryGetArchitecture(dllPath, out RuntimeArchitecture actualArchitecture, out string error))
                return new ProviderCheck(false, dllPath, $"{providerName} {label} DLL is invalid or unreadable: {error}");
            if (actualArchitecture != architecture)
                return new ProviderCheck(false, dllPath,
                    $"{providerName} registration is in the {label} registry view, but its DLL is {ArchitectureName(actualArchitecture)}: {dllPath}");
            return new ProviderCheck(true, dllPath, $"{providerName} {label} OLE DB provider is available.");
        }

        private static RegistryView RegistryViewFor(RuntimeArchitecture architecture) =>
            architecture == RuntimeArchitecture.X64 ? RegistryView.Registry64 : RegistryView.Registry32;

        private static string ArchitectureName(RuntimeArchitecture architecture) =>
            architecture == RuntimeArchitecture.X64 ? "x64" : "x86";
    }

    public sealed class WindowsRegistryValueReader : IRegistryValueReader
    {
        public RegistryValueRead ReadValue(RegistryHive hive, RegistryView view, string keyPath, string? valueName)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey? key = root.OpenSubKey(keyPath, writable: false))
                {
                    if (key == null) return new RegistryValueRead(null);
                    object? value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return new RegistryValueRead(value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is SecurityException || ex is ArgumentException)
            {
                return new RegistryValueRead(null, ex.Message);
            }
        }
    }

    public sealed class FilePeArchitectureReader : IPeArchitectureReader
    {
        public bool TryGetArchitecture(string path, out RuntimeArchitecture architecture, out string error)
        {
            architecture = RuntimeArchitecture.X86;
            error = "";
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D)
                    {
                        error = "Missing or truncated DOS header.";
                        return false;
                    }
                    stream.Position = 0x3C;
                    int peOffset = reader.ReadInt32();
                    if (peOffset < 0 || peOffset > stream.Length - 6)
                    {
                        error = "Invalid PE header offset.";
                        return false;
                    }
                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550)
                    {
                        error = "Missing PE signature.";
                        return false;
                    }
                    ushort machine = reader.ReadUInt16();
                    if (machine == 0x014C)
                    {
                        architecture = RuntimeArchitecture.X86;
                        return true;
                    }
                    if (machine == 0x8664)
                    {
                        architecture = RuntimeArchitecture.X64;
                        return true;
                    }
                    error = $"Unsupported PE machine type 0x{machine:X4}.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                ex is SecurityException || ex is ArgumentException)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    public static class DriverCatalog
    {
        public const string Ace16 = "Microsoft.ACE.OLEDB.16.0";
        public const string Ace12 = "Microsoft.ACE.OLEDB.12.0";
        public const string Jet40 = "Microsoft.Jet.OLEDB.4.0";

        public static bool IsSupportedProvider(string providerName) =>
            string.Equals(providerName, Ace16, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerName, Ace12, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(providerName, Jet40, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DriverChoice
    {
        public string ProviderName { get; }
        public RuntimeArchitecture Architecture { get; }
        public string DllPath { get; }

        public DriverChoice(string providerName, RuntimeArchitecture architecture, string dllPath)
        {
            ProviderName = providerName;
            Architecture = architecture;
            DllPath = dllPath;
        }
    }

    public sealed class DriverSelectionResult
    {
        public DriverChoice? Choice { get; }
        public string? Warning { get; }
        public string Error { get; }
        public string Diagnostics { get; }
        public bool Success => Choice != null;

        public DriverSelectionResult(DriverChoice? choice, string? warning, string error, string diagnostics)
        {
            Choice = choice;
            Warning = warning;
            Error = error;
            Diagnostics = diagnostics;
        }
    }

    public static class DriverSelector
    {
        public static DriverSelectionResult Select(IDriverProbe probe)
        {
            var diagnostics = new System.Collections.Generic.List<string>();
            RuntimeArchitecture[] architectures = probe.Is64BitOperatingSystem
                ? new[] { RuntimeArchitecture.X64, RuntimeArchitecture.X86 }
                : new[] { RuntimeArchitecture.X86 };
            foreach (RuntimeArchitecture architecture in architectures)
            {
                RuntimeCheck runtime = probe.CheckFramework48(architecture);
                diagnostics.Add(runtime.Detail);
                var providers = architecture == RuntimeArchitecture.X86
                    ? new[] { DriverCatalog.Ace16, DriverCatalog.Ace12, DriverCatalog.Jet40 }
                    : new[] { DriverCatalog.Ace16, DriverCatalog.Ace12 };
                var checks = new System.Collections.Generic.List<ProviderCheck>();
                foreach (string provider in providers)
                {
                    ProviderCheck check = probe.CheckOleDbProvider(provider, architecture);
                    checks.Add(check);
                    diagnostics.Add(check.Detail);
                }
                if (!runtime.Available) continue;

                for (int i = 0; i < providers.Length; i++)
                {
                    var check = checks[i];
                    var dllPath = check.DllPath;
                    if (!check.Available || dllPath == null) continue;
                    var choice = new DriverChoice(providers[i], architecture, dllPath);
                    var warnings = new System.Collections.Generic.List<string>();
                    if (architecture == RuntimeArchitecture.X86 && probe.Is64BitOperatingSystem)
                    {
                        warnings.Add("The compatible 64-bit provider/runtime path was unavailable. " +
                            string.Join(" ", diagnostics.GetRange(0, diagnostics.Count - providers.Length - 1)));
                    }
                    if (string.Equals(choice.ProviderName, DriverCatalog.Jet40, StringComparison.OrdinalIgnoreCase))
                        warnings.Add("Using the legacy 32-bit Microsoft Jet 4.0 provider as the final fallback.");
                    return new DriverSelectionResult(choice,
                        warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings), "", string.Join(Environment.NewLine, diagnostics));
                }
            }

            return new DriverSelectionResult(null, null,
                "AwardChecker cannot start because no compatible .NET Framework 4.8 and Microsoft OLE DB provider combination was found. " +
                "No database was opened. " + string.Join(Environment.NewLine, diagnostics),
                string.Join(Environment.NewLine, diagnostics));
        }
    }
}
