using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>
/// Egy sor a számlalistában. Ha <see cref="Bill"/> null, akkor a fix tétel ebben a hónapban még
/// nincs rögzítve („virtuális” sor) – koppintásra lesz belőle valódi számla.
/// </summary>
public sealed record BillRow(Bill? Bill, BillTemplate? Template, string Group, string Name, int Amount, bool Paid, DateOnly? DueDate)
{
    public bool IsVirtual => Bill is null;
    public bool IsOverdue => !Paid && DueDate is { } d && d < Period.Today;
    public string Key => Bill?.Id.ToString() ?? "t-" + Template!.Id;

    /// <summary>Túlfizetés (csak fizetett számlánál).</summary>
    public bool Overpaid { get; init; }

    /// <summary>Az állapot felirata: Rendezetlen / Fizetve / Túlfizetés.</summary>
    public string StatusText => !Paid ? "Rendezetlen" : Overpaid ? "Túlfizetés" : "Fizetve";
}

public sealed record BillGroupView(string Name, List<BillRow> Rows)
{
    public long Unpaid => Rows.Where(r => !r.Paid).Sum(r => (long)r.Amount);
    /// <summary>Befizetett összeg – a túlfizetéses tételek nélkül (azok külön, <see cref="OverpaidSum"/>).</summary>
    public long PaidSum => Rows.Where(r => r.Paid && !r.Overpaid).Sum(r => (long)r.Amount);
    public long OverpaidSum => Rows.Where(r => r.Overpaid).Sum(r => (long)r.Amount);
    public int PaidCount => Rows.Count(r => r.Paid);
}

public sealed record BillMonthView(string MonthKey, List<BillGroupView> Groups)
{
    public string Title => BillViews.MonthTitle(MonthKey);
    public IEnumerable<BillRow> Rows => Groups.SelectMany(g => g.Rows);
}

public static class BillViews
{
    /// <summary>"2026-10" → "2026-10 október"</summary>
    public static string MonthTitle(string key) =>
        BillMonth.TryParse(key, out var d) ? $"{key} {Fmt.MonthName(d.Month)}" : key;

    public static string GroupOf(Bill b) => string.IsNullOrWhiteSpace(b.Group) ? BillDefaults.OtherGroup : b.Group.Trim();

    /// <summary>Csoportok sorrendje: a fix tételek sorrendje, utána a többi ábécérendben, végül az Egyéb.</summary>
    public static List<string> Groups(IEnumerable<BillTemplate> templates, IEnumerable<Bill> bills)
    {
        var result = new List<string>();
        foreach (var t in templates)
            if (!result.Contains(t.Group, StringComparer.OrdinalIgnoreCase)) result.Add(t.Group);

        var extra = bills.Select(GroupOf)
            .Where(g => !result.Contains(g, StringComparer.OrdinalIgnoreCase) && g != BillDefaults.OtherGroup)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        result.AddRange(extra);

        if (!result.Contains(BillDefaults.OtherGroup)) result.Add(BillDefaults.OtherGroup);
        return result;
    }

    /// <summary>A fix tétel határideje az adott hónapban (a hónap utolsó napjára vágva).</summary>
    public static DateOnly? TemplateDueDate(BillTemplate t, string monthKey)
    {
        if (t.DueDay is not { } day || !BillMonth.TryParse(monthKey, out var first)) return null;
        var last = first.AddMonths(1).AddDays(-1).Day;
        return new DateOnly(first.Year, first.Month, Math.Clamp(day, 1, last));
    }

    /// <summary>
    /// A fix tétel határideje egy még nem rögzített hónapban: a legutóbbi korábbi hónap számlájának határidejét
    /// viszi tovább (ugyanannyiadik napra, ugyanannyi hónap eltolással), ha az nincs, a fix tétel saját határidő-napját.
    /// </summary>
    public static DateOnly? CarriedDueDate(IEnumerable<Bill> bills, BillTemplate t, string monthKey)
    {
        if (!BillMonth.TryParse(monthKey, out var target)) return TemplateDueDate(t, monthKey);

        var last = bills
            .Where(b => b.TemplateId == t.Id && b.DueDate is not null && string.CompareOrdinal(b.EffectiveMonth, monthKey) < 0)
            .OrderByDescending(b => b.EffectiveMonth, StringComparer.Ordinal)
            .ThenByDescending(b => b.DueDate)
            .FirstOrDefault();
        if (last is null || !BillMonth.TryParse(last.EffectiveMonth, out var lastMonth))
            return TemplateDueDate(t, monthKey);

        // Pl. az októberi számla november 5-én esedékes → a novemberi december 5-én.
        var due = last.DueDate!.Value;
        var offset = (due.Year - lastMonth.Year) * 12 + due.Month - lastMonth.Month;
        var first = target.AddMonths(offset);
        var lastDay = first.AddMonths(1).AddDays(-1).Day;
        return new DateOnly(first.Year, first.Month, Math.Min(due.Day, lastDay));
    }

    private static BillRow FromBill(Bill b) => new(b, null, GroupOf(b), b.Name, b.Amount, b.Paid, b.DueDate) { Overpaid = b.Paid && b.Overpaid };

    /// <summary>Egy hónap nézete: a fix tételek (rögzítve vagy még kitöltendőként) és az egyéb számlák, csoportosítva.</summary>
    public static BillMonthView Month(IEnumerable<Bill> bills, IReadOnlyList<BillTemplate> templates, string monthKey, string? groupFilter = null)
    {
        var inMonth = bills.Where(b => b.EffectiveMonth == monthKey).ToList();
        var used = new HashSet<Guid>();
        var rows = new List<BillRow>();

        foreach (var t in templates)
        {
            var bill = inMonth.FirstOrDefault(b => b.TemplateId == t.Id && !used.Contains(b.Id));
            if (bill is not null)
            {
                used.Add(bill.Id);
                rows.Add(FromBill(bill) with { Template = t });
            }
            else
            {
                rows.Add(new BillRow(null, t, t.Group, t.Name, t.DefaultAmount ?? 0, false, CarriedDueDate(bills, t, monthKey)));
            }
        }

        rows.AddRange(inMonth.Where(b => !used.Contains(b.Id)).OrderBy(b => b.CreatedAt).Select(FromBill));
        return new BillMonthView(monthKey, Arrange(rows, templates, inMonth, groupFilter));
    }

    /// <summary>Hét / év / teljes időszak: csak a ténylegesen rögzített számlák, hónapokra bontva (legújabb elöl).</summary>
    public static List<BillMonthView> Range(IEnumerable<Bill> bills, IReadOnlyList<BillTemplate> templates, Period p, string? groupFilter = null)
    {
        var list = bills.Where(b => p.Contains(b.EffectiveDate)).ToList();
        return list.GroupBy(b => b.EffectiveMonth)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BillMonthView(g.Key, Arrange(g.OrderBy(b => b.CreatedAt).Select(FromBill).ToList(), templates, list, groupFilter)))
            .Where(v => v.Groups.Count > 0)
            .ToList();
    }

    private static List<BillGroupView> Arrange(List<BillRow> rows, IReadOnlyList<BillTemplate> templates, IEnumerable<Bill> bills, string? groupFilter) =>
        Groups(templates, bills)
            .Where(g => groupFilter is null || string.Equals(g, groupFilter, StringComparison.OrdinalIgnoreCase))
            .Select(g => new BillGroupView(g, rows.Where(r => string.Equals(r.Group, g, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(g => g.Rows.Count > 0)
            .ToList();
}
