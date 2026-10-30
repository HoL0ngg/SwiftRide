using System.Text.RegularExpressions;

namespace SwiftRide.ApiGateway.Observability;

public static class CorrelationSanitizer
{
    private static readonly Regex ValidChars = new(@"^[a-zA-Z0-9_\-\.]{1,128}$", RegexOptions.Compiled);

    public static string SanitizeOrGenerate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Guid.NewGuid().ToString("D");
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > 128)
        {
            trimmed = trimmed[..128];
        }

        return ValidChars.IsMatch(trimmed) ? trimmed : Guid.NewGuid().ToString("D");
    }
}
