namespace ZsebApp.Services;

/// <summary>A közös tár (Firebase) hibakódjainak magyar üzenetei.</summary>
public static class SyncText
{
    public static string? ErrorMessage(string? code) => code switch
    {
        null or "" => null,
        "auth/invalid-credential" or "auth/wrong-password" or "auth/user-not-found" or "auth/invalid-login-credentials" =>
            "Hibás e-mail cím vagy jelszó.",
        "auth/invalid-email" or "auth/missing-email" => "Érvénytelen e-mail cím.",
        "auth/missing-password" => "Add meg a jelszót.",
        "auth/email-already-in-use" => "Ezzel az e-mail címmel már van fiók – lépj be vele.",
        "auth/weak-password" or "auth/password-does-not-meet-requirements" => "A jelszó legalább 6 karakter legyen.",
        "auth/network-request-failed" or "offline" or "unavailable" => "Nincs internetkapcsolat. Próbáld újra, ha lesz net.",
        "auth/too-many-requests" => "Túl sok próbálkozás. Próbáld újra pár perc múlva.",
        "auth/user-disabled" => "Ez a fiók le van tiltva.",
        "not-verified" => "Még nincs megerősítve. Kattints az e-mailben kapott linkre (nézd meg a Spam / Levélszemét mappát is), majd koppints újra.",
        "permission-denied" => "Nincs hozzáférésed a közös tárhoz (lehet, hogy kivettek belőle).",
        "invalid" => "Adj meg egy érvényes e-mail címet.",
        _ => $"Hiba történt ({code}).",
    };
}
