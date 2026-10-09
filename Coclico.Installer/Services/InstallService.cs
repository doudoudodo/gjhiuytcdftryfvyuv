using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Coclico.Installer.Models;
using Microsoft.Win32;

namespace Coclico.Installer.Services;

public record InstallProgressReport(double Percent, string Status, string SubStatus);

public static class InstallService
{
    private const string AppName = "Coclico";
    /// <summary>
    /// Version inscrite au registre (Programmes et fonctionnalités) — déduite
    /// de version.txt via Directory.Build.props, aucune constante à maintenir.
    /// </summary>
    public static readonly string AppVersion =
        typeof(InstallService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    private const string AppPublisher = "Coclico";
    private const string AppUrl = "https://github.com/Coclico-cy/Coclico";
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Coclico";

    public static bool IsCoclicoRunning()
    {
        return Process.GetProcessesByName("Coclico").Length > 0;
    }

    public static void CloseRunningCoclico()
    {
        foreach (var process in Process.GetProcessesByName("Coclico"))
        {
            try
            {
                if (!process.CloseMainWindow())
                {
                    process.Kill();
                }
                process.WaitForExit(3000);
            }
            catch
            {
                // Process already terminated
            }
        }
    }

    public static async Task InstallAsync(InstallConfig config, IProgress<InstallProgressReport> progress, CancellationToken ct = default)
    {
        // Async lambda: the multi-GB downloads below are awaited instead of
        // blocking a thread-pool thread with GetAwaiter().GetResult().
        await Task.Run(async () =>
        {
            progress.Report(new InstallProgressReport(5, "Préparation de l'environnement...", "Vérification des processus actifs"));

            if (IsCoclicoRunning())
            {
                progress.Report(new InstallProgressReport(8, "Fermeture de Coclico...", "Arrêt du processus en cours"));
                CloseRunningCoclico();
                Thread.Sleep(500);
            }

            Directory.CreateDirectory(config.InstallPath);

            // Recherche du payload
            progress.Report(new InstallProgressReport(12, "Recherche des composants d'installation...", ""));
            Stream? zipStream = GetPayloadStream();

            if (zipStream != null)
            {
                using (zipStream)
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    int totalEntries = archive.Entries.Count;
                    int extracted = 0;

                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(Path.Combine(config.InstallPath, entry.FullName));
                            continue;
                        }

                        string destinationPath = Path.Combine(config.InstallPath, entry.FullName);
                        string? targetDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        entry.ExtractToFile(destinationPath, overwrite: true);
                        extracted++;

                        double percent = 15 + ((double)extracted / totalEntries * 70);
                        progress.Report(new InstallProgressReport(percent, $"Extraction : {entry.Name}", $"{extracted}/{totalEntries} fichiers"));
                    }
                }
            }
            else
            {
                string devPublishPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "publish");
                if (Directory.Exists(devPublishPath))
                {
                    CopyDirectoryWithProgress(devPublishPath, config.InstallPath, progress, ct);
                }
                else
                {
                    throw new FileNotFoundException("Archive d'installation introuvable (payload.zip absent).");
                }
            }

            // Téléchargement à la demande du runtime NVIDIA CUDA 12 (non embarqué dans le setup)
            if (config.InstallCuda)
            {
                progress.Report(new InstallProgressReport(76, "Téléchargement du runtime NVIDIA CUDA 12 (~1,2 Go)...", "Connexion à nuget.org..."));
                try
                {
                    var cudaProgress = new Progress<InstallProgressReport>(cudaReport =>
                    {
                        progress.Report(new InstallProgressReport(76 + (cudaReport.Percent * 0.05), cudaReport.Status, cudaReport.SubStatus));
                    });
                    await CudaRuntimeDownloader.DownloadAsync(config.InstallPath, cudaProgress, ct);
                    progress.Report(new InstallProgressReport(81, "Runtime NVIDIA CUDA 12 installé avec succès.", ""));
                }
                catch (Exception cudaEx)
                {
                    progress.Report(new InstallProgressReport(81, "Téléchargement CUDA ignoré : " + cudaEx.Message, "L'IA locale fonctionnera en mode CPU."));
                }
            }

            // Téléchargement optionnel du Modèle IA GGUF
            if (config.InstallAiModel && config.SelectedAiModel != null)
            {
                progress.Report(new InstallProgressReport(82, $"Téléchargement du modèle IA ({config.SelectedAiModel.DisplayName})...", "Connexion HuggingFace..."));
                try
                {
                    var aiProgress = new Progress<InstallProgressReport>(aiReport =>
                    {
                        double globalPercent = 82 + (aiReport.Percent * 0.13);
                        progress.Report(new InstallProgressReport(globalPercent, aiReport.Status, aiReport.SubStatus));
                    });
                    await AiModelDownloader.DownloadModelAsync(config.InstallPath, config.SelectedAiModel, aiProgress, ct);
                }
                catch (Exception ex)
                {
                    progress.Report(new InstallProgressReport(85, "Téléchargement IA ignoré : " + ex.Message, ""));
                }
            }

            // Configuration des paramètres initiaux de Coclico (modèle IA sélectionné, GPU, etc.)
            ConfigureCoclicoSettings(config);

            // Creer le systeme de desinstallation - on utilise directement l'interface graphique
            progress.Report(new InstallProgressReport(88, "Configuration du gestionnaire de désinstallation...", ""));
            string currentExe = Environment.ProcessPath ?? string.Empty;
            string uninstallerPath = currentExe; // On utilise l'exe actuel pour la désinstallation

            string exePath = Path.Combine(config.InstallPath, "Coclico.exe");

            // Raccourcis
            progress.Report(new InstallProgressReport(92, "Création des raccourcis...", ""));
            if (config.CreateDesktopShortcut)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                ShortcutHelper.CreateShortcut(exePath, Path.Combine(desktop, "Coclico.lnk"), "Coclico - Assistant Système", exePath);
            }

            if (config.CreateStartMenuShortcut)
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", "Coclico");
                ShortcutHelper.CreateShortcut(exePath, Path.Combine(startMenu, "Coclico.lnk"), "Coclico - Assistant Système", exePath);
            }

            // Créer le dossier Uninstall et déployer le désinstallateur pour la désinstallation
            progress.Report(new InstallProgressReport(88, "Préparation de la désinstallation...", ""));
            string uninstallerDir = Path.Combine(config.InstallPath, "Uninstall");
            uninstallerPath = Path.Combine(uninstallerDir, "Coclico.Installer.exe");

            try
            {
                Directory.CreateDirectory(uninstallerDir);

                // Le setup embarque un désinstallateur léger (sans payload) :
                // bien plus petit que de copier le setup complet (~450 Mo).
                using (Stream? lightUninstaller = GetEmbeddedResourceStream("Coclico.Installer.uninstaller.bin"))
                {
                    if (lightUninstaller != null)
                    {
                        using (lightUninstaller)
                        using (var output = File.Create(uninstallerPath))
                        {
                            lightUninstaller.CopyTo(output);
                        }
                    }
                    else if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                    {
                        File.Copy(currentExe, uninstallerPath, overwrite: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erreur préparation désinstallation: {ex.Message}");
                uninstallerPath = currentExe; // Fallback au chemin actuel
            }

            // Inscription registre Windows
            progress.Report(new InstallProgressReport(96, "Enregistrement dans Windows...", "Ajout dans Applications et fonctionnalités"));
            RegisterUninstall(config.InstallPath, uninstallerPath, exePath);

            progress.Report(new InstallProgressReport(100, "Installation terminée avec succès !", "Coclico est prêt à être utilisé."));
        }, ct);
    }

    public static void ConfigureCoclicoSettings(InstallConfig config)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string coclicoDir = Path.Combine(appData, "Coclico");
            Directory.CreateDirectory(coclicoDir);
            string settingsFile = Path.Combine(coclicoDir, "settings.json");

            var settingsDict = new System.Collections.Generic.Dictionary<string, object>();
            if (File.Exists(settingsFile))
            {
                try
                {
                    string existingJson = File.ReadAllText(settingsFile);
                    using var doc = System.Text.Json.JsonDocument.Parse(existingJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        settingsDict[prop.Name] = prop.Value.Clone();
                    }
                }
                catch { }
            }

            if (config.InstallAiModel && config.SelectedAiModel != null)
            {
                settingsDict["aiProvider"] = "LocalGGUF";
                settingsDict["aiModel"] = config.SelectedAiModel.Id;
            }

            if (config.InstallCuda)
            {
                settingsDict["aiUseGpu"] = true;
            }

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(settingsDict, options));
        }
        catch { }
    }

    public static async Task UninstallAsync(IProgress<InstallProgressReport> progress, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            string installPath = Path.GetDirectoryName(Environment.ProcessPath) ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Coclico");

            progress.Report(new InstallProgressReport(10, "Fermeture des processus actifs...", ""));
            if (IsCoclicoRunning())
            {
                CloseRunningCoclico();
                Thread.Sleep(500);
            }

            progress.Report(new InstallProgressReport(30, "Suppression des raccourcis...", ""));
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Coclico.lnk");
            ShortcutHelper.DeleteShortcut(desktopShortcut);

            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Coclico");
            if (Directory.Exists(startMenuDir))
            {
                try { Directory.Delete(startMenuDir, recursive: true); } catch { }
            }

            progress.Report(new InstallProgressReport(55, "Nettoyage du Registre Windows...", ""));
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
            }
            catch { }

            progress.Report(new InstallProgressReport(75, "Suppression des fichiers...", ""));
            if (Directory.Exists(installPath))
            {
                foreach (string file in Directory.GetFiles(installPath, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { File.Delete(file); } catch { }
                }

                foreach (string dir in Directory.GetDirectories(installPath, "*", SearchOption.AllDirectories))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }

            progress.Report(new InstallProgressReport(95, "Finalisation de la désinstallation...", ""));
            ScheduleSelfDelete(installPath);

            progress.Report(new InstallProgressReport(100, "Coclico a été désinstallé.", "Merci d'avoir utilisé Coclico !"));
        }, ct);
    }

    public static async Task FullUninstallAsync(IProgress<InstallProgressReport> progress, string? installPath = null, bool keepUserData = false, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            string resolvedPath = ResolveInstallPath(installPath);

            progress.Report(new InstallProgressReport(5, "Fermeture des processus actifs...", "Arrêt des applications Coclico"));
            if (IsCoclicoRunning())
            {
                CloseRunningCoclico();
                Thread.Sleep(500);
            }

            progress.Report(new InstallProgressReport(10, "Suppression des raccourcis Bureau et Menu Demarrer...", ""));
            string desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Coclico.lnk");
            ShortcutHelper.DeleteShortcut(desktopShortcut);

            string startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Coclico");
            if (Directory.Exists(startMenuDir))
            {
                try { Directory.Delete(startMenuDir, recursive: true); } catch { }
            }

            progress.Report(new InstallProgressReport(25,
                keepUserData ? "Conservation des données utilisateur (%AppData%)..." : "Suppression des donnees de configuration (%AppData%)...",
                ""));
            if (!keepUserData)
            {
                string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coclico");
                if (Directory.Exists(appDataPath))
                {
                    try { Directory.Delete(appDataPath, recursive: true); } catch { }
                }

                string localAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coclico");
                if (Directory.Exists(localAppDataPath))
                {
                    try { Directory.Delete(localAppDataPath, recursive: true); } catch { }
                }
            }

            progress.Report(new InstallProgressReport(40, "Nettoyage des fichiers temporaires...", ""));
            string tempPath = Path.Combine(Path.GetTempPath(), "Coclico");
            if (Directory.Exists(tempPath))
            {
                try { Directory.Delete(tempPath, recursive: true); } catch { }
            }

            progress.Report(new InstallProgressReport(55, "Nettoyage du Registre Windows...", ""));
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
            }
            catch { }

            progress.Report(new InstallProgressReport(70, "Suppression du dossier d'installation...", resolvedPath));
            if (!string.IsNullOrEmpty(resolvedPath) && Directory.Exists(resolvedPath))
            {
                try
                {
                    Directory.Delete(resolvedPath, recursive: true);
                }
                catch
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(resolvedPath, "*", SearchOption.AllDirectories))
                        {
                            try { File.Delete(file); } catch { }
                        }
                        foreach (string dir in Directory.GetDirectories(resolvedPath, "*", SearchOption.AllDirectories))
                        {
                            try { Directory.Delete(dir, recursive: true); } catch { }
                        }
                        Directory.Delete(resolvedPath, recursive: true);
                    }
                    catch { }
                }
            }

            progress.Report(new InstallProgressReport(90, "Finalisation de la désinstallation complète...", ""));

            // L'exécutable de désinstallation ne peut pas se supprimer lui-même pendant son exécution :
            // une tâche différée nettoie le dossier restant à la fermeture de cette fenêtre.
            if (!string.IsNullOrEmpty(resolvedPath) && Directory.Exists(resolvedPath))
            {
                ScheduleSelfDelete(resolvedPath);
            }

            Thread.Sleep(500);

            progress.Report(new InstallProgressReport(100, "Désinstallation complète !", "Tous les fichiers, données et entrées de registre ont été supprimés."));
        }, ct);
    }

    private static string ResolveInstallPath(string? installPath)
    {
        if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
        {
            return installPath;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            var installLocation = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
            {
                return installLocation;
            }
        }
        catch { }

        string processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;

        // Le désinstallateur est copié dans <installation>\Uninstall\ lors de l'installation :
        // remonter d'un niveau pour cibler le dossier d'installation complet.
        if (!string.IsNullOrEmpty(processDir) &&
            Path.GetFileName(processDir.TrimEnd(Path.DirectorySeparatorChar))
                .Equals("Uninstall", StringComparison.OrdinalIgnoreCase))
        {
            string? parentDir = Path.GetDirectoryName(processDir.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                return parentDir;
            }
        }

        if (!string.IsNullOrEmpty(processDir) && Directory.Exists(processDir))
        {
            return processDir;
        }

        string localAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Coclico");
        if (Directory.Exists(localAppDataPath))
        {
            return localAppDataPath;
        }

        return string.Empty;
    }

    private static Stream? GetEmbeddedResourceStream(string resourceName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null && stream.Length > 100)
            {
                return stream;
            }
            stream?.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Stream? GetPayloadStream()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = "Coclico.Installer.payload.zip";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream != null && stream.Length > 100)
        {
            return stream;
        }
        stream?.Dispose();

        string localZip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "payload.zip");
        if (File.Exists(localZip) && new FileInfo(localZip).Length > 100)
        {
            return File.OpenRead(localZip);
        }

        string artifactZip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "payload.zip");
        if (File.Exists(artifactZip) && new FileInfo(artifactZip).Length > 100)
        {
            return File.OpenRead(artifactZip);
        }

        return null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
        }
    }

    private static void CopyDirectoryWithProgress(string sourceDir, string targetDir, IProgress<InstallProgressReport> progress, CancellationToken ct)
    {
        var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        int total = files.Length;
        int current = 0;

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);

            current++;
            double percent = 15 + ((double)current / total * 70);
            progress.Report(new InstallProgressReport(percent, $"Copie : {Path.GetFileName(file)}", $"{current}/{total}"));
        }
    }



    private static void RegisterUninstall(string installPath, string uninstallerPath, string mainExePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (key == null) return;

            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", AppPublisher);
            key.SetValue("DisplayIcon", $"{mainExePath},0");
            key.SetValue("InstallLocation", installPath);
            
            // Utiliser directement l'interface graphique pour la désinstallation
            key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" --uninstall --silent");
            key.SetValue("URLInfoAbout", AppUrl);
            key.SetValue("HelpLink", $"{AppUrl}/issues");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            try
            {
                long totalBytes = 0;
                foreach (string f in Directory.GetFiles(installPath, "*", SearchOption.AllDirectories))
                {
                    totalBytes += new FileInfo(f).Length;
                }
                key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Erreur inscription registre: {ex.Message}");
        }
    }

    private static void ScheduleSelfDelete(string installPath)
    {
        // 1. Voie propre : MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT) marque le fichier
        //    pour suppression par Windows au prochain redémarrage (registry PendingFileRenameOperations),
        //    ce qui fonctionne même si cmd.exe ou les fichiers sont verrouillés.
        bool scheduled = TryScheduleDeleteOnReboot(Path.Combine(installPath, "CoclicoUninstaller.exe"));

        try
        {
            // 2. Repli : l'exécutable de désinstallation ne peut pas se supprimer lui-même pendant
            //    son exécution : deux tentatives espacées nettoient le dossier après la fermeture de la fenêtre.
            //    La suppression ne se fait que si Coclico n'a pas été réinstallé entre-temps
            //    (présence de Coclico.exe), pour ne jamais détruire une nouvelle installation.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 12 /nobreak > NUL & if not exist \"{installPath}\\Coclico.exe\" rmdir /s /q \"{installPath}\" & timeout /t 8 /nobreak > NUL & if not exist \"{installPath}\\Coclico.exe\" rmdir /s /q \"{installPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch { }

        _ = scheduled;
    }

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    /// <summary>
    /// Programme la suppression du désinstalleur au prochain redémarrage via
    /// MoveFileEx(DELAY_UNTIL_REBOOT) — aucune fenêtre cmd, aucune course avec les verrous.
    /// </summary>
    private static bool TryScheduleDeleteOnReboot(string filePath)
    {
        try
        {
            return File.Exists(filePath) && MoveFileEx(filePath, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch
        {
            return false;
        }
    }
}
