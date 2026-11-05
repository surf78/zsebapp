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
    private const string BankKey = "zseb.bank";
    private const string SettingsKey = "zseb.settings";

    private Task? _loading;

    public List<TodoItem> Todos { get; private set; } = [];
    public List<Note> Notes { get; private set; } = [];
    public List<Expense> Expenses { get; private set; } = [];
    public List<Bill> Bills { get; private set; } = [];
    public List<BillTemplate> BillTemplates { get; private set; } = [];
    public List<MeterReading> Meters { get; private set; } = [];
    /// <summary>Bank napló: a K&amp;H SMS-ekből (lásd <see cref="AddBankSmsAsync"/>).</summary>
    public List<BankEntry> Bank { get; private set; } = [];
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
        Bank = await store.GetAsync(BankKey, AppJsonContext.Default.ListBankEntry) ?? [];
        Settings = await store.GetAsync(SettingsKey, AppJsonContext.Default.AppSettings) ?? new();
        Settings.TodoGroups ??= [];
        Settings.BankAliases ??= [];
        await store.RemoveAsync(CurrentTodoGroupKey);   // régi verzióból maradt

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
            await SaveMetersAsync();
        }
    }

    // ---------- Csoportnevek ----------

    /// <summary>Korábbi csoportnevek és az új nevük (pl. elírás javítása).</summary>
    private static readonly (string Old, string New)[] GroupRenames =
    [
        ("Ház (Petróczy)", "Ház (Petrőczy)"),
    ];

    /// <summary>Korábbi mérőnevek és az új nevük (a régi egyetlen villanyóra a házé, a régi vízóra a főóra lett).</summary>
    private static readonly (string Old, string New)[] MeterRenames =
    [
        ("Villany", "Áram – Ház"),
        ("Villany – Ház", "Áram – Ház"),
        ("Villany – Lakás", "Áram – Lakás"),
        ("Víz", "Víz – Főóra (405)"),
        ("Víz – Főóra", "Víz – Főóra (405)"),
        ("Víz – Locsoló", "Víz – Locsoló (586)"),
    ];

    private bool NeedsGroupMigration() =>
        GroupRenames.Any(r => BillTemplates.Any(t => t.Group == r.Old) || Bills.Any(b => b.Group == r.Old))
        || MeterRenames.Any(r => Meters.Any(m => m.Meter == r.Old));

    /// <summary>A régi csoportneveket átírja az újra. Igaz, ha változott valami.</summary>
    private bool MigrateGroupNames()
    {
        var changed = false;
        foreach (var (oldName, newName) in GroupRenames)
        {
            foreach (var t in BillTemplates.Where(t => t.Group == oldName)) { t.Group = newName; changed = true; }
            foreach (var b in Bills.Where(b => b.Group == oldName)) { b.Group = newName; changed = true; }
        }
        foreach (var (oldName, newName) in MeterRenames)
            foreach (var m in Meters.Where(m => m.Meter == oldName)) { m.Meter = newName; changed = true; }
        return changed;
    }

    /// <summary>A közös tárból még a régi név jött: kis késleltetéssel átírjuk és visszamentjük (a felhőbe is).</summary>
    private async Task MigrateGroupNamesLaterAsync()
    {
        await Task.Delay(300);
        if (!MigrateGroupNames()) return;
        await SaveBillTemplatesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
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
    public Task SaveBankAsync() => SaveAsync(BankKey, Bank, AppJsonContext.Default.ListBankEntry);
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
        ["bank"] = JsonSerializer.Serialize(Bank, AppJsonContext.Default.ListBankEntry),
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
            "bank" => Apply(BankKey, json, Bank, x => Bank = x, t => t.Id, AppJsonContext.Default.ListBankEntry),
            "settings" => ApplySettings(json),
            _ => json,
        };
        if (syncName == "todos") TodosChanged?.Invoke();
        if (syncName is "bills" or "billtemplates" or "meters" && NeedsGroupMigration()) _ = MigrateGroupNamesLaterAsync();
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
            incoming.BankAliases ??= [];
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

    /// <summary>Az alapértelmezett csoport: ide kerül minden teendő, ha nincs másik csoport megnyitva.</summary>
    public const string DefaultTodoGroup = "Általános";

    /// <summary>Korábbi verzió itt jegyezte meg a kiválasztott csoportot – már nem használjuk.</summary>
    private const string CurrentTodoGroupKey = "zseb.todos.current";

    /// <summary>Az alapértelmezett csoporthoz tartozik-e (csoport nélküli vagy „Általános”).</summary>
    public static bool IsDefaultTodoGroup(string? g) => string.IsNullOrWhiteSpace(g) || SameName(g, DefaultTodoGroup);

    private static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>A saját teendő-csoportok (az Általános nélkül): a beállításokban tároltak (létrehozási sorrendben), majd a teendőkön szereplő továbbiak.</summary>
    public List<string> TodoGroups
    {
        get
        {
            var result = new List<string>();
            foreach (var g in Settings.TodoGroups.Concat(Todos.Select(t => t.Group ?? "")))
                if (!IsDefaultTodoGroup(g) && !result.Any(r => SameName(r, g))) result.Add(g.Trim());
            return result;
        }
    }

    /// <summary>
    /// A Teendők oldalon megnyitott csoport (null = Általános). Ebbe kerülnek az új (gépelt vagy hangos) teendők.
    /// Nem tároljuk: az app indításakor mindig az Általános van megnyitva.
    /// </summary>
    public string? CurrentTodoGroup { get; private set; }

    /// <summary>A megnyitott csoport, ha még létezik (a másik telefonon közben törölhették); null = Általános.</summary>
    public string? ValidCurrentTodoGroup => TodoGroups.FirstOrDefault(g => SameName(g, CurrentTodoGroup));

    /// <summary>A megnyitott csoport neve (az Általános is).</summary>
    public string CurrentTodoGroupName => ValidCurrentTodoGroup ?? DefaultTodoGroup;

    public Task SetCurrentTodoGroupAsync(string? group)
    {
        CurrentTodoGroup = IsDefaultTodoGroup(group) ? null : group!.Trim();
        return Task.CompletedTask;
    }

    /// <summary>Új csoport. Igaz, ha létrejött (ugyanilyen nevű még nem volt).</summary>
    public async Task<bool> AddTodoGroupAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || IsDefaultTodoGroup(name) || TodoGroups.Any(g => SameName(g, name))) return false;
        Settings.TodoGroups.Add(name);
        await SaveSettingsAsync();
        return true;
    }

    public async Task RenameTodoGroupAsync(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || IsDefaultTodoGroup(oldName) || IsDefaultTodoGroup(newName)) return;
        if (SameName(oldName, newName) && oldName.Trim() == newName) return;
        var i = Settings.TodoGroups.FindIndex(g => SameName(g, oldName));
        if (i >= 0) Settings.TodoGroups[i] = newName; else Settings.TodoGroups.Add(newName);
        Settings.TodoGroups = Settings.TodoGroups.Where((g, k) => Settings.TodoGroups.FindIndex(x => SameName(x, g)) == k).ToList();
        foreach (var t in Todos.Where(t => SameName(t.Group, oldName))) t.Group = newName;
        if (SameName(CurrentTodoGroup, oldName)) await SetCurrentTodoGroupAsync(newName);
        await SaveSettingsAsync();
        await SaveTodosAsync();
        TodosChanged?.Invoke();
    }

    /// <summary>Csoport törlése. A teendői átkerülnek az Általános csoportba.</summary>
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
        Bank = Bank,
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
                Settings.BankAliases ??= [];
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
        if (backup.Bank is not null) Bank = backup.Bank;
        if (backup.Kind is null && backup.BillTemplates is not null)
            BillTemplates = backup.BillTemplates;
        MigrateGroupNames();
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
        await SaveBankAsync();
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
        Bank = [];
        BillTemplates = BillDefaults.Create();
        // Mentéssel (nem kulcs-törléssel), hogy közös használatnál a felhőből is törlődjön.
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
        await SaveBankAsync();
        await SaveBillTemplatesAsync();
        TodosChanged?.Invoke();
    }

    // ---------- Bank napló (K&H SMS) ----------

    /// <summary>
    /// Egy banki SMS (vagy Apple Pay / push üzenet, lásd <see cref="BankQuick"/>) feldolgozása: bekerül a Bank naplóba, terhelésnél a Kiadások közé is.
    /// <paramref name="sourceId"/> = a közös tárba érkezett SMS azonosítója: ebből képzett azonosítókkal
    /// két telefon sem rögzíti kétszer. Visszaadja a (meglévő vagy új) bejegyzést; összeg nélküli üzenetnél null.
    /// </summary>
    public async Task<BankEntry?> AddBankSmsAsync(string text, string? sourceId = null, DateTime? receivedAt = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;

        var id = sourceId is null ? Guid.NewGuid() : BankSms.StableGuid("sms:" + sourceId);
        if (Bank.FirstOrDefault(b => b.Id == id) is { } existing) return existing;

        // Csatorna: SMS (null), Apple Pay vagy a bank push-üzenete (Parancsok, előtaggal).
        var source = BankQuick.Channel(text, out var body);
        var p = source == BankSources.ApplePay ? BankQuick.ParseApplePay(body) : BankSms.Parse(body);
        // Összeg nélküli üzenet (pl. üdvözlés, egyszeri jelszó) nem kerül a naplóba.
        if (p is null || p.Amount <= 0) return null;
        var at = receivedAt ?? DateTime.Now;
        var when = p.TransactionAt ?? at;
        var shown = source == BankSources.ApplePay ? BankSources.Label(source) + "\n" + body : body;

        // Ha ugyanez a tétel már megjött az Enable Bankingtől, ez csak kiegészíti (pontos idő, friss egyenleg).
        var twin = Bank
            .Where(b => b.Source == BankSources.EnableBanking && !b.Text.Contains(MarkStart)
                        && !Bank.Any(o => o.Source != BankSources.EnableBanking && o.Ref == b.Ref) && IsNear(b, p.Kind, p.Amount, when))
            .OrderBy(b => Math.Abs((b.When.Date - when.Date).TotalDays))
            .FirstOrDefault()
            // Vagy egy másik azonnali csatornán (SMS / Apple Pay / push) jött már meg, fél órán belül.
            ?? Bank
            .Where(b => b.Source != BankSources.EnableBanking && b.Source != source && !b.Text.Contains(Mark(source))
                        && b.Kind == p.Kind && b.Amount == p.Amount && Math.Abs((b.When - when).TotalMinutes) <= 30)
            .OrderBy(b => Math.Abs((b.When - when).TotalMinutes))
            .FirstOrDefault();
        if (twin is not null)
        {
            twin.Text += "\n\n" + Mark(source) + "\n" + body;
            if (p.TransactionAt is { } t) twin.TransactionAt = t;
            else if (twin.DateOnlyKnown) twin.TransactionAt = at;
            if (p.Balance is { } bal) twin.Balance = bal;
            if (twin.Merchant.Length == 0) twin.Merchant = p.Merchant;
            await SaveBankAsync();
            Changed?.Invoke();
            return twin;
        }

        var entry = new BankEntry
        {
            Id = id,
            Source = source,
            ReceivedAt = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second),
            TransactionAt = p.TransactionAt,
            Kind = p.Kind,
            Amount = p.Amount,
            Balance = p.Balance,
            Merchant = p.Merchant,
            Text = shown,
        };
        await AttachExpenseAsync(entry, body);
        Bank.Add(entry);
        await SaveBankAsync();
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Egy másik csatornán ugyanarról érkezett üzenet ezzel a fejléccel kerül a tétel nyers szövege alá.</summary>
    private const string MarkStart = "— ";
    private static string Mark(string? source) => MarkStart + BankSources.Label(source) + " —";

    /// <summary>
    /// Egy Enable Banking tétel (a Google Apps Script küldi, <see cref="BankEb"/>). A tranzakció kulcsából képzett
    /// azonosítóval kétszer nem kerül be; ha ugyanez már SMS-ből megvan, csak összekapcsoljuk (nem lesz dupla kiadás).
    /// </summary>
    public async Task<BankEntry?> AddBankEbAsync(string text, DateTime? receivedAt = null)
    {
        if (BankEb.Parse(text) is not { } t || t.Amount <= 0) return null;
        var at = receivedAt ?? DateTime.Now;
        var when = t.Date ?? at;

        if (t.Pending)
        {
            // Függő tétel: ha már megvan (függőként, vagy könyvelve / SMS-ből / Apple Pay-ből), nem kerül be újra.
            if (Bank.FirstOrDefault(b => b.PendingRef == t.Key || b.Ref == t.Key) is { } known) return known;
            var ptwin = Bank
                .Where(b => b.Ref is null && b.PendingRef is null && (b.Source != BankSources.EnableBanking) && IsNear(b, t.Kind, t.Amount, when))
                .OrderBy(b => Math.Abs((b.When.Date - when.Date).TotalDays))
                .FirstOrDefault();
            if (ptwin is not null)
            {
                ptwin.PendingRef = t.Key;
                await SaveBankAsync();
                Changed?.Invoke();
                return ptwin;
            }
            // Ha a könyvelt párja már megjött (ritka sorrend), az már tartalmazza.
            if (Bank.Any(b => b.Source == BankSources.EnableBanking && !b.Pending && b.PendingRef is null
                              && IsNear(b, t.Kind, t.Amount, when) && b.ReceivedAt >= at.AddDays(-1)))
                return null;
            var pentry = NewEbEntry(BankSms.StableGuid("ebp:" + t.Key), t, at);
            pentry.Ref = null;
            pentry.PendingRef = t.Key;
            pentry.Pending = true;
            await AttachExpenseAsync(pentry, t.Name + " " + t.Remittance);
            Bank.Add(pentry);
            await SaveBankAsync();
            Changed?.Invoke();
            return pentry;
        }

        var id = BankSms.StableGuid("eb:" + t.Key);
        if (Bank.FirstOrDefault(b => b.Id == id || b.Ref == t.Key) is { } existing) return existing;

        // Könyvelt tétel: ha volt hozzá függő, az lesz könyvelt (nem lesz dupla kiadás).
        var pending = Bank
            .Where(b => b.PendingRef is not null && b.Ref is null && IsNear(b, t.Kind, t.Amount, when))
            .OrderBy(b => Math.Abs((b.When.Date - when.Date).TotalDays))
            .FirstOrDefault();
        if (pending is not null)
        {
            pending.Ref = t.Key;
            pending.Pending = false;
            if (pending.Source == BankSources.EnableBanking)
            {
                pending.TransactionAt = t.Date ?? pending.TransactionAt;
                pending.Text = BankEb.Describe(t);
                if (t.Name.Length > 0) pending.Merchant = t.Name;
            }
            pending.Balance ??= t.Balance;
            await SaveBankAsync();
            Changed?.Invoke();
            return pending;
        }

        var twin = Bank
            .Where(b => b.Source != BankSources.EnableBanking && b.Ref is null && IsNear(b, t.Kind, t.Amount, when))
            .OrderBy(b => Math.Abs((b.When.Date - when.Date).TotalDays))
            .FirstOrDefault();
        if (twin is not null)
        {
            twin.Ref = t.Key;
            twin.Balance ??= t.Balance;
            await SaveBankAsync();
            Changed?.Invoke();
            return twin;
        }

        var entry = NewEbEntry(id, t, at);
        await AttachExpenseAsync(entry, t.Name + " " + t.Remittance);
        Bank.Add(entry);
        await SaveBankAsync();
        Changed?.Invoke();
        return entry;
    }

    private static BankEntry NewEbEntry(Guid id, BankEb.Tx t, DateTime at) => new()
    {
        Id = id,
        Source = BankSources.EnableBanking,
        Ref = t.Key,
        ReceivedAt = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second),
        TransactionAt = t.Date,
        Kind = t.Kind,
        Amount = t.Amount,
        Balance = t.Balance,
        Merchant = t.Name.Length > 0 ? t.Name : t.Remittance.Length > 60 ? t.Remittance[..60].TrimEnd() : t.Remittance,
        Text = BankEb.Describe(t),
    };

    /// <summary>
    /// A bank szerint jelenleg függő tételek: a függőként felvett, de már nem függő és nem is könyvelt bank API tételt
    /// (visszavont kártyás zárolás) a kiadásával együtt töröljük; más forrású tételnél csak a függő-kapcsolatot bontjuk.
    /// </summary>
    public async Task<int> ApplyPendingListAsync(IReadOnlyCollection<string> keys, DateTime at)
    {
        var gone = Bank.Where(b => b.Pending && b.PendingRef is { } k && !keys.Contains(k) && b.ReceivedAt < at).ToList();
        var removed = 0;
        foreach (var b in gone)
        {
            if (b.Source == BankSources.EnableBanking)
            {
                await DeleteBankEntryAsync(b, withExpense: true);
                removed++;
            }
            else
            {
                b.PendingRef = null;
                b.Pending = false;
            }
        }
        if (gone.Count > removed) await SaveBankAsync();
        if (gone.Count > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>A bank API egyenlege (minden lekérésnél jön); régebbi üzenet nem írja felül az újabbat.</summary>
    public async Task SetBankBalanceAsync(long amount, DateTime at)
    {
        if (Settings.BankBalanceAt is { } prev && prev > at) return;
        Settings.BankBalance = amount;
        Settings.BankBalanceAt = at;
        await SaveSettingsAsync();
        Changed?.Invoke();
    }

    /// <summary>A „Felhasználható összeg” kártya adatai: a legutóbbi valódi egyenleg (SMS, bank API vagy kézi),
    /// és ha azóta jöttek tételek (bank API / Apple Pay / push), azokkal számolt becslés.</summary>
    public sealed record BalanceView(long Amount, DateTime At, long BaseAmount, DateTime BaseAt, int LaterCount, bool Estimated);

    public BalanceView? LatestBankBalance()
    {
        var e = Bank.Where(b => b.Balance is not null).OrderByDescending(b => b.When).ThenByDescending(b => b.ReceivedAt).FirstOrDefault();
        (long Amount, DateTime At)? fromEntry = e is null ? null : (e.Balance!.Value, e.DateOnlyKnown ? e.ReceivedAt : e.When);
        (long Amount, DateTime At)? fromApi = Settings.BankBalance is { } a && Settings.BankBalanceAt is { } t ? (a, t) : null;
        var anchor = fromApi is null ? fromEntry
            : fromEntry is null ? fromApi
            : fromApi.Value.At >= fromEntry.Value.At ? fromApi : fromEntry;
        if (anchor is not { } an) return null;

        // Későbbi tételek, amelyek még nincsenek benne az egyenlegben: a bank API saját tételei (az SMS-es párjuk már benne
        // van), amelyek az alap után érkeztek és nem korábbi napra szólnak, és az Apple Pay / push tételek az alap után.
        var later = Bank.Where(b => b.Balance is null && b != e && (
                b.Source == BankSources.EnableBanking
                    ? b.ReceivedAt > an.At && b.When.Date >= an.At.Date
                    : (b.Source == BankSources.ApplePay || b.Source == BankSources.Push) && b.When > an.At))
            .ToList();
        var delta = later.Sum(b => b.Kind == BankKinds.Credit ? b.Amount : b.Kind == BankKinds.Debit ? -b.Amount : 0);
        var lastAt = later.Count == 0 ? an.At : later.Max(b => b.Source == BankSources.EnableBanking ? b.ReceivedAt : b.When);
        return new BalanceView(an.Amount + delta, lastAt, an.Amount, an.At, later.Count, later.Count > 0);
    }

    /// <summary>Kézzel megadott tényleges egyenleg (pl. a mobilbankból): ettől számolunk tovább.</summary>
    public async Task SetManualBankBalanceAsync(long amount)
    {
        var now = DateTime.Now;
        Settings.BankBalance = amount;
        Settings.BankBalanceAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
        await SaveSettingsAsync();
        Changed?.Invoke();
    }

    /// <summary>Ugyanaz a tranzakció lehet: azonos irány és összeg, legfeljebb 4 nap eltérés (a kártyás tételt később könyvelik).</summary>
    private static bool IsNear(BankEntry b, string kind, long amount, DateTime when) =>
        b.Kind == kind && b.Amount == amount && Math.Abs((b.When.Date - when.Date).TotalDays) <= 4;

    /// <summary>Terhelésnél kiadás is lesz belőle (a tétel azonosítójából képzett azonosítóval, így nem duplázódik).</summary>
    private async Task AttachExpenseAsync(BankEntry entry, string text)
    {
        if (entry.Kind != BankKinds.Debit || entry.Amount <= 0) return;
        var alias = FindBankAlias(entry.Merchant);
        var expense = new Expense
        {
            Id = BankSms.StableGuid("sms-expense:" + entry.Id),
            Amount = (int)Math.Min(entry.Amount, int.MaxValue),
            Category = alias?.Category ?? BankSms.GuessCategory(entry.Merchant, text),
            Date = DateOnly.FromDateTime(entry.When),
            Description = BankDisplayName(entry) is { Length: > 0 } dn && dn != entry.Kind ? dn : "K&H terhelés",
        };
        if (!Expenses.Any(e => e.Id == expense.Id))
        {
            Expenses.Add(expense);
            await SaveExpensesAsync();
        }
        entry.ExpenseId = expense.Id;
    }

    // ---------- Párosított nevek ----------

    /// <summary>A partner névből képzett összehasonlító kulcs (kisbetű, ékezet és fölös szóköz nélkül).</summary>
    public static string AliasKey(string merchant) =>
        string.Join(' ', BankSms.Plain(merchant ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',', '.');

    public BankAlias? FindBankAlias(string merchant)
    {
        var key = AliasKey(merchant);
        return key.Length == 0 ? null : Settings.BankAliases.FirstOrDefault(a => AliasKey(a.Match) == key);
    }

    /// <summary>A naplóban látható név: saját név → párosított név → partner → típus.</summary>
    public string BankDisplayName(BankEntry b) =>
        !string.IsNullOrWhiteSpace(b.Label) ? b.Label!
        : FindBankAlias(b.Merchant)?.Name is { Length: > 0 } n ? n
        : b.Merchant.Length > 0 ? b.Merchant : b.Kind;

    /// <summary>
    /// Név (és kategória) párosítása. <paramref name="forAll"/> = minden tételnél ennél a partnernél (a későbbieknél is),
    /// különben csak ennél az egynél. Üres név = a párosítás / saját név törlése. A kapcsolódó kiadások is frissülnek.
    /// </summary>
    public async Task SetBankNameAsync(BankEntry entry, string name, string? category, bool forAll)
    {
        name = (name ?? "").Trim();
        if (forAll && entry.Merchant.Length > 0)
        {
            var alias = FindBankAlias(entry.Merchant);
            if (name.Length == 0)
            {
                if (alias is not null) Settings.BankAliases.Remove(alias);
            }
            else if (alias is null)
            {
                Settings.BankAliases.Add(new BankAlias { Match = entry.Merchant, Name = name, Category = category });
            }
            else
            {
                alias.Name = name;
                alias.Category = category;
            }
            entry.Label = null;
            await SaveSettingsAsync();
        }
        else
        {
            entry.Label = name.Length == 0 ? null : name;
        }

        // Kapcsolódó kiadások: ennél a tételnél, illetve párosításnál minden azonos partnerű (saját név nélküli) tételnél.
        var key = AliasKey(entry.Merchant);
        var affected = forAll && key.Length > 0
            ? Bank.Where(b => b == entry || (AliasKey(b.Merchant) == key && string.IsNullOrWhiteSpace(b.Label))).ToList()
            : [entry];
        var expensesChanged = false;
        foreach (var b in affected)
        {
            if (b.ExpenseId is not { } eid || Expenses.FirstOrDefault(e => e.Id == eid) is not { } exp) continue;
            exp.Description = BankDisplayName(b) is { } dn && dn != b.Kind ? dn : "K&H terhelés";
            if (category is not null) exp.Category = category;
            expensesChanged = true;
        }
        if (expensesChanged) await SaveExpensesAsync();
        await SaveBankAsync();
        Changed?.Invoke();
    }

    public async Task DeleteBankAliasAsync(BankAlias alias)
    {
        Settings.BankAliases.Remove(alias);
        await SaveSettingsAsync();
        Changed?.Invoke();
    }

    /// <summary>Bank napló bejegyzés törlése; ha volt belőle kiadás, azt is törli.</summary>
    public async Task DeleteBankEntryAsync(BankEntry entry, bool withExpense)
    {
        Bank.Remove(entry);
        if (withExpense && entry.ExpenseId is { } eid && Expenses.RemoveAll(e => e.Id == eid) > 0)
            await SaveExpensesAsync();
        await SaveBankAsync();
        Changed?.Invoke();
    }
}
