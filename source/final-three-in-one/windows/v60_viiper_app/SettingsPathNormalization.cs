using System;
using System.IO;

namespace Y700Switch2V60Viiper;

// Settings-only normalization is independent from Windows registry/runtime discovery.
public static class SettingsPathNormalization
{
    public static string NormalizeUsbipExecutablePath(string? value)
    {
        try
        {
            string text = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')).Trim();
            string path = Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : "";
            return path.Length > 0 && string.Equals(Path.GetFileName(path), "usbip.exe", StringComparison.OrdinalIgnoreCase)
                ? path : "";
        }
        catch { return ""; }
    }
}
