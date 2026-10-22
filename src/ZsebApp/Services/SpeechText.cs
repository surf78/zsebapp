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
        _ => $"A hangfelismerés nem sikerült ({code}). A billentyűzet mikrofon gombjával is diktálhatsz.",
    };
}
