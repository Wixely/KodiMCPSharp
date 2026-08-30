using System.Text;
using System.Text.RegularExpressions;

namespace KodiMCPSharp.Security;

public sealed partial class SafeText
{
    private readonly int _absoluteMaximumLength = 4096;

    public string? Clean(string? value, int maximumLength = 512)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        maximumLength = Math.Clamp(maximumLength, 1, _absoluteMaximumLength);
        var trimmed = value.Trim();
        if (LooksSensitive(trimmed)) return "[redacted]";

        var builder = new StringBuilder(Math.Min(trimmed.Length, maximumLength));
        foreach (var character in trimmed)
        {
            if (builder.Length >= maximumLength) break;
            if (!char.IsControl(character) || character == '\t') builder.Append(character);
        }
        return builder.ToString();
    }

    public string Error(string? value) => Clean(value, 200) ?? "Kodi request failed";

    internal static bool LooksSensitive(string value) =>
        value.Contains("://", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("\\\\", StringComparison.Ordinal) ||
        DrivePath().IsMatch(value) ||
        value.StartsWith('/') ||
        value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("apikey=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("cookie=", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(^|\s)[A-Za-z]:[\\/]")]
    private static partial Regex DrivePath();
}
