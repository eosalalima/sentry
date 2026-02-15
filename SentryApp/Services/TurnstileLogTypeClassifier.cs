using System.Text;

namespace SentryApp.Services;

public static class TurnstileLogTypeClassifier
{
    public static bool IsIn(string? logType)
        => string.Equals(Normalize(logType), "IN", StringComparison.OrdinalIgnoreCase);

    public static bool IsOutFamily(string? logType)
    {
        var normalized = Normalize(logType);
        return normalized is "OUT" or "BREAKOUT" || normalized.Contains("OUT", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(string? logType)
    {
        if (string.IsNullOrWhiteSpace(logType))
            return string.Empty;

        var trimmed = logType.Trim();
        var buffer = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
                buffer.Append(char.ToUpperInvariant(ch));
        }

        return buffer.ToString();
    }
}
