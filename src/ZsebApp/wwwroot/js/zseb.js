// Kis JavaScript-segédek, amelyeket a C# kód JS interopon keresztül hív.
window.zseb = {
    // iPhone-on a megosztási lapot nyitja meg ("Mentés a Fájlokba"), máshol letölti a fájlt.
    saveFile: async function (filename, text, mime) {
        mime = mime || 'application/json';
        // CSV-nél BOM, hogy az Excel UTF-8-ként (ékezetekkel) nyissa meg.
        if (mime.indexOf('text/csv') === 0) text = '\uFEFF' + text;
        const file = new File([text], filename, { type: mime });

        if (navigator.canShare && navigator.canShare({ files: [file] })) {
            try {
                await navigator.share({ files: [file], title: filename });
                return 'shared';
            } catch (e) {
                if (e && e.name === 'AbortError') return 'cancelled';
                // Egyéb hiba esetén (pl. lejárt felhasználói gesztus) letöltéssel próbálkozunk.
            }
        }

        const url = URL.createObjectURL(file);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 2000);
        return 'downloaded';
    },

    // Hangfelismerés (Web Speech API). A kattintást közvetlenül JS-ben kezeljük,
    // mert a Safari csak felhasználói érintésből indítja a mikrofont.
    // stopWord (pl. "vége"): ha a mondat ezzel a szóval végződik, a felvétel leáll és
    // az OnSpeechEnd(text, true) jelzi, hogy a szöveget menteni kell (a záró szó nélkül).
    speech: {
        _session: null,

        isSupported: function () {
            return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
        },

        // "holnap fogorvos vége." -> { text: "holnap fogorvos", hit: true }
        stripStopWord: function (text, stopWord) {
            if (!stopWord) return { text: text, hit: false };
            const re = new RegExp('^([\\s\\S]*?)(?:^|[\\s,.;:!?]+)' + stopWord + '[\\s.!?]*$', 'iu');
            const m = text.match(re);
            return m ? { text: m[1], hit: true } : { text: text, hit: false };
        },

        // Felvétel indítása / leállítása (érintésből kell hívni – a Safari csak így engedi a mikrofont).
        // iOS-en a folyamatos (continuous) mód gyakran nem ad vissza szöveget, ezért rövid,
        // egymás utáni felismeréseket indítunk, és a szöveget összefűzzük, amíg el nem hangzik
        // a záró szó ("vége"), meg nem állítják, vagy le nem jár az idő.
        // Állapotjelzés a .NET felé: OnSpeechStatus('audio' | 'sound' | 'speech' | 'no-result').
        toggle: function (dotnet, lang, stopWord) {
            const s = window.zseb.speech;
            if (s._session) {                                   // második koppintás: leállítás
                s._session.userStop = true;
                try { s._session.rec && s._session.rec.stop(); } catch (e) { }
                return;
            }

            const Rec = window.SpeechRecognition || window.webkitSpeechRecognition;
            if (!Rec) {
                dotnet.invokeMethodAsync('OnSpeechError', 'unsupported');
                return;
            }

            const sess = {
                rec: null, prefix: '', text: '', committed: false, userStop: false,
                gotResult: false, restarts: 0, lastError: null, started: Date.now(),
                watchdog: 0, noResultTimer: 0
            };
            s._session = sess;

            const status = function (kind) { dotnet.invokeMethodAsync('OnSpeechStatus', kind).catch(function () { }); };
            const clearTimers = function () { clearTimeout(sess.watchdog); clearTimeout(sess.noResultTimer); };
            const finish = function () {
                clearTimers();
                if (s._session === sess) s._session = null;
                // Semmilyen szöveg nem jött, és nem a felhasználó állította le: jelezzük, mi lehet a gond.
                if (!sess.gotResult && !sess.userStop) dotnet.invokeMethodAsync('OnSpeechError', 'no-result');
                dotnet.invokeMethodAsync('OnSpeechEnd', sess.text, sess.committed);
            };

            const startOne = function () {
                const rec = new Rec();
                rec.lang = lang;
                rec.interimResults = true;
                rec.continuous = false;
                rec.maxAlternatives = 1;
                sess.rec = rec;

                rec.onstart = function () { if (sess.restarts === 0) dotnet.invokeMethodAsync('OnSpeechStart'); };
                rec.onaudiostart = function () { if (!sess.gotResult) status('audio'); };
                rec.onsoundstart = function () { if (!sess.gotResult) status('sound'); };
                rec.onspeechstart = function () { if (!sess.gotResult) status('speech'); };
                rec.onresult = function (e) {
                    if (sess.committed) return;
                    // A teljes eredménylistából építjük újra (iOS-en így nincs duplázás), elé az eddigi szöveg.
                    const raw = Array.from(e.results).map(function (r) { return r[0].transcript; }).join(' ');
                    const full = (sess.prefix + ' ' + raw).trim();
                    const r = s.stripStopWord(full, stopWord);
                    sess.text = r.text;
                    sess.gotResult = true;
                    dotnet.invokeMethodAsync('OnSpeechResult', sess.text);
                    if (r.hit) {
                        sess.committed = true;
                        try { rec.stop(); } catch (err) { }
                    }
                };
                rec.onerror = function (e) { sess.lastError = e.error || 'unknown'; };
                rec.onend = function () {
                    if (s._session !== sess) return;            // közben megszakították
                    sess.prefix = sess.text;
                    const err = sess.lastError;
                    sess.lastError = null;
                    if (err && err !== 'no-speech' && err !== 'aborted') {
                        dotnet.invokeMethodAsync('OnSpeechError', err);
                        finish();
                        return;
                    }
                    // Szünet után újraindítjuk, amíg a "vége" el nem hangzik (max. 60 mp).
                    if (stopWord && !sess.committed && !sess.userStop &&
                        Date.now() - sess.started < 60000 && sess.restarts < 30) {
                        sess.restarts++;
                        try { startOne(); return; } catch (e2) { /* nem indult újra: befejezzük */ }
                    }
                    finish();
                };
                rec.start();
            };

            sess.watchdog = setTimeout(function () {
                sess.userStop = true;
                try { sess.rec && sess.rec.abort(); } catch (e) { }
            }, 65000);
            sess.noResultTimer = setTimeout(function () { if (!sess.gotResult && s._session === sess) status('no-result'); }, 7000);

            try {
                startOne();
            } catch (err) {
                clearTimers();
                s._session = null;
                dotnet.invokeMethodAsync('OnSpeechError', 'start-failed');
            }
        },

        attach: function (button, dotnet, lang, stopWord) {
            const handler = function () { window.zseb.speech.toggle(dotnet, lang, stopWord); };

            button.addEventListener('click', handler);
            button._zsebSpeechHandler = handler;
        },

        // Dupla koppintás a képernyőn (a Teendők oldalon) = mikrofon gomb.
        // Gombra, szövegmezőre, pipára és a hangpanelre koppintást nem számolunk.
        attachDoubleTap: function (dotnet, lang, stopWord) {
            const s = window.zseb.speech;
            s.detachDoubleTap();
            let lastTime = 0, lastX = 0, lastY = 0;
            const ignore = 'input, textarea, select, button, a, label, .chips, .voice-sheet, .tabbar, .voice-fab';
            const onTouchEnd = function (e) {
                if (e.changedTouches.length !== 1 || e.touches.length > 0) return;
                const t = e.target;
                if (t.closest && t.closest(ignore)) { lastTime = 0; return; }
                const p = e.changedTouches[0];
                const now = Date.now();
                const near = Math.abs(p.clientX - lastX) < 40 && Math.abs(p.clientY - lastY) < 40;
                if (now - lastTime < 350 && near) {
                    lastTime = 0;
                    e.preventDefault();                       // ne legyen belőle kattintás / nagyítás
                    s.toggle(dotnet, lang, stopWord);         // érintésből indul -> a Safari engedi
                    return;
                }
                lastTime = now; lastX = p.clientX; lastY = p.clientY;
            };
            const onDblClick = function (e) {                 // asztali böngészőben egérrel
                const t = e.target;
                if (t.closest && t.closest(ignore)) return;
                s.toggle(dotnet, lang, stopWord);
            };
            document.addEventListener('touchend', onTouchEnd, { passive: false });
            document.addEventListener('dblclick', onDblClick);
            s._dbl = { onTouchEnd: onTouchEnd, onDblClick: onDblClick };
        },

        detachDoubleTap: function () {
            const s = window.zseb.speech;
            if (s._dbl) {
                document.removeEventListener('touchend', s._dbl.onTouchEnd);
                document.removeEventListener('dblclick', s._dbl.onDblClick);
                s._dbl = null;
            }
        },

        // Felvétel megszakítása visszahívások nélkül (Mégse gomb, oldal elhagyása).
        cancel: function () {
            const s = window.zseb.speech;
            const sess = s._session;
            if (!sess) return;
            s._session = null;
            clearTimeout(sess.watchdog);
            clearTimeout(sess.noResultTimer);
            const rec = sess.rec;
            if (rec) {
                rec.onstart = rec.onresult = rec.onerror = rec.onend = null;
                rec.onaudiostart = rec.onsoundstart = rec.onspeechstart = null;
                try { rec.abort(); } catch (e) { }
            }
        },

        detach: function (button) {
            if (button && button._zsebSpeechHandler) {
                button.removeEventListener('click', button._zsebSpeechHandler);
                delete button._zsebSpeechHandler;
            }
            window.zseb.speech.cancel();
        }
    },

    // Lapozás a fülek között vízszintes húzással. Húzás közben a tartalom követi az ujjat,
    // elengedéskor átúszik a szomszédos fülre (vagy visszarugózik). A .NET oldalon
    // OnSwipe(+1 = következő, -1 = előző). A fül indexét az .app data-tab-* attribútumai adják.
    swipe: {
        _attached: false,

        attach: function (dotnet) {
            const sw = window.zseb.swipe;
            if (sw._attached) return;
            sw._attached = true;

            const reduce = !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
            let sx = 0, sy = 0, st = 0, dx = 0, width = 1;
            let active = false, decided = false, dragging = false;
            let canPrev = false, canNext = false, el = null;

            function reset(target) {
                target.style.transition = 'transform .22s cubic-bezier(.2,.8,.2,1), opacity .22s ease-out';
                target.style.transform = '';
                target.style.opacity = '';
                setTimeout(function () { target.style.transition = ''; target.style.willChange = ''; }, 260);
            }

            document.addEventListener('touchstart', function (e) {
                active = decided = dragging = false;
                dx = 0;
                if (e.touches.length !== 1) return;
                const t = e.target;
                // Szövegmezőben, vízszintesen görgethető chip-sorban és a hangpanelen nem lapozunk.
                if (t.closest && t.closest('input, textarea, select, .chips-scroll, .no-swipe, .voice-sheet')) return;
                const app = document.querySelector('.app');
                el = document.querySelector('main.content');
                if (!app || !el) return;
                const idx = parseInt(app.getAttribute('data-tab-index') || '-1', 10);
                const cnt = parseInt(app.getAttribute('data-tab-count') || '0', 10);
                if (idx < 0) return;
                canPrev = idx > 0;
                canNext = idx < cnt - 1;
                width = el.getBoundingClientRect().width || window.innerWidth;
                sx = e.touches[0].clientX;
                sy = e.touches[0].clientY;
                st = Date.now();
                active = true;
            }, { passive: true });

            document.addEventListener('touchmove', function (e) {
                if (!active || e.touches.length !== 1) return;
                const ddx = e.touches[0].clientX - sx;
                const ddy = e.touches[0].clientY - sy;
                if (!decided) {
                    if (Math.abs(ddx) < 8 && Math.abs(ddy) < 8) return;
                    decided = true;
                    dragging = Math.abs(ddx) > Math.abs(ddy) * 1.2;   // vízszintes szándék?
                    if (!dragging) { active = false; return; }        // függőleges görgetés marad
                    el.style.transition = 'none';
                    el.style.willChange = 'transform';
                }
                e.preventDefault();                                   // húzás közben ne görgessen
                dx = ddx;
                const allowed = (dx < 0 && canNext) || (dx > 0 && canPrev);
                const shown = allowed ? dx : dx * 0.25;               // szélső fülnél ellenállás
                el.style.transform = 'translateX(' + shown + 'px)';
                el.style.opacity = String(1 - Math.min(Math.abs(shown) / width, 1) * 0.35);
            }, { passive: false });

            function end() {
                if (!active || !dragging) { active = false; return; }
                active = dragging = false;
                const target = el;
                const dir = dx < 0 ? 1 : -1;
                const allowed = (dir === 1 && canNext) || (dir === -1 && canPrev);
                const velocity = Math.abs(dx) / Math.max(1, Date.now() - st);   // px / ms
                const go = allowed && (Math.abs(dx) > width * 0.28 || (Math.abs(dx) > 40 && velocity > 0.45));
                if (!go) { reset(target); return; }

                // Átúszás: a mostani felület kicsúszik, az új a másik oldalról érkezik (CSS animáció).
                target.style.transition = reduce ? 'none' : 'transform .17s ease-in, opacity .17s ease-in';
                target.style.transform = 'translateX(' + (dir === 1 ? -width : width) + 'px)';
                target.style.opacity = '0.25';
                setTimeout(function () {
                    dotnet.invokeMethodAsync('OnSwipe', dir).catch(function () { reset(target); });
                }, reduce ? 0 : 160);
            }

            document.addEventListener('touchend', end, { passive: true });
            document.addEventListener('touchcancel', end, { passive: true });
        }
    }
};

// Nagyítás tiltása iOS-en: a Safari a user-scalable=no-t figyelmen kívül hagyja,
// ezért a csippentéses (pinch) gesztust itt fogjuk meg. A dupla koppintásos nagyítást
// a CSS "touch-action: manipulation" tiltja (így a gyors egymás utáni koppintások is működnek).
(function () {
    ['gesturestart', 'gesturechange', 'gestureend'].forEach(function (type) {
        document.addEventListener(type, function (e) { e.preventDefault(); }, { passive: false });
    });
    document.addEventListener('touchmove', function (e) {
        if (e.touches && e.touches.length > 1) e.preventDefault();
    }, { passive: false });
})();

// Tartós tárhely kérése, hogy a böngésző ne törölje az adatokat helyhiány esetén.
if (navigator.storage && navigator.storage.persist) {
    navigator.storage.persist().catch(() => { });
}
