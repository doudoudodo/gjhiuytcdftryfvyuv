using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Coclico.Services;

namespace Coclico.Modules.Power.Services;

public sealed record PowerSchemeInfo(string Guid, string Name, bool IsActive);

public sealed record PowerPlanSetupReport(
    bool Success,
    IReadOnlyDictionary<CoclicoPowerMode, string> PlanGuids,
    int AppliedSettings,
    int SkippedSettings,
    IReadOnlyList<string> Messages)
{
    public static PowerPlanSetupReport Failure(string message) =>
        new(false, new Dictionary<CoclicoPowerMode, string>(), 0, 0, [message]);
}

public sealed record PowerStateSummary(
    string ActiveSchemeName,
    string ActiveSchemeGuid,
    CoclicoPowerMode? ActiveCoclicoMode,
    bool IsElevated,
    bool HasBattery,
    bool IsOnAcPower);

/// <summary>
/// Crée, configure et pilote les plans d'alimentation Windows « Coclico »
/// exclusivement via l'outil powercfg (PowerShell/cmd), sans écriture directe
/// dans le registre. Chaque paramètre est validé contre les capacités réelles
/// du PC avant d'être appliqué : les paramètres absents (EPP sur vieux CPU,
/// ASPM sans PCIe, etc.) sont ignorés et comptés comme ignorés.
/// </summary>
public sealed class CpuPowerService
{
    private static readonly Regex GuidRegex = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    public bool IsElevated => App.IsRunningAsAdministrator();

    public bool HasBattery
    {
        get
        {
            try
            {
                return NativePower.HasBattery();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsOnAcPower
    {
        get
        {
            try
            {
                return NativePower.IsOnAcPower();
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Liste les plans d'alimentation Windows connus de powercfg.
    /// </summary>
    public async Task<IReadOnlyList<PowerSchemeInfo>> ListSchemesAsync(CancellationToken ct = default)
    {
        (_, string output) = await RunPowerCfgAsync("/list", ct).ConfigureAwait(false);
        List<PowerSchemeInfo> schemes = [];

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            Match guidMatch = GuidRegex.Match(line);
            if (!guidMatch.Success)
            {
                continue;
            }

            string name = ExtractSchemeName(line);
            bool isActive = line.EndsWith("*", StringComparison.Ordinal);
            schemes.Add(new PowerSchemeInfo(guidMatch.Value.ToLowerInvariant(), name, isActive));
        }

        return schemes;
    }

    public async Task<PowerSchemeInfo?> GetActiveSchemeAsync(CancellationToken ct = default)
    {
        (_, string output) = await RunPowerCfgAsync("/getactivescheme", ct).ConfigureAwait(false);
        Match guidMatch = GuidRegex.Match(output);
        if (!guidMatch.Success)
        {
            return null;
        }

        string name = ExtractSchemeName(output);
        return new PowerSchemeInfo(guidMatch.Value.ToLowerInvariant(), name, true);
    }

    /// <summary>
    /// S'assure que les trois plans Coclico existent et portent les paramètres
    /// recommandés par le catalogue, adaptés aux capacités du PC.
    /// </summary>
    public async Task<PowerPlanSetupReport> EnsureCoclicoPlansAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsElevated)
        {
            return PowerPlanSetupReport.Failure("Les droits administrateur sont requis pour créer ou modifier les plans d'alimentation.");
        }

        Dictionary<CoclicoPowerMode, string> planGuids = [];
        List<string> messages = [];
        int applied = 0;
        int skipped = 0;

        Dictionary<CoclicoPowerMode, string> existing = await FindCoclicoPlansAsync(ct).ConfigureAwait(false);

        foreach (CoclicoPowerMode mode in Enum.GetValues<CoclicoPowerMode>())
        {
            ct.ThrowIfCancellationRequested();
            string planName = PowerPlanCatalog.GetPlanName(mode);

            if (existing.TryGetValue(mode, out string? guid))
            {
                planGuids[mode] = guid;
                messages.Add($"Plan « {planName} » déjà présent.");
            }
            else
            {
                progress?.Report($"Création du plan « {planName} »...");
                string? newGuid = await DuplicateSchemeAsync(PowerPlanCatalog.GetBaseSchemeGuid(mode), ct).ConfigureAwait(false);
                if (newGuid == null)
                {
                    messages.Add($"Échec de création du plan « {planName} ».");
                    continue;
                }

                (int renameExit, _) = await RunPowerCfgAsync($"/changename {newGuid} \"{planName}\" \"{PowerPlanCatalog.GetPlanDescription(mode)}\"", ct).ConfigureAwait(false);
                if (renameExit != 0)
                {
                    messages.Add($"Plan créé mais renommage impossible : {planName}.");
                }

                planGuids[mode] = newGuid;
                messages.Add($"Plan « {planName} » créé.");
            }

            progress?.Report($"Configuration de « {planName} »...");
            (int planApplied, int planSkipped) = await ApplySettingsAsync(planGuids[mode], PowerPlanCatalog.GetSettings(mode), ct).ConfigureAwait(false);
            applied += planApplied;
            skipped += planSkipped;
        }

        bool success = planGuids.Count == Enum.GetValues<CoclicoPowerMode>().Length;
        return new PowerPlanSetupReport(success, planGuids, applied, skipped, messages);
    }

    /// <summary>
    /// Active un plan Coclico : le crée au besoin, puis le définit comme plan actif.
    /// </summary>
    public async Task<(bool Success, string Message)> ActivatePlanAsync(CoclicoPowerMode mode, CancellationToken ct = default)
    {
        if (!IsElevated)
        {
            return (false, "Les droits administrateur sont requis pour changer de plan d'alimentation.");
        }

        Dictionary<CoclicoPowerMode, string> plans = await FindCoclicoPlansAsync(ct).ConfigureAwait(false);
        if (!plans.TryGetValue(mode, out string? guid))
        {
            PowerPlanSetupReport report = await EnsureCoclicoPlansAsync(null, ct).ConfigureAwait(false);
            if (!report.PlanGuids.TryGetValue(mode, out guid))
            {
                return (false, $"Impossible de trouver ou de créer le plan « {PowerPlanCatalog.GetPlanName(mode)} ».");
            }
        }

        (int exit, string output) = await RunPowerCfgAsync($"/setactive {guid}", ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return (false, $"powercfg a refusé l'activation : {output.Trim()}");
        }

        return (true, $"Plan « {PowerPlanCatalog.GetPlanName(mode)} » activé.");
    }

    /// <summary>
    /// Supprime les plans Coclico après être revenu sur le plan Equilibré de Windows.
    /// </summary>
    public async Task<(bool Success, string Message)> DeleteCoclicoPlansAsync(CancellationToken ct = default)
    {
        if (!IsElevated)
        {
            return (false, "Les droits administrateur sont requis pour supprimer les plans.");
        }

        Dictionary<CoclicoPowerMode, string> plans = await FindCoclicoPlansAsync(ct).ConfigureAwait(false);
        if (plans.Count == 0)
        {
            return (true, "Aucun plan Coclico présent.");
        }

        bool anyActive = (await GetActiveSchemeAsync(ct).ConfigureAwait(false)) is { } active
            && plans.ContainsValue(active.Guid);
        if (anyActive)
        {
            _ = await RunPowerCfgAsync($"/setactive {PowerPlanCatalog.BaseBalancedGuid}", ct).ConfigureAwait(false);
        }

        foreach (string guid in plans.Values)
        {
            (int exit, _) = await RunPowerCfgAsync($"/delete {guid}", ct).ConfigureAwait(false);
            if (exit != 0)
            {
                return (false, "Un plan Coclico est utilisé par Windows et n'a pas pu être supprimé.");
            }
        }

        return (true, "Plans Coclico supprimés.");
    }

    public async Task<PowerStateSummary> GetStateSummaryAsync(CancellationToken ct = default)
    {
        PowerSchemeInfo? active = await GetActiveSchemeAsync(ct).ConfigureAwait(false);
        return new PowerStateSummary(
            ActiveSchemeName: active?.Name ?? "—",
            ActiveSchemeGuid: active?.Guid ?? string.Empty,
            ActiveCoclicoMode: active == null ? null : PowerPlanCatalog.TryGetModeFromPlanName(active.Name),
            IsElevated: IsElevated,
            HasBattery: HasBattery,
            IsOnAcPower: IsOnAcPower);
    }

    /// <summary>
    /// Retrouve les GUID des plans Coclico existants, par nom exact.
    /// Les noms sont volontairement en ASCII (choix powercfg) pour rester
    /// lisibles quelle que soit la codepage de sortie de l'outil.
    /// </summary>
    public async Task<Dictionary<CoclicoPowerMode, string>> FindCoclicoPlansAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PowerSchemeInfo> schemes = await ListSchemesAsync(ct).ConfigureAwait(false);
        Dictionary<CoclicoPowerMode, string> result = [];

        foreach (PowerSchemeInfo scheme in schemes)
        {
            CoclicoPowerMode? mode = PowerPlanCatalog.TryGetModeFromPlanName(scheme.Name);
            if (mode.HasValue)
            {
                result[mode.Value] = scheme.Guid;
            }
        }

        return result;
    }

    private static string ExtractSchemeName(string line)
    {
        int open = line.IndexOf('(');
        int close = line.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return string.Empty;
        }

        return line.Substring(open + 1, close - open - 1).Trim().TrimEnd('*').Trim();
    }

    private async Task<string?> DuplicateSchemeAsync(string baseSchemeGuid, CancellationToken ct)
    {
        (int exit, string output) = await RunPowerCfgAsync($"/duplicatescheme {baseSchemeGuid}", ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }

        Match match = GuidRegex.Match(output);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Applique les paramètres d'un plan en validant d'abord, sous-groupe par
    /// sous-groupe, que chaque paramètre existe réellement sur ce PC.
    /// </summary>
    private async Task<(int Applied, int Skipped)> ApplySettingsAsync(
        string schemeGuid,
        IReadOnlyList<PowerSettingOverride> settings,
        CancellationToken ct)
    {
        Dictionary<string, HashSet<string>> available = await QueryAvailableSettingsAsync(schemeGuid, ct).ConfigureAwait(false);

        int applied = 0;
        int skipped = 0;

        foreach (PowerSettingOverride setting in settings)
        {
            ct.ThrowIfCancellationRequested();

            if (!available.TryGetValue(setting.SubGroupGuid, out HashSet<string>? groupSettings)
                || !groupSettings.Contains(setting.SettingGuid))
            {
                skipped++;
                continue;
            }

            int acExit = (await RunPowerCfgAsync(string.Join(" ", PowerPlanCatalog.BuildSetIndexArguments(schemeGuid, setting, ac: true)), ct).ConfigureAwait(false)).ExitCode;
            int dcExit = (await RunPowerCfgAsync(string.Join(" ", PowerPlanCatalog.BuildSetIndexArguments(schemeGuid, setting, ac: false)), ct).ConfigureAwait(false)).ExitCode;

            if (acExit == 0 || dcExit == 0)
            {
                applied++;
            }
            else
            {
                skipped++;
            }
        }

        return (applied, skipped);
    }

    /// <summary>
    /// Interroge powercfg pour connaître les paramètres réellement disponibles
    /// de chaque sous-groupe utilisé par le catalogue sur ce PC précis.
    /// </summary>
    private async Task<Dictionary<string, HashSet<string>>> QueryAvailableSettingsAsync(string schemeGuid, CancellationToken ct)
    {
        Dictionary<string, HashSet<string>> result = [];
        string[] subGroups =
        [
            PowerPlanCatalog.SubProcessor,
            PowerPlanCatalog.SubUsb,
            PowerPlanCatalog.SubWireless,
            PowerPlanCatalog.SubPciExpress,
            PowerPlanCatalog.SubVideo,
            PowerPlanCatalog.SubDisk,
            PowerPlanCatalog.SubSleep
        ];

        foreach (string subGroup in subGroups)
        {
            (_, string output) = await RunPowerCfgAsync($"/q {schemeGuid} {subGroup}", ct).ConfigureAwait(false);
            HashSet<string> settingGuids = [];

            foreach (Match match in GuidRegex.Matches(output))
            {
                _ = settingGuids.Add(match.Value.ToLowerInvariant());
            }

            result[subGroup] = settingGuids;
        }

        return result;
    }

    private static async Task<(int ExitCode, string Output)> RunPowerCfgAsync(string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                // Codepage indisponible : powercfg émettra du texte dans la page par défaut.
            }

            using Process? proc = Process.Start(psi);
            if (proc == null)
            {
                return (-1, string.Empty);
            }

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
            Task exitTask = proc.WaitForExitAsync(ct);

            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);

            string output = await stdoutTask.ConfigureAwait(false);
            string error = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, $"CpuPowerService.RunPowerCfg({arguments})");
            return (-1, ex.Message);
        }
    }

    internal static class NativePower
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public static bool HasBattery()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                return false;
            }

            return status.ACLineStatus is not 255 && status.BatteryFlag is not 128 and not 255;
        }

        public static bool IsOnAcPower()
        {
            if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                return true;
            }

            return status.ACLineStatus == 1;
        }
    }
}
