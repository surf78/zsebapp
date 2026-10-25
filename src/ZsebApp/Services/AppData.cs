using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>
/// Az app összes adata. Egyszer tölt be, minden módosítás után ment a telefonra (localStorage),
/// és ha be vagy jelentkezve a közös tárba, a felhőbe is (zseb.sync.push – a JS csak a változásokat írja ki).
/// </summary>
public sealed class AppData(LocalStore store, IJSRuntime js)
{
    private const string TodosKey = "zseb.todos";
    private const string NotesKey = "zseb.notes";
    private const string ExpensesKey = "zseb.expenses";
    private const string BillsKey = "zseb.bills";
    private const string BillTemplatesKey = "zseb.billtemplates";
    private const string MetersKey = "zseb.meters";
    private const string SettingsKey = "zseb.settings";

    private Task? _loading;

    public List<TodoItem> Todos { get; private set; } = [];
    public List<Note> Notes { get; private set; } = [];
    public List<Expense> Expenses { get; private set; } = [];
    public List<Bill> Bills { get; private set; } = [];
    public List<BillTemplate> BillTemplates { get; private set; } = [];
    public List<MeterReading> Meters { get; private set; } = [];
    public AppSettings Settings { get; private set; } = new();

    /// <summary>Jelez, ha a teendők máshonnan (pl. a lebegő mikrofon gombról) változtak.</summary>
    public event Action? TodosChanged;

    /// <summary>Jelez, ha az adatok a felhőből (a másik telefonról) frissültek – az oldalak ilyenkor újrarajzolnak.</summary>
    public event Action? Changed;

    /// <summary>A bejelentkezett felhasználó neve (a közös tárban); ő lesz az új teendők rögzítője.</summary>
    public string? UserName { get; set; }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        Todos = await store.GetAsync(TodosKey, AppJsonContext.Default.ListTodoItem) ?? [];
        Notes = await store.GetAsync(NotesKey, AppJsonContext.Default.ListNote) ?? [];
        Expenses = await store.GetAsync(ExpensesKey, AppJsonContext.Default.ListExpense) ?? [];
        Bills = await store.GetAsync(BillsKey, AppJsonContext.Default.ListBill) ?? [];
        Meters = await store.GetAsync(MetersKey, AppJsonContext.Default.ListMeterReading) ?? [];
        Settings = await store.GetAsync(SettingsKey, AppJsonContext.Default.AppSettings) ?? new();
        Settings.TodoGroups ??= [];
        CurrentTodoGroup = await store.GetRawAsync(CurrentTodoGroupKey);

        // Fix tételek: első indításkor (vagy teljes törlés után) az alapértelmezett lista.
        var templates = await store.GetAsync(BillTemplatesKey, AppJsonContext.Default.ListBillTemplate);
        if (templates is null)
        {
            BillTemplates = BillDefaults.Create();
            await SaveBillTemplatesAsync();
        }
        else
        {
            BillTemplates = templates;
        }

        if (MigrateGroupNames())
        {
            await SaveBillTemplatesAsync();
            await SaveBillsAsync();
        }
    }

    // ---------- Csoportnevek ----------

    /// <summary>Korábbi csoportnevek és az új nevük (pl. elírás javítása).</summary>
    private static readonly (string Old, string New)[] GroupRenames =
    [
        ("Ház (Petróczy)", "Ház (Petrőczy)"),
    ];

    private bool NeedsGroupMigration() =>
        GroupRenames.Any(r => BillTemplates.Any(t => t.Group == r.Old) || Bills.Any(b => b.Group == r.Old));

    /// <summary>A régi csoportneveket átírja az újra. Igaz, ha változott valami.</summary>
    private bool MigrateGroupNames()
    {
        var changed = false;
        foreach (var (oldName, newName) in GroupRenames)
        {
            foreach (var t in BillTemplates.Where(t => t.Group == oldName)) { t.Group = newName; changed = true; }
            foreach (var b in Bills.Where(b => b.Group == oldName)) { b.Group = newName; changed = true; }
        }
        return changed;
    }

    /// <summary>A közös tárból még a régi név jött: kis késleltetéssel átírjuk és visszamentjük (a felhőbe is).</summary>
    private async Task MigrateGroupNamesLaterAsync()
    {
        await Task.Delay(300);
        if (!MigrateGroupNames()) return;
        await SaveBillTemplatesAsync();
        await SaveBillsAsync();
        Changed?.Invoke();
    }

    /// <summary>Csoport átnevezése: a fix tételeknél és a már rögzített számláknál is.</summary>
    public async Task RenameBillGroupAsync(string oldName, string newName)
    {
        static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        foreach (var t in BillTemplates.Where(t => Same(t.Group, oldName))) t.Group = newName;
        // Az Egyéb csoportba a csoport nélküli ("") számlák is beletartoznak.
        var other = Same(oldName, BillDefaults.OtherGroup);
        foreach (var b in Bills.Where(b => b.Group.Trim().Length > 0 ? Same(b.Group, oldName) : other)) b.Group = newName;
        await SaveBillTemplatesAsync();
        await SaveBillsAsync();
    }

    public Task SaveTodosAsync() => SaveAsync(TodosKey, Todos, AppJsonContext.Default.ListTodoItem);
    public Task SaveNotesAsync() => SaveAsync(NotesKey, Notes, AppJsonContext.Default.ListNote);
    public Task SaveExpensesAsync() => SaveAsync(ExpensesKey, Expenses, AppJsonContext.Default.ListExpense);
    public Task SaveBillsAsync() => SaveAsync(BillsKey, Bills, AppJsonContext.Default.ListBill);
    public Task SaveBillTemplatesAsync() => SaveAsync(BillTemplatesKey, BillTemplates, AppJsonContext.Default.ListBillTemplate);
    public Task SaveMetersAsync() => SaveAsync(MetersKey, Meters, AppJsonContext.Default.ListMeterReading);
    public Task SaveSettingsAsync() => SaveAsync(SettingsKey, Settings, AppJsonContext.Default.AppSettings);

    /// <summary>Mentés a telefonra, és (ha van kapcsolat) a változások kiírása a közös tárba.</summary>
    private async Task SaveAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        await store.SetRawAsync(key, json);
        if (SyncActive)
            await js.InvokeVoidAsync("zseb.sync.push", SyncName(key), json);
    }

    // ---------- Közös tár (szinkron) ----------

    /// <summary>Igaz, ha be vagy jelentkezve és kapcsolódtál a közös tárhoz (a SyncService állítja).</summary>
    public bool SyncActive { get; set; }

    /// <summary>"zseb.todos" -> "todos" (a felhőben ezen a néven tároljuk).</summary>
    private static string SyncName(string key) => key["zseb.".Length..];

    /// <summary>A telefonon lévő összes adat (a közös tár létrehozásakor ezt töltjük fel).</summary>
    public string ExportAllJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["todos"] = JsonSerializer.Serialize(Todos, AppJsonContext.Default.ListTodoItem),
        ["notes"] = JsonSerializer.Serialize(Notes, AppJsonContext.Default.ListNote),
        ["expenses"] = JsonSerializer.Serialize(Expenses, AppJsonContext.Default.ListExpense),
        ["bills"] = JsonSerializer.Serialize(Bills, AppJsonContext.Default.ListBill),
        ["billtemplates"] = JsonSerializer.Serialize(BillTemplates, AppJsonContext.Default.ListBillTemplate),
        ["meters"] = JsonSerializer.Serialize(Meters, AppJsonContext.Default.ListMeterReading),
        ["settings"] = JsonSerializer.Serialize(Settings, AppJsonContext.Default.AppSettings),
    }, AppJsonContext.Default.DictionaryStringString);

    /// <summary>
    /// A felhőből érkezett lista átvétele. Az elemek sorrendje és a változatlan elemek (objektumai) megmaradnak,
    /// így egy épp szerkesztett, de a másik telefonon nem módosított elem szerkesztése sem vész el.
    /// Visszaadja az átvett lista JSON-ját (ehhez méri a JS a későbbi helyi változásokat).
    /// </summary>
    public string ApplyRemote(string syncName, string json)
    {
        string result = syncName switch
        {
            "todos" => Apply(TodosKey, json, Todos, x => Todos = x, t => t.Id, AppJsonContext.Default.ListTodoItem),
            "notes" => Apply(NotesKey, json, Notes, x => Notes = x, t => t.Id, AppJsonContext.Default.ListNote),
            "expenses" => Apply(ExpensesKey, json, Expenses, x => Expenses = x, t => t.Id, AppJsonContext.Default.ListExpense),
            "bills" => Apply(BillsKey, json, Bills, x => Bills = x, t => t.Id, AppJsonContext.Default.ListBill),
            "billtemplates" => Apply(BillTemplatesKey, json, BillTemplates, x => BillTemplates = x, t => t.Id, AppJsonContext.Default.ListBillTemplate),
            "meters" => Apply(MetersKey, json, Meters, x => Meters = x, t => t.Id, AppJsonContext.Default.ListMeterReading),
            "settings" => ApplySettings(json),
            _ => json,
        };
        if (syncName == "todos") TodosChanged?.Invoke();
        if (syncName is "bills" or "billtemplates" && NeedsGroupMigration()) _ = MigrateGroupNamesLaterAsync();
        Changed?.Invoke();
        return result;
    }

    private string Apply<T>(string key, string json, List<T> current, Action<List<T>> set, Func<T, Guid> id,
        JsonTypeInfo<List<T>> typeInfo)
    {
        var incoming = JsonSerializer.Deserialize(json, typeInfo) ?? [];
        var itemInfo = (JsonTypeInfo<T>)AppJsonContext.Default.GetTypeInfo(typeof(T))!;
        string Ser(T x) => JsonSerializer.Serialize(x, itemInfo);

        var old = new Dictionary<Guid, T>();
        foreach (var o in current) old[id(o)] = o;
        var byId = new Dictionary<Guid, T>();
        foreach (var item in incoming)
        {
            var key2 = id(item);
            // Változatlan elem: a régi objektum marad (a hivatkozások érvényesek maradnak).
            byId[key2] = old.TryGetValue(key2, out var o) && Ser(o) == Ser(item) ? o : item;
        }

        // Sorrend: a meglévők az eddigi sorrendben, utánuk az újak.
        var merged = new List<T>(byId.Count);
        foreach (var o in current)
            if (byId.Remove(id(o), out var keep)) merged.Add(keep);
        foreach (var item in incoming)
            if (byId.Remove(id(item), out var added)) merged.Add(added);

        set(merged);
        var result = JsonSerializer.Serialize(merged, typeInfo);
        _ = store.SetRawAsync(key, result);
        return result;
    }

    private string ApplySettings(string json)
    {
        var incoming = json == "null" ? null : JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings);
        if (incoming is not null)
        {
            incoming.TodoGroups ??= [];
            Settings = incoming;
        }
        var result = JsonSerializer.Serialize(Settings, AppJsonContext.Default.AppSettings);
        _ = store.SetRawAsync(SettingsKey, result);
        return result;
    }

    /// <summary>Új teendő felvétele bárhonnan (pl. hangról), értesíti a Teendők oldalt.</summary>
    public async Task<TodoItem?> AddTodoAsync(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return null;
        var item = new TodoItem { Title = title, CreatedBy = SyncActive ? UserName : null, Group = ValidCurrentTodoGroup };
        Todos.Add(item);
        await SaveTodosAsync();
        TodosChanged?.Invoke();
        return item;
    }

    // ---------- Teendő-csoportok ----------

    private const string CurrentTodoGroupKey = "zseb.todos.current";

    private static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Az összes teendő-csoport: a beállításokban tároltak (létrehozási sorrendben), majd a teendőkön szereplő továbbiak.</summary>
    public List<string> TodoGroups
    {
        get
        {
            var result = new List<string>();
            foreach (var g in Settings.TodoGroups.Concat(Todos.Select(t => t.Group ?? "")))
                if (!string.IsNullOrWhiteSpace(g) && !result.Any(r => SameName(r, g))) result.Add(g.Trim());
            return result;
        }
    }

    /// <summary>A Teendők oldalon kiválasztott csoport (null = Mind). Ebbe kerülnek az új (gépelt vagy hangos) teendők.</summary>
    public string? CurrentTodoGroup { get; private set; }

    /// <summary>A kiválasztott csoport, ha még létezik (a másik telefonon közben törölhették).</summary>
    public string? ValidCurrentTodoGroup => TodoGroups.FirstOrDefault(g => SameName(g, CurrentTodoGroup));

    public Task SetCurrentTodoGroupAsync(string? group)
    {
        CurrentTodoGroup = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        // Csak ezen a telefonon jegyezzük meg (nem közös).
        return CurrentTodoGroup is null ? store.RemoveAsync(CurrentTodoGroupKey).AsTask() : store.SetRawAsync(CurrentTodoGroupKey, CurrentTodoGroup);
    }

    /// <summary>Új csoport. Igaz, ha létrejött (ugyanilyen nevű még nem volt).</summary>
    public async Task<bool> AddTodoGroupAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || TodoGroups.Any(g => SameName(g, name))) return false;
        Settings.TodoGroups.Add(name);
        await SaveSettingsAsync();
        return true;
    }

    public async Task RenameTodoGroupAsync(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || SameName(oldName, newName) && oldName.Trim() == newName) return;
        var i = Settings.TodoGroups.FindIndex(g => SameName(g, oldName));
        if (i >= 0) Settings.TodoGroups[i] = newName; else Settings.TodoGroups.Add(newName);
        Settings.TodoGroups = Settings.TodoGroups.Where((g, k) => Settings.TodoGroups.FindIndex(x => SameName(x, g)) == k).ToList();
        foreach (var t in Todos.Where(t => SameName(t.Group, oldName))) t.Group = newName;
        if (SameName(CurrentTodoGroup, oldName)) await SetCurrentTodoGroupAsync(newName);
        await SaveSettingsAsync();
        await SaveTodosAsync();
        TodosChanged?.Invoke();
    }

    /// <summary>Csoport törlése. A teendői megmaradnak, csoport nélkül (a Mind nézetben).</summary>
    public async Task DeleteTodoGroupAsync(string name)
    {
        Settings.TodoGroups.RemoveAll(g => SameName(g, name));
        foreach (var t in Todos.Where(t => SameName(t.Group, name))) t.Group = null;
        if (SameName(CurrentTodoGroup, name)) await SetCurrentTodoGroupAsync(null);
        await SaveSettingsAsync();
        await SaveTodosAsync();
        TodosChanged?.Invoke();
    }

    // ---------- Törlési jelszó ----------

    public bool HasDeletePassword => !string.IsNullOrEmpty(Settings.DeletePasswordHash);

    public static string HashPassword(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zsebapp:" + password)));

    public bool CheckDeletePassword(string password) =>
        HasDeletePassword && string.Equals(Settings.DeletePasswordHash, HashPassword(password), StringComparison.Ordinal);

    public Task SetDeletePasswordAsync(string password)
    {
        Settings.DeletePasswordHash = HashPassword(password);
        return SaveSettingsAsync();
    }

    // ---------- Mentés / visszaállítás ----------

    /// <summary>Adatmentés: teendők, jegyzetek, kiadások, számlák, mérőórák.</summary>
    public Backup CreateDataBackup() => new()
    {
        Kind = BackupKinds.Data,
        ExportedAt = DateTime.Now,
        Todos = Todos,
        Notes = Notes,
        Expenses = Expenses,
        Bills = Bills,
        Meters = Meters,
    };

    /// <summary>Beállítás-mentés: fix számlatételek (csoportokkal) és a törlési jelszó.</summary>
    public Backup CreateSettingsBackup() => new()
    {
        Kind = BackupKinds.Settings,
        ExportedAt = DateTime.Now,
        Todos = null,
        Notes = null,
        Expenses = null,
        Bills = null,
        Meters = null,
        BillTemplates = BillTemplates,
        Settings = Settings,
    };

    /// <summary>
    /// Visszaállítás: a beállítás-mentés csak a fix tételeket és a jelszót írja felül, az adatmentés csak az adatokat;
    /// a régi (típus nélküli) mentés az adatokat és – ha benne vannak – a fix tételeket.
    /// </summary>
    public async Task RestoreAsync(Backup backup)
    {
        if (backup.IsSettings)
        {
            if (backup.BillTemplates is not null)
            {
                BillTemplates = backup.BillTemplates;
                MigrateGroupNames();
                await SaveBillTemplatesAsync();
            }
            if (backup.Settings is not null)
            {
                Settings = backup.Settings;
                Settings.TodoGroups ??= [];
                await SaveSettingsAsync();
            }
            Changed?.Invoke();
            return;
        }

        Todos = backup.Todos ?? [];
        Notes = backup.Notes ?? [];
        Expenses = backup.Expenses ?? [];
        Bills = backup.Bills ?? [];
        Meters = backup.Meters ?? [];
        if (backup.Kind is null && backup.BillTemplates is not null)
            BillTemplates = backup.BillTemplates;
        MigrateGroupNames();
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
        await SaveBillTemplatesAsync();
        TodosChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Minden adat törlése. A fix számlatételek visszaállnak az alapértelmezettre, a jelszó megmarad.</summary>
    public async Task ClearAllAsync()
    {
        Todos = [];
        Notes = [];
        Expenses = [];
        Bills = [];
        Meters = [];
        BillTemplates = BillDefaults.Create();
        // Mentéssel (nem kulcs-törléssel), hogy közös használatnál a felhőből is törlődjön.
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
        await SaveBillTemplatesAsync();
        TodosChanged?.Invoke();
    }
}
