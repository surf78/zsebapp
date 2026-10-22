using ZsebApp.Models;

namespace ZsebApp.Services;

/// <summary>Az app összes adata. Egyszer tölt be, minden módosítás után ment.</summary>
public sealed class AppData(LocalStore store)
{
    private const string TodosKey = "zseb.todos";
    private const string NotesKey = "zseb.notes";
    private const string ExpensesKey = "zseb.expenses";
    private const string BillsKey = "zseb.bills";
    private const string MetersKey = "zseb.meters";

    private Task? _loading;

    public List<TodoItem> Todos { get; private set; } = [];
    public List<Note> Notes { get; private set; } = [];
    public List<Expense> Expenses { get; private set; } = [];
    public List<Bill> Bills { get; private set; } = [];
    public List<MeterReading> Meters { get; private set; } = [];

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        Todos = await store.GetAsync(TodosKey, AppJsonContext.Default.ListTodoItem) ?? [];
        Notes = await store.GetAsync(NotesKey, AppJsonContext.Default.ListNote) ?? [];
        Expenses = await store.GetAsync(ExpensesKey, AppJsonContext.Default.ListExpense) ?? [];
        Bills = await store.GetAsync(BillsKey, AppJsonContext.Default.ListBill) ?? [];
        Meters = await store.GetAsync(MetersKey, AppJsonContext.Default.ListMeterReading) ?? [];
    }

    public Task SaveTodosAsync() => store.SetAsync(TodosKey, Todos, AppJsonContext.Default.ListTodoItem).AsTask();
    public Task SaveNotesAsync() => store.SetAsync(NotesKey, Notes, AppJsonContext.Default.ListNote).AsTask();
    public Task SaveExpensesAsync() => store.SetAsync(ExpensesKey, Expenses, AppJsonContext.Default.ListExpense).AsTask();
    public Task SaveBillsAsync() => store.SetAsync(BillsKey, Bills, AppJsonContext.Default.ListBill).AsTask();
    public Task SaveMetersAsync() => store.SetAsync(MetersKey, Meters, AppJsonContext.Default.ListMeterReading).AsTask();

    public Backup CreateBackup() => new()
    {
        ExportedAt = DateTime.Now,
        Todos = Todos,
        Notes = Notes,
        Expenses = Expenses,
        Bills = Bills,
        Meters = Meters,
    };

    public async Task RestoreAsync(Backup backup)
    {
        Todos = backup.Todos ?? [];
        Notes = backup.Notes ?? [];
        Expenses = backup.Expenses ?? [];
        Bills = backup.Bills ?? [];
        Meters = backup.Meters ?? [];
        await SaveTodosAsync();
        await SaveNotesAsync();
        await SaveExpensesAsync();
        await SaveBillsAsync();
        await SaveMetersAsync();
    }

    public async Task ClearAllAsync()
    {
        Todos = [];
        Notes = [];
        Expenses = [];
        Bills = [];
        Meters = [];
        await store.RemoveAsync(TodosKey);
        await store.RemoveAsync(NotesKey);
        await store.RemoveAsync(ExpensesKey);
        await store.RemoveAsync(BillsKey);
        await store.RemoveAsync(MetersKey);
    }
}
