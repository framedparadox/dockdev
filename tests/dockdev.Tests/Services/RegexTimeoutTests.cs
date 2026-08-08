using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using dockdev.Services.Masking;
using dockdev.Services.Tools;
using Xunit;

namespace dockdev.Tests.Services;

/// <summary>
/// Design doc §21: "Every <see cref="Regex"/> in the app — the Regex Tester's user pattern <i>and</i>
/// the masker's rule patterns — is constructed with an explicit <see cref="Regex.MatchTimeout"/>."
/// <para>
/// That rule had exactly the fate §21 predicts for unenforced settings: the masker's twenty-odd
/// rules carried one and <c>ColorTools</c>'s parser did not, and nothing in the suite could tell.
/// This is the enforcement — reflective rather than a source scan, because what matters is the
/// timeout the constructed object ended up with, not the shape of the call that made it. A regex
/// built without one reports <see cref="Regex.InfiniteMatchTimeout"/>, which is the state this
/// refuses.
/// </para>
/// </summary>
public class RegexTimeoutTests
{
    [Fact]
    public void EveryStaticRegexInTheApp_HasAnExplicitMatchTimeout()
    {
        var offenders = new List<string>();
        int examined = 0;

        foreach (var (name, regex) in StaticRegexFields())
        {
            examined++;
            if (regex.MatchTimeout == Regex.InfiniteMatchTimeout)
                offenders.Add(name);
        }

        // A reflective test that silently matches nothing passes forever, so the sweep asserts it
        // found something before it asserts anything about what it found.
        Assert.True(examined > 0, "The reflective sweep found no static Regex fields at all — it " +
                                  "is no longer enforcing anything.");

        Assert.True(offenders.Count == 0,
            "Constructed without a MatchTimeout, which design doc §21 requires of every Regex in " +
            "the app — a pattern with no timeout evaluated on pasted text is a hang, not a slow " +
            "parse: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryMaskerRulePattern_HasAnExplicitMatchTimeout()
    {
        // The rule pack builds its patterns at runtime rather than holding them in static fields,
        // so the sweep above cannot see them — and §21 names them specifically.
        foreach (var rule in PiiRuleSet.Default)
        {
            foreach (var (which, pattern) in new[] { ("key", rule.KeyPattern), ("value", rule.ValuePattern) })
            {
                if (pattern is null)
                    continue;
                Assert.True(pattern.MatchTimeout != Regex.InfiniteMatchTimeout,
                    $"Masker rule '{rule.Id}' has a {which} pattern with no MatchTimeout.");
            }
        }
    }

    [Fact]
    public void UserSuppliedPattern_TimesOutRatherThanHanging()
    {
        // §21's ReDoS clause, end to end: the Regex Tester's own input is the one place a user can
        // trivially write catastrophic backtracking, and it must come back with a result rather
        // than take the window with it.
        //
        // `^(\w+\s?)*$` against a subject that cannot match is Microsoft's own documented example
        // for RegexMatchTimeout, chosen over the textbook `(a+)+` precisely because .NET's regex
        // reducer defeats several of the textbook ones outright — a ReDoS test the optimizer
        // quietly makes linear is a test that passes for the wrong reason.
        //
        // Fully qualified: newer .NET versions expose System.Text.RegularExpressions.RegexRunner
        // (the regex source-generator's own runner base class), which otherwise collides with this
        // app's dockdev.Services.Tools.RegexRunner under the `using`s above.
        var result = dockdev.Services.Tools.RegexRunner.Run(
            @"^(\w+\s?)*$",
            new string('a', 30) + "!",
            RegexOptions.None,
            replacement: null);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// Every <see cref="Regex"/> held in a static field anywhere in the app assembly. Static fields
    /// are where a pre-built pattern lives; an instance field would mean a regex compiled per
    /// object, which nothing here does.
    /// </summary>
    private static IEnumerable<(string Name, Regex Regex)> StaticRegexFields()
    {
        var assembly = typeof(ColorTools).Assembly;

        IReadOnlyList<Type?> types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some WinRT-projected/XAML-generated types will not load outside a UI host. None of
            // them hold patterns, and the ones that do still come back in ex.Types.
            types = ex.Types;
        }

        foreach (var type in types)
        {
            if (type is null || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                continue;

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var field in fields)
            {
                if (field.FieldType != typeof(Regex))
                    continue;

                Regex? value;
                try
                {
                    value = field.GetValue(null) as Regex;
                }
                catch (Exception)
                {
                    // A static initializer that needs a UI thread. Nothing that holds a regex does.
                    continue;
                }

                if (value is not null)
                    yield return ($"{type.FullName}.{field.Name}", value);
            }
        }
    }
}
