using System.Text.RegularExpressions;

namespace ZsebApp.Services;

/// <summary>A hangfelismerésből kapott szöveg rendbetétele és a hibakódok magyar üzenetei.</summary>
public static class SpeechText
{
    public const string Language = "hu-HU";

    /// <summary>Ha a mondat ezzel a szóval végződik, a teendő automatikusan mentésre kerül.</summary>
    public const string StopWord = "vége";

    /// <summary>Szóközök rendezése, nagy kezdőbetű, a felismerő által odatett záró pont levágása.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var t = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        t = t.TrimEnd('.');
        return t.Length == 0 ? "" : char.ToUpper(t[0]) + t[1..];
    }

    private static readonly Regex StopWordAtEnd = new(
        @"^(?<rest>[\s\S]*?)(?:^|[\s,.;:!?]+)" + StopWord + @"[\s.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Ha a szöveg a záró szóval („vége”) végződik, visszaadja nélküle (pl. billentyűzetes diktálásnál).</summary>
    public static bool TryStripStopWord(string? text, out string rest)
    {
        rest = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = StopWordAtEnd.Match(text);
        if (!m.Success) return false;
        rest = m.Groups["rest"].Value.Trim();
        return true;
    }

    /// <summary>Felvétel közbeni állapot üzenete (a JS OnSpeechStatus hívásához).</summary>
    public static string? StatusMessage(string kind) => kind switch
    {
        "audio" => "A mikrofon bekapcsolt – mondhatod.",
        "sound" => "Hangot hallok…",
        "speech" => "Beszédet hallok, felismerés folyamatban…",
        "no-result" =>
            "Nem érkezik felismert szöveg. Ellenőrizd, hogy be van-e kapcsolva a Diktálás " +
            "(Beállítások → Általános → Billentyűzet → Diktálás) és van-e internet. " +
            "Addig a billentyűzet mikrofon gombjával is diktálhatsz – a „vége” szóra ott is ment.",
        _ => null,
    };

    /// <summary>A Web Speech API hibakódjaihoz tartozó, felhasználónak szóló üzenet (null = nem kell üzenet).</summary>
    public static string? ErrorMessage(string code) => code switch
    {
        "unsupported" =>
            "A hangfelismerés itt nem érhető el. Koppints a mezőbe, és diktálj a billentyűzet mikrofon gombjával.",
        "not-allowed" or "service-not-allowed" =>
            "Nincs engedély a mikrofonhoz. Engedélyezd a mikrofont, és kapcsold be a Diktálást " +
            "(Beállítások → Általános → Billentyűzet). Addig a billentyűzet mikrofon gombjával is diktálhatsz.",
        "no-speech" => "Nem hallottam semmit. Koppints újra, és mondd a feladatot.",
        "audio-capture" => "Nem érhető el a mikrofon. Lehet, hogy egy másik app használja.",
        "network" => "A hangfelismeréshez internetkapcsolat kell.",
        "language-not-supported" => "A magyar hangfelismerés nem érhető el ezen az eszközön.",
        "aborted" => null,
        "no-result" => StatusMessage("no-result"),
        "no-start" =>
            "Az iPhone nem indította el a hangfelismerést. Koppints újra a mikrofonra; ha így sem megy, " +
            "a billentyűzet mikrofon gombjával diktálj – a „vége” szóra ott is ment. " +
            "(Részletek: Beállítások → Hangfelismerés napló.)",
        _ => $"A hangfelismerés nem sikerült ({code}). A billentyűzet mikrofon gombjával is diktálhatsz.",
    };
}
