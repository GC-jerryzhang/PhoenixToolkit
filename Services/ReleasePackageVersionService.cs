using System.Diagnostics;
using System.Globalization;

namespace PhoenixToolkit.Services;

internal static class ReleasePackageVersionService
{
    public static string? GetArchiveVersion(FileVersionInfo version) =>
        NormalizeArchiveVersion(version.FileVersion);

    public static string? NormalizeArchiveVersion(string? version)
    {
        if (!TryParseNumericVersion(version, 3, out var parts))
            return null;

        return $"v{parts[0]}.{parts[1]}.{parts[2]}";
    }

    public static string? NormalizeSourceVersion(string? version)
    {
        if (!TryParseNumericVersion(version, 1, out var parts))
            return null;

        return $"v{parts[0]}.{parts[1]}.{parts[2]}";
    }

    private static bool TryParseNumericVersion(string? version, int minimumComponents, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var components = value.Split('.');
        if (components.Length < minimumComponents || components.Length > 4)
            return false;

        var parsed = new int[3];
        for (var index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(components[index], NumberStyles.None, CultureInfo.InvariantCulture, out var component))
                return false;
            if (index < parsed.Length)
                parsed[index] = component;
        }

        parts = parsed;
        return true;
    }
}
