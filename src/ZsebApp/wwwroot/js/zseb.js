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
        _rec: null,

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

        attach: function (button, dotnet, lang, stopWord) {
            const s = window.zseb.speech;
            const handler = function () {
                // Második koppintás: leállítás, az eddigi szöveg megmarad.
                if (s._rec) {
                    try { s._rec.stop(); } catch (e) { }
                    return;
                }

                const Rec = window.SpeechRecognition || window.webkitSpeechRecognition;
                if (!Rec) {
                    dotnet.invokeMethodAsync('OnSpeechError', 'unsupported');
                    return;
                }

                // Minden felvételhez új példány – iOS-en az újrahasznált példány beakadhat.
                const rec = new Rec();
                rec.lang = lang;
                rec.interimResults = true;
                // Záró szónál folyamatos módban figyelünk, hogy a szünet ne vágja el a mondatot.
                rec.continuous = !!stopWord;
                rec.maxAlternatives = 1;

                let text = '';
                let committed = false;
                // Biztonsági időkorlát, ha a Safari "beragad".
                const watchdog = setTimeout(function () { try { rec.abort(); } catch (e) { } }, stopWord ? 60000 : 15000);

                rec.onstart = function () { dotnet.invokeMethodAsync('OnSpeechStart'); };
                rec.onresult = function (e) {
                    if (committed) return;
                    // Mindig a teljes eredménylistából építjük újra a szöveget (iOS-en így nincs duplázás).
                    const raw = Array.from(e.results).map(function (r) { return r[0].transcript; }).join(' ');
                    const r = s.stripStopWord(raw, stopWord);
                    text = r.text;
                    dotnet.invokeMethodAsync('OnSpeechResult', text);
                    if (r.hit) {
                        committed = true;
                        try { rec.stop(); } catch (err) { }
                    }
                };
                rec.onerror = function (e) { dotnet.invokeMethodAsync('OnSpeechError', e.error || 'unknown'); };
                rec.onend = function () {
                    clearTimeout(watchdog);
                    s._rec = null;
                    dotnet.invokeMethodAsync('OnSpeechEnd', text, committed);
                };

                s._rec = rec;
                try {
                    rec.start();
                } catch (err) {
                    clearTimeout(watchdog);
                    s._rec = null;
                    dotnet.invokeMethodAsync('OnSpeechError', 'start-failed');
                }
            };

            button.addEventListener('click', handler);
            button._zsebSpeechHandler = handler;
        },

        // Felvétel megszakítása visszahívások nélkül (Mégse gomb, oldal elhagyása).
        cancel: function () {
            const s = window.zseb.speech;
            if (s._rec) {
                const rec = s._rec;
                rec.onstart = rec.onresult = rec.onerror = rec.onend = null;
                try { rec.abort(); } catch (e) { }
                s._rec = null;
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
