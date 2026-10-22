# ZsebApp

Teendők, jegyzetek és kiadáskövető egy appban. C#-ban írt **Blazor WebAssembly PWA**, amit iPhone-on a főképernyőről lehet indítani. Nem kell hozzá Mac, és nem kell Apple fejlesztői fiók.

## Funkciók

- **Teendők:** hozzáadás, késznek jelölés, törlés, a kész elemek egy gombbal törölhetők.
  **Hangalapú rögzítés magyarul:** a mikrofon gombra koppintva mondd ki a feladatot. A szöveg élőben megjelenik a mezőben, ellenőrzés után a + gombbal mented.
- **Jegyzetek:** lista és szerkesztő automatikus mentéssel.
- **Kiadások:** összeg, kategória, dátum és megjegyzés rögzítése. Havi összesítő kategóriánkénti sávokkal, napokra bontott lista, hónapváltó.
- **Számlák:** megnevezés (gyorsgombok: Gázszámla, Villanyszámla, Vízdíj…), összeg és állapot. Az állapot **Rendezetlen** (piros, ez az alapértelmezés) vagy **Fizetve** (zöld), a listában az állapotra koppintva váltható. Fent összesítő mutatja a rendezetlen és a kifizetett összeget.
- **Mérőórák** (a Számlák fülön, felül a „Mérőórák” gombbal): gáz-, villany- és vízóra-állás rögzítése dátummal és megjegyzéssel. Mutatja az utolsó állást, az előző leolvasás óta eltelt fogyasztást, a napi átlagot és az utolsó 6 időszak fogyasztását. Nem enged a korábbinál kisebb állást beírni.
- **Számlák szűrése:** a lista fölötti „Szűrés” sávban csoportra (a számla megnevezése, pl. Gázszámla) és időszakra (hét / hónap / év, léptethető) lehet szűrni; az összesítő a szűrt számlákat mutatja. A számlákhoz dátum is megadható.
- **Riport** (Beállítások → Riport): kiadások és mérőóra-fogyasztás hétre, hónapra, évre vagy a teljes időszakra – összeg, napi átlag, összehasonlítás az előző időszakkal, időbeli és kategóriánkénti bontás. Exportálható CSV-be (Excelben megnyitható).
- **Beállítások:** mentés JSON-fájlba (iPhone-on a megosztási lapon a „Mentés a Fájlokba” választható), visszaállítás fájlból, minden adat törlése.
- Az app offline is működik, és támogatja a sötét módot. Az adatok a telefonon maradnak (localStorage).

## Projektszerkezet

```
src/ZsebApp/
  Models/        adatmodellek (TodoItem, Note, Expense, Bill, Backup, Categories)
  Services/      AppData (állapot + mentés), LocalStore (localStorage), Fmt (magyar formázás)
  Pages/         Todos, Notes, Expenses, Bills, Settings
  Layout/        MainLayout (alsó fülsáv)
  Shared/        Icon, EmptyState
  wwwroot/       index.html, CSS, ikonok, manifest, service worker
.github/workflows/deploy.yml   automatikus publikálás GitHub Pagesre
```

## 1. Futtatás a gépeden (Windows)

1. Telepítsd a [.NET 10 SDK-t](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Nyisd meg a `ZsebApp.slnx`-et Visual Studióban (2022 17.14+ vagy 2026), és nyomj F5-öt. Parancssorból:

   ```
   cd src\ZsebApp
   dotnet run
   ```

3. Böngészőben: http://localhost:5118. Ha a Chrome/Edge fejlesztői eszközeiben (F12) iPhone-nézetre váltasz, telefonos méretben látod.

## 2. Feltöltés GitHub Pagesre

1. Hozz létre egy új GitHub-repót, például `zsebapp` néven. Lehet privát is, ha a fiókod támogatja a Pagest privát repón, egyébként nyilvános.
2. Töltsd fel a mappa tartalmát a `main` ágra:

   ```
   git init
   git add .
   git commit -m "ZsebApp"
   git branch -M main
   git remote add origin https://github.com/<felhasznalonev>/zsebapp.git
   git push -u origin main
   ```

3. A repóban: **Settings → Pages → Build and deployment → Source: GitHub Actions**.
4. Az **Actions** fülön lefut a „Publikálás GitHub Pagesre” workflow. Néhány perc múlva az app elérhető itt:
   `https://<felhasznalonev>.github.io/zsebapp/`

Minden további `git push` automatikusan frissíti az appot.

## 3. Telepítés az iPhone-ra

1. Nyisd meg a fenti címet a **Safariban**.
2. Koppints a **Megosztás** gombra.
3. Válaszd a **Főképernyőhöz adás** lehetőséget.

Ezután saját ikonnal, teljes képernyőn indul, és internet nélkül is működik.

**Frissítés:** új verzió feltöltése után nyisd meg az appot, majd zárd be teljesen az alkalmazásváltóban (felfelé húzással), és indítsd újra.

## Fontos tudnivalók

- **Az adatok csak a telefonon vannak.** Ha törlöd az appot a főképernyőről, az adatok is elvesznek. Időnként készíts mentést a Beállítások fülön.
- **Más cím, más tárhely.** A Safariban megnyitott oldal és a főképernyőre tett app külön tárhelyet használ, ezért mindig a főképernyős appot használd.
- **Hangfelismerés:** a Safari beépített beszédfelismerőjét (Web Speech API, `hu-HU`) használja, ehhez internet kell, és be kell kapcsolni a Diktálást (Beállítások → Általános → Billentyűzet → Diktálás engedélyezése). Első használatkor az iPhone engedélyt kér a mikrofonhoz. Ha a főképernyőről indított appban a funkció nem érhető el, az app ezt jelzi. Ilyenkor a billentyűzet mikrofon gombjával lehet diktálni, ami magyarul is működik.
- **Megjelenés („Lagúna” téma):** a háttér a `wwwroot/img/hatter.jpg` fotó, a színek és a gombstílusok a `wwwroot/css/app.css` elején, változókként vannak (`--sail`, `--ocean`, `--glass`…). Másik képhez egyszerűen cseréld le a `hatter.jpg` fájlt (álló, kb. 750×1300 px-es kép a legjobb). Sötét módban a háttér automatikusan esti hangulatúra sötétül.
- **Új kategória** felvételéhez szerkeszd a `Models/Models.cs` fájlban a `Categories.All` listát.
