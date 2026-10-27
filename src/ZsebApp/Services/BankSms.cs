using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>A banki (K&amp;H) SMS-ek szövegének feldolgozása: típus, összeg, egyenleg, dátum, kereskedő.</summary>
public static partial class BankSms
{
    public sealed record Parsed(string Kind, long Amount, long? Balance, DateTime? TransactionAt, string Merchant);

    // Összeg: "-2.000 HUF", "+50 000 Ft", "12.345,50 HUF", "2000 HUF"
    private const string NumberPattern = @"(?<![\d:/.,])(?<sign>[+\-−])?\s?(?<num>\d{1,3}(?:[.\u00A0 ]\d{3})+|\d+)(?:,(?<frac>\d{1,2}))?";
    private const string AmountPattern = NumberPattern + @"\s*(?:HUF|Ft)\b";

    [GeneratedRegex(AmountPattern, RegexOptions.IgnoreCase)]
    private static partial Regex AmountRegex();

    // Egyenleg: "Felhasználható összeg: 84.898 HUF", "Egyenleg: 84 898 Ft". Az ékezet nélküli, kisbetűs szövegen
    // keressük (a K&H „Felhasznàlhatò” alakot is küld), a pozíciók megegyeznek az eredetiével.
    [GeneratedRegex(@"(?:felhasznalhato\s+(?:osszeg|egyenleg)|egyenleg|elerheto\s+osszeg)\s*:?\s*" + NumberPattern + @"(?:\s*(?:HUF|Ft)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex BalanceRegex();

    // Dátum: "2026.10.25 12:34", "2026.10.25. 12:34:56", "2026-10-25 12:34", "26.10.25 12:34"
    [GeneratedRegex(@"(?<y>20\d{2}|\d{2})[.\-/](?<m>\d{1,2})[.\-/](?<d>\d{1,2})\.?(?:\s*(?<h>\d{1,2}):(?<mi>\d{2})(?::\d{2})?)?")]
    private static partial Regex DateRegex();

    // Kereskedő / partner a szokásos címkék után
    [GeneratedRegex(@"(?:hely|elfogad[oó]|kereskedő|kereskedo|partner|kedvezm[eé]nyezett|bek[uü]ld[oő]|utal[oó]|k[oö]zlem[eé]ny)\s*:\s*(?<v>[^;\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MerchantLabelRegex();

    /// <summary>Ékezetek nélküli, kisbetűs változat a kulcsszavak kereséséhez.</summary>
    private const string Accented = "áàâäãåéèêëíìîïóòôöõőúùûüűýÿçñ";
    private const string Ascii    = "aaaaaaeeeeiiiiooooooouuuuuyycn";

    /// <summary>Ékezetek nélküli, kisbetűs változat a kulcsszavak kereséséhez (a hossz nem változik).</summary>
    public static string Plain(string text)
    {
        var chars = text.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var k = Accented.IndexOf(chars[i]);
            if (k >= 0) chars[i] = Ascii[k];
        }
        return new string(chars);
    }

    public static Parsed Parse(string text)
    {
        text = (text ?? "").Trim();
        var plain = Plain(text);

        // Egyenleg (a többi összegtől elkülönítve) – az ékezet nélküli szövegen, ha a hossz azonos (pozíciók egyeznek).
        long? balance = null;
        var balanceMatch = BalanceRegex().Match(plain.Length == text.Length ? plain : text);
        if (balanceMatch.Success) balance = ToLong(balanceMatch);

        // A tranzakció összege: az egyenlegen kívüli összegek közül az első előjeles (a K&H így küldi: „-2.000 HUF”),
        // ha nincs ilyen, az első (a kártyaszám „...1234” + „HUF” fejléc-sort így nem nézzük összegnek).
        var candidates = AmountRegex().Matches(text).Cast<Match>()
            .Where(m => !(balanceMatch.Success && m.Index >= balanceMatch.Index && m.Index < balanceMatch.Index + balanceMatch.Length))
            .ToList();
        var amountMatch = candidates.FirstOrDefault(m => m.Groups["sign"].Success) ?? candidates.FirstOrDefault();
        var amount = amountMatch is null ? 0 : Math.Abs(ToLong(amountMatch));
        var sign = amountMatch is not null && amountMatch.Groups["sign"].Success ? amountMatch.Groups["sign"].Value : null;

        // Típus: kulcsszavak, ennek híján az előjel
        // Sorrend: kifejezett „terhelés” / „jóváírás” szó, majd az összeg előjele, végül egyéb kulcsszavak.
        string kind;
        if (plain.Contains("terhel"))
            kind = BankKinds.Debit;
        else if (plain.Contains("jovair"))
            kind = BankKinds.Credit;
        else if (sign is "-" or "−")
            kind = BankKinds.Debit;
        else if (sign == "+")
            kind = BankKinds.Credit;
        else if (Regex.IsMatch(plain, @"vasarlas|kifizetes|keszpenzfelvetel|levonas|kartyas"))
            kind = BankKinds.Debit;
        else if (Regex.IsMatch(plain, @"beerkezett|bejovo|erkezett"))
            kind = BankKinds.Credit;
        else
            kind = BankKinds.Unknown;

        return new Parsed(kind, amount, balance, ParseDate(text), ParseMerchant(text, amountMatch, balanceMatch));
    }

    private static long ToLong(Match m)
    {
        var digits = new string(m.Groups["num"].Value.Where(char.IsDigit).ToArray());
        var value = long.TryParse(digits, out var v) ? v : 0;
        // A fillér (",50") elhagyható – forintra kerekítünk.
        if (m.Groups["frac"].Success && m.Groups["frac"].Value.Length > 0 && m.Groups["frac"].Value[0] >= '5') value++;
        var sign = m.Groups["sign"].Success ? m.Groups["sign"].Value : "";
        return sign is "-" or "−" ? -value : value;
    }

    private static DateTime? ParseDate(string text)
    {
        foreach (Match m in DateRegex().Matches(text))
        {
            var y = int.Parse(m.Groups["y"].Value);
            if (y < 100) y += 2000;
            var mo = int.Parse(m.Groups["m"].Value);
            var d = int.Parse(m.Groups["d"].Value);
            if (mo is < 1 or > 12 || d is < 1 or > 31 || d > DateTime.DaysInMonth(y, mo)) continue;
            var h = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value) : 0;
            var mi = m.Groups["mi"].Success ? int.Parse(m.Groups["mi"].Value) : 0;
            if (h > 23 || mi > 59) continue;
            return new DateTime(y, mo, d, h, mi, 0);
        }
        return null;
    }

    private static string ParseMerchant(string text, Match? amount, Match balance)
    {
        var label = MerchantLabelRegex().Match(text);
        if (label.Success) return Tidy(label.Groups["v"].Value);
        if (amount is null) return "";

        // K&H mobilinfo: „… Terhelés / Azonnali Forint átutalás BK / -2.000 HUF / Unicredit Bank Hungary Zrt., Budapest /
        // Felhasználható összeg: 84.898 HUF” – a partner az összeg és az egyenleg között áll (akár több sorban).
        var end = balance.Success && balance.Index > amount.Index ? balance.Index : text.Length;
        // Ha soronként jön (K&H kártyás vásárlás: „313 HUF / RONI ABC BUDAPEST HU / VARGA ISTVÁN / Egyenleg: …”),
        // az összeg utáni első nem üres sor a kereskedő (utána a kártyabirtokos neve jönne).
        var betweenRaw = DateRegex().Replace(text[(amount.Index + amount.Length)..end], " ");
        var firstLine = betweenRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length >= 3);
        if (firstLine is not null) return Tidy(firstLine);

        // Ha ott nincs semmi: az összeg előtti sor (pl. „Azonnali Forint átutalás BK”), ha nem fejléc.
        var before = text[..amount.Index].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (before.Length > 0)
        {
            var last = before[^1];
            var lp = Plain(last);
            if (last.Length >= 3 && !lp.StartsWith("terhel") && !lp.StartsWith("jovair") && lp != "huf" && !lp.Contains("mobilinfo"))
                return Tidy(last);
        }

        // Egyéb formátum: az összeg utáni első értelmes szakasz.
        var rest = text[(amount.Index + amount.Length)..];
        foreach (var part in rest.Split([';', '\n', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim().Trim(',', '.', ':').Trim();
            if (p.Length < 3) continue;
            var pp = Plain(p);
            if (balance.Success && part.Contains(balance.Value)) continue;
            if (DateRegex().IsMatch(p) && p.Length < 22) continue;
            if (Regex.IsMatch(pp, @"egyenleg|felhasznalhato|kartya|szamla|^k&h|^kh\b|^\*|\d{4}\*|\*{2,}")) continue;
            return Tidy(p);
        }
        return "";
    }

    private static string Tidy(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ").Trim(' ', ',', '.', ';', ':', '*');
        return s.Length > 60 ? s[..60].TrimEnd() : s;
    }

    /// <summary>Kategória tipp a kereskedő neve alapján.</summary>
    public static string GuessCategory(string merchant, string text)
    {
        var p = Plain(merchant + " " + text);
        if (Regex.IsMatch(p, @"lidl|aldi|tesco|spar|penny|auchan|\bcba\b|coop|real\b|pekseg|hentes|abc\b|dm\b|rossmann|interspar|elelmiszer|kisbolt")) return "Élelmiszer";
        if (Regex.IsMatch(p, @"\bmol\b|omv|shell|lukoil|\bmav\b|volan|\bbkk\b|bkv|uber|bolt\.eu|parkol|autopalya|e-matrica|benzin|ttomas")) return "Közlekedés";
        if (Regex.IsMatch(p, @"gyogyszertar|patika|pharma|orvos|fogorvos|klinika|optika")) return "Egészség";
        if (Regex.IsMatch(p, @"eon|e\.on|mvm|fogaz|vizmu|elmu|emasz|digi|telekom|vodafone|yettel|upc|one\.hu|kozos koltseg")) return "Rezsi";
        if (Regex.IsMatch(p, @"netflix|spotify|youtube|hbo|disney|mozi|cinema|steam|playstation|etterem|restaurant|pizza|kave|cafe|mcdonald|burger|kfc|wolt|foodpanda")) return "Szórakozás";
        if (Regex.IsMatch(p, @"h&m|zara|c&a|reserved|decathlon|deichmann|ccc\b|primark|pepco|ruha")) return "Ruházat";
        return Categories.Default;
    }

    /// <summary>Ugyanabból a bemenetből mindig ugyanaz az azonosító (így két telefon nem rögzíti kétszer).</summary>
    public static Guid StableGuid(string seed) =>
        // MD5 a böngészős (WebAssembly) .NET-ben nem elérhető – SHA-256 első 16 bájtja.
        new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));
}
