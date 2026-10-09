using System.IO;
using Serilog;

namespace Coclico.Services;

/// <summary>
/// Journalisation Coclico : façade unique au-dessus de Serilog.
/// Une seule destination (coclico-*.log, rétention 14 jours), un seul format ;
/// chaque message n'est plus écrit deux fois et le flush est géré par Serilog.
/// </summary>
public static class LoggingService
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Coclico", "logs");

    private static bool _serilogConfigured;

    /// <summary>
    /// Configures the shared Serilog logger once. Called lazily by this class
    /// and explicitly by ServiceContainer.Build so early startup messages
    /// (before the container exists) are not lost.
    /// </summary>
    public static void EnsureSerilog()
    {
        if (_serilogConfigured)
        {
            return;
        }

        lock (typeof(LoggingService))
        {
            if (_serilogConfigured)
            {
                return;
            }

            try
            {
                _ = Directory.CreateDirectory(LogDir);
                PurgeLegacyLogs();
            }
            catch { }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(LogDir, "coclico-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .Enrich.FromLogContext()
                .CreateLogger();

            _serilogConfigured = true;
        }
    }

    static LoggingService()
    {
        EnsureSerilog();
    }

    public static void LogInfo(string message)
    {
        try { Log.Information("{Message}", message); } catch { }
    }

    public static void LogWarning(string message)
    {
        try { Log.Warning("{Message}", message); } catch { }
    }

    public static void LogError(string message)
    {
        try { Log.Error("{Message}", message); } catch { }
    }

    public static void LogDebug(string message)
    {
        try { Log.Debug("{Message}", message); } catch { }
    }

    public static void LogException(Exception? ex, string? context = null)
    {
        if (ex == null)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrEmpty(context))
            {
                Log.Error(ex, "[{Context}] {Message}", context, ex.Message);
            }
            else
            {
                Log.Error(ex, "{Message}", ex.Message);
            }
        }
        catch { }
    }

    public static Task ShutdownAsync()
    {
        try { Log.CloseAndFlush(); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// One-time cleanup of the legacy dual-logging files (log_YYYYMMDD.txt,
    /// written by the removed house-made channel): they are no longer
    /// produced and are purged after 14 days like the Serilog files.
    /// </summary>
    private static void PurgeLegacyLogs()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (string file in Directory.GetFiles(LogDir, "log_*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
