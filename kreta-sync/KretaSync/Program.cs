// KretaSync – Kréta házi feladatok és jegyek szinkronizálása Firestore-ba (ZsebApp "Iskola" oldalhoz).
//
// Parancsok:
//   login           – egyszeri böngészős bejelentkezés (OAuth2 + PKCE, mint a hivatalos iOS app): kiírja / megnyitja a címet
//   login-finish U  – a bejelentkezés vége: U = a böngésző végső (…?code=…) címe, pl. login-finish (Get-Clipboard);
//                     a refresh tokent a Firestore-ba menti (kreta_private/token)
//   login-password  – tartalék: felhasználónév + jelszó (régi password grant, ha a KRÉTA még engedi)
//   sync            – (alapértelmezett) lekérés + Firestore frissítés; ezt futtatja a GitHub Actions
//
// Környezeti változók:
//   GOOGLE_APPLICATION_CREDENTIALS – a Firebase service account JSON fájl útvonala
//   FIREBASE_PROJECT_ID            – a ZsebApp Firebase projekt azonosítója
//   KRETA_INSTITUTE                – intézményi kód (a xxxx.e-kreta.hu cím xxxx része)
//   ZSEB_HOUSEHOLD                 – a ZsebApp közös tárának azonosítója (households/{hid}); az adatok ide kerülnek
//   KRETA_USER_AGENT               – opcionális, felülírja az alapértelmezett User-Agentet
//
// FIGYELEM: a KRÉTA mobil API nem hivatalos, bármikor változhat. Az alábbi konstansok
// a nyílt forrású kliensek (Firka / reFilc, kreta-tui) által használt értékek.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Cloud.Firestore;

const string Idp = "https://idp.e-kreta.hu";
const string TokenUrl = Idp + "/connect/token";
const string IosClientId = "kreta-ellenorzo-student-mobile-ios";
const string AndroidClientId = "kreta-ellenorzo-mobile-android";
const string RedirectUri = "https://mobil.e-kreta.hu/ellenorzo-student/prod/oauthredirect";
// A token-végpont a nem az iOS appnak látszó kéréseknél bontja a kapcsolatot („forcibly closed”) –
// ugyanaz a User-Agent kell, mint a hivatalos iOS appé (kreta-tui alapján).
const string TokenUserAgent = "eKretaStudent/264745 CFNetwork/1494.0.7 Darwin/23.4.0";
const string Scope = "openid email offline_access kreta-ellenorzo-webapi.public kreta-eugyintezes-webapi.public " +
                     "kreta-fileservice-webapi.public kreta-mobile-global-webapi.public kreta-dkt-webapi.public kreta-ier-webapi.public";

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

var projectId = Env("FIREBASE_PROJECT_ID");
var institute = Env("KRETA_INSTITUTE").Trim().ToLowerInvariant();
var userAgent = Environment.GetEnvironmentVariable("KRETA_USER_AGENT") ?? "hu.ekreta.student/1.0.5/Android/0/0";

var householdId = Env("ZSEB_HOUSEHOLD").Trim();

var db = FirestoreDb.Create(projectId);
// A token a háztartáson kívül, kliens által elérhetetlen helyen van; az iskolai adatok a ZsebApp közös tárában
// (households/{hid}/iskola_*), így a háztartás tagjai (István és a felesége) látják.
var tokenDoc = db.Collection("kreta_private").Document("token");
var household = db.Collection("households").Document(householdId);
var statusDoc = household.Collection("iskola_meta").Document("status");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

var command = args.Length > 0 ? args[0] : "sync";
try
{
    switch (command)
    {
        case "login": LoginStart(); break;
        case "login-finish": await LoginFinish(string.Join(" ", args.Skip(1))); break;
        case "login-interactive": await LoginBrowser(); break;
        case "login-password": await LoginPassword(); break;
        case "sync": await Sync(); break;
        default:
            Console.WriteLine("Használat: dotnet run -- [login | login-finish <cím> | login-password | sync]");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    // A teljes hibalánc (pl. „An error occurred while sending the request” mögött a valódi ok: DNS, TLS, proxy…).
    var msg = ex.Message;
    for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException) msg += " → " + inner.Message;
    Console.Error.WriteLine("HIBA: " + msg);
    try
    {
        await statusDoc.SetAsync(new Dictionary<string, object?>
        {
            ["utolsoHiba"] = msg,
            ["utolsoHibaIdo"] = Timestamp.GetCurrentTimestamp(),
        }, SetOptions.MergeAll);
    }
    catch { /* a hibaállapot mentése nem kritikus */ }
    return 1; // a GitHub Actions ilyenkor e-mailt küld a sikertelen futásról
}

// ───────────────────────── Bejelentkezés ─────────────────────────

// Kétlépéses bejelentkezés (a konzolba beillesztés nélkül): a PKCE adatokat egy helyi fájlba tesszük.
string PendingFile() => Path.Combine(Path.GetTempPath(), "kreta-login-pending.json");

void LoginStart()
{
    var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
    var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var state = B64Url(RandomNumberGenerator.GetBytes(16));
    var nonce = B64Url(RandomNumberGenerator.GetBytes(16));
    File.WriteAllText(PendingFile(), JsonSerializer.Serialize(new Dictionary<string, string> { ["verifier"] = verifier, ["state"] = state }));

    var url = Idp + "/connect/authorize?" + BuildQuery(new()
    {
        ["client_id"] = IosClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = RedirectUri,
        ["scope"] = Scope,
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
        ["state"] = state,
        ["nonce"] = nonce,
        ["prompt"] = "login",
    });

    Console.WriteLine("1) Megnyitom a böngészőt a KRÉTA bejelentkezéshez. Ha nem nyílik meg, másold be ezt a címet:");
    Console.WriteLine(url);
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    Console.WriteLine();
    Console.WriteLine("2) Jelentkezz be a gondviselői fiókoddal. A végén a böngésző egy mobil.e-kreta.hu/...?code=... címre ugrik");
    Console.WriteLine("   (lehet, hogy hibaoldalt mutat, az nem baj). Másold ki a TELJES címet a címsorból (Ctrl+A, Ctrl+C).");
    Console.WriteLine("3) Utána ebben az ablakban futtasd (a vágólapról olvassa a címet):");
    Console.WriteLine("   dotnet run -- login-finish (Get-Clipboard)");
}

async Task LoginFinish(string pasted)
{
    if (!File.Exists(PendingFile())) throw new Exception("Előbb futtasd: dotnet run -- login");
    var pending = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PendingFile()))!;
    var q = ParseQuery(pasted.Trim());
    if (!q.TryGetValue("code", out var code))
        throw new Exception("Nem találtam 'code' paramétert a címben. A böngésző VÉGSŐ (…oauthredirect?code=…) címét másold ki, majd: dotnet run -- login-finish (Get-Clipboard)");
    if (q.TryGetValue("state", out var st) && st != pending["state"])
        throw new Exception("A 'state' nem egyezik – futtasd újra: dotnet run -- login");

    var tok = await PostToken(new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = RedirectUri,
        ["client_id"] = IosClientId,
        ["code_verifier"] = pending["verifier"],
    });
    await SaveToken(tok, IosClientId);
    File.Delete(PendingFile());
    Console.WriteLine("Kész! A refresh token elmentve a Firestore-ba. Próba: dotnet run -- sync");
}

async Task LoginBrowser()
{
    var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
    var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    var state = B64Url(RandomNumberGenerator.GetBytes(16));
    var nonce = B64Url(RandomNumberGenerator.GetBytes(16));

    var url = Idp + "/connect/authorize?" + BuildQuery(new()
    {
        ["client_id"] = IosClientId,
        ["response_type"] = "code",
        ["redirect_uri"] = RedirectUri,
        ["scope"] = Scope,
        ["code_challenge"] = challenge,
        ["code_challenge_method"] = "S256",
        ["state"] = state,
        ["nonce"] = nonce,
        ["prompt"] = "login",
    });

    Console.WriteLine("1) Megnyitom a böngészőt a KRÉTA bejelentkezéshez. Ha nem nyílik meg, másold be ezt a címet:");
    Console.WriteLine(url);
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    Console.WriteLine();
    Console.WriteLine("2) Jelentkezz be a gondviselői fiókoddal (2FA is mehet).");
    Console.WriteLine("   A végén a böngésző egy mobil.e-kreta.hu/...?code=... címre ugrik – lehet, hogy hibaoldalt mutat, az nem baj.");
    Console.WriteLine("   Másold ki a TELJES címet a címsorból, és illeszd be ide:");
    Console.Write("> ");
    var pasted = Console.ReadLine()?.Trim() ?? "";

    var q = ParseQuery(pasted);
    if (!q.TryGetValue("code", out var code))
        throw new Exception("Nem találtam 'code' paramétert a beillesztett címben.");
    if (q.TryGetValue("state", out var st) && st != state)
        throw new Exception("A 'state' nem egyezik – indítsd újra a login parancsot.");

    var tok = await PostToken(new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = RedirectUri,
        ["client_id"] = IosClientId,
        ["code_verifier"] = verifier,
    });
    await SaveToken(tok, IosClientId);
    Console.WriteLine("Kész! A refresh token elmentve a Firestore-ba. Próba: dotnet run -- sync");
}

async Task LoginPassword()
{
    Console.Write("Felhasználónév: ");
    var user = Console.ReadLine()?.Trim() ?? "";
    Console.Write("Jelszó: ");
    var pwd = ReadSecret();

    var tok = await PostToken(new()
    {
        ["grant_type"] = "password",
        ["userName"] = user,
        ["password"] = pwd,
        ["institute_code"] = institute,
        ["client_id"] = AndroidClientId,
    });
    await SaveToken(tok, AndroidClientId);
    Console.WriteLine("Kész! A refresh token elmentve a Firestore-ba. Próba: dotnet run -- sync");
}

async Task<JsonElement> PostToken(Dictionary<string, string> form)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(form) };
    req.Headers.TryAddWithoutValidation("User-Agent", TokenUserAgent);
    req.Headers.TryAddWithoutValidation("Accept", "*/*");
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        if (body.Contains("invalid_grant") && form["grant_type"] == "refresh_token")
            throw new Exception("A mentett token lejárt vagy visszavonták – futtasd újra helyben: dotnet run -- login");
        throw new Exception($"Token kérés sikertelen ({(int)resp.StatusCode}): {body}");
    }
    using var doc = JsonDocument.Parse(body);
    return doc.RootElement.Clone();
}

async Task SaveToken(JsonElement tok, string clientId)
{
    if (!tok.TryGetProperty("refresh_token", out var rt) || string.IsNullOrEmpty(rt.GetString()))
        throw new Exception("A válaszban nincs refresh_token.");
    await tokenDoc.SetAsync(new Dictionary<string, object?>
    {
        ["refreshToken"] = rt.GetString(),
        ["clientId"] = clientId,
        ["frissitve"] = Timestamp.GetCurrentTimestamp(),
    });
}

async Task<string> GetAccessToken()
{
    var snap = await tokenDoc.GetSnapshotAsync();
    if (!snap.Exists)
        throw new Exception("Nincs mentett token – futtasd helyben: dotnet run -- login");

    var clientId = snap.GetValue<string>("clientId");
    var tok = await PostToken(new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = snap.GetValue<string>("refreshToken"),
        ["client_id"] = clientId,
        ["institute_code"] = institute,
        ["refresh_user_data"] = "false",
    });
    // A refresh token egyszer használatos lehet: az újat AZONNAL elmentjük.
    if (tok.TryGetProperty("refresh_token", out _))
        await SaveToken(tok, clientId);

    return tok.GetProperty("access_token").GetString()!;
}

// ───────────────────────── Szinkron ─────────────────────────

async Task Sync()
{
    var access = await GetAccessToken();
    var baseUrl = $"https://{institute}.e-kreta.hu/ellenorzo/V3/Sajat";
    var today = DateTime.UtcNow.Date;

    var hazi = await GetArray(access, $"{baseUrl}/HaziFeladatok?datumTol={today.AddDays(-14):yyyy-MM-dd}");
    var jegyek = await GetArray(access, $"{baseUrl}/Ertekelesek?datumTol={today.AddDays(-45):yyyy-MM-dd}");

    var ujHazi = await Upsert("iskola_hazi", hazi, MapHazi);
    var ujJegy = await Upsert("iskola_jegyek", jegyek, MapJegy);

    await statusDoc.SetAsync(new Dictionary<string, object?>
    {
        ["utolsoSzinkron"] = Timestamp.GetCurrentTimestamp(),
        ["utolsoHiba"] = null,
        ["haziDb"] = hazi.Count,
        ["jegyDb"] = jegyek.Count,
    }, SetOptions.MergeAll);

    Console.WriteLine($"OK – házi feladat: {hazi.Count} ({ujHazi} új), jegy: {jegyek.Count} ({ujJegy} új)");
}

async Task<List<JsonElement>> GetArray(string accessToken, string url)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Authorization = new("Bearer", accessToken);
    using var resp = await http.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
        throw new Exception($"Lekérés sikertelen ({(int)resp.StatusCode}) {url}: {Truncate(body, 300)}");

    using var doc = JsonDocument.Parse(body);
    if (doc.RootElement.ValueKind != JsonValueKind.Array)
        throw new Exception($"Váratlan válasz {url}: {Truncate(body, 300)}");
    return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
}

async Task<int> Upsert(string collection, List<JsonElement> items, Func<JsonElement, Dictionary<string, object?>> map)
{
    var withId = items.Where(i => !string.IsNullOrEmpty(Str(i, "Uid"))).ToList();
    if (withId.Count == 0) return 0;

    var col = household.Collection(collection);
    var refs = withId.Select(i => col.Document(DocId(Str(i, "Uid")!))).ToList();
    var snaps = await db.GetAllSnapshotsAsync(refs);
    var existing = snaps.Where(s => s.Exists).Select(s => s.Id).ToHashSet();

    var now = Timestamp.GetCurrentTimestamp();
    var batch = db.StartBatch();
    int inBatch = 0, uj = 0;

    for (int i = 0; i < withId.Count; i++)
    {
        var data = map(withId[i]);
        data["frissitve"] = now;
        if (!existing.Contains(refs[i].Id))
        {
            data["elsoLatva"] = now;
            data["olvasott"] = false; // a ZsebApp állítja true-ra
            uj++;
        }
        batch.Set(refs[i], data, SetOptions.MergeAll);
        if (++inBatch == 400)
        {
            await batch.CommitAsync();
            batch = db.StartBatch();
            inBatch = 0;
        }
    }
    if (inBatch > 0) await batch.CommitAsync();
    return uj;
}

Dictionary<string, object?> MapHazi(JsonElement h) => new()
{
    ["uid"] = Str(h, "Uid"),
    ["tantargy"] = Str(h, "Tantargy", "Nev") ?? Str(h, "TantargyNeve"),
    ["tanar"] = Str(h, "RogzitoTanarNeve"),
    ["szoveg"] = HtmlToText(Str(h, "Szoveg")),
    ["feladasDatum"] = Ts(Str(h, "FeladasDatuma")),
    ["hatarido"] = Ts(Str(h, "HataridoDatuma")),
    ["hataridoNap"] = LocalDay(Str(h, "HataridoDatuma")),
    ["rogzitve"] = Ts(Str(h, "RogzitesIdopontja")),
    ["csatolmanyDb"] = h.TryGetProperty("Csatolmanyok", out var c) && c.ValueKind == JsonValueKind.Array
        ? c.GetArrayLength() : 0,
};

Dictionary<string, object?> MapJegy(JsonElement j) => new()
{
    ["uid"] = Str(j, "Uid"),
    ["tantargy"] = Str(j, "Tantargy", "Nev"),
    ["ertek"] = Str(j, "SzovegesErtek"),
    ["szamErtek"] = Int(j, "SzamErtek"),
    ["suly"] = Int(j, "SulySzazalekErteke"),
    ["tema"] = Str(j, "Tema"),
    ["tipus"] = Str(j, "Tipus", "Leiras") ?? Str(j, "Tipus", "Nev"),
    ["mod"] = Str(j, "Mod", "Leiras"),
    ["ertekFajta"] = Str(j, "ErtekFajta", "Nev"),
    ["tanar"] = Str(j, "ErtekeloTanarNeve"),
    ["datum"] = Ts(Str(j, "KeszitesDatuma")),
    ["datumNap"] = LocalDay(Str(j, "KeszitesDatuma")),
    ["rogzitve"] = Ts(Str(j, "RogzitesDatuma")),
};

// ───────────────────────── Segédfüggvények ─────────────────────────

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
        ? v
        : throw new Exception($"Hiányzó környezeti változó: {name}");

static string? Str(JsonElement e, params string[] path)
{
    foreach (var p in path)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
    }
    return e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };
}

static long? Int(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

static Timestamp? Ts(string? iso) =>
    DateTimeOffset.TryParse(iso, out var d) ? Timestamp.FromDateTimeOffset(d) : null;

static string? LocalDay(string? iso)
{
    if (!DateTimeOffset.TryParse(iso, out var d)) return null;
    var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest");
    return TimeZoneInfo.ConvertTime(d, tz).ToString("yyyy-MM-dd");
}

static string HtmlToText(string? html)
{
    if (string.IsNullOrWhiteSpace(html)) return "";
    var s = Regex.Replace(html, @"<\s*br\s*/?>|</\s*(p|div|li)\s*>", "\n", RegexOptions.IgnoreCase);
    s = Regex.Replace(s, "<[^>]+>", "");
    s = WebUtility.HtmlDecode(s);
    s = Regex.Replace(s, @"[ \t]+\n", "\n");
    s = Regex.Replace(s, @"\n{3,}", "\n\n");
    return s.Trim();
}

static string DocId(string uid) => Regex.Replace(uid, @"[/\\#\[\]\*\?]", "_");

static string B64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string BuildQuery(Dictionary<string, string> p) =>
    string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

static Dictionary<string, string> ParseQuery(string url)
{
    var result = new Dictionary<string, string>();
    var qi = url.IndexOf('?');
    if (qi < 0) return result;
    var query = url[(qi + 1)..];
    var hash = query.IndexOf('#');
    if (hash >= 0) query = query[..hash];
    foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = part.Split('=', 2);
        result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
    }
    return result;
}

static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
        sb.Append(k.KeyChar);
    }
    Console.WriteLine();
    return sb.ToString();
}

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
