# kreta-sync

Félóránként lekéri a KRÉTA-ból Izabella házi feladatait és jegyeit, és beírja a ZsebApp közös tárába.
A ZsebApp **Iskola** füle innen olvas (István és a felesége is látja).

```
KRÉTA ──(GitHub Actions, 30 percenként, 6–21 óra)──► Firestore: households/{hid}/iskola_hazi, iskola_jegyek, iskola_meta/status ──► ZsebApp Iskola fül
```

A program és a workflow ebben a repóban van (`kreta-sync/`, `.github/workflows/kreta-sync.yml`). Jelszó, token és
kulcs **nincs** a repóban: a KRÉTA refresh token a Firestore `kreta_private/token` dokumentumában van (kliens nem éri el),
a Firebase kulcs GitHub Secretben.

## Beállítás (egyszer)

### 1. Firebase service account kulcs
Firebase konzol → ⚙️ Projektbeállítások → **Szolgáltatásfiókok** → *Új privát kulcs létrehozása*.
A letöltött JSON-t tedd a repón **kívülre** (pl. `C:\Titkos\zsebapp-sa.json`). Soha ne commitold.

### 2. Intézményi kód
A webes KRÉTA címének eleje: `https://`**`xxxxxx`**`.e-kreta.hu` → ez a `KRETA_INSTITUTE`.

### 3. Egyszeri bejelentkezés a saját gépeden (.NET 8 SDK kell)
PowerShellben:

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\Titkos\zsebapp-sa.json"
$env:FIREBASE_PROJECT_ID = "zsebapp"
$env:ZSEB_HOUSEHOLD      = "QApBpD8OjydvIeiIdFgG"
$env:KRETA_INSTITUTE     = "xxxxxx"
cd "C:\Cloud agent\ZsebApp_5\ZsebApp\kreta-sync\KretaSync"
dotnet run -- login
```

Megnyílik a KRÉTA bejelentkezés. Lépj be a gondviselői fiókkal. A végén a böngésző egy
`mobil.e-kreta.hu/...?code=...` címre ugrik (lehet, hogy hibaoldalt mutat, az nem baj).
Másold ki a teljes címet a címsorból (Ctrl+A, Ctrl+C), majd ugyanabban a PowerShell-ablakban:

```powershell
dotnet run -- login-finish (Get-Clipboard)
```
Ha a böngésző nem marad azon a címen: F12 → Network fül, ott látszik a `?code=`-os kérés.

Utána próba: `dotnet run -- sync` → ha kiírja, hogy `OK – házi feladat: …`, a ZsebApp Iskola fülén megjelennek az adatok.

> Ha a böngészős login nem megy, tartaléknak: `dotnet run -- login-password` (régi módszer, lehet, hogy a KRÉTA már nem engedi).

### 4. GitHub Secrets
`surf78/zsebapp` → Settings → Secrets and variables → Actions → *New repository secret*:

| Név | Érték |
|---|---|
| `FIREBASE_SA_JSON` | a service account JSON fájl **teljes tartalma** |
| `ZSEB_HOUSEHOLD` | `QApBpD8OjydvIeiIdFgG` |
| `KRETA_INSTITUTE` | az intézményi kód |

Majd Actions fül → **Kréta szinkron** → *Run workflow*, hogy kipróbáld.

### 5. Firestore szabályok
A `firebase/firestore.rules` már tartalmazza az iskolai részt (`iskola_*` olvasás a tagoknak, csak az `olvasott` mező írható;
`kreta_private` senkinek). A konzolban (Firestore → Rules) közzé kell tenni.

## Ha eltörik
- **Sikertelen futásról a GitHub e-mailt küld**, a ZsebApp Iskola fülén pedig piros sáv jelenik meg.
- `A mentett token lejárt…` → futtasd újra helyben a 3. lépést (`dotnet run -- login`).
- `invalid_client`, `invalid_request` vagy furcsa átirányítás a loginnál → valószínűleg a KRÉTA megváltoztatta a bejelentkezést.
  A `Program.cs` tetején lévő konstansokat (client_id, redirect_uri, scope) össze kell vetni a Firka / reFilc aktuális forráskódjával.
- A KRÉTA mobil API nem hivatalos, bármikor változhat.

## Firestore adatszerkezet (households/{hid}/…)

**`iskola_hazi/{uid}`**: `tantargy`, `tanar`, `szoveg` (sima szöveg), `feladasDatum`, `hatarido` (Timestamp),
`hataridoNap` ("yyyy-MM-dd", magyar idő), `rogzitve`, `csatolmanyDb`, `elsoLatva`, `frissitve`, `olvasott` (bool).

**`iskola_jegyek/{uid}`**: `tantargy`, `ertek` (pl. "Jeles(5)"), `szamErtek` (1–5 vagy null), `suly` (%), `tema`,
`tipus`, `mod`, `ertekFajta`, `tanar`, `datum` (Timestamp), `datumNap`, `rogzitve`, `elsoLatva`, `frissitve`, `olvasott`.

**`iskola_meta/status`**: `utolsoSzinkron`, `utolsoHiba`, `utolsoHibaIdo`, `haziDb`, `jegyDb`.
