using System.Text;
using System.Text.Json;

namespace dockdev.Services.Tools;

/// <summary>
/// JWT Decoder's engine (design doc §14.7). Splits header/payload/signature and Base64url-decodes
/// the first two. <b>Never verifies a signature</b> — a tool that shows a green tick next to an
/// unverified token is worse than no tool.
/// </summary>
public static class JwtTools
{
    public sealed record Decoded(
        string HeaderJson, string PayloadJson, string Signature,
        string? Algorithm, string? Type, IReadOnlyList<string> Warnings,
        DateTimeOffset? Exp, DateTimeOffset? Iat, DateTimeOffset? Nbf);

    public static bool TryDecode(string token, out Decoded result, out string error)
    {
        result = new Decoded("", "", "", null, null, [], null, null, null);
        error = "";

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            error = "A JWT has three dot-separated parts: header.payload.signature.";
            return false;
        }

        if (!TryDecodeSegment(parts[0], out var headerJson) || !TryDecodeSegment(parts[1], out var payloadJson))
        {
            error = "Header or payload is not valid Base64url.";
            return false;
        }

        string? alg = null, typ = null;
        var warnings = new List<string>();
        try
        {
            using var header = JsonDocument.Parse(headerJson);
            if (header.RootElement.TryGetProperty("alg", out var algEl))
                alg = algEl.GetString();
            if (header.RootElement.TryGetProperty("typ", out var typEl))
                typ = typEl.GetString();
        }
        catch (JsonException) { /* header shown raw either way */ }

        if (string.Equals(alg, "none", StringComparison.OrdinalIgnoreCase))
            warnings.Add("alg is \"none\" — this token carries no signature at all.");
        if (typ is not null && !string.Equals(typ, "JWT", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"typ is \"{typ}\", not the expected \"JWT\".");

        DateTimeOffset? exp = null, iat = null, nbf = null;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            exp = ReadUnixSeconds(payload.RootElement, "exp");
            iat = ReadUnixSeconds(payload.RootElement, "iat");
            nbf = ReadUnixSeconds(payload.RootElement, "nbf");
        }
        catch (JsonException) { }

        var prettyHeader = TryPrettyPrint(headerJson);
        var prettyPayload = TryPrettyPrint(payloadJson);

        result = new Decoded(prettyHeader, prettyPayload, parts[2], alg, typ, warnings, exp, iat, nbf);
        return true;
    }

    /// <summary>A live "expired 3 hours ago" / "expires in 2 days" chip.</summary>
    public static string HumanizeRelative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = when - now;
        var past = delta < TimeSpan.Zero;
        var abs = delta.Duration();
        string span = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays}d {abs.Hours}h"
                     : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours}h {abs.Minutes}m"
                     : $"{(int)abs.TotalMinutes}m";
        return past ? $"expired {span} ago" : $"expires in {span}";
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el) && el.TryGetInt64(out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        {
            if (seconds is >= -62_135_596_800L and <= 253_402_300_799L)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        return null;
    }

    private static bool TryDecodeSegment(string segment, out string json)
    {
        json = "";
        try
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string TryPrettyPrint(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
