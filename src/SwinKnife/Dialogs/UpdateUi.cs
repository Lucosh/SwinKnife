using System.Windows;
using SwinKnife.Core;

namespace SwinKnife.Dialogs;

/// <summary>Domande e avanzamento dell'aggiornamento automatico (vedi <see cref="Updater"/>).</summary>
public static class UpdateUi
{
    private static bool _busy;

    /// <summary>Controlla se c'è una nuova versione; con manual=true dice anche "sei aggiornato" e mostra gli errori.</summary>
    public static async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            UpdateInfo? u;
            try
            {
                u = await Updater.CheckAsync();
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Controllo aggiornamenti");
                if (manual) Dlg.Error(L.T("Impossibile controllare gli aggiornamenti:\n") + ex.Message);
                return;
            }
            if (u == null)
            {
                if (manual) Dlg.Info(L.T($"Hai già l'ultima versione di SwinKnife ({AppInfo.Version})."));
                return;
            }
            var intro = L.T($"È disponibile SwinKnife {u.Version.ToString(3)} (hai la {AppInfo.Version}).");
            if (!Updater.InstalledWithSetup() || u.SetupUrl == null)
            {
                // copia portable o compilata da sé: non posso sostituirla, rimando alla pagina di download
                if (Dlg.Confirm(intro + "\n\n" + L.T("Questa copia non è stata installata con l'installer, quindi non può aggiornarsi da sola: scarica la nuova versione da GitHub."),
                        L.T("Aggiornamento disponibile"), L.T("Apri la pagina di download"), L.T("Più tardi")))
                    Util.OpenExternal(u.PageUrl);
                return;
            }
            if (!Dlg.Confirm(intro + "\n\n" + L.T("Aggiornare adesso? SwinKnife si chiuderà e si riaprirà da solo a installazione finita."),
                    L.T("Aggiornamento disponibile"), L.T("Aggiorna ora"), L.T("Più tardi")))
                return;

            string setup;
            try
            {
                MainWindow.Notify(L.T("Download dell'aggiornamento…"), 0);
                setup = await Updater.DownloadAsync(u, new Progress<double>(p => MainWindow.Notify(L.T($"Download dell'aggiornamento: {p * 100:0} %"), 0)));
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Download aggiornamento");
                MainWindow.Notify("");
                Dlg.Error(L.T("Aggiornamento non riuscito:\n") + ex.Message);
                return;
            }
            if (MainWindow.Instance is { } w && !w.CanCloseAllPages())
            {
                MainWindow.Notify(L.T("Aggiornamento rimandato."));
                return;
            }
            try
            {
                Updater.RunInstaller(setup);
                App.Quit();
            }
            catch (Exception ex) // es. l'utente ha rifiutato la richiesta UAC di un'installazione "per tutti gli utenti"
            {
                AppInfo.Log(ex, "Avvio installer aggiornamento");
                Dlg.Error(L.T("Aggiornamento non riuscito:\n") + ex.Message);
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
