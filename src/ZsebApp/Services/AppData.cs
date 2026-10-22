using System.Security.Cryptography;
using System.Text;
using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>Az app összes adata. Egyszer tölt be, minden módosítás után ment.</summary>
public sealed class AppData(LocalStore store)
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

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        Todos = await store.GetAsync(TodosKey, AppJsonContext.Default.ListTodoItem) ?? [];
        Notes = await store.GetAsync(NotesKey, AppJsonContext.Default.ListNote) ?? [];
        Expenses = await store.GetAsync(ExpensesKey, AppJsonContext.Default.ListExpense) ?? [];
        Bills = await store.GetAsync(BillsKey, AppJsonContext.Default.ListBill) ?? [];
        Meters = await store.GetAsync(MetersKey, AppJsonContext.Default.ListMeterReading) ?? [];
        Settings = await store.GetAsync(SettingsKey, AppJsonContext.Default.AppSettings) ?? new();

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
    }

    public Task SaveTodosAsync() => store.SetAsync(TodosKey, Todos, AppJsonContext.Default.ListTodoItem).AsTask();
    public Task SaveNotesAsync() => store.SetAsync(NotesKey, Notes, AppJsonContext.Default.ListNote).AsTask();
    public Task SaveExpensesAsync() => store.SetAsync(ExpensesKey, Expenses, AppJsonContext.Default.ListExpense).AsTask();
    public Task SaveBillsAsync() => store.SetAsync(BillsKey, Bills, AppJsonContext.Default.ListBill).AsTask();
    public Task SaveBillTemplatesAsync() => store.SetAsync(BillTemplatesKey, BillTemplates, AppJsonContext.Default.ListBillTemplate).AsTask();
    public Task SaveMetersAsync() => store.SetAsync(MetersKey, Meters, AppJsonContext.Default.ListMeterReading).AsTask();
    public Task SaveSettingsAsync() => store.SetAsync(SettingsKey, Settings, AppJsonContext.Default.AppSettings).AsTask();

    /// <summary>Új teendő felvétele bárhonnan (pl. hangról), értesíti a Teendők oldalt.</summary>
    public async Task<TodoItem?> AddTodoAsync(string title)
    {
        title = title.Trim();
        if (title.Length == 0) return null;
        var item = new TodoItem { Title = title };
        Todos.Add(item);
        await SaveTodosAsync();
        TodosChanged?.Invoke();
        return item;
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

    public Backup CreateBackup() => new()
    {
        ExportedAt = DateTime.Now,
        Todos = Todos,
        Notes = Notes,
        Expenses = Expenses,
        Bills = Bills,
        Meters = Meters,
        BillTemplates = BillTemplates,
    };

    public async Task RestoreAsync(Backup backup)
    {
        Todos = backup.Todos ?? [];
        Notes = backup.Notes ?? [];
        Expenses = backup.Expenses ?? [];
        Bills = backup.Bills ?? [];
        Meters = backup.Meters ?? [];
        if (backup.BillTemplates is not null)
            BillTemplates = backup.BillTemplates;
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
        await SaveBillTemplatesAsync();
        TodosChanged?.Invoke();
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
        await store.RemoveAsync(TodosKey);
        await store.RemoveAsync(NotesKey);
        await store.RemoveAsync(ExpensesKey);
        await store.RemoveAsync(BillsKey);
        await store.RemoveAsync(MetersKey);
        await SaveBillTemplatesAsync();
        TodosChanged?.Invoke();
    }
}
