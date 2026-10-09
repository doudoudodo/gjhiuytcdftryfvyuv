namespace Coclico.Services;

/// <summary>
/// Point d'entrée centralisé et ultra-léger pour toutes les opérations de journalisation,
/// d'audit et de télémétrie de l'application Coclico.
/// </summary>
public static class AppLog
{
    public static void Info(string message) => LoggingService.LogInfo(message);

    public static void Warn(string message) => LoggingService.LogWarning(message);

    public static void Error(string message, Exception? ex = null)
    {
        LoggingService.LogError(message);
        if (ex != null)
        {
            LoggingService.LogException(ex, message);
        }
    }

    public static void Exception(Exception ex, string? context = null)
    {
        LoggingService.LogException(ex, context);
    }

    /// <summary>
    /// Enregistre une action utilisateur ou système dans l'audit en arrière-plan non bloquant.
    /// Une écriture d'audit qui échoue est journalisée au lieu d'être silencieusement perdue.
    /// </summary>
    public static void Action(string action, string target, bool success = true, string? details = null, string actor = "User")
    {
        LoggingService.LogInfo($"[Action] {actor} → {action} on {target} (Success: {success})");
        try
        {
            IAuditLog? audit = ServiceContainer.GetOptional<IAuditLog>();
            if (audit != null)
            {
                var entry = new AuditEntry(
                    Timestamp: DateTimeOffset.UtcNow,
                    Actor: actor,
                    Action: action,
                    Target: target,
                    Success: success,
                    Details: details
                );
                _ = WriteAuditEntryAsync(audit, entry);
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
    }

    private static async Task WriteAuditEntryAsync(IAuditLog audit, AuditEntry entry)
    {
        try
        {
            await audit.LogAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, $"AppLog.Action({entry.Action})");
        }
    }
}
