using System.Globalization;
using System.Text;
using ZsebApp.Models;

namespace ZsebApp.Services;

public sealed record ReportBucket(string Label, long Sum);

public sealed record ExpenseReport(
    long Total,
    int Count,
    long? PreviousTotal,
    long DailyAverage,
    List<ReportBucket> ByCategory,
    List<ReportBucket> Breakdown);

public sealed record MeterReport(
    MeterType Type,
    decimal? Usage,
    int Days,
    int Readings,
    MeterReading? Last)
{
    public decimal? DailyUsage => Usage is { } u && Days > 0 ? u / Days : null;
}

/// <summary>Riport-számítások a Beállítások → Riport oldalhoz.</summary>
public static class Reports
{
    public static ExpenseReport Expenses(IReadOnlyCollection<Expense> all, Period p)
    {
        var items = all.Where(e => p.Contains(e.Date)).ToList();
        var total = items.Sum(e => (long)e.Amount);

        long? previous = p.Kind == PeriodKind.All
            ? null
            : all.Where(e => p.Shift(-1).Contains(e.Date)).Sum(e => (long)e.Amount);

        var days = p.Kind == PeriodKind.All
            ? (items.Count == 0 ? 0 : Period.Today.DayNumber - items.Min(e => e.Date).DayNumber + 1)
            : p.ElapsedDays;
        var daily = days > 0 ? (long)Math.Round(total / (double)days) : 0;

        var byCategory = items.GroupBy(e => e.Category)
            .Select(g => new ReportBucket(g.Key, g.Sum(e => (long)e.Amount)))
            .OrderByDescending(b => b.Sum)
            .ToList();

        return new ExpenseReport(total, items.Count, previous, daily, byCategory, Breakdown(items, p));
    }

    /// <summary>Időbeli bontás: hétnél napokra, hónapnál hetekre, évnél hónapokra, teljes időszaknál évekre.</summary>
    private static List<ReportBucket> Breakdown(List<Expense> items, Period p)
    {
        switch (p.Kind)
        {
            case PeriodKind.Week:
                return Enumerable.Range(0, 7)
                    .Select(i => p.Start.AddDays(i))
                    .Select(d => new ReportBucket($"{Fmt.DayShort(d)} {d.Day}.", items.Where(e => e.Date == d).Sum(e => (long)e.Amount)))
                    .ToList();

            case PeriodKind.Month:
            {
                var result = new List<ReportBucket>();
                var start = p.Start;
                while (start < p.End)
                {
                    // a hónapon belüli naptári hetek (hétfőtől vasárnapig)
                    var weekEnd = Period.Containing(PeriodKind.Week, start).End;
                    var end = weekEnd < p.End ? weekEnd : p.End;
                    var s = start;
                    result.Add(new ReportBucket(
                        $"{s.Day}–{end.AddDays(-1).Day}.",
                        items.Where(e => e.Date >= s && e.Date < end).Sum(e => (long)e.Amount)));
                    start = end;
                }
                return result;
            }

            case PeriodKind.Year:
                return Enumerable.Range(1, 12)
                    .Select(m => new ReportBucket(Fmt.MonthShort(m), items.Where(e => e.Date.Month == m).Sum(e => (long)e.Amount)))
                    .ToList();

            default:
                return items.GroupBy(e => e.Date.Year).OrderBy(g => g.Key)
                    .Select(g => new ReportBucket($"{g.Key}", g.Sum(e => (long)e.Amount)))
                    .ToList();
        }
    }

    /// <summary>
    /// Mérőnkénti fogyasztás: azoknak a leolvasási időszakoknak az összege, amelyek záró leolvasása
    /// az adott időszakba esik (az első leolvasásnak nincs előzménye, így ahhoz nem tartozik fogyasztás).
    /// </summary>
    public static List<MeterReport> Meters(IReadOnlyCollection<MeterReading> all, Period p) =>
        MeterTypes.All.Select(type =>
        {
            var rows = MeterMath.Rows(all, type.Name).Where(r => p.Contains(r.Reading.Date)).ToList();
            var withUsage = rows.Where(r => r.Usage is not null).ToList();
            return new MeterReport(
                type,
                withUsage.Count == 0 ? null : withUsage.Sum(r => r.Usage!.Value),
                withUsage.Sum(r => r.Days ?? 0),
                rows.Count,
                rows.LastOrDefault()?.Reading);
        }).ToList();

    // ---------- CSV (pontosvesszős, Excelben magyar beállítással közvetlenül megnyitható) ----------

    public static string ExpensesCsv(IEnumerable<Expense> all, Period p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dátum;Kategória;Megjegyzés;Összeg (Ft)");
        foreach (var e in all.Where(e => p.Contains(e.Date)).OrderBy(e => e.Date))
            sb.AppendLine($"{e.Date:yyyy-MM-dd};{Csv(e.Category)};{Csv(e.Description)};{e.Amount}");
        return sb.ToString();
    }

    public static string MetersCsv(IEnumerable<MeterReading> all, Period p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Mérő;Dátum;Állás;Mértékegység;Fogyasztás az előző óta;Napok;Megjegyzés");
        var list = all.ToList();
        foreach (var type in MeterTypes.All)
        {
            foreach (var r in MeterMath.Rows(list, type.Name).Where(r => p.Contains(r.Reading.Date)))
            {
                sb.AppendLine(string.Join(';',
                    type.Name,
                    r.Reading.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Dec(r.Reading.Value),
                    type.Unit,
                    r.Usage is { } u ? Dec(u) : "",
                    r.Days?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Csv(r.Reading.Note)));
            }
        }
        return sb.ToString();
    }

    // Magyar Excel tizedesvesszőt vár.
    private static string Dec(decimal d) => d.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string Csv(string? s)
    {
        s ??= "";
        return s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
