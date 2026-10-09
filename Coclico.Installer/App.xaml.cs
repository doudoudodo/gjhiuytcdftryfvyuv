using System;
using System.IO;
using System.Linq;
using System.Windows;
using Wpf.Ui.Appearance;

namespace Coclico.Installer;

public partial class App : Application
{
    public static bool IsUninstallMode { get; private set; }
    public static bool IsSilentMode { get; private set; }

    static App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash(e.ExceptionObject as Exception);
        };
    }

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (s, e) =>
        {
            LogCrash(e.Exception);
            MessageBox.Show(
                $"Erreur au démarrage : {e.Exception.Message}\n\n{e.Exception.InnerException?.Message}",
                "Coclico Setup - Erreur",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "coclico-setup-crash.txt");
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        }
        catch { }

        IsUninstallMode = e.Args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || arg.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        IsSilentMode = e.Args.Any(arg => arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) || arg.Equals("/silent", StringComparison.OrdinalIgnoreCase));

        if (IsSilentMode)
        {
            RunSilent();
            return;
        }

        if (IsUninstallMode)
        {
            try
            {
                // Fermer la fenêtre de désinstallation doit terminer le processus :
                // en OnExplicitShutdown, le processus resterait vivant sans fenêtre.
                ShutdownMode = ShutdownMode.OnLastWindowClose;
                var uninstallWindow = new Views.UninstallWindow();
                uninstallWindow.Show();
                return;
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                // Fallback: try silent uninstall if UI fails
                try
                {
                    RunSilent();
                }
                catch
                {
                    Shutdown();
                }
            }
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private void RunSilent()
    {
        try
        {
            // Executer sur le pool de threads : bloquer le thread UI sur l'await
            // pendant OnStartup provoquerait un blocage (le dispatcher ne pompe pas encore).
            if (IsUninstallMode)
            {
                Task.Run(() => Services.InstallService.FullUninstallAsync(new Progress<Services.InstallProgressReport>())).GetAwaiter().GetResult();
            }
            else
            {
                var config = new Models.InstallConfig();
                Task.Run(() => Services.InstallService.InstallAsync(config, new Progress<Services.InstallProgressReport>())).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
        finally
        {
            // Shutdown() seul ne termine pas le processus s'il est appele pendant
            // OnStartup (avant la boucle de messages) : sortie explicite garantie.
            try { Shutdown(); } catch { }
            Environment.Exit(0);
        }
    }
}

