using System.Globalization;

namespace ZsebApp.Services;

/// <summary>Magyar formázás kultúra-adatok betöltése nélkül (kisebb letöltési méret).</summary>
public static class Fmt
{
    private static readonly NumberFormatInfo Numbers = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NegativeSign = "−",
    };

    private static readonly string[] Months =
    [
        "január", "február", "március", "április", "május", "június",
        "július", "augusztus", "szeptember", "október", "november", "december",
    ];

    private static readonly string[] Days =
    [
        "vasárnap", "hétfő", "kedd", "szerda", "csütörtök", "péntek", "szombat",
    ];

    public static string Ft(long amount) => amount.ToString("N0", Numbers) + " Ft";

    /// <summary>Mérőállás / fogyasztás: ezres tagolás, legfeljebb 3 tizedes, pl. "12 345,678".</summary>
    public static string Number(decimal value, int maxDecimals = 3) =>
        value.ToString("#,0." + new string('#', maxDecimals), Numbers);

    public static string MonthTitle(int year, int month) => $"{year}. {Months[month - 1]}";

    public static string ShortDate(DateOnly d) => $"{Months[d.Month - 1]} {d.Day}.";

    private static readonly string[] MonthAbbr =
        ["jan.", "febr.", "márc.", "ápr.", "máj.", "jún.", "júl.", "aug.", "szept.", "okt.", "nov.", "dec."];

    /// <summary>Rövidített dátum szűk helyre: "aug. 22."</summary>
    public static string TinyDate(DateOnly d) => $"{MonthAbbr[d.Month - 1]} {d.Day}.";

    public static string MonthName(int month) => Months[month - 1];
    public static string MonthShort(int month) => MonthAbbr[month - 1];

    private static readonly string[] DayAbbr = ["V", "H", "K", "Sze", "Cs", "P", "Szo"];

    /// <summary>A hét napjának rövid neve: H, K, Sze, Cs, P, Szo, V.</summary>
    public static string DayShort(DateOnly d) => DayAbbr[(int)d.DayOfWeek];

    /// <summary>Előjeles százalékos változás, pl. "+12%" / "−5%".</summary>
    public static string Change(long current, long previous)
    {
        if (previous == 0) return current == 0 ? "0%" : "új";
        var pct = (int)Math.Round((current - previous) * 100.0 / previous);
        return pct > 0 ? $"+{pct}%" : pct < 0 ? $"−{-pct}%" : "0%";
    }

    /// <summary>Az idei dátumnál év nélkül, egyébként évvel: "október 21." / "2025. december 31."</summary>
    public static string SmartDate(DateOnly d) =>
        d.Year == DateTime.Now.Year ? ShortDate(d) : $"{d.Year}. {ShortDate(d)}";

    public static string DayHeader(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return "Ma";
        if (d == today.AddDays(-1)) return "Tegnap";
        return $"{ShortDate(d)}, {Days[(int)d.DayOfWeek]}";
    }

    public static string Updated(DateTime t)
    {
        var d = DateOnly.FromDateTime(t);
        var time = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d == today) return $"ma {time}";
        if (d == today.AddDays(-1)) return $"tegnap {time}";
        return d.Year == today.Year ? ShortDate(d) : $"{d.Year}. {ShortDate(d)}";
    }

    public static string FileStamp(DateTime t) => t.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
}
