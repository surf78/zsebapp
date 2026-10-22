using System.Globalization;

namespace ZsebApp.Services;

public enum PeriodKind { All, Week, Month, Year }

/// <summary>
/// Időszak szűréshez és riporthoz: teljes időszak, ISO hét (hétfőtől vasárnapig), naptári hónap vagy év.
/// Az <see cref="End"/> kizárólagos (az utolsó nap utáni nap).
/// </summary>
public readonly record struct Period(PeriodKind Kind, DateOnly Start, DateOnly End)
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static Period All => new(PeriodKind.All, DateOnly.MinValue, DateOnly.MaxValue);

    public static Period Containing(PeriodKind kind, DateOnly d) => kind switch
    {
        PeriodKind.Week => FromStart(kind, d.AddDays(-(((int)d.DayOfWeek + 6) % 7))),
        PeriodKind.Month => FromStart(kind, new DateOnly(d.Year, d.Month, 1)),
        PeriodKind.Year => FromStart(kind, new DateOnly(d.Year, 1, 1)),
        _ => All,
    };

    private static Period FromStart(PeriodKind kind, DateOnly start) => kind switch
    {
        PeriodKind.Week => new(kind, start, start.AddDays(7)),
        PeriodKind.Month => new(kind, start, start.AddMonths(1)),
        PeriodKind.Year => new(kind, start, start.AddYears(1)),
        _ => All,
    };

    /// <summary>Előző (-1) / következő (+1) időszak.</summary>
    public Period Shift(int n) => Kind switch
    {
        PeriodKind.Week => FromStart(Kind, Start.AddDays(7 * n)),
        PeriodKind.Month => FromStart(Kind, Start.AddMonths(n)),
        PeriodKind.Year => FromStart(Kind, Start.AddYears(n)),
        _ => this,
    };

    public bool Contains(DateOnly d) => Kind == PeriodKind.All || (d >= Start && d < End);

    public bool IsCurrent => Kind != PeriodKind.All && Contains(Today);

    /// <summary>Az időszakból eddig eltelt napok száma (a jövőbeli napok nélkül).</summary>
    public int ElapsedDays
    {
        get
        {
            if (Kind == PeriodKind.All) return 0;
            var end = End <= Today ? End : Today.AddDays(1);
            return Math.Max(0, end.DayNumber - Start.DayNumber);
        }
    }

    public string Title => Kind switch
    {
        PeriodKind.Week =>
            $"{ISOWeek.GetYear(Start.ToDateTime(TimeOnly.MinValue))}. {ISOWeek.GetWeekOfYear(Start.ToDateTime(TimeOnly.MinValue))}. hét",
        PeriodKind.Month => BillViews.MonthTitle(Models.BillMonth.Key(Start)),
        PeriodKind.Year => $"{Start.Year}",
        _ => "Teljes időszak",
    };

    /// <summary>Kiegészítő sor a címhez, pl. hétnél "okt. 19. – okt. 25."</summary>
    public string? Subtitle => Kind == PeriodKind.Week ? $"{Fmt.TinyDate(Start)} – {Fmt.TinyDate(End.AddDays(-1))}" : null;

    /// <summary>Az előző időszak megnevezése összehasonlításhoz.</summary>
    public string PreviousName => Kind switch
    {
        PeriodKind.Week => "előző héthez",
        PeriodKind.Month => "előző hónaphoz",
        PeriodKind.Year => "előző évhez",
        _ => "",
    };

    public static string KindLabel(PeriodKind k) => k switch
    {
        PeriodKind.Week => "Hét",
        PeriodKind.Month => "Hónap",
        PeriodKind.Year => "Év",
        _ => "Mind",
    };
}
