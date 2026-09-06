using System.Globalization;

namespace dockdev.Services.Tools;

public enum EpochUnit { Seconds, Milliseconds, Microseconds }

/// <summary>Timestamp Converter's engine (design doc §14.14): epoch ⇄ ISO 8601 ⇄ RFC 1123 ⇄
/// local/UTC/named timezone, with magnitude-based unit auto-detection.</summary>
public static class TimestampTools
{
    /// <summary>Guesses which epoch unit a pasted number is, by magnitude: seconds since 1970 are
    /// ~10 digits today, milliseconds ~13, microseconds ~16.</summary>
    public static EpochUnit DetectUnit(long value)
    {
<<<<<<< HEAD
        // Math.Abs(long.MinValue) throws OverflowException; treat that single edge value as the
        // largest-magnitude bucket.
=======
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
        if (value == long.MinValue)
            return EpochUnit.Microseconds;
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture).Length;
        return digits switch
        {
            <= 11 => EpochUnit.Seconds,
            <= 14 => EpochUnit.Milliseconds,
            _ => EpochUnit.Microseconds,
        };
    }

    public static DateTimeOffset FromEpoch(long value, EpochUnit unit) => unit switch
    {
        EpochUnit.Seconds => DateTimeOffset.FromUnixTimeSeconds(value),
        EpochUnit.Milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(value),
        _ => DateTimeOffset.FromUnixTimeMilliseconds(value / 1000).AddTicks(value % 1000 * 10),
    };

    public static long ToEpoch(DateTimeOffset when, EpochUnit unit) => unit switch
    {
        EpochUnit.Seconds => when.ToUnixTimeSeconds(),
        EpochUnit.Milliseconds => when.ToUnixTimeMilliseconds(),
        _ => when.ToUnixTimeMilliseconds() * 1000,
    };

    public static string ToIso8601(DateTimeOffset when) => when.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);

    public static string ToRfc1123(DateTimeOffset when) => when.ToString("r", CultureInfo.InvariantCulture);

    public static bool TryParseAny(string text, out DateTimeOffset result)
    {
        text = text.Trim();
        if (long.TryParse(text, out var number))
        {
<<<<<<< HEAD
            // FromEpoch can throw for extreme magnitudes (milliseconds/microseconds scaling pushes
            // the value past DateTimeOffset's range); fail the parse instead of the app.
=======
            result = FromEpoch(number, DetectUnit(number));
            return true;
>>>>>>> 7203e6b12c66d9a2bf1e4a30b756d88612412177
            try
            {
                result = FromEpoch(number, DetectUnit(number));
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                result = default;
                return false;
            }
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>"3 days ago" / "in 2 hours".</summary>
    public static string ToRelative(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = when - now;
        bool past = delta < TimeSpan.Zero;
        var abs = delta.Duration();
        string span = abs.TotalDays >= 1 ? $"{(int)abs.TotalDays} day(s)"
                     : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours} hour(s)"
                     : abs.TotalMinutes >= 1 ? $"{(int)abs.TotalMinutes} minute(s)"
                     : $"{(int)abs.TotalSeconds} second(s)";
        return past ? $"{span} ago" : $"in {span}";
    }

    public static IReadOnlyList<string> CommonTimeZoneIds { get; } =
    [
        TimeZoneInfo.Local.Id, "UTC", "Eastern Standard Time", "Pacific Standard Time",
        "GMT Standard Time", "Central European Standard Time", "India Standard Time",
        "China Standard Time", "Tokyo Standard Time", "AUS Eastern Standard Time",
    ];

    public static DateTimeOffset ConvertToZone(DateTimeOffset when, string timeZoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTime(when, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return when;
        }
    }
}
