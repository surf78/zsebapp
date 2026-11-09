using System.Text.Json;
using Microsoft.JSInterop;
using ZsebApp.Models;

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
    /// <summary>A „Lekérés most” gomb kérésének állapota: requested | running | done | error (az Apps Script írja).</summary>
    public string? EbState { get; set; }
    public string? EbMsg { get; set; }
    /// <summary>A kérés ideje (ISO).</summary>
    public string? EbAt { get; set; }
    /// <summary>Az utolsó állapotváltás ideje (ISO).</summary>
    public string? EbDone { get; set; }

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

    /// <summary>A Parancsokból érkezett banki SMS vagy az Apps Scriptből érkezett Enable Banking tétel (a JS hívja, utána törli a beérkező dokumentumot).</summary>
    [JSInvokable]
    public async Task<bool> OnSmsInbox(string id, string text, string? at)
    {
        var ok = await ProcessInboxItem(id, text, at);
        if (ok)
        {
            try
            {
                var n = await data.RepairBankDuplicatesAsync();
                if (n > 0) Log("Bank napló", $"{n} dupla tétel összevonva");
            }
            catch (Exception ex)
            {
                Log("Bank napló", "Dupla tételek javítása sikertelen: " + ex.Message);
            }
        }
        return ok;
    }

    private async Task<bool> ProcessInboxItem(string id, string text, string? at)
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
            if (BankEb.IsPendingList(text))
            {
                if (BankEb.ParsePendingList(text) is { } pl)
                {
                    var removed = await data.ApplyPendingListAsync(pl.Keys, pl.At);
                    if (removed > 0) Log("Bank API: függő tételek", $"{removed} visszavont függő tétel törölve");
                }
                return true;
            }
            if (BankEb.IsBalance(text))
            {
                if (BankEb.ParseBalance(text) is { } bal)
                {
                    await data.SetBankBalanceAsync(bal.Amount, bal.At);
                    Log("Bank API: egyenleg", $"Egyenleg frissítve: {Fmt.Ft(bal.Amount)}");
                }
                else Log(preview, "Hibás egyenleg-üzenet – kihagyva.");
                return true;
            }
            if (BankEb.IsEb(text))
            {
                var tx = BankEb.Parse(text);
                var eb = await data.AddBankEbAsync(text, when);
                Log(tx is null ? preview : $"Bank API: {tx.Name} {tx.Remittance}".Trim(), eb is null
                    ? "Hibás banki tétel – kihagyva."
                    : eb.Source == BankSources.EnableBanking ? $"Rögzítve (bank API{(eb.Pending ? ", függő" : "")}): {eb.Kind}, {Fmt.Ft(eb.Amount)}"
                    : $"Már megvolt SMS-ből – összekapcsolva: {Fmt.Ft(eb.Amount)}");
                return true;
            }
            var entry = await data.AddBankSmsAsync(text, id, when);
            var channel = BankSources.Label(BankQuick.Channel(text, out _));
            Log(preview, entry is null
                ? "Nem találtam benne összeget – nem került a naplóba."
                : entry.Id != BankSms.StableGuid("sms:" + id)
                    ? $"Már megvolt ({BankSources.Label(entry.Source)}) – összekapcsolva: {Fmt.Ft(entry.Amount)}"
                    : $"Rögzítve ({channel}): {entry.Kind}, {Fmt.Ft(entry.Amount)}");
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

    /// <summary>Iskola (KRÉTA): a kreta-sync által a Firestore-ba írt házi feladatok, jegyek, állapot.</summary>
    public IskolaData Iskola { get; private set; } = new();
    public bool IskolaLoaded { get; private set; }
    public event Action? IskolaChanged;

    [JSInvokable]
    public void OnIskola(string json)
    {
        try
        {
            Iskola = JsonSerializer.Deserialize(json, AppJsonContext.Default.IskolaData) ?? new IskolaData();
        }
        catch (JsonException)
        {
            Iskola = new IskolaData();
        }
        IskolaLoaded = true;
        IskolaChanged?.Invoke();
    }

    /// <summary>Olvasottnak jelölés (kind = "hazi" | "jegyek"). Azonnal helyben is, a felhő megerősíti.</summary>
    public async Task<string?> MarkIskolaReadAsync(string kind, string id)
    {
        if (kind == "hazi" && Iskola.Hazi.FirstOrDefault(h => h.Id == id) is { } h) h.Olvasott = true;
        if (kind == "jegyek" && Iskola.Jegyek.FirstOrDefault(j => j.Id == id) is { } j) j.Olvasott = true;
        IskolaChanged?.Invoke();
        return await Call("zseb.sync.markIskolaRead", kind, id);
    }

    /// <summary>A közös tár létrehozásakor a telefonon lévő adatok (a JS szinkron hívja).</summary>
    [JSInvokable]
    public string ExportAll() => data.ExportAllJson();

    // Mindegyik null-t ad vissza, ha sikerült, különben a hibakódot (lásd SyncText.ErrorMessage).
    /// <summary>„Lekérés most”: az Apps Script percenként figyeli, és azonnal lekéri a K&amp;H tételeit.</summary>
    public Task<string?> RequestBankFetchAsync() => Call("zseb.sync.requestBankFetch");
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
