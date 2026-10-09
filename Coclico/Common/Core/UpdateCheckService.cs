using System.Reflection;
using System.Timers;
using Microsoft.Extensions.Logging;

namespace Coclico.Services;

public class UpdateCheckService
{
    private readonly UpdateManager _updateManager;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly SettingsService _settingsService;
    private System.Timers.Timer? _updateCheckTimer;

    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    public event EventHandler<Exception>? CheckFailed;

    // Single instance owned by the DI container: no hand-rolled singleton,
    // dependencies are injected once instead of being frozen by the first caller.
    public UpdateCheckService(UpdateManager updateManager, ILogger<UpdateCheckService> logger, SettingsService settingsService)
    {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public void Start()
    {
        if (IsRunning)
        {
            _logger.LogWarning("UpdateCheckService is already running");
            return;
        }

        _logger.LogInformation("Starting UpdateCheckService (startup check, then roughly every 6 hours)");

        _ = CheckForUpdatesAsync();

        // Vérification au démarrage puis toutes les ~6 heures, avec un léger décalage
        // aléatoire pour répartir les appels API GitHub entre les installations.
        TimeSpan interval = TimeSpan.FromHours(6) + TimeSpan.FromMinutes(Random.Shared.Next(0, 30));
        _updateCheckTimer = new System.Timers.Timer(interval.TotalMilliseconds);
        // System.Timers.Timer Elapsed handlers must not be 'async void': the check
        // (which catches its own exceptions) is observed on the thread pool.
        _updateCheckTimer.Elapsed += (_, _) => _ = CheckForUpdatesAsync();
        _updateCheckTimer.AutoReset = true;
        _updateCheckTimer.Start();

        IsRunning = true;
    }

    public void Stop()
    {
        if (_updateCheckTimer != null)
        {
            _updateCheckTimer.Stop();
            _updateCheckTimer.Dispose();
            _updateCheckTimer = null;
        }
        IsRunning = false;
        _logger.LogInformation("UpdateCheckService stopped");
    }

    public async Task<GitHubRelease?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation($"Checking for updates (current version: {CurrentVersion})");

        try
        {
            GitHubRelease? release = await _updateManager.CheckForUpdatesAsync(CurrentVersion, ct);

            if (release != null)
            {
                _logger.LogInformation($"Update available: {release.TagName}");
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(release));
            }
            else
            {
                _logger.LogInformation("Already up-to-date");
            }

            return release;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to check for updates: {ex.Message}");
            CheckFailed?.Invoke(this, ex);
            return null;
        }
    }

    public bool IsRunning { get; private set; } = false;

    public string CurrentVersion { get; } = GetCurrentVersion();

    public static string GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
            ?? "1.0.0";
    }
}

public class UpdateAvailableEventArgs(GitHubRelease release) : EventArgs
{
    public GitHubRelease Release { get; } = release;
}
