using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Appearance;

namespace SwinKnife;

public partial class App : Application
{
    public static readonly Color Accent = Color.FromRgb(0xE5, 0x48, 0x4D);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // usati dall'installer: attivano/rimuovono il menu del tasto destro senza aprire la finestra
        if (e.Args.Contains("--register-shell") || e.Args.Contains("--unregister-shell"))
        {
            L.Apply();
            try
            {
                if (e.Args.Contains("--register-shell")) ShellIntegration.Install();
                else ShellIntegration.Uninstall();
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Menu contestuale da riga di comando");
            }
            Shutdown();
            return;
        }

        // istanza singola: se SwinKnife è già aperto gli passo gli argomenti ed esco
        // dopo un riavvio (privilegi di amministratore o cambio lingua) aspetto che l'istanza precedente si chiuda
        var elevated = e.Args.Contains("--elevated") || e.Args.Contains("--restart");
        if (!SingleInstance.Acquire(elevated) && SingleInstance.Forward(e.Args))
        {
            Shutdown();
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        L.Apply();
        Directory.CreateDirectory(AppInfo.DataDir);
        AppInfo.CleanTemp();

        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppInfo.Log(ex.Exception, "Task non osservato");
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => AppInfo.Log($"Errore fatale: {ex.ExceptionObject}");

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplyAccent();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.HandleArgs(e.Args);
        SingleInstance.Listen(args => Dispatcher.InvokeAsync(() =>
        {
            window.BringToFront();
            window.HandleArgs(args);
        }));
        ShellIntegration.RefreshIfInstalled();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        L.DumpMissing();
        base.OnExit(e);
    }

    /// <summary>Rosso "svizzero" pieno con testo bianco (WPF-UI di default schiarisce l'accento nel tema scuro).</summary>
    public static void ApplyAccent()
    {
        ApplicationAccentColorManager.Apply(Accent, Color.FromRgb(0xEB, 0x5E, 0x62), Accent, Color.FromRgb(0xD3, 0x3A, 0x3F));
        var res = Current.Resources;
        res["TextOnAccentFillColorPrimary"] = Colors.White;
        res["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
        res["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppInfo.Log(e.Exception, "Eccezione non gestita");
        e.Handled = true;
        Dlg.Error(L.T($"{e.Exception.Message}\n\nDettagli salvati in:\n{AppInfo.LogFile}"), L.T("Errore imprevisto"));
    }
}
