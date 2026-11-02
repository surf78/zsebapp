/**
 * ZsebApp – K&H bankforgalom az Enable Bankingen át (Google Apps Script).
 *
 * Mit csinál: naponta 4× (7, 12, 17, 22 óra körül) lekéri a K&H számla könyvelt tételeit az Enable Banking
 * API-ján (PSD2) keresztül, és az újakat beküldi a ZsebApp közös tárába (ugyanoda, ahová a Parancsok
 * az SMS-eket küldte). A ZsebApp a következő megnyitáskor feldolgozza őket: Bank napló + terhelésnél Kiadás.
 *
 * Beállítás: a ZsebApp → Beállítások → Bank kapcsolat (K&H) leírása szerint.
 * Ezt a fájlt nem kell szerkeszteni – minden adatot a webalkalmazás oldalán kell megadni.
 *
 * A titkos kulcs (PEM) csak ebben a Google-projektben (Script Properties) tárolódik.
 */

const API = 'https://api.enablebanking.com';
const ELSO_NAPOK = 7;          // az első lekérésnél ennyi napra visszamenőleg
const VISSZA_NAPOK = 5;        // utána mindig ennyi napot nézünk vissza (a későn könyvelt tételek miatt)
const ORAK = [7, 12, 17, 22];  // a bank háttér-lekérésnél napi 4-et enged

// ---------------------------------------------------------------- webalkalmazás

function doGet(e) {
  const p = (e && e.parameter) || {};
  let uzenet = '';
  if (p.error) {
    uzenet = hibaDoboz('A bank / Enable Banking nem engedélyezte: ' + (p.error_description || p.error));
  } else if (p.code) {
    try {
      uzenet = okDoboz(munkamenetLetrehozas_(p.code, p.state));
    } catch (err) {
      uzenet = hibaDoboz(String(err.message || err));
    }
  }
  return HtmlService.createHtmlOutput(oldal_(uzenet))
    .setTitle('ZsebApp – Bank kapcsolat')
    .addMetaTag('viewport', 'width=device-width, initial-scale=1');
}

/** A beállító űrlap mentése (a weboldal hívja). Az üresen hagyott mező a régi értéket megtartja. */
function mentesBeallitas(f) {
  const props = PropertiesService.getScriptProperties();
  const set = (k, v) => { if (v !== undefined && v !== null && String(v).trim() !== '') props.setProperty(k, String(v).trim()); };
  set('APP_ID', f.appId);
  if (f.key && f.key.trim()) {
    const key = f.key.replace(/\\n/g, '\n').trim();
    if (key.indexOf('BEGIN RSA PRIVATE KEY') >= 0) {
      throw new Error('Ez PKCS#1 kulcs („BEGIN RSA PRIVATE KEY”). PKCS#8 kell („BEGIN PRIVATE KEY”): ' +
        'openssl pkcs8 -topk8 -nocrypt -in kulcs.pem -out kulcs8.pem');
    }
    if (key.indexOf('BEGIN PRIVATE KEY') < 0) throw new Error('A kulcs nem PEM formátumú (a .pem fájl teljes tartalma kell, a BEGIN/END sorokkal).');
    props.setProperty('PRIVATE_KEY', key);
  }
  set('INBOX_URL', f.inboxUrl);
  set('SECRET', f.secret);
  props.setProperty('IBAN', (f.iban || '').replace(/\s/g, '').toUpperCase());
  // Ellenőrzés: aláírható-e a kulccsal, és elfogadja-e az Enable Banking.
  const app = api_('get', '/application');
  return 'Mentve. Enable Banking alkalmazás: ' + (app.name || '') + ' (' + (app.environment || '') + ', ' +
    (app.active ? 'aktív' : 'még nem aktív – a Control Panelben kapcsold össze a számlát') + ').';
}

/** Az összekapcsolás indítása: visszaadja a bank jóváhagyó oldalának címét (a weboldal megnyitja). */
function kapcsolatInditas() {
  kotelezo_(['APP_ID', 'PRIVATE_KEY']);
  const props = PropertiesService.getScriptProperties();
  const aspsp = khBank_();
  const maxSec = aspsp.maximum_consent_validity || 180 * 86400;
  const validUntil = new Date(Date.now() + Math.min(maxSec, 180 * 86400) * 1000 - 3600 * 1000);
  const state = Utilities.getUuid();
  props.setProperty('STATE', state);

  const body = {
    access: { valid_until: validUntil.toISOString() },
    aspsp: { name: aspsp.name, country: aspsp.country },
    state: state,
    redirect_url: webappUrl_(),
    psu_type: 'personal',
    language: 'hu',
  };
  // A K&H a bejelentkezés előtt a számla IBAN-ját kéri – ha megadtad, előre kitöltjük.
  const methods = (aspsp.auth_methods || []).filter(m => !m.psu_type || m.psu_type === 'personal');
  const method = methods[0];
  if (method) {
    if (method.name) body.auth_method = method.name;
    const iban = props.getProperty('IBAN');
    const cred = (method.credentials || []).find(c => /iban/i.test(c.name || '') || /iban|számlaszám|account/i.test(c.title || ''));
    if (iban && cred) {
      body.credentials = {};
      body.credentials[cred.name] = iban;
      body.credentials_autosubmit = false;
    }
  }
  const r = api_('post', '/auth', body);
  return r.url;
}

/** Lekérés azonnal (a weboldal gombja). psu = { ip, ua } a böngészőből: így a bank „jelen lévő felhasználós”
 *  lekérésnek veszi, és nem számít bele a napi 4 háttér-lekérésbe. */
function lekeresMost(psu) {
  return lekeres_(psuOk_(psu));
}

/** A napi 4 időzítés bekapcsolása (a régieket törli). */
function idozitesBe() {
  ScriptApp.getProjectTriggers()
    .filter(t => ['lekeres', 'keresFigyeles'].indexOf(t.getHandlerFunction()) >= 0)
    .forEach(t => ScriptApp.deleteTrigger(t));
  ORAK.forEach(h => ScriptApp.newTrigger('lekeres').timeBased().atHour(h).nearMinute(10).everyDays(1).create());
  // A ZsebApp „Lekérés most” gombja: percenként megnézzük, kért-e lekérést az app.
  ScriptApp.newTrigger('keresFigyeles').timeBased().everyMinutes(1).create();
  return 'Időzítés bekapcsolva: naponta ' + ORAK.join(', ') + ' óra körül, és percenként figyeli a ZsebApp „Lekérés most” gombját (időzóna: ' +
    Session.getScriptTimeZone() + ').';
}

/** Csak a ZsebApp gombja: nincs napi automatikus lekérés, a bank napi keretét te használod el a gombbal. */
function csakGomb() {
  ScriptApp.getProjectTriggers()
    .filter(t => ['lekeres', 'keresFigyeles'].indexOf(t.getHandlerFunction()) >= 0)
    .forEach(t => ScriptApp.deleteTrigger(t));
  ScriptApp.newTrigger('keresFigyeles').timeBased().everyMinutes(1).create();
  return 'Automatikus lekérés kikapcsolva. A ZsebApp „Lekérés a banktól most” gombja működik (percenként figyeli).';
}

// ---------------------------------------------------------------- a ZsebApp gombja (percenként)

/**
 * A ZsebApp „Lekérés most” gombja a households/{hid}/ebreq/{titkos kulcs} dokumentumot írja (state = requested).
 * Ez a függvény percenként fut: ha van kérés, lefuttatja a lekérést, és az eredményt visszaírja (az app kiírja).
 */
function keresFigyeles() {
  const props = PropertiesService.getScriptProperties();
  if (!props.getProperty('INBOX_URL') || !props.getProperty('SECRET')) return;
  let url;
  try {
    url = keresUrl_();
    const res = UrlFetchApp.fetch(url, { muteHttpExceptions: true });
    if (res.getResponseCode() !== 200) return;          // nincs kérés (404) vagy nem elérhető
    const f = (JSON.parse(res.getContentText()).fields) || {};
    const state = f.state && f.state.stringValue;
    const at = f.at && f.at.stringValue ? new Date(f.at.stringValue).getTime() : 0;
    // Új kérés, vagy egy 5 percnél régebben elakadt.
    if (!(state === 'requested' || (state === 'running' && Date.now() - at > 5 * 60000))) return;
    // Az app a kéréssel a telefon IP-címét és böngészőjét is küldi (msg = {"psu":{…}}): ezzel a bank
    // „jelen lévő felhasználós” lekérésnek veszi, és nem számít bele a napi 4 háttér-lekérésbe.
    let psu = null;
    try { psu = psuOk_(JSON.parse((f.msg && f.msg.stringValue) || '{}').psu); } catch (e) { }
    keresAllapot_(url, 'running', 'Lekérés folyamatban…');
    const r = lekeres_(psu);
    keresAllapot_(url, String(r).indexOf('Hiba') === 0 ? 'error' : 'done', String(r));
  } catch (err) {
    try { if (url) keresAllapot_(url, 'error', String(err.message || err)); } catch (e) { }
  }
}

function keresUrl_() {
  const props = PropertiesService.getScriptProperties();
  return props.getProperty('INBOX_URL').replace('/smsinbox?', '/ebreq/' + encodeURIComponent(props.getProperty('SECRET')) + '?');
}

function keresAllapot_(url, state, msg) {
  const mask = ['state', 'msg', 'done'].map(k => '&updateMask.fieldPaths=' + k).join('');
  UrlFetchApp.fetch(url + mask, {
    method: 'patch',
    contentType: 'application/json',
    muteHttpExceptions: true,
    payload: JSON.stringify({ fields: {
      state: { stringValue: state },
      msg: { stringValue: msg.slice(0, 400) },
      done: { stringValue: new Date().toISOString() },
    } }),
  });
}

// ---------------------------------------------------------------- lekérés (időzítve is ez fut)

/** Az időzített (háttér-) lekérés. Az időzítő egy eseményobjektumot ad át – azt nem használjuk. */
function lekeres() {
  return lekeres_(null);
}

let PSU_ = null;   // a futó lekérés PSU-fejlécei (ha a felhasználó indította)

function psuOk_(p) {
  return p && typeof p.ip === 'string' && /^[0-9a-fA-F:.]{3,45}$/.test(p.ip)
    ? { ip: p.ip, ua: String(p.ua || '').slice(0, 300) } : null;
}

function lekeres_(psu) {
  PSU_ = psu || null;
  const lock = LockService.getScriptLock();
  if (!lock.tryLock(30000)) return 'Egy másik lekérés épp fut.';
  const props = PropertiesService.getScriptProperties();
  try {
    kotelezo_(['APP_ID', 'PRIVATE_KEY', 'INBOX_URL', 'SECRET', 'SESSION_ID', 'ACCOUNTS']);
    const validUntil = props.getProperty('VALID_UNTIL');
    if (validUntil && new Date(validUntil).getTime() < Date.now()) {
      throw new Error('Lejárt a banki hozzáférés (' + validUntil.slice(0, 10) + '). Nyisd meg a webalkalmazást, és kapcsold össze újra a K&H-val.');
    }

    const elso = !props.getProperty('LAST_OK');
    const from = napStr_(new Date(Date.now() - (elso ? ELSO_NAPOK : VISSZA_NAPOK) * 86400000));
    let kuldott = 0, osszes = 0;

    JSON.parse(props.getProperty('ACCOUNTS')).forEach(acc => {
      const raw = tranzakciok_(acc.uid, from).filter(t => !t.status || t.status === 'BOOK');
      const withBal = raw.filter(t => t.balance_after_transaction && t.balance_after_transaction.amount !== undefined)
        .sort((a, b) => String(b.booking_date || '').localeCompare(String(a.booking_date || '')))[0];
      const lastBal = withBal ? { amount: String(withBal.balance_after_transaction.amount),
        currency: withBal.balance_after_transaction.currency || 'HUF', type: 'tétel utáni' } : null;
      const txs = raw
        .map(t => tetel_(acc, t))
        .filter(t => t)
        .sort((a, b) => (a.d < b.d ? -1 : a.d > b.d ? 1 : 0));
      osszes += txs.length;
      const ujak = txs.filter(t => !elkuldve_(t.d, t.k));
      // Az aktuális egyenleg külön üzenetben megy (a ZsebApp „Felhasználható összeg” kártyája).
      // Háttér-lekérésnél nem kérjük külön (az is a napi 4-be számítana): ilyenkor a legutolsó tétel utáni egyenleg megy, ha van.
      let bal = null;
      if (PSU_) bal = egyenleg_(acc.uid);
      if (bal === null && lastBal) bal = lastBal;   // a K&H nem mindig ad egyenleget – a tétel utáni egyenleg, ha van
      if (bal !== null) egyenlegBekuld_(acc, bal);
      ujak.forEach(t => {
        bekuld_(t);
        jelol_(t.d, t.k);
        kuldott++;
      });
    });

    takarit_();
    const msg = new Date().toLocaleString('hu-HU') + ': ' + kuldott + ' új tétel beküldve (' + osszes + ' könyvelt tétel ' + from + ' óta' +
      (PSU_ ? ', felhasználói lekérés' : ', háttér-lekérés') + ').';
    props.setProperty('LAST_OK', new Date().toISOString());
    props.setProperty('LAST_RESULT', msg);
    props.deleteProperty('LAST_ERROR');
    lejaratFigyeles_();
    return msg;
  } catch (err) {
    const msg = new Date().toLocaleString('hu-HU') + ': ' + String(err.message || err);
    props.setProperty('LAST_ERROR', msg);
    ertesit_('ZsebApp bank: hiba a lekérésnél', msg + '\n\nWebalkalmazás: ' + webappUrl_());
    return 'Hiba: ' + msg;
  } finally {
    lock.releaseLock();
  }
}

// ---------------------------------------------------------------- Enable Banking

function munkamenetLetrehozas_(code, state) {
  const props = PropertiesService.getScriptProperties();
  if (!state || state !== props.getProperty('STATE')) throw new Error('Érvénytelen visszatérés (state) – indítsd újra az összekapcsolást.');
  const s = api_('post', '/sessions', { code: code });
  const wanted = (props.getProperty('IBAN') || '').toUpperCase();
  let accounts = (s.accounts || []).map(a => ({
    uid: a.uid,
    iban: ((a.account_id && a.account_id.iban) || '').toUpperCase(),
    name: a.name || a.product || '',
  }));
  if (wanted) {
    const only = accounts.filter(a => a.iban === wanted);
    if (only.length) accounts = only;
  }
  if (!accounts.length) throw new Error('A bank nem adott hozzáférést egyetlen számlához sem.');
  props.setProperty('SESSION_ID', s.session_id);
  props.setProperty('ACCOUNTS', JSON.stringify(accounts));
  props.setProperty('VALID_UNTIL', (s.access && s.access.valid_until) || '');
  props.deleteProperty('STATE');
  props.deleteProperty('WARNED_DAY');
  const elso = lekeres();
  return 'Összekapcsolva: ' + accounts.map(a => a.iban || a.name || a.uid).join(', ') +
    '. A hozzáférés érvényes: ' + ((s.access && s.access.valid_until) || '?').slice(0, 10) + '. ' + elso;
}

function khBank_() {
  const r = api_('get', '/aspsps?country=HU');
  const list = r.aspsps || [];
  const kh = list.find(a => /^k\s*&\s*h/i.test(a.name)) || list.find(a => /k&h|k and h|kereskedelmi és hitel/i.test(a.name));
  if (!kh) throw new Error('A K&H nem található az Enable Banking magyar bankjai között. Elérhetők: ' + list.map(a => a.name).join(', '));
  return kh;
}

function tranzakciok_(uid, from) {
  const all = [];
  let cont = null;
  for (let i = 0; i < 20; i++) {
    let path = '/accounts/' + encodeURIComponent(uid) + '/transactions?date_from=' + from;
    if (cont) path += '&continuation_key=' + encodeURIComponent(cont);
    const r = api_('get', path);
    (r.transactions || []).forEach(t => all.push(t));
    cont = r.continuation_key;
    if (!cont) break;
  }
  return all;
}

/** A számla egyenlege: { amount, currency, type } vagy null. Az eredményt / hibát a webalkalmazás oldalán is mutatjuk. */
function egyenleg_(uid) {
  const props = PropertiesService.getScriptProperties();
  const stamp = new Date().toLocaleString('hu-HU') + ': ';
  try {
    const r = api_('get', '/accounts/' + encodeURIComponent(uid) + '/balances');
    const list = (r.balances || []).filter(x => x.balance_amount && x.balance_amount.amount !== undefined);
    let b = null;
    for (const type of ['ITAV', 'CLAV', 'XPCD', 'ITBD', 'CLBD', 'OPAV', 'FWAV']) {
      b = list.find(x => x.balance_type === type);
      if (b) break;
    }
    if (!b) b = list[0] || null;
    props.setProperty('LAST_BALANCE', stamp + (b
      ? b.balance_amount.amount + ' ' + (b.balance_amount.currency || '') + ' (' + (b.balance_type || '?') + ')'
      : 'a bank nem adott egyenleget (' + list.length + ' tétel: ' + (r.balances || []).map(x => x.balance_type).join(', ') + ')'));
    return b ? { amount: String(b.balance_amount.amount), currency: b.balance_amount.currency || 'HUF', type: b.balance_type || '' } : null;
  } catch (err) {
    props.setProperty('LAST_BALANCE', stamp + 'hiba: ' + String(err.message || err).slice(0, 300));
    return null;   // az egyenleg nem kötelező
  }
}

/** Az egyenleg külön üzenetben (minden lekérésnél, akkor is, ha nincs új tétel): EB1B {b, c, t, at}. */
function egyenlegBekuld_(acc, bal) {
  const props = PropertiesService.getScriptProperties();
  const body = { b: bal.amount, c: bal.currency, t: bal.type, at: new Date().toISOString(), i: (acc.iban || '').slice(-4) };
  const url = props.getProperty('INBOX_URL') + '&documentId=ebbal_' + hash_(acc.uid + '|' + body.at).slice(0, 24);
  const res = UrlFetchApp.fetch(url, {
    method: 'post',
    contentType: 'application/json',
    muteHttpExceptions: true,
    payload: JSON.stringify({ fields: {
      text: { stringValue: 'EB1B ' + JSON.stringify(body) },
      secret: { stringValue: props.getProperty('SECRET') },
      at: { stringValue: body.at },
    } }),
  });
  if (res.getResponseCode() >= 300) throw new Error('A ZsebApp nem fogadta el az egyenleget (' + res.getResponseCode() + ').');
}

/** Az Enable Banking tranzakcióból a ZsebAppnak küldött rövid tétel. */
function tetel_(acc, t) {
  const amt = t.transaction_amount || {};
  if (amt.amount === undefined || amt.amount === null) return null;
  const debit = t.credit_debit_indicator === 'DBIT';
  const party = debit ? t.creditor : t.debtor;
  const rem = (t.remittance_information || []).join(' ').replace(/\s+/g, ' ').trim();
  const d = t.transaction_date || t.booking_date || t.value_date;
  if (!d) return null;
  const raw = t.entry_reference || t.transaction_id ||
    [d, amt.amount, t.credit_debit_indicator, party && party.name, rem].join('|');
  return {
    k: hash_(acc.uid + '|' + raw).slice(0, 32),
    t: debit ? 'D' : t.credit_debit_indicator === 'CRDT' ? 'C' : '',
    a: String(amt.amount),
    c: amt.currency || 'HUF',
    d: String(d).slice(0, 10),
    n: ((party && party.name) || '').slice(0, 100),
    r: rem.slice(0, 300),
  };
}

function api_(method, path, body) {
  const opt = {
    method: method,
    muteHttpExceptions: true,
    headers: { Authorization: 'Bearer ' + jwt_() },
  };
  if (PSU_) {
    opt.headers['Psu-Ip-Address'] = PSU_.ip;
    if (PSU_.ua) opt.headers['Psu-User-Agent'] = PSU_.ua;
    opt.headers['Psu-Accept-Language'] = 'hu-HU';
  }
  if (body) {
    opt.contentType = 'application/json';
    opt.payload = JSON.stringify(body);
  }
  const res = UrlFetchApp.fetch(API + path, opt);
  const code = res.getResponseCode();
  const text = res.getContentText();
  if (code >= 300) {
    let detail = text.slice(0, 400);
    try { const j = JSON.parse(text); detail = j.message || j.detail || j.error || detail; } catch (e) { }
    if (code === 401 || code === 403) {
      detail += ' – ha a hozzáférés lejárt vagy visszavonták, kapcsold össze újra a webalkalmazásban.';
    }
    if (code === 429) detail += ' – túl sok lekérés (a bank naponta 4-et enged a háttérben), később újra próbálja.';
    throw new Error('Enable Banking ' + code + ': ' + (typeof detail === 'string' ? detail : JSON.stringify(detail)));
  }
  return text ? JSON.parse(text) : {};
}

function jwt_() {
  const props = PropertiesService.getScriptProperties();
  const now = Math.floor(Date.now() / 1000);
  const enc = o => b64url_(Utilities.newBlob(JSON.stringify(o)).getBytes());
  const input = enc({ typ: 'JWT', alg: 'RS256', kid: props.getProperty('APP_ID') }) + '.' +
    enc({ iss: 'enablebanking.com', aud: 'api.enablebanking.com', iat: now, exp: now + 3600 });
  const sig = Utilities.computeRsaSha256Signature(input, props.getProperty('PRIVATE_KEY'));
  return input + '.' + b64url_(sig);
}

// ---------------------------------------------------------------- beküldés a ZsebAppnak

function bekuld_(t) {
  const props = PropertiesService.getScriptProperties();
  const url = props.getProperty('INBOX_URL') + '&documentId=eb_' + t.k;
  const text = 'EB1 ' + JSON.stringify(t);
  const res = UrlFetchApp.fetch(url, {
    method: 'post',
    contentType: 'application/json',
    muteHttpExceptions: true,
    payload: JSON.stringify({
      fields: {
        text: { stringValue: text },
        secret: { stringValue: props.getProperty('SECRET') },
        at: { stringValue: new Date().toISOString() },
      },
    }),
  });
  const code = res.getResponseCode();
  if (code === 409) return;   // már a beérkezőben vár
  if (code >= 300) {
    throw new Error('A ZsebApp nem fogadta el (' + code + '): ' + res.getContentText().slice(0, 300) +
      (code === 403 ? ' – ellenőrizd a beküldési címet és a kulcsot (ZsebApp → Beállítások → Bank kapcsolat).' : ''));
  }
}

// Melyik tételt küldtük már: napi bontásban (SENT_ÉÉÉÉ-HH-NN = kulcsok vesszővel), 20 napig.
function elkuldve_(day, k) {
  const v = PropertiesService.getScriptProperties().getProperty('SENT_' + day) || '';
  return v.split(',').indexOf(k) >= 0;
}

function jelol_(day, k) {
  const props = PropertiesService.getScriptProperties();
  const v = props.getProperty('SENT_' + day);
  props.setProperty('SENT_' + day, v ? v + ',' + k : k);
}

function takarit_() {
  const props = PropertiesService.getScriptProperties();
  const limit = napStr_(new Date(Date.now() - 20 * 86400000));
  Object.keys(props.getProperties())
    .filter(k => k.indexOf('SENT_') === 0 && k.slice(5) < limit)
    .forEach(k => props.deleteProperty(k));
}

// ---------------------------------------------------------------- segédek

function lejaratFigyeles_() {
  const props = PropertiesService.getScriptProperties();
  const vu = props.getProperty('VALID_UNTIL');
  if (!vu) return;
  const napok = Math.floor((new Date(vu).getTime() - Date.now()) / 86400000);
  if (napok <= 7) {
    ertesit_('ZsebApp bank: ' + napok + ' nap múlva lejár a K&H hozzáférés',
      'A banki hozzáférés ' + vu.slice(0, 10) + '-én lejár. Nyisd meg a webalkalmazást, és kapcsold össze újra:\n' + webappUrl_());
  }
}

/** E-mail a saját címedre – naponta legfeljebb egy. */
function ertesit_(subject, body) {
  const props = PropertiesService.getScriptProperties();
  const today = napStr_(new Date());
  if (props.getProperty('WARNED_DAY') === today) return;
  props.setProperty('WARNED_DAY', today);
  try {
    MailApp.sendEmail(Session.getEffectiveUser().getEmail(), subject, body);
  } catch (e) { }
}

function kotelezo_(keys) {
  const props = PropertiesService.getScriptProperties();
  const nevek = { APP_ID: 'Application ID', PRIVATE_KEY: 'titkos kulcs (.pem)', INBOX_URL: 'ZsebApp beküldési cím',
    SECRET: 'ZsebApp kulcs', SESSION_ID: 'K&H összekapcsolás', ACCOUNTS: 'K&H összekapcsolás' };
  const hiany = keys.filter(k => !props.getProperty(k)).map(k => nevek[k] || k);
  if (hiany.length) throw new Error('Hiányzik: ' + Array.from(new Set(hiany)).join(', ') + '.');
}

function webappUrl_() {
  return ScriptApp.getService().getUrl();
}

function napStr_(d) {
  return Utilities.formatDate(d, 'UTC', 'yyyy-MM-dd');
}

function b64url_(bytes) {
  return Utilities.base64EncodeWebSafe(bytes).replace(/=+$/, '');
}

function hash_(s) {
  return Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, s, Utilities.Charset.UTF_8)
    .map(b => ((b + 256) % 256).toString(16).padStart(2, '0')).join('');
}

function esc_(s) {
  return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

function okDoboz(s) { return '<p class="ok">' + esc_(s) + '</p>'; }
function hibaDoboz(s) { return '<p class="err">' + esc_(s) + '</p>'; }

function oldal_(uzenet) {
  const p = PropertiesService.getScriptProperties().getProperties();
  const kapcsolva = !!p.SESSION_ID;
  const triggers = ScriptApp.getProjectTriggers().map(t => t.getHandlerFunction());
  const idozitve = triggers.indexOf('lekeres') >= 0;
  const gomb = triggers.indexOf('keresFigyeles') >= 0;
  const sor = (cim, ertek) => '<div class="row"><span>' + cim + '</span><b>' + esc_(ertek) + '</b></div>';
  return `<!doctype html><html><head><meta charset="utf-8"><style>
    body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;max-width:640px;margin:0 auto;padding:16px;color:#123;background:#f3f8fa}
    h1{font-size:22px} h2{font-size:17px;margin-top:28px}
    .card{background:#fff;border-radius:12px;padding:16px;margin:12px 0;box-shadow:0 1px 3px #0002}
    label{display:block;font-weight:600;margin:12px 0 4px} input,textarea{width:100%;box-sizing:border-box;padding:10px;border:1px solid #bcd;border-radius:8px;font:inherit}
    textarea{height:110px;font-family:monospace;font-size:12px}
    button{background:#0a7f8f;color:#fff;border:0;border-radius:10px;padding:12px 16px;font:inherit;font-weight:600;margin:8px 8px 0 0;cursor:pointer}
    button.sec{background:#e3eef1;color:#0a5f6b} .note{color:#567;font-size:14px}
    .ok{background:#e5f4e4;padding:10px;border-radius:8px} .err{background:#fbebeb;padding:10px;border-radius:8px}
    .row{display:flex;justify-content:space-between;gap:12px;padding:4px 0;border-bottom:1px solid #eef} .row b{text-align:right;word-break:break-all}
    code{background:#eef4f6;padding:2px 4px;border-radius:4px;word-break:break-all}
  </style></head><body>
  <h1>ZsebApp – Bank kapcsolat (K&amp;H)</h1>
  ${uzenet}
  <div id="msg"></div>

  <div class="card">
    ${sor('Webalkalmazás címe (Redirect URL)', webappUrl_())}
    ${sor('Enable Banking alkalmazás', p.APP_ID ? 'megadva' : 'nincs megadva')}
    ${sor('Titkos kulcs (.pem)', p.PRIVATE_KEY ? 'megadva' : 'nincs megadva')}
    ${sor('ZsebApp beküldés', p.INBOX_URL && p.SECRET ? 'megadva' : 'nincs megadva')}
    ${sor('K&H összekapcsolás', kapcsolva ? 'érvényes eddig: ' + String(p.VALID_UNTIL || '?').slice(0, 10) : 'még nincs')}
    ${sor('Időzítés', idozitve ? 'bekapcsolva (' + ORAK.join(', ') + ' óra)' : 'kikapcsolva (csak kézi lekérés)')}
    ${sor('ZsebApp gomb (Lekérés most)', gomb ? 'figyeli (percenként)' : 'nincs bekapcsolva – nyomd meg az Időzítés bekapcsolása gombot')}
    ${sor('Utolsó lekérés', p.LAST_RESULT || '–')}
    ${sor('Utolsó egyenleg', p.LAST_BALANCE || '–')}
    ${p.LAST_ERROR ? '<p class="err">' + esc_(p.LAST_ERROR) + '</p>' : ''}
  </div>

  <h2>1. Beállítások</h2>
  <div class="card">
    <p class="note">A Redirect URL-hez az Enable Banking Control Panelben a fenti webalkalmazás-címet add meg. Az üresen hagyott mező a korábbi értéket megtartja.</p>
    <label>Application ID (Enable Banking)</label><input id="appId" placeholder="${p.APP_ID ? esc_(p.APP_ID) : 'pl. 0377783c-3414-…'}">
    <label>Titkos kulcs – a letöltött .pem fájl teljes tartalma</label><textarea id="key" placeholder="${p.PRIVATE_KEY ? '(megadva – csak cseréhez töltsd ki)' : '-----BEGIN PRIVATE KEY-----'}"></textarea>
    <label>ZsebApp beküldési cím (URL)</label><input id="inboxUrl" placeholder="${p.INBOX_URL ? '(megadva)' : 'https://firestore.googleapis.com/…/smsinbox?key=…'}">
    <label>ZsebApp kulcs</label><input id="secret" placeholder="${p.SECRET ? '(megadva)' : 'a ZsebApp Beállításaiból'}">
    <label>K&amp;H számla IBAN (nem kötelező – ha több számlád van, csak ezt figyeli)</label><input id="iban" value="${esc_(p.IBAN || '')}" placeholder="HU..">
    <button onclick="mentes()">Mentés és ellenőrzés</button>
  </div>

  <h2>2. Összekapcsolás a K&amp;H-val</h2>
  <div class="card">
    <p class="note">A bank jóváhagyó oldalára visz (K&amp;H mobilbank / SMS kód). Utána ide tér vissza. Kb. 180 naponként meg kell ismételni – előtte e-mailt kapsz.</p>
    <button onclick="kapcsolat()">${kapcsolva ? 'Újra összekapcsolás' : 'Összekapcsolás'}</button>
    <span id="authlink"></span>
  </div>

  <h2>3. Automatikus lekérés</h2>
  <div class="card">
    <p class="note">A bank a háttérben naponta 4 lekérést enged – ezért 4 időpont van. A „Lekérés most” (itt és a ZsebAppban) is beleszámít. Az időzítés bekapcsolása a ZsebApp gombját is bekapcsolja.</p>
    <button onclick="futtat('idozitesBe')">Időzítés bekapcsolása</button>
    <button class="sec" onclick="futtat('csakGomb')">Csak kézi lekérés (ZsebApp gomb)</button>
    <button class="sec" onclick="futtat('lekeresMost')">Lekérés most</button>
  </div>

  <script>
    const msg = (t, ok) => { document.getElementById('msg').innerHTML = '<p class="' + (ok ? 'ok' : 'err') + '"></p>'; document.querySelector('#msg p').textContent = t; window.scrollTo(0, 0); };
    const v = id => document.getElementById(id).value;
    function mentes() {
      msg('Mentés…', true);
      google.script.run.withSuccessHandler(r => msg(r + ' Az állapot frissítéséhez töltsd újra az oldalt.', true))
        .withFailureHandler(e => msg(e.message, false))
        .mentesBeallitas({ appId: v('appId'), key: v('key'), inboxUrl: v('inboxUrl'), secret: v('secret'), iban: v('iban') });
    }
    function kapcsolat() {
      msg('A bank oldalának előkészítése…', true);
      google.script.run.withSuccessHandler(url => {
        msg('Kész – koppints a gombra a K&H jóváhagyáshoz.', true);
        document.getElementById('authlink').innerHTML = '<a target="_top"><button>Tovább a K&amp;H-hoz →</button></a>';
        document.querySelector('#authlink a').href = url;
      }).withFailureHandler(e => msg(e.message, false)).kapcsolatInditas();
    }
    function futtat(fn) {
      msg('Folyamatban…', true);
      const run = arg => google.script.run.withSuccessHandler(r => msg(r, !String(r).startsWith('Hiba'))).withFailureHandler(e => msg(e.message, false))[fn](arg);
      if (fn !== 'lekeresMost') return run();
      // A böngésző IP-címe a PSU-fejléchez (így nem számít bele a bank napi 4 háttér-lekérésébe).
      fetch('https://api.ipify.org?format=json').then(r => r.json()).then(j => run({ ip: j.ip, ua: navigator.userAgent })).catch(() => run(null));
    }
  </script>
  </body></html>`;
}
