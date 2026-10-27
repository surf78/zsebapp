namespace ZsebApp.Models;

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    /// <summary>Ki vette fel (közös használatnál a bejelentkezett felhasználó neve).</summary>
    public string? CreatedBy { get; set; }
    /// <summary>Teendő-csoport (pl. „Bevásárlás”); null = csoport nélkül.</summary>
    public string? Group { get; set; }
}

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body);
}

public sealed class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Összeg forintban.</summary>
    public int Amount { get; set; }
    public string Category { get; set; } = Categories.Default;
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public string Description { get; set; } = "";
}

/// <summary>Egy banki SMS (K&amp;H) feldolgozva: terhelés vagy jóváírás, összeg, egyenleg. A Bank naplóban jelenik meg.</summary>
public sealed class BankEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Mikor érkezett az SMS (a Parancsok küldi; ha nincs, a feldolgozás ideje).</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    /// <summary>A tranzakció időpontja az SMS szövegéből (ha benne van).</summary>
    public DateTime? TransactionAt { get; set; }
    /// <summary>"Terhelés", "Jóváírás" vagy "Ismeretlen" (lásd <see cref="BankKinds"/>).</summary>
    public string Kind { get; set; } = BankKinds.Unknown;
    /// <summary>Az összeg forintban, pozitív szám (az irányt a <see cref="Kind"/> adja).</summary>
    public long Amount { get; set; }
    /// <summary>Az SMS-ben szereplő felhasználható összeg / egyenleg.</summary>
    public long? Balance { get; set; }
    /// <summary>Kereskedő / partner / közlemény, ha kiolvasható.</summary>
    public string Merchant { get; set; } = "";
    /// <summary>Az SMS teljes szövege.</summary>
    public string Text { get; set; } = "";
    /// <summary>Terhelésnél az ebből létrehozott kiadás azonosítója.</summary>
    public Guid? ExpenseId { get; set; }
    /// <summary>Csak ennél a tételnél megadott név (felülírja a párosítást).</summary>
    public string? Label { get; set; }

    public DateTime When => TransactionAt ?? ReceivedAt;
}

/// <summary>Párosítás: ha a banki SMS partnere <see cref="Match"/>, a naplóban és a kiadásoknál <see cref="Name"/> látszik.</summary>
public sealed class BankAlias
{
    /// <summary>A partner neve, ahogy az SMS-ben szerepel.</summary>
    public string Match { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>A kiadás kategóriája ennél a partnernél (null = a tipp marad).</summary>
    public string? Category { get; set; }
}

public static class BankKinds
{
    public const string Debit = "Terhelés";
    public const string Credit = "Jóváírás";
    public const string Unknown = "Ismeretlen";
}

/// <summary>Egy hónap számlája / utalása, pl. Ház (Petrőczy) – Gázszámla – 2026-10 – Rendezetlen – 12 340 Ft.</summary>
public sealed class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Csoport, pl. "Ház (Petrőczy)", "Lakás (Székfű)", "Rendszeres utalás". Üres = Egyéb.</summary>
    public string Group { get; set; } = "";
    /// <summary>Összeg forintban.</summary>
    public int Amount { get; set; }
    /// <summary>false = Rendezetlen (alapértelmezett), true = Fizetve.</summary>
    public bool Paid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? PaidAt { get; set; }

    /// <summary>Túlfizetés: fizetve, de a kelleténél többet (zöld, mint a Fizetve). Csak Paid = true mellett értelmes.</summary>
    public bool Overpaid { get; set; }

    /// <summary>Fizetési határidő (nem kötelező). JSON-ban "date" néven – a korábbi verzió ezt használta.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public DateOnly? DueDate { get; set; }

    /// <summary>Melyik hónap számlája, "yyyy-MM". Régi adatoknál üres – ott a határidő / rögzítés hónapja számít.</summary>
    public string? Month { get; set; }

    /// <summary>Ha fix tételből (sablonból) jött létre, annak azonosítója.</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>A számla dátuma szűréshez: határidő, ha nincs, a hónap 1-je, ha az sincs, a rögzítés napja.</summary>
    public DateOnly EffectiveDate =>
        DueDate ?? (Month is { } m && BillMonth.TryParse(m, out var d) ? d : DateOnly.FromDateTime(CreatedAt));

    public string EffectiveMonth => Month ?? BillMonth.Key(EffectiveDate);
}

/// <summary>Fix (minden hónapban esedékes) tétel, pl. Lakás (Székfű) – Közös költség.</summary>
public sealed class BillTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Alapértelmezett összeg (pl. rendszeres utalásnál); null, ha havonta változik.</summary>
    public int? DefaultAmount { get; set; }
    /// <summary>A határidő napja a hónapban (1–31); null, ha nincs megadva.</summary>
    public int? DueDay { get; set; }
}

public static class BillDefaults
{
    public const string OtherGroup = "Egyéb";

    /// <summary>Az első indításkor létrehozott fix tételek. Később szabadon törölhetők, bővíthetők.</summary>
    public static List<BillTemplate> Create() =>
    [
        new() { Group = "Ház (Petrőczy)", Name = "Gázszámla" },
        new() { Group = "Ház (Petrőczy)", Name = "Víz" },
        new() { Group = "Ház (Petrőczy)", Name = "Áram" },
        new() { Group = "Ház (Petrőczy)", Name = "Hulladékszállítás" },
        new() { Group = "Lakás (Székfű)", Name = "Gázszámla" },
        new() { Group = "Lakás (Székfű)", Name = "Víz" },
        new() { Group = "Lakás (Székfű)", Name = "Áram" },
        new() { Group = "Lakás (Székfű)", Name = "Közös költség" },
        new() { Group = "Rendszeres utalás", Name = "Babaváró", DefaultAmount = 45834 },
        new() { Group = "Rendszeres utalás", Name = "Nyugdíj-megtakarítás", DefaultAmount = 39848 },
        new() { Group = "Rendszeres utalás", Name = "YouTube", DefaultAmount = 6990 },
        new() { Group = "Rendszeres utalás", Name = "iCloud", DefaultAmount = 1290 },
    ];
}

/// <summary>Hónap-kulcs ("2026-10") kezelése.</summary>
public static class BillMonth
{
    public static string Key(DateOnly d) => $"{d.Year:D4}-{d.Month:D2}";

    public static bool TryParse(string key, out DateOnly firstDay)
    {
        firstDay = default;
        if (key.Length != 7 || key[4] != '-') return false;
        if (!int.TryParse(key[..4], out var y) || !int.TryParse(key[5..], out var m) || m is < 1 or > 12) return false;
        firstDay = new DateOnly(y, m, 1);
        return true;
    }
}

/// <summary>Jelszó és egyéb beállítások.</summary>
public sealed class AppSettings
{
    /// <summary>A „Minden adat törlése” jelszavának SHA-256 kivonata (nem a jelszó maga).</summary>
    public string? DeletePasswordHash { get; set; }

    /// <summary>A teendők csoportjai (a sorrend a létrehozásé). Régi beállításokban nincs – üres lista.</summary>
    public List<string> TodoGroups { get; set; } = [];

    /// <summary>Bank napló: partner → saját név párosítások (pl. „Unicredit Bank Hungary Zrt., Budapest” → „PayPal számla”).</summary>
    public List<BankAlias> BankAliases { get; set; } = [];
}

/// <summary>Mérőóra-leolvasás, pl. Gáz – 12 345,678 m³ – 2026.10.21.</summary>
public sealed class MeterReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>A mérő neve: "Gáz", "Áram – Ház", "Áram – Lakás", "Víz – Locsoló (586)" vagy "Víz – Főóra (405)" (lásd <see cref="MeterTypes"/>).</summary>
    public string Meter { get; set; } = MeterTypes.All[0].Name;
    public decimal Value { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public string Note { get; set; } = "";
}

public sealed record MeterType(string Name, string Unit, string Color, string Icon);

public static class MeterTypes
{
    public static readonly IReadOnlyList<MeterType> All =
    [
        new("Gáz", "m³", "#ff7a00", "flame"),
        new("Áram – Ház", "kWh", "#e0a800", "bolt"),
        new("Áram – Lakás", "kWh", "#8a6df0", "bolt"),
        new("Víz – Locsoló (586)", "m³", "#12a37a", "drop"),
        new("Víz – Főóra (405)", "m³", "#0a8fc4", "drop"),
    ];

    public static MeterType Get(string name) => All.FirstOrDefault(m => m.Name == name) ?? All[0];
}

/// <summary>Teljes adatmentés (export / import).</summary>
/// <remarks>
/// Kétféle mentés van: <see cref="BackupKinds.Data"/> (teendők, jegyzetek, kiadások, számlák, mérőórák) és
/// <see cref="BackupKinds.Settings"/> (fix számlatételek + törlési jelszó). A régi, típus nélküli mentés mindent tartalmaz.
/// </remarks>
public sealed class Backup
{
    public int Version { get; set; } = 1;
    /// <summary>"data" | "settings" | null (régi, teljes mentés).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TodoItem>? Todos { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Note>? Notes { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Expense>? Expenses { get; set; } = [];
    /// <summary>Régebbi mentésekben nincs benne – ilyenkor üres lista.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<Bill>? Bills { get; set; } = [];
    /// <summary>Régebbi mentésekben nincs benne – ilyenkor üres lista.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<MeterReading>? Meters { get; set; } = [];
    /// <summary>Fix számlatételek. Régebbi mentésekben nincs benne (null) – ilyenkor a jelenlegiek maradnak.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<BillTemplate>? BillTemplates { get; set; }
    /// <summary>Bank napló (K&amp;H SMS-ek). Régebbi mentésekben nincs benne (null) – ilyenkor a jelenlegi marad.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<BankEntry>? Bank { get; set; }
    /// <summary>Program-beállítások (törlési jelszó). Csak a beállítás-mentésben van.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AppSettings? Settings { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSettings => Kind == BackupKinds.Settings;
}

public static class BackupKinds
{
    public const string Data = "data";
    public const string Settings = "settings";
}

public sealed record Category(string Name, string Color);

public static class Categories
{
    public const string Default = "Egyéb";

    public static readonly IReadOnlyList<Category> All =
    [
        new("Élelmiszer", "#34c759"),
        new("Közlekedés", "#007aff"),
        new("Rezsi", "#ff9500"),
        new("Szórakozás", "#af52de"),
        new("Egészség", "#ff2d55"),
        new("Ruházat", "#5ac8fa"),
        new("Egyéb", "#8e8e93"),
    ];

    public static string ColorOf(string name) =>
        All.FirstOrDefault(c => c.Name == name)?.Color ?? "#8e8e93";
}
