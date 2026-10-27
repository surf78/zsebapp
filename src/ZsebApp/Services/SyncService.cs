using System.Text.Json;
using Microsoft.JSInterop;

namespace ZsebApp.Services;

/// <summary>A közös tár (Firebase) állapota, ahogy a JS jelenti.</summary>
public sealed class SyncState
{
    /// <summary>Be van-e állítva a Firebase (wwwroot/js/firebase-config.js).</summary>
    public bool Configured { get; set; }
    /// <summary>off | loading | signedOut | verify | noHousehold | connected | error</summary>
    public string Phase { get; set; } = "off";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = [];
    public bool IsOwner { get; set; }
    public bool Online { get; set; } = true;
    /// <summary>Van még fel nem töltött helyi módosítás.</summary>
    public bool Pending { get; set; }
    /// <summary>Minden lista a szerverről jött (élő kapcsolat).</summary>
    public bool Synced { get; set; }
    public string? Error { get; set; }

    // Bank SMS (Parancsok → Firestore): a háztartás azonosítója, a titkos kulcs és a Firebase projekt adatai.
    public string? HouseholdId { get; set; }
    public string? SmsSecret { get; set; }
    public string? ApiKey { get; set; }
    public string? ProjectId { get; set; }
    /// <summary>A beérkezőben várakozó (még fel nem dolgozott) SMS-ek száma.</summary>
    public int InboxPending { get; set; }
    /// <summary>Ha a beérkező SMS-ek figyelése hibára futott (pl. a szabályok nincsenek közzétéve).</summary>
    public string? InboxError { get; set; }

    public bool Connected => Phase == "connected";

    /// <summary>A Parancsok által hívandó cím (Firestore REST), vagy null, ha nincs kapcsolat.</summary>
    public string? SmsInboxUrl => Connected && HouseholdId is not null && ApiKey is not null && ProjectId is not null
        ? $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/households/{HouseholdId}/smsinbox?key={ApiKey}"
        : null;
}

public sealed record InboxLogItem(DateTime At, string Preview, string Result);

/// <summary>Híd a .NET és a zseb.sync (Firebase) JS modul között.</summary>
public sealed class SyncService(IJSRuntime js, AppData data) : IDisposable
{
    private DotNetObjectReference<SyncService>? self;
    private bool started;

    public SyncState State { get; private set; } = new();

    /// <summary>Jelez, ha a kapcsolat / bejelentkezés állapota változott.</summary>
    public event Action? StateChanged;

    /// <summary>Indításkor egyszer: ha be van állítva a Firebase, bejelentkeztet (a megjegyzett fiókkal) és szinkronizál.</summary>
    public async Task StartAsync()
    {
        if (started) return;
        started = true;
        await data.EnsureLoadedAsync();
        self = DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeAsync<bool>("zseb.sync.init", self);
        }
        catch (JSException)
        {
            // A szinkron modul nem töltődött be (pl. régi gyorstár) – az app helyben tovább működik.
        }
    }

    [JSInvokable]
    public void OnSyncState(string json)
    {
        State = JsonSerializer.Deserialize(json, AppJsonContext.Default.SyncState) ?? new SyncState();
        data.SyncActive = State.Connected;
        data.UserName = string.IsNullOrWhiteSpace(State.Name) ? null : State.Name;
        StateChanged?.Invoke();
    }

    /// <summary>A felhőből érkezett lista (a JS szinkron hívja); visszaadja a .NET-es JSON-t.</summary>
    [JSInvokable]
    public string OnRemoteData(string key, string json) => data.ApplyRemote(key, json);

    /// <summary>A beérkezett SMS-ek feldolgozásának naplója (ezen a telefonon, a legutóbbi 20) – hibakereséshez.</summary>
    public List<InboxLogItem> InboxLog { get; } = [];

    /// <summary>A Parancsokból érkezett banki SMS (a JS hívja, utána törli a beérkező dokumentumot).</summary>
    [JSInvokable]
    public async Task<bool> OnSmsInbox(string id, string text, string? at)
    {
        DateTime? when = DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dto) ? dto.LocalDateTime : null;
        var preview = (text ?? "").Replace('\n', ' ').Trim();
        if (preview.Length > 80) preview = preview[..80] + "…";
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Log(preview, "Üres szöveg érkezett – a Parancsban a „Parancs bemenete” helyett az üzenet tartalmát kell beküldeni.");
                return true;
            }
            var entry = await data.AddBankSmsAsync(text, id, when);
            Log(preview, entry is null
                ? "Nem találtam benne összeget – nem került a naplóba."
                : $"Rögzítve: {entry.Kind}, {Fmt.Ft(entry.Amount)}");
            return true;                               // feldolgozva → a JS törölheti a beérkezőt
        }
        catch (Exception ex)
        {
            Log(preview, "Hiba a feldolgozásnál: " + ex.Message);
            return false;                              // megmarad a beérkezőben, később újrapróbáljuk
        }
    }

    private void Log(string preview, string result)
    {
        InboxLog.Insert(0, new InboxLogItem(DateTime.Now, preview, result));
        if (InboxLog.Count > 20) InboxLog.RemoveAt(InboxLog.Count - 1);
        StateChanged?.Invoke();
    }

    /// <summary>A közös tár létrehozásakor a telefonon lévő adatok (a JS szinkron hívja).</summary>
    [JSInvokable]
    public string ExportAll() => data.ExportAllJson();

    // Mindegyik null-t ad vissza, ha sikerült, különben a hibakódot (lásd SyncText.ErrorMessage).
    public Task<string?> SignInAsync(string email, string password) => Call("zseb.sync.signIn", email, password);
    public Task<string?> RegisterAsync(string name, string email, string password) => Call("zseb.sync.register", name, email, password);
    public Task<string?> ResendVerificationAsync() => Call("zseb.sync.resendVerification");
    public Task<string?> CheckVerifiedAsync() => Call("zseb.sync.checkVerified");
    public Task<string?> ResetPasswordAsync(string email) => Call("zseb.sync.resetPassword", email);
    public Task<string?> CreateHouseholdAsync() => Call("zseb.sync.createHousehold");
    public Task<string?> AddMemberAsync(string email) => Call("zseb.sync.addMember", email);
    public Task<string?> RemoveMemberAsync(string email) => Call("zseb.sync.removeMember", email);
    public Task<string?> SetSmsSecretAsync(string? secret) => Call("zseb.sync.setSmsSecret", secret);
    /// <summary>Próba: ugyanúgy küld be egy SMS-t, ahogy a Parancsok fogja (REST-en, kulccsal).</summary>
    public Task<string?> SendTestSmsAsync(string text) => Call("zseb.sync.sendTestSms", text);
    public Task FindHouseholdAsync() => js.InvokeVoidAsync("zseb.sync.findHousehold").AsTask();
    public Task SignOutAsync() => js.InvokeVoidAsync("zseb.sync.signOut").AsTask();

    private async Task<string?> Call(string fn, params object?[] args)
    {
        try
        {
            return await js.InvokeAsync<string?>(fn, args);
        }
        catch (JSException ex)
        {
            return ex.Message;
        }
    }

    public void Dispose() => self?.Dispose();
}
