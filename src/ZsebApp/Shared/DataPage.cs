using Microsoft.AspNetCore.Components;
using ZsebApp.Services;

namespace ZsebApp.Shared;

/// <summary>
/// Alaposztály az adatokat mutató oldalakhoz: ha a közös tárból (a másik telefonról) új adat érkezik,
/// az oldal magától újrarajzol.
/// </summary>
public abstract class DataPage : ComponentBase, IDisposable
{
    [Inject] protected AppData Data { get; set; } = default!;

    protected override void OnInitialized() => Data.Changed += OnDataChanged;

    private void OnDataChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Data.Changed -= OnDataChanged;
        GC.SuppressFinalize(this);
    }
}
