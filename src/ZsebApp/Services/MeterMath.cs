using System.Globalization;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>Egy leolvasás a fogyasztással együtt (az előző leolvasáshoz képest).</summary>
public sealed record MeterRow(MeterReading Reading, decimal? Usage, int? Days)
{
    public decimal? DailyUsage => Usage is { } u && Days is > 0 ? u / Days.Value : null;
}

/// <summary>Mérőóra-számítások: fogyasztás, napi átlag, beírt érték ellenőrzése.</summary>
public static class MeterMath
{
    /// <summary>Az adott mérő leolvasásai időrendben (legrégebbi elöl), fogyasztással.</summary>
    public static List<MeterRow> Rows(IEnumerable<MeterReading> all, string meter)
    {
        var list = all.Where(r => r.Meter == meter).OrderBy(r => r.Date).ThenBy(r => r.Value).ToList();
        var rows = new List<MeterRow>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            if (i == 0)
            {
                rows.Add(new MeterRow(list[i], null, null));
                continue;
            }
            var prev = list[i - 1];
            rows.Add(new MeterRow(list[i], list[i].Value - prev.Value, list[i].Date.DayNumber - prev.Date.DayNumber));
        }
        return rows;
    }

    /// <summary>
    /// "12 345,678" / "12345.678" / "12 345" → decimal. Szóközt és nem törhető szóközt figyelmen kívül hagy.
    /// </summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray()).Replace(',', '.');
        if (t.Count(c => c == '.') > 1) return false;
        return decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    /// <summary>
    /// Ellenőrzi, hogy az új állás illeszkedik-e az időrendbe: nem lehet kisebb egy korábbi,
    /// és nem lehet nagyobb egy későbbi leolvasásnál. Hibánál magyar üzenetet ad vissza.
    /// </summary>
    public static string? Validate(IEnumerable<MeterReading> all, string meter, decimal value, DateOnly date)
    {
        var unit = MeterTypes.Get(meter).Unit;
        var same = all.Where(r => r.Meter == meter).ToList();

        var before = same.Where(r => r.Date <= date).OrderByDescending(r => r.Date).ThenByDescending(r => r.Value).FirstOrDefault();
        if (before is not null && value < before.Value)
            return $"Az állás nem lehet kisebb, mint a {Fmt.ShortDate(before.Date)} leolvasás ({Fmt.Number(before.Value)} {unit}).";

        var after = same.Where(r => r.Date > date).OrderBy(r => r.Date).ThenBy(r => r.Value).FirstOrDefault();
        if (after is not null && value > after.Value)
            return $"Az állás nem lehet nagyobb, mint a későbbi, {Fmt.ShortDate(after.Date)} leolvasás ({Fmt.Number(after.Value)} {unit}).";

        return null;
    }
}
