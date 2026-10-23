using System.Text.Json.Serialization;
using ZsebApp.Models;

namespace ZsebApp.Services;

// Forráskód-generált JSON-szerializáció: trimmelt WebAssembly buildben is megbízhatóan működik.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(List<Note>))]
[JsonSerializable(typeof(List<Expense>))]
[JsonSerializable(typeof(List<Bill>))]
[JsonSerializable(typeof(List<MeterReading>))]
[JsonSerializable(typeof(List<BillTemplate>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Backup))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(Expense))]
[JsonSerializable(typeof(Bill))]
[JsonSerializable(typeof(BillTemplate))]
[JsonSerializable(typeof(MeterReading))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SyncState))]
public sealed partial class AppJsonContext : JsonSerializerContext;
