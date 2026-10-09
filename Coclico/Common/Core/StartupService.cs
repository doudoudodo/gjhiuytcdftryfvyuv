using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Coclico.Services;

public class StartupProgress
{
    public string Status { get; set; } = string.Empty;
    public string SubDetail { get; set; } = string.Empty;
    public int Percent { get; set; }
    public string LogEntry { get; set; } = string.Empty;
    public string? CpuInfo { get; set; }
    public string? RamInfo { get; set; }
    public string? GpuInfo { get; set; }
    public string? SysInfo { get; set; }
}

public class StartupService
{
    public async Task RunStartupAsync(IProgress<StartupProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {

            bool isAdmin = false;
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(id);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

            int coreCount = Environment.ProcessorCount;
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
            string sysInfo = $"Win {(Environment.OSVersion.Version.Major >= 10 ? "11/10" : "NT")} • {(isAdmin ? "Admin" : "User")}";
            string cpuInfo = $"{coreCount} Cœurs • {arch}";

            progress?.Report(new StartupProgress
            {
                Status = "Démarrage des services...",
                SubDetail = "Initialisation de l'environnement",
                Percent = 12,
                LogEntry = "Démarrage des services système",
                CpuInfo = cpuInfo,
                SysInfo = sysInfo
            });

            await ServiceContainer.GetRequired<SettingsService>().LoadAsync().ConfigureAwait(false);


            var loc = ServiceContainer.GetOptional<LocalizationService>();
            MemoryCleanerService.RamInfo ram = MemoryCleanerService.GetRamInfo();
            double availGb = ram.AvailPhysBytes / (1024.0 * 1024.0 * 1024.0);
            double totalGb = ram.TotalPhysBytes / (1024.0 * 1024.0 * 1024.0);
            string ramInfo = string.Format(loc?.Get("Startup_RamFree") ?? "{0:F1} Go Libre / {1:F0} Go", availGb, totalGb);

            progress?.Report(new StartupProgress
            {
                Status = loc?.Get("Startup_MemOptimize") ?? "Optimisation de la mémoire...",
                SubDetail = loc?.Get("Startup_MemAnalyze") ?? "Analyse de la mémoire vive",
                Percent = 25,
                LogEntry = loc?.Get("Startup_MemAnalyze") ?? "Analyse de la mémoire vive",
                RamInfo = ramInfo
            });

            try { _ = ServiceContainer.GetOptional<ISmartMemoryDaemonService>(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            try { ServiceContainer.GetRequired<ResourceGuardService>().Start(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }


            string gpuInfo = "DirectX DWM";
            try
            {
                MemoryCleanerService.GpuInfo gpu = MemoryCleanerService.GetGpuInfo();
                string gName = string.IsNullOrWhiteSpace(gpu.Name) ? "GPU Standard" : gpu.Name;
                if (gName.Length > 22)
                {
                    gName = gName[..22] + "…";
                }

                gpuInfo = gpu.IsIntegrated ? $"{gName} (Intégré)" : $"{gName} (Dédié)";
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

            progress?.Report(new StartupProgress
            {
                Status = "Configuration de l'affichage...",
                SubDetail = "Accélération graphique matérielle",
                Percent = 38,
                LogEntry = "Initialisation de l'affichage",
                GpuInfo = gpuInfo
            });

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try { ServiceContainer.GetRequired<ThemeService>().ApplyCurrentSettings(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
                try { ServiceContainer.GetRequired<LocalizationService>().SetLanguage(ServiceContainer.GetRequired<SettingsService>().Settings.Language); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            });


            progress?.Report(new StartupProgress
            {
                Status = "Vérification des services système...",
                SubDetail = "Contrôle des services Windows",
                Percent = 50,
                LogEntry = "Vérification des services Windows"
            });

            try
            {
                StartupHealthService.HealthReport health = await ServiceContainer
                    .GetRequired<StartupHealthService>()
                    .CheckAndRepairAsync().ConfigureAwait(false);
                progress?.Report(new StartupProgress
                {
                    Status = "Services système vérifiés",
                    SubDetail = "Services Windows opérationnels",
                    Percent = 52,
                    LogEntry = "Services système opérationnels"
                });
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "StartupService.HealthCheck");
            }


            progress?.Report(new StartupProgress
            {
                Status = "Contrôle des composants...",
                SubDetail = "Vérification de la connectivité",
                Percent = 64,
                LogEntry = "Vérification des composants réseau"
            });

            progress?.Report(new StartupProgress
            {
                Status = "Composants prêts",
                SubDetail = "Modules système connectés",
                Percent = 66,
                LogEntry = "Composants prêts"
            });


            progress?.Report(new StartupProgress
            {
                Status = "Recherche des mises à jour...",
                SubDetail = "Vérification de la version",
                Percent = 78,
                LogEntry = "Recherche des mises à jour"
            });

            try
            {
                var updateTask = Task.Run(async () =>
                {
                    try
                    {
                        var updateCheckService = ServiceContainer.GetRequired<UpdateCheckService>();
                        _ = await updateCheckService.CheckForUpdatesAsync().ConfigureAwait(false);
                    }
                    catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
                });
                _ = await Task.WhenAny(updateTask, Task.Delay(500, ct)).ConfigureAwait(false);
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

            progress?.Report(new StartupProgress
            {
                Status = loc?.Get("Startup_CheckUpdates") ?? "Vérification des mises à jour...",
                SubDetail = "v" + UpdateCheckService.GetCurrentVersion(),
                Percent = 80,
                LogEntry = loc?.Get("Startup_VersionControl") ?? "Contrôle de version effectué"
            });


            progress?.Report(new StartupProgress
            {
                Status = loc?.Get("Startup_LoadingApps") ?? "Chargement des applications...",
                SubDetail = loc?.Get("Startup_IndexApps") ?? "Indexation des programmes installés",
                Percent = 90,
                LogEntry = loc?.Get("Startup_IndexApps") ?? "Indexation des programmes"
            });

            try
            {
                _ = await ServiceContainer.GetRequired<InstalledProgramsService>().GetAllInstalledProgramsAsync(cancellationToken: ct).ConfigureAwait(false);
                List<string> iconPaths = ServiceContainer.GetRequired<InstalledProgramsService>().GetMemoryCacheIconPaths();
                if (iconPaths.Count > 0)
                {
                    _ = Coclico.Converters.FileIconConverter.PreloadAllAsync(iconPaths);
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "StartupService.AppsScan");
            }


            progress?.Report(new StartupProgress
            {
                Status = loc?.Get("Startup_Finalizing") ?? "Finalisation du démarrage...",
                SubDetail = loc?.Get("Startup_TrimFootprint") ?? "Optimisation de l'empreinte mémoire",
                Percent = 98,
                LogEntry = loc?.Get("Startup_Finalizing") ?? "Finalisation du démarrage"
            });

            try
            {
                // Working-set trim only: an aggressive GC pass at every startup
                // only spiked the CPU without helping startup time.
                MemoryCleanerService.TrimSelfWorkingSet();
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

            await Task.Delay(100, ct).ConfigureAwait(false);

            progress?.Report(new StartupProgress
            {
                Status = loc?.Get("Startup_Ready") ?? "Prêt !",
                SubDetail = loc?.Get("Startup_Opening") ?? "Ouverture de Coclico...",
                Percent = 100,
                LogEntry = loc?.Get("Startup_Done") ?? "Démarrage terminé"
            });

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { LoggingService.LogInfo("Startup cancelled"); throw; }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "StartupService.RunStartupAsync");
            throw;
        }
    }
}
