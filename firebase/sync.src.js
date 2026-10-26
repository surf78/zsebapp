// ZsebApp – közös adattár (Firebase Auth + Firestore).
// Ebből készül a wwwroot/js/sync.bundle.js (npm run build). A .NET oldal a window.zseb.sync függvényeit hívja.
//
// Adatszerkezet:  households/{hid}                 { owner, memberEmails[], createdAt }
//                 households/{hid}/{kulcs}/{id}    { j: "<az elem JSON-ja>", by, at }
// kulcs: todos | notes | expenses | bills | billtemplates | meters | settings (ez utóbbinál id = "main").
//                 memberIndex/{email}              { hid } – ebből találja meg a belépő, melyik háztartás tagja.
//                 households/{hid}/smsinbox/{id}   { text, secret, at } – a Parancsok (iPhone) ide küldi a banki SMS-t
//                                                  REST-en, a háztartás smsSecret kulcsával; az app feldolgozza és törli.
//
// Szinkron: a .NET minden mentéskor elküldi a teljes listát (push). Csak azt írjuk ki, ami ahhoz képest változott,
// amit a .NET legutóbb látott ("seen") – így a másik telefonról közben érkezett módosításokat nem írjuk felül.
// A felhőből érkező változásokat az OnRemoteData(kulcs, json) adja át; az visszaadja a .NET-es (kanonikus) JSON-t.

import { initializeApp } from 'firebase/app';
import {
    initializeAuth, indexedDBLocalPersistence, browserLocalPersistence, onAuthStateChanged,
    signInWithEmailAndPassword, createUserWithEmailAndPassword, sendEmailVerification,
    sendPasswordResetEmail, updateProfile, signOut
} from 'firebase/auth';
import {
    initializeFirestore, persistentLocalCache, memoryLocalCache, collection, doc, query, where,
    getDoc, getDocs, addDoc, setDoc, deleteDoc, updateDoc, onSnapshot, writeBatch, serverTimestamp,
    arrayUnion, arrayRemove
} from 'firebase/firestore';

const KEYS = ['todos', 'notes', 'expenses', 'bills', 'billtemplates', 'meters', 'bank', 'settings'];
const HID_KEY = 'zseb.sync.hid';

const z = window.zseb = window.zseb || {};

const S = {
    dotnet: null, app: null, auth: null, db: null,
    user: null, hid: null, household: null,
    phase: 'off', error: null,
    unsubs: [], keys: {}, online: navigator.onLine
};

function email() { return ((S.user && S.user.email) || '').toLowerCase(); }
function displayName() {
    const u = S.user;
    if (!u) return '';
    return (u.displayName || '').trim() || (u.email || '').split('@')[0];
}
function lsGet(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }
function lsSet(k, v) { try { v === null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch (e) { } }

// ---------- Állapotjelzés a .NET felé ----------

function pending() { return KEYS.some(k => S.keys[k] && S.keys[k].pending); }
function synced() { return KEYS.every(k => S.keys[k] && S.keys[k].live); }

function report() {
    if (!S.dotnet) return;
    const h = S.household;
    const st = {
        configured: !!S.app,
        phase: S.phase,
        email: email(),
        name: displayName(),
        members: h ? (h.memberEmails || []) : [],
        isOwner: !!(h && S.user && h.owner === S.user.uid),
        online: S.online,
        pending: pending(),
        synced: S.phase === 'connected' && synced(),
        error: S.error,
        householdId: S.hid,
        smsSecret: h ? (h.smsSecret || null) : null,
        apiKey: S.app ? S.app.options.apiKey : null,
        projectId: S.app ? S.app.options.projectId : null
    };
    S.dotnet.invokeMethodAsync('OnSyncState', JSON.stringify(st)).catch(() => { });
}

function setPhase(phase, error) {
    S.phase = phase;
    S.error = error || null;
    report();
}

function errCode(e) { return (e && (e.code || e.message)) || String(e); }

// ---------- Elemek összehasonlítása ----------

// A .NET JSON-jából: [{ id, j }] – j az elem kanonikus JSON-ja.
function toEntries(key, json) {
    if (key === 'settings') {
        const o = JSON.parse(json || 'null');
        return o ? [{ id: 'main', j: JSON.stringify(o) }] : [];
    }
    return (JSON.parse(json || '[]') || []).map(it => ({ id: String(it.id), j: JSON.stringify(it) }));
}

function toMap(entries) {
    const m = new Map();
    entries.forEach(e => m.set(e.id, e.j));
    return m;
}

function sameMap(a, b) {
    if (a.size !== b.size) return false;
    for (const [k, v] of a) if (b.get(k) !== v) return false;
    return true;
}

// ---------- Kapcsolódás a háztartáshoz (listenerek) ----------

function stopListeners() {
    S.unsubs.forEach(u => { try { u(); } catch (e) { } });
    S.unsubs = [];
    S.keys = {};
    S.household = null;
}

function connect(hid) {
    stopListeners();
    S.hid = hid;
    S.inbox = new Map();
    lsSet(HID_KEY, hid);
    setPhase('connected');

    // Banki SMS-ek a Parancsokból: feldolgozzuk (Bank napló + Kiadások), majd töröljük a beérkezőt.
    S.unsubs.push(onSnapshot(collection(S.db, 'households', hid, 'smsinbox'), snap => {
        snap.docs.forEach(d => {
            const v = d.data();
            if (!S.inbox.has(d.id)) S.inbox.set(d.id, { text: String(v.text || ''), at: v.at ? String(v.at) : null });
        });
        processInbox();
    }, err => console.error('ZsebApp smsinbox', err)));

    const hRef = doc(S.db, 'households', hid);
    S.unsubs.push(onSnapshot(hRef, snap => {
        if (!snap.exists()) return;
        S.household = snap.data();
        report();
    }, err => onListenError(err)));

    KEYS.forEach(key => {
        // readyKey: ezen a telefonon ez a lista már legalább egyszer megjött a szerverről, így
        // a helyi gyorstár (akár üres) állapota is megbízható – offline indulásnál is.
        const readyKey = 'zseb.sync.ready.' + hid + '.' + key;
        const k = S.keys[key] = { seen: null, gotServer: lsGet(readyKey) === '1', pending: false };
        S.unsubs.push(onSnapshot(collection(S.db, 'households', hid, key), { includeMetadataChanges: true }, snap => {
            const fromCache = snap.metadata.fromCache;
            k.pending = snap.metadata.hasPendingWrites;
            k.live = !fromCache;
            // Új telefonon a helyi gyorstár eleinte üres – ezzel nem írjuk felül a meglévő adatokat.
            if (fromCache && snap.empty && !k.gotServer) { report(); return; }
            if (!fromCache && !k.gotServer) { k.gotServer = true; lsSet(readyKey, '1'); }

            const remote = new Map();
            snap.docs.forEach(d => { const j = d.data().j; if (typeof j === 'string') remote.set(d.id, j); });
            if (k.seen && sameMap(remote, k.seen)) { report(); return; }   // nincs új (pl. a saját írásunk visszhangja)

            let json;
            if (key === 'settings') json = remote.has('main') ? remote.get('main') : 'null';
            else json = '[' + Array.from(remote.values()).join(',') + ']';

            try {
                const canonical = S.dotnet.invokeMethod('OnRemoteData', key, json);
                k.seen = toMap(toEntries(key, canonical));
            } catch (e) {
                console.error('ZsebApp sync: OnRemoteData', key, e);
            }
            report();
            processInbox();
        }, err => onListenError(err)));
    });
}

// A beérkezett SMS-eket csak akkor dolgozzuk fel, ha a Bank és a Kiadások lista már megjött a felhőből
// (különben a .NET mentése nem jutna fel). Egyszerre egyet, sorban.
async function processInbox() {
    if (S.inboxBusy || !S.inbox || S.inbox.size === 0 || !S.hid) return;
    const ready = ['bank', 'expenses'].every(k => S.keys[k] && S.keys[k].seen);
    if (!ready) return;
    S.inboxBusy = true;
    try {
        for (const [id, v] of Array.from(S.inbox.entries())) {
            if (v.done) continue;
            const hid = S.hid;
            try {
                const ok = await S.dotnet.invokeMethodAsync('OnSmsInbox', id, v.text, v.at);
                if (ok && hid === S.hid) {
                    v.done = true;
                    deleteDoc(doc(S.db, 'households', hid, 'smsinbox', id)).catch(e => console.error('ZsebApp smsinbox törlés', e));
                }
            } catch (e) {
                console.error('ZsebApp OnSmsInbox', e);
            }
        }
    } finally {
        S.inboxBusy = false;
    }
}

function onListenError(err) {
    console.error('ZsebApp sync', err);
    if (err && err.code === 'permission-denied') {
        // Kivettek a háztartásból (vagy a szabályok nem engedik): leválunk, a helyi adatok megmaradnak.
        stopListeners();
        lsSet(HID_KEY, null);
        S.hid = null;
        setPhase('noHousehold', 'permission-denied');
    } else {
        S.error = errCode(err);
        report();
    }
}

async function findHousehold() {
    setPhase('loading');
    try {
        // 1. A tagsági index (memberIndex/{email}) – ezt a szabályok biztosan engedik olvasni.
        let hid = null;
        const idx = await getDoc(doc(S.db, 'memberIndex', email()));
        if (idx.exists()) hid = idx.data().hid;
        // 2. Tartalék: keresés a háztartások között.
        if (!hid) {
            try {
                const res = await getDocs(query(collection(S.db, 'households'), where('memberEmails', 'array-contains', email())));
                const docs = res.docs.slice().sort((a, b) => {
                    const ta = a.data().createdAt, tb = b.data().createdAt;
                    return (ta ? ta.toMillis() : 0) - (tb ? tb.toMillis() : 0);
                });
                if (docs.length) hid = docs[0].id;
            } catch (e2) { /* a szabályok nem engedik a keresést – nem baj */ }
        }
        if (!hid) {
            lsSet(HID_KEY, null);
            setPhase('noHousehold');
            return;
        }
        connect(hid);
    } catch (e) {
        // Offline indulásnál a korábban megjegyzett háztartáshoz kapcsolódunk.
        const cached = lsGet(HID_KEY);
        if (cached) connect(cached);
        else setPhase('error', errCode(e));
    }
}

async function onUser(user) {
    S.user = user;
    if (!user) {
        stopListeners();
        S.hid = null;
        setPhase('signedOut');
        return;
    }
    if (!user.emailVerified) {
        stopListeners();
        setPhase('verify');
        return;
    }
    const cached = lsGet(HID_KEY);
    if (cached) {
        connect(cached);           // azonnal (offline is), a listenerek jelzik, ha már nem vagyunk tagok
    } else {
        await findHousehold();
    }
}

// ---------- A .NET által hívott függvények ----------

z.sync = {
    init: function (dotnet) {
        S.dotnet = dotnet;
        const cfg = window.zsebFirebaseConfig;
        if (!cfg || !cfg.apiKey) { setPhase('off'); return false; }
        if (S.app) { report(); return true; }

        S.app = initializeApp(cfg);
        S.auth = initializeAuth(S.app, { persistence: [indexedDBLocalPersistence, browserLocalPersistence] });
        S.auth.languageCode = 'hu';
        try {
            S.db = initializeFirestore(S.app, { localCache: persistentLocalCache() });
        } catch (e) {
            S.db = initializeFirestore(S.app, { localCache: memoryLocalCache() });
        }
        window.addEventListener('online', () => { S.online = true; report(); });
        window.addEventListener('offline', () => { S.online = false; report(); });
        setPhase('loading');
        onAuthStateChanged(S.auth, u => { onUser(u); });
        return true;
    },

    signIn: async function (mail, password) {
        try {
            S.error = null;
            await signInWithEmailAndPassword(S.auth, (mail || '').trim(), password || '');
            return null;
        } catch (e) { return errCode(e); }
    },

    register: async function (name, mail, password) {
        try {
            S.error = null;
            const cred = await createUserWithEmailAndPassword(S.auth, (mail || '').trim(), password || '');
            if (name && name.trim()) await updateProfile(cred.user, { displayName: name.trim() });
            await sendEmailVerification(cred.user);
            S.user = cred.user;
            setPhase('verify');
            return null;
        } catch (e) { return errCode(e); }
    },

    resendVerification: async function () {
        try { await sendEmailVerification(S.auth.currentUser); return null; }
        catch (e) { return errCode(e); }
    },

    checkVerified: async function () {
        try {
            const u = S.auth.currentUser;
            if (!u) return 'auth/no-current-user';
            await u.reload();
            if (!u.emailVerified) return 'not-verified';
            await u.getIdToken(true);       // az új token már tartalmazza a megerősítést
            await onUser(u);
            return null;
        } catch (e) { return errCode(e); }
    },

    resetPassword: async function (mail) {
        try { await sendPasswordResetEmail(S.auth, (mail || '').trim()); return null; }
        catch (e) { return errCode(e); }
    },

    signOut: async function () {
        stopListeners();
        lsSet(HID_KEY, null);
        S.hid = null;
        try { await signOut(S.auth); } catch (e) { }
        setPhase('signedOut');
    },

    findHousehold: async function () { await findHousehold(); },

    // A Parancsokhoz tartozó titkos kulcs beállítása (null = kikapcsolás).
    setSmsSecret: async function (secret) {
        if (!S.hid) return 'invalid';
        try { await updateDoc(doc(S.db, 'households', S.hid), { smsSecret: secret || null }); return null; }
        catch (e) { return errCode(e); }
    },

    // Próba: ugyanúgy küld be egy SMS-t, ahogy a Parancsok fogja (REST, API-kulcs + titkos kulcs, bejelentkezés nélkül).
    sendTestSms: async function (text) {
        const h = S.household;
        if (!S.hid || !h || !h.smsSecret) return 'no-secret';
        const url = 'https://firestore.googleapis.com/v1/projects/' + S.app.options.projectId +
            '/databases/(default)/documents/households/' + S.hid + '/smsinbox?key=' + S.app.options.apiKey;
        try {
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fields: {
                    text: { stringValue: text },
                    secret: { stringValue: h.smsSecret },
                    at: { stringValue: new Date().toISOString() }
                } })
            });
            if (!res.ok) return 'http-' + res.status;
            return null;
        } catch (e) { return errCode(e); }
    },

    // Közös tár létrehozása, a telefonon lévő adatok feltöltésével.
    createHousehold: async function () {
        try {
            if (!navigator.onLine) return 'offline';
            const ref = await addDoc(collection(S.db, 'households'), {
                owner: S.user.uid, memberEmails: [email()], createdAt: serverTimestamp()
            });
            await setDoc(doc(S.db, 'memberIndex', email()), { hid: ref.id });
            const all = JSON.parse(S.dotnet.invokeMethod('ExportAll'));
            await writeEntries(ref.id, all);
            connect(ref.id);
            return null;
        } catch (e) { return errCode(e); }
    },

    addMember: async function (mail) {
        mail = (mail || '').trim().toLowerCase();
        if (!mail || !S.hid) return 'invalid';
        try {
            await updateDoc(doc(S.db, 'households', S.hid), { memberEmails: arrayUnion(mail) });
            await setDoc(doc(S.db, 'memberIndex', mail), { hid: S.hid });
            return null;
        } catch (e) { return errCode(e); }
    },

    removeMember: async function (mail) {
        if (!S.hid) return 'invalid';
        try {
            await deleteDoc(doc(S.db, 'memberIndex', mail));     // előbb ezt: utána már nem lennénk jogosultak
            await updateDoc(doc(S.db, 'households', S.hid), { memberEmails: arrayRemove(mail) });
            return null;
        } catch (e) { return errCode(e); }
    },

    // A .NET mentéskor hívja. Ha nincs kapcsolat, nem csinál semmit (a helyi mentés megtörtént).
    push: function (key, json) {
        if (S.phase !== 'connected' || !S.hid) return;
        const k = S.keys[key];
        if (!k || !k.seen) return;      // még nem jött meg a felhős állapot – nem írunk vakon
        const entries = toEntries(key, json);
        const now = toMap(entries);
        const ops = [];
        entries.forEach(e => { if (k.seen.get(e.id) !== e.j) ops.push({ id: e.id, j: e.j }); });
        k.seen.forEach((_, id) => { if (!now.has(id)) ops.push({ id: id, del: true }); });
        k.seen = now;
        if (ops.length === 0) return;
        commitOps(S.hid, key, ops);
    }
};

function commitOps(hid, key, ops) {
    // Offline a commit csak visszakapcsolódáskor teljesül – nem várjuk meg, a Firestore sorba állítja.
    for (let i = 0; i < ops.length; i += 400) {
        const batch = writeBatch(S.db);
        ops.slice(i, i + 400).forEach(op => {
            const ref = doc(S.db, 'households', hid, key, op.id);
            if (op.del) batch.delete(ref);
            else batch.set(ref, { j: op.j, by: email(), at: serverTimestamp() });
        });
        batch.commit().catch(e => { S.error = errCode(e); report(); });
    }
}

async function writeEntries(hid, all) {
    const waits = [];
    for (const key of KEYS) {
        if (!(key in all)) continue;
        const ops = toEntries(key, all[key]).map(e => ({ id: e.id, j: e.j }));
        for (let i = 0; i < ops.length; i += 400) {
            const batch = writeBatch(S.db);
            ops.slice(i, i + 400).forEach(op =>
                batch.set(doc(S.db, 'households', hid, key, op.id), { j: op.j, by: email(), at: serverTimestamp() }));
            waits.push(batch.commit());
        }
    }
    await Promise.all(waits);
}
