using System.Security.Cryptography;
using System.Text;

namespace dockdev.Services.Tools;

/// <summary>Which character classes a generated secret may draw from.</summary>
public sealed record PasswordOptions(
    int Length = 20,
    bool Lowercase = true,
    bool Uppercase = true,
    bool Digits = true,
    bool Symbols = true,
    bool ExcludeAmbiguous = false);

/// <summary>
/// Random secret generation. Every character comes from
/// <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/> — a CSPRNG with rejection
/// sampling, so there is no modulo bias — because a "password generator" backed by
/// <see cref="Random"/> would be worse than useless: it looks exactly as random as the real thing.
/// </summary>
public static class PasswordTools
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Symbols = "!#$%&()*+-.:;<=>?@[]^_{|}~";

    /// <summary>Characters a reader can't reliably tell apart in a typical monospace font.</summary>
    private const string Ambiguous = "Il1O0o";

    public static string Generate(PasswordOptions options)
    {
        var alphabet = Alphabet(options);
        if (alphabet.Length == 0)
            return "";
        int length = Math.Clamp(options.Length, 4, 512);
        return new string(RandomNumberGenerator.GetItems<char>(alphabet, length));
    }

    public static IReadOnlyList<string> GenerateMany(PasswordOptions options, int count) =>
        [.. Enumerable.Range(0, Math.Clamp(count, 1, 500)).Select(_ => Generate(options))];

    /// <summary>
    /// Shannon entropy of the generation process in bits — <c>length × log2(alphabet)</c>. This is
    /// the honest figure for a randomly generated secret precisely because the string carries no
    /// structure a cracker could exploit; it says nothing about a password a human chose.
    /// </summary>
    public static double EntropyBits(PasswordOptions options)
    {
        var alphabet = Alphabet(options);
        return alphabet.Length == 0 ? 0 : Math.Clamp(options.Length, 4, 512) * Math.Log2(alphabet.Length);
    }

    private static string Alphabet(PasswordOptions options)
    {
        var sb = new StringBuilder();
        if (options.Lowercase)
            sb.Append(Lower);
        if (options.Uppercase)
            sb.Append(Upper);
        if (options.Digits)
            sb.Append(Digits);
        if (options.Symbols)
            sb.Append(Symbols);

        var alphabet = sb.ToString();
        return options.ExcludeAmbiguous
            ? new string(alphabet.Where(c => !Ambiguous.Contains(c, StringComparison.Ordinal)).ToArray())
            : alphabet;
    }
}
