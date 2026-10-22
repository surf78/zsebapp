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

    // Lapozás a fülek között vízszintes húzással. A .NET oldalon OnSwipe(+1 = következő, -1 = előző).
    swipe: {
        _attached: false,

        attach: function (dotnet) {
            const sw = window.zseb.swipe;
            if (sw._attached) return;
            sw._attached = true;

            let sx = 0, sy = 0, st = 0, ok = false;
            document.addEventListener('touchstart', function (e) {
                if (e.touches.length !== 1) { ok = false; return; }
                const t = e.target;
                // Szövegmezőben, vízszintesen görgethető chip-sorban és a hangpanelen nem lapozunk.
                ok = !(t.closest && t.closest('input, textarea, select, .chips-scroll, .no-swipe, .voice-sheet'));
                sx = e.touches[0].clientX;
                sy = e.touches[0].clientY;
                st = Date.now();
            }, { passive: true });

            document.addEventListener('touchend', function (e) {
                if (!ok) return;
                ok = false;
                const t = e.changedTouches[0];
                const dx = t.clientX - sx;
                const dy = t.clientY - sy;
                if (Math.abs(dx) > 70 && Math.abs(dy) < Math.abs(dx) * 0.6 && Date.now() - st < 700) {
                    dotnet.invokeMethodAsync('OnSwipe', dx < 0 ? 1 : -1);
                }
            }, { passive: true });
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
