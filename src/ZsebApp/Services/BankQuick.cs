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
        var amountLine = lines.FirstOrDefault(l => Regex.IsMatch(l, @"\d")) ?? "";
        var amount = LenientAmount(amountLine);
        if (amount is null or 0) return null;
        var rest = lines.Where(l => l != amountLine).ToList();
        var merchant = rest.FirstOrDefault() ?? "";
        if (merchant.Length == 0 && rest.Count > 1) merchant = rest[1];
        var negative = amount < 0;
        // Az Apple Pay fizetés terhelés; ha visszatérítésként pozitív előjellel jön („+”), jóváírás.
        var kind = amountLine.TrimStart().StartsWith('+') ? BankKinds.Credit : BankKinds.Debit;
        return new BankSms.Parsed(kind, Math.Abs(amount.Value), null, null, Tidy(merchant));
    }

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
