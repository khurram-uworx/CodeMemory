namespace CodeMemory.AspNet.Scheduling;

public sealed class CronExpression
{
    readonly int[] minutes;
    readonly int[] hours;
    readonly int[] dom;
    readonly int[] months;
    readonly int[] dow;

    public static CronExpression Parse(string cron)
    {
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException("Cron expression must have exactly 5 fields (min hour dom month dow).");

        return new CronExpression(
            parseField(fields[0], 0, 59),
            parseField(fields[1], 0, 23),
            parseField(fields[2], 1, 31),
            parseField(fields[3], 1, 12),
            parseField(fields[4], 0, 6));
    }

    static int[] parseField(string field, int min, int max)
    {
        if (field == "*")
            return [];

        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var stepParts = part.Split('/');
            var step = stepParts.Length > 1 ? int.Parse(stepParts[1]) : 1;

            var rangeParts = stepParts[0].Split('-');
            int rangeStart, rangeEnd;

            if (rangeParts[0] == "*")
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else if (rangeParts.Length == 2)
            {
                rangeStart = int.Parse(rangeParts[0]);
                rangeEnd = int.Parse(rangeParts[1]);
            }
            else
            {
                rangeStart = int.Parse(rangeParts[0]);
                rangeEnd = rangeStart;
            }

            for (var i = rangeStart; i <= rangeEnd; i += step)
                if (i >= min && i <= max)
                    values.Add(i);
        }

        var sorted = values.OrderBy(static v => v).ToArray();

        if (sorted.Length == max - min + 1 && sorted[0] == min && sorted[^1] == max)
            return [];

        return sorted;
    }

    static bool matchesField(int[] values, int value, int min, int max)
    {
        if (values.Length == 0) return true;
        return Array.BinarySearch(values, value) >= 0;
    }

    static int? nextMatch(int[] values, int current, int min, int max)
    {
        if (values.Length == 0) return current;

        foreach (var v in values)
            if (v > current)
                return v;

        return null;
    }

    CronExpression(int[] minutes, int[] hours, int[] dom, int[] months, int[] dow)
    {
        this.minutes = minutes;
        this.hours = hours;
        this.dom = dom;
        this.months = months;
        this.dow = dow;
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset from)
    {
        var candidate = from.UtcDateTime.AddMinutes(1);
        candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
            candidate.Hour, candidate.Minute, 0, DateTimeKind.Utc);

        var limit = from.UtcDateTime.AddYears(4);

        while (candidate <= limit)
        {
            if (!matchesField(months, candidate.Month, 1, 12))
            {
                candidate = candidate.AddMonths(1);
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                continue;
            }

            var domWild = dom.Length == 0;
            var dowWild = dow.Length == 0;
            var domMatch = domWild || matchesField(dom, candidate.Day, 1, 31);
            var dowMatch = dowWild || matchesField(dow, (int)candidate.DayOfWeek, 0, 6);

            var dayMatch = (domWild && dowWild) || (domWild && dowMatch) || (dowWild && domMatch) || (domMatch || dowMatch);

            if (!dayMatch)
            {
                candidate = candidate.AddDays(1);
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, DateTimeKind.Utc);
                continue;
            }

            if (!matchesField(hours, candidate.Hour, 0, 23))
            {
                var nextHour = nextMatch(hours, candidate.Hour, 0, 23);
                if (nextHour.HasValue)
                {
                    candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
                        nextHour.Value, 0, 0, DateTimeKind.Utc);
                }
                else
                {
                    candidate = candidate.AddDays(1);
                    candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, DateTimeKind.Utc);
                }
                continue;
            }

            if (!matchesField(minutes, candidate.Minute, 0, 59))
            {
                var nextMinute = nextMatch(minutes, candidate.Minute, 0, 59);
                if (nextMinute.HasValue)
                {
                    candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
                        candidate.Hour, nextMinute.Value, 0, DateTimeKind.Utc);
                }
                else
                {
                    candidate = candidate.AddHours(1);
                    candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day,
                        candidate.Hour, 0, 0, DateTimeKind.Utc);
                }
                continue;
            }

            return new DateTimeOffset(candidate, TimeSpan.Zero);
        }

        return null;
    }
}
