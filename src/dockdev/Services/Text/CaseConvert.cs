using System.Text;

namespace dockdev.Services.Text;

/// <summary>Case conversions and slugify for Text Toolkit (design doc §14.10).</summary>
public static class CaseConvert
{
    private static List<string> Words(string input)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        bool lastLower = false;

        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString());
            current.Clear();
        }

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_')
            {
                Flush();
                lastLower = false;
                continue;
            }
            if (char.IsUpper(c) && lastLower)
                Flush();
            current.Append(c);
            lastLower = char.IsLower(c);
        }
        Flush();
        return words;
    }

    public static string ToCamelCase(string input)
    {
        var words = Words(input);
        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
            sb.Append(i == 0 ? words[i].ToLowerInvariant() : Capitalize(words[i]));
        return sb.ToString();
    }

    public static string ToPascalCase(string input) => string.Concat(Words(input).Select(Capitalize));

    public static string ToSnakeCase(string input) => string.Join("_", Words(input).Select(w => w.ToLowerInvariant()));

    public static string ToKebabCase(string input) => string.Join("-", Words(input).Select(w => w.ToLowerInvariant()));

    public static string ToConstantCase(string input) => string.Join("_", Words(input).Select(w => w.ToUpperInvariant()));

    public static string ToTitleCase(string input) => string.Join(" ", Words(input).Select(Capitalize));

    public static string ToSentenceCase(string input)
    {
        var lower = input.ToLowerInvariant();
        for (int i = 0; i < lower.Length; i++)
        {
            if (char.IsLetter(lower[i]))
                return lower[..i] + char.ToUpperInvariant(lower[i]) + lower[(i + 1)..];
        }
        return lower;
    }

    public static string Slugify(string input)
    {
        var words = Words(input.Normalize(System.Text.NormalizationForm.FormKD))
            .Select(w => new string(w.Where(c => char.IsLetterOrDigit(c)).ToArray()))
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant());
        return string.Join("-", words);
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}
