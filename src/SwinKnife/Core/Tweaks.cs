using Microsoft.Win32;

namespace SwinKnife.Core;

/// <summary>Una modifica di privacy/ottimizzazione reversibile. IsApplied = true quando la protezione è attiva.</summary>
public sealed class TweakDef
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Desc { get; init; }
    public bool NeedsAdmin { get; init; }
    public required Func<bool> IsApplied { get; init; }
    public required Action<bool> Apply { get; init; }
}

/// <summary>Interruttori di privacy e pulizia di Windows (stile ShutUp10), tutti reversibili.</summary>
public static class Tweaks
{
    private static int GetDword(RegistryKey root, string path, string name, int fallback)
    {
        try { using var k = root.OpenSubKey(path); return k?.GetValue(name) is int i ? i : fallback; }
        catch { return fallback; }
    }

    private static void SetDword(RegistryKey root, string path, string name, int value)
    {
        using var k = root.CreateSubKey(path, true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>Un tweak che vale un valore DWORD: applied=protectValue, altrimenti restoreValue.</summary>
    private static TweakDef Dword(string id, string title, string desc, bool admin, RegistryKey root, string path, string name,
        int protectValue, int restoreValue)
        => new()
        {
            Id = id, Title = title, Desc = desc, NeedsAdmin = admin,
            IsApplied = () => GetDword(root, path, name, restoreValue) == protectValue,
            Apply = on => SetDword(root, path, name, on ? protectValue : restoreValue),
        };

    public static List<TweakDef> All()
    {
        const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        return new List<TweakDef>
        {
            Dword("ads", L.T("Annunci personalizzati"), L.T("Disattiva l'ID pubblicitario usato dalle app per profilarti."),
                false, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1),
            Dword("startsugg", L.T("Suggerimenti nel menu Start"), L.T("Toglie le app consigliate e le pubblicità dal menu Start."),
                false, Registry.CurrentUser, cdm, "SystemPaneSuggestionsEnabled", 0, 1),
            Dword("tips", L.T("Suggerimenti e trucchi di Windows"), L.T("Disattiva le notifiche con consigli e promozioni di Windows."),
                false, Registry.CurrentUser, cdm, "SubscribedContent-338389Enabled", 0, 1),
            Dword("lockads", L.T("Curiosità e pubblicità nella schermata di blocco"), L.T("Toglie i contenuti promozionali dalla schermata di blocco."),
                false, Registry.CurrentUser, cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
            Dword("tailored", L.T("Esperienze personalizzate"), L.T("Impedisce a Windows di usare i tuoi dati diagnostici per consigli su misura."),
                false, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1),
            Dword("bgapps", L.T("App in background"), L.T("Impedisce alle app del Microsoft Store di girare quando non le usi (risparmio batteria)."),
                false, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, 0),
            Dword("websearch", L.T("Ricerca sul web nel menu Start"), L.T("Mostra solo i risultati del PC, senza Bing e i suggerimenti dal web."),
                false, Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, 0),
            Dword("telemetry", L.T("Telemetria al minimo"), L.T("Riduce al minimo i dati diagnostici inviati a Microsoft."),
                true, Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1),
            Dword("cortana", L.T("Cortana"), L.T("Disattiva l'assistente Cortana."),
                true, Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1),
            Dword("advertisingwifi", L.T("Connessione automatica agli hotspot"), L.T("Impedisce a Windows di connettersi da solo agli hotspot Wi-Fi aperti suggeriti."),
                false, Registry.CurrentUser, @"Software\Microsoft\WcmSvc\wifinetworkmanager\config", "AutoConnectAllowedOEM", 0, 1),
        };
    }
}
