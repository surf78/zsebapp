using System.Globalization;
using System.Text;
using System.Text.Json;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>
/// Az Enable Banking (K&amp;H bank API) tételei. A Google Apps Script (<c>enablebanking/Code.gs</c>) a banki SMS-ekkel
/// azonos bemenetre (<c>households/{hid}/smsinbox</c>) küldi őket, a szövegük: <c>EB1 {json}</c>.
/// </summary>
public static class BankEb
{
    public const string Prefix = "EB1 ";

    /// <summary>Egy feldolgozott tétel. <see cref="Amount"/> forintban, pozitív; <see cref="Kind"/> a <see cref="BankKinds"/> szerint.</summary>
    public sealed record Tx(string Key, string Kind, long Amount, string Currency, DateTime? Date, string Name, string Remittance, long? Balance);

    /// <summary>Egyenleg-üzenet (minden lekérésnél): <c>EB1B {"b":"17361.00","c":"HUF","t":"ITAV","at":"…"}</c>.</summary>
    public const string BalancePrefix = "EB1B ";

    public static bool IsBalance(string? text) => text is not null && text.StartsWith(BalancePrefix, StringComparison.Ordinal);

    /// <summary>Az egyenleg (forintra kerekítve) és az időpontja; hibás üzenetnél null.</summary>
    public static (long Amount, DateTime At)? ParseBalance(string text)
    {
        if (!IsBalance(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[BalancePrefix.Length..]);
            var r = doc.RootElement;
            if (Money(r, "b") is not { } b) return null;
            var at = DateTimeOffset.TryParse(Str(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime : DateTime.Now;
            return (b, new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsEb(string? text) => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Az <c>EB1 {json}</c> szöveg értelmezése; hibás vagy kulcs/összeg nélküli üzenetnél null.</summary>
    public static Tx? Parse(string text)
    {
        if (!IsEb(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[Prefix.Length..]);
            var r = doc.RootElement;
            var key = Str(r, "k");
            if (key.Length == 0) return null;
            var amount = Money(r, "a");
            if (amount is null || amount == 0) return null;
            var kind = Str(r, "t") switch
            {
                "D" => BankKinds.Debit,
                "C" => BankKinds.Credit,
                _ => amount < 0 ? BankKinds.Debit : BankKinds.Unknown,
            };
            DateTime? date = DateTime.TryParseExact(Str(r, "d"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
            var currency = Str(r, "c");
            return new Tx(key, kind, Math.Abs(amount.Value), currency.Length == 0 ? "HUF" : currency, date,
                Tidy(Str(r, "n"), 60), Tidy(Str(r, "r"), 300), Money(r, "b"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A naplóban „nyers üzenetként” látható, olvasható szöveg.</summary>
    public static string Describe(Tx t)
    {
        var sb = new StringBuilder();
        sb.Append("K&H (Enable Banking)\n");
        sb.Append(t.Kind).Append('\n');
        sb.Append(t.Kind == BankKinds.Debit ? "-" : t.Kind == BankKinds.Credit ? "+" : "")
          .Append(Money(t.Amount, t.Currency)).Append('\n');
        if (t.Name.Length > 0) sb.Append(t.Name).Append('\n');
        if (t.Remittance.Length > 0 && t.Remittance != t.Name) sb.Append(t.Remittance).Append('\n');
        if (t.Date is { } d) sb.Append("Könyvelve: ").Append(Fmt.FullDate(d)).Append('\n');
        if (t.Balance is { } b) sb.Append("Egyenleg: ").Append(Money(b, t.Currency));
        return sb.ToString().TrimEnd();
    }

    private static string Money(long amount, string currency) =>
        currency == "HUF" ? Fmt.Ft(amount) : Fmt.Ft(amount)[..^3] + " " + currency;

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    /// <summary>"2000.00" / "-2000" / 2000.5 → forintra kerekítve.</summary>
    private static long? Money(JsonElement r, string name)
    {
        var s = Str(r, name).Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)
            ? (long)Math.Round(m, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string Tidy(string s, int max)
    {
        s = string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > max ? s[..max].TrimEnd() : s;
    }
}
