using System.Globalization;
using System.Text;

namespace DevDX.Services.Tools;

/// <summary>One parsed cron expression, or the reason it wouldn't parse.</summary>
public sealed record CronSchedule(bool Success, string Error, bool[] Minutes, bool[] Hours, bool[] DaysOfMonth, bool[] Months, bool[] DaysOfWeek);

/// <summary>
/// Standard five-field cron (<c>minute hour day-of-month month day-of-week</c>), plus the common
/// <c>@hourly</c>-style macros. Supports <c>*</c>, <c>a-b</c>, <c>a-b/n</c>, <c>*&#47;n</c> and
/// comma lists, and three-letter month/day names.
/// <para>
/// The next occurrences are found by walking forward a minute at a time rather than by solving
/// the fields arithmetically: a year of minutes is barely half a million cheap array lookups, and
/// it is impossible to get the day-of-month/day-of-week OR-rule subtly wrong that way.
/// </para>
/// </summary>
public static class CronTools
{
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>How far <see cref="NextOccurrences"/> will look before giving up — an expression
    /// like <c>0 0 30 2 *</c> (30 February) never matches, and must not spin forever.</summary>
    private static readonly TimeSpan SearchLimit = TimeSpan.FromDays(366 * 4);

    public static CronSchedule Parse(string expression)
    {
        var text = (expression ?? "").Trim();
        if (text.Length == 0)
            return Fail("Cron.Error.Empty");

        text = text.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => text,
        };

        var fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return Fail("Cron.Error.FieldCount");

        if (!TryField(fields[0], 0, 59, null, out var minutes, out var bad) ||
            !TryField(fields[1], 0, 23, null, out var hours, out bad) ||
            !TryField(fields[2], 1, 31, null, out var daysOfMonth, out bad) ||
            !TryField(fields[3], 1, 12, MonthNames, out var months, out bad) ||
            !TryField(fields[4], 0, 6, DayNames, out var daysOfWeek, out bad))
        {
            return new CronSchedule(false, bad, [], [], [], [], []);
        }

        return new CronSchedule(true, "", minutes, hours, daysOfMonth, months, daysOfWeek);
    }

    /// <summary>
    /// The next <paramref name="count"/> times the schedule fires strictly after
    /// <paramref name="after"/>. Empty if the expression can never match.
    /// </summary>
    public static IReadOnlyList<DateTime> NextOccurrences(CronSchedule schedule, DateTime after, int count)
    {
        var results = new List<DateTime>();
        if (!schedule.Success || count <= 0)
            return results;

        // Cron has minute resolution, so start from the top of the next minute.
        var cursor = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var deadline = cursor + SearchLimit;

        while (cursor < deadline && results.Count < count)
        {
            if (!schedule.Months[cursor.Month - 1])
            {
                // Nothing this month can match — skip to the first of the next one.
                cursor = new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, cursor.Kind).AddMonths(1);
                continue;
            }
            if (!DayMatches(schedule, cursor))
            {
                cursor = cursor.Date.AddDays(1);
                continue;
            }
            if (!schedule.Hours[cursor.Hour])
            {
                cursor = cursor.Date.AddHours(cursor.Hour + 1);
                continue;
            }
            if (schedule.Minutes[cursor.Minute])
                results.Add(cursor);
            cursor = cursor.AddMinutes(1);
        }
        return results;
    }

    /// <summary>
    /// Cron's one genuine oddity: when <em>both</em> day-of-month and day-of-week are restricted,
    /// a day matching <em>either</em> fires. When only one is restricted, only that one counts.
    /// </summary>
    private static bool DayMatches(CronSchedule schedule, DateTime day)
    {
        bool domRestricted = schedule.DaysOfMonth.Any(v => !v);
        bool dowRestricted = schedule.DaysOfWeek.Any(v => !v);
        bool dom = schedule.DaysOfMonth[day.Day - 1];
        bool dow = schedule.DaysOfWeek[(int)day.DayOfWeek];

        if (domRestricted && dowRestricted)
            return dom || dow;
        return dom && dow;
    }

    /// <summary>A plain-English reading of the expression, for the line above the next-run list.</summary>
    public static string Describe(CronSchedule schedule)
    {
        if (!schedule.Success)
            return "";

        var sb = new StringBuilder();
        sb.Append(Loc.Format("Cron.At", Part(schedule.Minutes, 0, Loc.Get("Cron.EveryMinute"), "Cron.Minute"),
                                        Part(schedule.Hours, 0, Loc.Get("Cron.EveryHour"), "Cron.Hour")));
        sb.Append(", ");
        sb.Append(Part(schedule.DaysOfMonth, 1, Loc.Get("Cron.EveryDayOfMonth"), "Cron.DayOfMonth"));
        sb.Append(", ");
        sb.Append(Part(schedule.Months, 1, Loc.Get("Cron.EveryMonth"), "Cron.Month"));
        sb.Append(", ");
        sb.Append(Part(schedule.DaysOfWeek, 0, Loc.Get("Cron.EveryDayOfWeek"), "Cron.DayOfWeek"));
        return sb.ToString();
    }

    private static string Part(bool[] field, int offset, string everyText, string listKey)
    {
        if (field.All(v => v))
            return everyText;
        var values = field.Select((on, i) => (on, i)).Where(t => t.on).Select(t => (t.i + offset).ToString(CultureInfo.InvariantCulture));
        return Loc.Format(listKey, string.Join(", ", values));
    }

    private static bool TryField(string field, int min, int max, string[]? names, out bool[] set, out string error)
    {
        set = new bool[max - min + 1];
        error = "";

        foreach (var term in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int step = 1;
            var body = term;
            int slash = term.IndexOf('/');
            if (slash >= 0)
            {
                body = term[..slash];
                if (!int.TryParse(term[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = Loc.Format("Cron.Error.Term", term);
                    return false;
                }
            }

            int from, to;
            if (body is "*" or "")
            {
                (from, to) = (min, max);
            }
            else
            {
                int dash = body.IndexOf('-', 1);
                if (dash > 0)
                {
                    if (!TryValue(body[..dash], min, max, names, out from) ||
                        !TryValue(body[(dash + 1)..], min, max, names, out to))
                    {
                        error = Loc.Format("Cron.Error.Term", term);
                        return false;
                    }
                }
                else
                {
                    if (!TryValue(body, min, max, names, out from))
                    {
                        error = Loc.Format("Cron.Error.Term", term);
                        return false;
                    }
                    // A bare "5/15" means "from 5 to the end of the range, every 15".
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                error = Loc.Format("Cron.Error.Term", term);
                return false;
            }
            for (int v = from; v <= to; v += step)
                set[v - min] = true;
        }

        if (set.All(v => !v))
        {
            error = Loc.Format("Cron.Error.Term", field);
            return false;
        }
        return true;
    }

    private static bool TryValue(string token, int min, int max, string[]? names, out int value)
    {
        token = token.Trim().ToLowerInvariant();
        if (names is not null)
        {
            int index = Array.IndexOf(names, token);
            if (index >= 0)
            {
                value = index + min;
                return true;
            }
        }
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return false;
        // Cron accepts both 0 and 7 for Sunday.
        if (names == DayNames && value == 7)
            value = 0;
        return value >= min && value <= max;
    }

    private static CronSchedule Fail(string key) => new(false, Loc.Get(key), [], [], [], [], []);
}
