using System.Text;
using System.Text.Json;

namespace DevDX.Services.Text;

/// <summary>Escape/unescape for JSON strings, C#, SQL, shell and regex literals (design doc §14.10).</summary>
public static class EscapeCodecs
{
    public static string JsonEscape(string s) => JsonSerializer.Serialize(s)[1..^1];

    public static string JsonUnescape(string s)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? s; }
        catch { return s; }
    }

    public static string CSharpEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    public static string CSharpUnescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    '"' => '"',
                    '\\' => '\\',
                    _ => s[i],
                });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    public static string SqlEscape(string s) => s.Replace("'", "''");

    public static string SqlUnescape(string s) => s.Replace("''", "'");

    /// <summary>POSIX-shell single-quote escaping: the only universally safe way to quote an
    /// arbitrary string for a shell command line.</summary>
    public static string ShellEscape(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static string ShellUnescape(string s)
    {
        var trimmed = s.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            trimmed = trimmed[1..^1];
        return trimmed.Replace("'\\''", "'");
    }

    public static string RegexEscape(string s) => System.Text.RegularExpressions.Regex.Escape(s);

    public static string RegexUnescape(string s) => System.Text.RegularExpressions.Regex.Unescape(s);
}
