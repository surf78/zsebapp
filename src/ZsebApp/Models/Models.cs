namespace ZsebApp.Models;

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
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

/// <summary>Egy hónap számlája / utalása, pl. Ház (Petróczy) – Gázszámla – 2026-10 – Rendezetlen – 12 340 Ft.</summary>
public sealed class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Csoport, pl. "Ház (Petróczy)", "Lakás (Székfű)", "Rendszeres utalás". Üres = Egyéb.</summary>
    public string Group { get; set; } = "";
    /// <summary>Összeg forintban.</summary>
    public int Amount { get; set; }
    /// <summary>false = Rendezetlen (alapértelmezett), true = Fizetve.</summary>
    public bool Paid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? PaidAt { get; set; }

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
        new() { Group = "Ház (Petróczy)", Name = "Gázszámla" },
        new() { Group = "Ház (Petróczy)", Name = "Víz" },
        new() { Group = "Ház (Petróczy)", Name = "Áram" },
        new() { Group = "Ház (Petróczy)", Name = "Hulladékszállítás" },
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
}

/// <summary>Mérőóra-leolvasás, pl. Gáz – 12 345,678 m³ – 2026.10.21.</summary>
public sealed class MeterReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>A mérő neve: "Gáz", "Villany" vagy "Víz" (lásd <see cref="MeterTypes"/>).</summary>
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
        new("Villany", "kWh", "#e0a800", "bolt"),
        new("Víz", "m³", "#0a8fc4", "drop"),
    ];

    public static MeterType Get(string name) => All.FirstOrDefault(m => m.Name == name) ?? All[0];
}

/// <summary>Teljes adatmentés (export / import).</summary>
public sealed class Backup
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; } = DateTime.Now;
    public List<TodoItem> Todos { get; set; } = [];
    public List<Note> Notes { get; set; } = [];
    public List<Expense> Expenses { get; set; } = [];
    /// <summary>Régebbi mentésekben nincs benne – ilyenkor üres lista.</summary>
    public List<Bill> Bills { get; set; } = [];
    /// <summary>Régebbi mentésekben nincs benne – ilyenkor üres lista.</summary>
    public List<MeterReading> Meters { get; set; } = [];
    /// <summary>Fix számlatételek. Régebbi mentésekben nincs benne (null) – ilyenkor a jelenlegiek maradnak.</summary>
    public List<BillTemplate>? BillTemplates { get; set; }
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
