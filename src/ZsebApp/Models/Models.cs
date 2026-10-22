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

/// <summary>Számla / csekk, pl. Gázszámla – Rendezetlen – 12 340 Ft.</summary>
public sealed class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Összeg forintban.</summary>
    public int Amount { get; set; }
    /// <summary>false = Rendezetlen (alapértelmezett), true = Fizetve.</summary>
    public bool Paid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? PaidAt { get; set; }
    /// <summary>A számla dátuma (szűréshez). Régebben rögzített számláknál üres – ott a rögzítés napja számít.</summary>
    public DateOnly? Date { get; set; }

    public DateOnly EffectiveDate => Date ?? DateOnly.FromDateTime(CreatedAt);
}

public static class BillPresets
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Gázszámla", "Villanyszámla", "Vízdíj", "Közös költség", "Internet", "Telefon", "Biztosítás",
    ];
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
