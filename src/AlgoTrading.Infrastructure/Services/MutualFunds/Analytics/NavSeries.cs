namespace AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

/// <summary>One dated value: a scheme's NAV or an index's level.</summary>
public readonly record struct NavPoint(DateOnly Date, double Value);

/// <summary>
/// A daily series, oldest first, one value per date, every value above zero: a scheme's NAVs or a total-return
/// index. Immutable; the lookups the report's rules need (on or after, on or before, exactly on) are binary searches.
/// </summary>
/// <remarks>
/// A point found by <see cref="OnOrAfter"/> or <see cref="OnOrBefore"/> is usable only within
/// <see cref="ToleranceDays"/> of the day asked for: a NAV a week away from the date a period needs is a gap in the
/// history, not that date's NAV.
/// </remarks>
public sealed class NavSeries
{
    /// <summary>How far, in calendar days, a found point may be from the date asked for.</summary>
    public const int ToleranceDays = 7;

    private readonly NavPoint[] _points;

    /// <summary>Sorts the points, keeps the last value given for a date and drops values that are not above zero.</summary>
    public NavSeries(IEnumerable<NavPoint> points)
    {
        var byDate = new SortedDictionary<DateOnly, double>();
        foreach (var point in points)
        {
            if (point.Value > 0 && double.IsFinite(point.Value)) byDate[point.Date] = point.Value;
        }

        _points = byDate.Select(p => new NavPoint(p.Key, p.Value)).ToArray();
    }

    private NavSeries(NavPoint[] sorted) => _points = sorted;

    public static NavSeries Empty { get; } = new(Array.Empty<NavPoint>());

    public int Count => _points.Length;
    public bool IsEmpty => _points.Length == 0;
    public IReadOnlyList<NavPoint> Points => _points;
    public NavPoint this[int index] => _points[index];

    /// <summary>The first date; throws on an empty series.</summary>
    public DateOnly First => _points[0].Date;

    /// <summary>The last date; throws on an empty series.</summary>
    public DateOnly Last => _points[^1].Date;

    /// <summary>The points on or before <paramref name="asOf"/>: the series as it stood that day.</summary>
    public NavSeries Until(DateOnly asOf)
    {
        int end = IndexOnOrBefore(asOf);
        if (end == _points.Length - 1) return this;
        return end < 0 ? Empty : new NavSeries(_points[..(end + 1)]);
    }

    /// <summary>The index of the first point dated on or after <paramref name="date"/>, or -1.</summary>
    public int IndexOnOrAfter(DateOnly date)
    {
        int lo = 0, hi = _points.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_points[mid].Date < date) lo = mid + 1;
            else hi = mid;
        }

        return lo < _points.Length ? lo : -1;
    }

    /// <summary>The index of the last point dated on or before <paramref name="date"/>, or -1.</summary>
    public int IndexOnOrBefore(DateOnly date)
    {
        int lo = 0, hi = _points.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_points[mid].Date <= date) lo = mid + 1;
            else hi = mid;
        }

        return lo - 1;
    }

    /// <summary>The first point on or after <paramref name="date"/> and within the tolerance of it.</summary>
    public NavPoint? OnOrAfter(DateOnly date)
    {
        int i = IndexOnOrAfter(date);
        return i >= 0 && _points[i].Date.DayNumber - date.DayNumber <= ToleranceDays ? _points[i] : null;
    }

    /// <summary>The last point on or before <paramref name="date"/> and within the tolerance of it.</summary>
    public NavPoint? OnOrBefore(DateOnly date)
    {
        int i = IndexOnOrBefore(date);
        return i >= 0 && date.DayNumber - _points[i].Date.DayNumber <= ToleranceDays ? _points[i] : null;
    }

    /// <summary>The value dated exactly <paramref name="date"/>.</summary>
    public bool TryGet(DateOnly date, out double value)
    {
        int i = IndexOnOrBefore(date);
        if (i >= 0 && _points[i].Date == date)
        {
            value = _points[i].Value;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>The points dated in both series, from this one and from <paramref name="other"/>, oldest first.</summary>
    public (NavSeries Mine, NavSeries Theirs) Common(NavSeries other)
    {
        var mine = new List<NavPoint>();
        var theirs = new List<NavPoint>();
        int i = 0, j = 0;
        while (i < _points.Length && j < other._points.Length)
        {
            var a = _points[i].Date;
            var b = other._points[j].Date;
            if (a == b)
            {
                mine.Add(_points[i++]);
                theirs.Add(other._points[j++]);
            }
            else if (a < b)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return (new NavSeries(mine.ToArray()), new NavSeries(theirs.ToArray()));
    }
}

/// <summary>Calendar arithmetic the report's periods are defined by.</summary>
public static class FundDates
{
    /// <summary>The same month and day <paramref name="years"/> years earlier; 29 Feb becomes 28 Feb in a non-leap year.</summary>
    public static DateOnly YearsBefore(DateOnly date, int years) => date.AddYears(-years);

    /// <summary>The same day <paramref name="months"/> months earlier, or that month's last day when it is shorter.</summary>
    public static DateOnly MonthsBefore(DateOnly date, int months) => date.AddMonths(-months);

    /// <summary>The first day of the month <paramref name="months"/> months before <paramref name="date"/>'s month.</summary>
    public static DateOnly MonthStart(DateOnly date, int monthsBefore = 0) => new DateOnly(date.Year, date.Month, 1).AddMonths(-monthsBefore);

    public static int Days(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;
}
