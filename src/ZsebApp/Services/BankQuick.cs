using System.Globalization;
using System.Text.RegularExpressions;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>
/// Azonnali csatornák az iPhone Parancsokból (ugyanarra a beérkező címre, mint az SMS):
/// <list type="bullet">
/// <item><c>ZSEB-APPLEPAY</c> – a Tárca „Tranzakció” automatizálás (Apple Pay fizetés): 2. sor összeg, 3. sor kereskedő, 4. sor név.</item>
/// <item><c>ZSEB-PUSH</c> – iOS 27 „Értesítés” automatizálás a K&amp;H mobilbank push-üzenetére: utána a cím és a szöveg (mint egy SMS).</item>
/// </list>
/// </summary>
public static partial class BankQuick
{
    public const string ApplePayPrefix = "ZSEB-APPLEPAY";
    public const string PushPrefix = "ZSEB-PUSH";

    /// <summary>A szöveg csatornája: <see cref="BankSources"/> érték, SMS-nél null; <paramref name="body"/> az előtag nélküli rész.</summary>
    public static string? Channel(string text, out string body)
    {
        text = (text ?? "").Trim();
        foreach (var (prefix, source) in new[] { (ApplePayPrefix, BankSources.ApplePay), (PushPrefix, BankSources.Push) })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                body = text[prefix.Length..].Trim().TrimStart(':').Trim();
                return source;
            }
        }
        body = text;
        return null;
    }

    /// <summary>Apple Pay tétel: összeg (forintra kerekítve), kereskedő. Összeg nélkül null.</summary>
    public static BankSms.Parsed? ParseApplePay(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        // Az összeg sora: elsősorban a pénznemes („2519 Ft”, „HUF 2,000.00”, „12,99 €”), különben az első számos sor.
        // (A kereskedő nevében is lehet szám, pl. „LIDL HU 181”, ezért a pénznemes sor az elsődleges.)
        var amountLine = lines.FirstOrDefault(l => CurrencyRegex().IsMatch(l))
            ?? lines.FirstOrDefault(l => Regex.IsMatch(l, @"^[+\-−]?\s*\d[\d\s\u00A0\u202F.,']*$"))
            ?? lines.FirstOrDefault(l => Regex.IsMatch(l, @"\d")) ?? "";
        var amount = LenientAmount(amountLine);
        if (amount is null or 0) return null;
        // Kereskedő: az összeg előtti utolsó nem-összeg sor (a Tárca értesítésében: „K&H Bank” / „Roni ABC” / „2519 Ft”),
        // ha ilyen nincs, az utána következő első.
        var idx = Array.IndexOf(lines, amountLine);
        var merchant = lines.Take(idx).LastOrDefault(l => !IsCardName(l)) ?? lines.Take(idx).LastOrDefault()
            ?? lines.Skip(idx + 1).FirstOrDefault() ?? "";
        // Az Apple Pay fizetés terhelés; ha visszatérítésként pozitív előjellel jön („+”), jóváírás.
        var kind = amountLine.TrimStart().StartsWith('+') || Regex.IsMatch(BankSms.Plain(body), @"visszaterit|jovair")
            ? BankKinds.Credit : BankKinds.Debit;
        return new BankSms.Parsed(kind, Math.Abs(amount.Value), null, null, Tidy(merchant));
    }

    /// <summary>A Tárca értesítés címe a kártya neve (pl. „K&amp;H Bank”, „Mastercard betéti kártya”) – ez nem kereskedő.</summary>
    private static bool IsCardName(string line) =>
        Regex.IsMatch(BankSms.Plain(line), @"^(k\s*&\s*h|kh)\b|\bbank$|mastercard|\bvisa\b|kartya|apple pay|tarca");

    [GeneratedRegex(@"\d.*(Ft|HUF|EUR|€|\$|£)|(Ft|HUF|EUR|€|\$|£)\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();

    /// <summary>
    /// Összeg bármilyen formában: „2 000 Ft”, „2 000,50 Ft”, „HUF 2,000.00”, „Ft 2.000”, „-2000”.
    /// A tizedesjel az utolsó elválasztó, ha utána 1–2 számjegy áll (vagy ha . és , is szerepel).
    /// </summary>
    public static long? LenientAmount(string s)
    {
        var m = NumberRegex().Match(s ?? "");
        if (!m.Success) return null;
        var num = Regex.Replace(m.Groups["n"].Value, @"[\s  ']", "");
        var lastDot = num.LastIndexOf('.');
        var lastComma = num.LastIndexOf(',');
        var sepIdx = Math.Max(lastDot, lastComma);
        string intPart = num, frac = "";
        if (sepIdx >= 0)
        {
            var after = num.Length - sepIdx - 1;
            var both = lastDot >= 0 && lastComma >= 0;
            var single = num.Count(c => c == num[sepIdx]) == 1;
            if (both || (single && after is 1 or 2))
            {
                intPart = num[..sepIdx];
                frac = num[(sepIdx + 1)..];
            }
        }
        intPart = new string(intPart.Where(char.IsDigit).ToArray());
        if (!long.TryParse(intPart.Length == 0 ? "0" : intPart, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        if (frac.Length > 0 && frac[0] >= '5') v++;
        return m.Groups["sign"].Success && m.Groups["sign"].Value is "-" or "−" ? -v : v;
    }

    [GeneratedRegex(@"(?<sign>[+\-−])?\s*(?:[A-Za-z$€£Ft]{1,3}\s*)?(?<n>\d[\d\s  .,']*)")]
    private static partial Regex NumberRegex();

    private static string Tidy(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ").Trim(' ', ',', '.', ';', ':');
        return s.Length > 60 ? s[..60].TrimEnd() : s;
    }
}
