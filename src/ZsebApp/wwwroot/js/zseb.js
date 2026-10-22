// Kis JavaScript-segédek, amelyeket a C# kód JS interopon keresztül hív.
window.zseb = {
    // iPhone-on a megosztási lapot nyitja meg ("Mentés a Fájlokba"), máshol letölti a fájlt.
    saveFile: async function (filename, text, mime) {
        mime = mime || 'application/json';
        // CSV-nél BOM, hogy az Excel UTF-8-ként (ékezetekkel) nyissa meg.
        if (mime.indexOf('text/csv') === 0) text = '﻿' + text;
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
    speech: {
        _rec: null,

        isSupported: function () {
            return !!(window.SpeechRecognition || window.webkitSpeechRecognition);
        },

        attach: function (button, dotnet, lang) {
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
                rec.continuous = false;
                rec.maxAlternatives = 1;

                let text = '';
                // Biztonsági időkorlát: ha a Safari "beragad", 15 mp után leállítjuk.
                const watchdog = setTimeout(function () { try { rec.abort(); } catch (e) { } }, 15000);

                rec.onstart = function () { dotnet.invokeMethodAsync('OnSpeechStart'); };
                rec.onresult = function (e) {
                    // Mindig a teljes eredménylistából építjük újra a szöveget (iOS-en így nincs duplázás).
                    text = Array.from(e.results).map(function (r) { return r[0].transcript; }).join(' ');
                    dotnet.invokeMethodAsync('OnSpeechResult', text);
                };
                rec.onerror = function (e) { dotnet.invokeMethodAsync('OnSpeechError', e.error || 'unknown'); };
                rec.onend = function () {
                    clearTimeout(watchdog);
                    s._rec = null;
                    dotnet.invokeMethodAsync('OnSpeechEnd', text);
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

        detach: function (button) {
            if (button && button._zsebSpeechHandler) {
                button.removeEventListener('click', button._zsebSpeechHandler);
                delete button._zsebSpeechHandler;
            }
            const s = window.zseb.speech;
            if (s._rec) {
                // Az oldal már megszűnt: ne hívjunk vissza a .NET-be.
                const rec = s._rec;
                rec.onstart = rec.onresult = rec.onerror = rec.onend = null;
                try { rec.abort(); } catch (e) { }
                s._rec = null;
            }
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
