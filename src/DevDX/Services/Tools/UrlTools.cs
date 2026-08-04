using System.Net;
using System.Text;

namespace DevDX.Services.Tools;

/// <summary>
/// URL &amp; Encoding's engine (design doc §14.6): RFC 3986 percent-encoding, form (space→<c>+</c>)
/// encoding, HTML entities, and a URL parser with an editable query table. Query parsing is
/// hand-rolled against <see cref="Uri.UnescapeDataString"/> rather than <c>System.Web.HttpUtility</c>,
/// which is a compatibility shim this app has no other reason to pull in.
/// </summary>
public static class UrlTools
{
    /// <summary>RFC 3986 percent-encoding: every reserved character escaped, spaces become
    /// <c>%20</c>.</summary>
    public static string EncodeRfc3986(string text) => Uri.EscapeDataString(text);

    public static string DecodeRfc3986(string text)
    {
        try { return Uri.UnescapeDataString(text); }
        catch { return text; }
    }

    /// <summary>Form (<c>application/x-www-form-urlencoded</c>) encoding: spaces become
    /// <c>+</c>, which differs from RFC 3986 and trips people up (design doc §14.6).</summary>
    public static string EncodeForm(string text) => WebUtility.UrlEncode(text) ?? "";

    public static string DecodeForm(string text) => WebUtility.UrlDecode(text) ?? "";

    public static string EncodeHtml(string text) => WebUtility.HtmlEncode(text) ?? "";

    public static string DecodeHtml(string text) => WebUtility.HtmlDecode(text) ?? "";

    public sealed record ParsedUrl(
        string Scheme, string Host, int Port, string Path, string Fragment,
        List<(string Key, string Value)> Query)
    {
        public string Rebuild()
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(Scheme) ? "https" : Scheme).Append("://").Append(Host);
            if (Port > 0)
                sb.Append(':').Append(Port);
            sb.Append(Path.StartsWith('/') || Path.Length == 0 ? Path : "/" + Path);

            if (Query.Count > 0)
            {
                sb.Append('?');
                for (int i = 0; i < Query.Count; i++)
                {
                    if (i > 0)
                        sb.Append('&');
                    sb.Append(Uri.EscapeDataString(Query[i].Key)).Append('=').Append(Uri.EscapeDataString(Query[i].Value));
                }
            }
            if (!string.IsNullOrEmpty(Fragment))
                sb.Append('#').Append(Fragment);
            return sb.ToString();
        }
    }

    public static bool TryParse(string url, out ParsedUrl parsed)
    {
        parsed = new ParsedUrl("", "", 0, "", "", []);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var query = new List<(string, string)>();
        var rawQuery = uri.Query.TrimStart('?');
        if (rawQuery.Length > 0)
        {
            foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                var key = idx >= 0 ? pair[..idx] : pair;
                var value = idx >= 0 ? pair[(idx + 1)..] : "";
                query.Add((Uri.UnescapeDataString(key.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' '))));
            }
        }

        parsed = new ParsedUrl(
            uri.Scheme, uri.Host, uri.IsDefaultPort ? 0 : uri.Port, uri.AbsolutePath,
            uri.Fragment.TrimStart('#'), query);
        return true;
    }
}
