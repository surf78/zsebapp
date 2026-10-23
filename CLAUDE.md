# ZsebApp – útmutató Claude-nak

Személyes iPhone-app (iPhone 11) István számára: teendők, jegyzetek, kiadások, számlák, mérőórák.

## Munkamód
- **Mindig magyarul válaszolj.**
- Minden módosítás után **automatikusan commitolj és pusholj a `master` ágra** – István nem akar kézzel commitolni.
  A push után a GitHub Actions („Publikálás GitHub Pagesre”) magától publikál: https://vargaistvan1978.github.io/zsebapp/
- A commit üzenet legyen rövid, magyar, és írja le a változást.
- A felület szövegei magyarok; dátumformátum pl. „2026. 10. 22.”, hónap fejléc „2026-10 október”, pénz „12 340 Ft”.

## Technika
- .NET 10 Blazor WebAssembly PWA (`src/ZsebApp`), saját szerver nincs: minden adat a telefon `localStorage`-ében (`AppData`, `LocalStore`).
- Közös használat (István és a felesége): Firebase Auth (e-mail + jelszó, megerősített cím) + Firestore, offline gyorstárral.
  - Forrás: `firebase/sync.src.js` → `npm install && npm run build` a `firebase` mappában → `wwwroot/js/sync.bundle.js` (a buildelt fájl is a repóban van, mert a GitHub Actions csak `dotnet publish`-t futtat).
  - Beállítások: `wwwroot/js/firebase-config.js` (`window.zsebFirebaseConfig`, null = csak helyi tárolás). Szabályok: `firebase/firestore.rules` (a Firebase konzolban kell közzétenni).
  - Felhőben: `households/{hid}` (owner, memberEmails), `households/{hid}/{kulcs}/{id}` = `{ j: elem JSON }`, `memberIndex/{email}` = `{ hid }`.
  - `AppData.SaveXAsync` → localStorage + `zseb.sync.push` (a JS csak a .NET által legutóbb látott állapothoz képesti változást írja ki); a felhőből jövő adat: `SyncService.OnRemoteData` → `AppData.ApplyRemote` (+ `Changed` esemény; az oldalak a `DataPage` alaposztállyal frissülnek).
  - Felület: `Shared/SyncPanel.razor` a Beállítások tetején; a teendőknél `TodoItem.CreatedBy` mutatja, ki vette fel.
- JSON: forrásgenerált `AppJsonContext` – új modelltípust ott is fel kell venni. Régi mentésekkel visszafelé kompatibilisnek kell maradni (új mezők legyenek opcionálisak / alapértékesek).
- JavaScript-segédek: `wwwroot/js/zseb.js` (fájlmentés/megosztás, hangfelismerés „vége” záró szóval, húzásos lapozás, nagyítás tiltása).
- Stílus: `wwwroot/css/app.css` – „Lagúna” téma, háttér: `wwwroot/img/hatter.jpg`, üveghatású kártyák, CSS-változók a fájl elején. Asztali böngészőben telefon-keretben jelenik meg (≥600 px).
- Offline: `service-worker.published.js` (a scope-ból számolja az alap-URL-t, almappában is működik).
- GitHub Pages: a workflow a build előtt átírja a `<base href>`-et a repó nevére.

## Fő képernyők
- Teendők (`/`) – felvétel dátuma, hangrögzítés („… vége” → automatikus mentés)
- Jegyzetek (`/jegyzetek`)
- Kiadások (`/kiadasok`)
- Számlák (`/szamlak`) – havi nézet, csoportok, fix tételek (`BillTemplate`, alapértékek: `BillDefaults`), fizetési határidő, Rendezetlen (piros, alapértelmezett) / Fizetve (zöld); Mérőórák (`/szamlak/merok`)
- Beállítások (`/beallitasok`) – mentés/visszaállítás JSON-ba, jelszóval védett teljes törlés; Riport (`/beallitasok/riport`) – kiadások + mérőóra-fogyasztás, CSV export
- Lebegő mikrofon gomb minden oldalon (a Teendők kivételével), fülváltás balra/jobbra húzással.

## Ellenőrzés módosítás után
- `dotnet build src/ZsebApp` – ha a NuGet nem érhető el, a Razor-komponensek egy Blazor-host nélküli `HtmlRenderer`-es tesztprojekttel is lefordíthatók és tesztelhetők.
- iPhone 11 nézet: 414×896, világos és sötét mód.
