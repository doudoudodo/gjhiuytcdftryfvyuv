using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Coclico.Services;

/// <summary>
/// Service d'audit haute performance, non-bloquant et basé sur un Channel asynchrone en arrière-plan.
/// </summary>
public sealed class AuditLogService : IAuditLog, IDisposable
{
    private readonly string _auditDir;
    // Wait (backpressure) instead of DropOldest: an audit entry must never be
    // silently discarded under load — producers briefly slow down instead.
    private readonly Channel<AuditEntry> _channel = Channel.CreateBounded<AuditEntry>(
        new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _consumer;
    private readonly CancellationTokenSource _cts = new();

    private const int TailReadBytes = 256 * 1024;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AuditLogService()
    {
        _auditDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Coclico", "audit");
        _ = Directory.CreateDirectory(_auditDir);
        _consumer = Task.Run(ConsumeAuditAsync);
        LoggingService.LogInfo($"[AuditLog] Initialisé (Mode Asynchrone Channel) → {_auditDir}");
    }

    public Task LogAsync(AuditEntry entry)
    {
        // Waits when the bounded channel is full (backpressure) so no audit
        // entry is dropped; the consumer drains it in the background.
        return _channel.Writer.WriteAsync(entry).AsTask();
    }

    private async Task ConsumeAuditAsync()
    {
        string? currentPath = null;
        FileStream? stream = null;
        StreamWriter? writer = null;

        try
        {
            await foreach (AuditEntry? entry in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                string expectedPath = CurrentAuditFilePath();
                if (currentPath != expectedPath)
                {
                    writer?.Dispose();
                    stream?.Dispose();
                    currentPath = expectedPath;
                    stream = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, true);
                    writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
                }

                if (writer != null)
                {
                    string json = JsonSerializer.Serialize(entry, _json);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    if (_channel.Reader.Count == 0)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuditLogService] Consumer error: {ex}");
        }
        finally
        {
            try
            {
                if (writer != null)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    writer.Dispose();
                }
                stream?.Dispose();
            }
            catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int maxCount = 100)
    {
        var entries = new List<AuditEntry>(maxCount);

        try
        {
            string[] files = Directory.GetFiles(_auditDir, "audit-*.log")
                .OrderByDescending(f => f)
                .ToArray();

            foreach (string? file in files)
            {
                if (entries.Count >= maxCount)
                {
                    break;
                }

                await foreach (string? line in ReadLinesFromEndAsync(file).ConfigureAwait(false))
                {
                    if (entries.Count >= maxCount)
                    {
                        break;
                    }

                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        continue;
                    }

                    try
                    {
                        AuditEntry? entry = JsonSerializer.Deserialize<AuditEntry>(trimmed, _json);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                    catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AuditLogService.GetRecentAsync");
        }

        return entries.AsReadOnly();
    }

    /// <summary>
    /// Reads only the tail of the file (last <see cref="TailReadBytes"/> bytes)
    /// instead of loading the whole file in memory, then yields lines from the end.
    /// </summary>
    private static async IAsyncEnumerable<string> ReadLinesFromEndAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        string[] allLines = [];
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            long tailLength = Math.Min(stream.Length, TailReadBytes);
            stream.Seek(-tailLength, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? content = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(content))
            {
                allLines = content.Split('\n');
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }

        for (int i = allLines.Length - 1; i >= 0; i--)
        {
            yield return allLines[i];
        }
    }

    private string CurrentAuditFilePath()
    {
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        return Path.Combine(_auditDir, $"audit-{today}.log");
    }

    public void Prune(TimeSpan olderThan)
    {
        try
        {
            // Prune by the date embedded in the file name (audit-YYYY-MM-DD.log):
            // reliable even if the file was copied, unlike CreationTimeUtc.
            DateTime cutoff = DateTime.UtcNow - olderThan;
            foreach (string file in Directory.GetFiles(_auditDir, "audit-*.log"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 6 &&
                    DateTime.TryParseExact(name["audit-".Length..], "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime fileDate) &&
                    fileDate < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "AuditLogService.Prune");
        }
    }

    public void Dispose()
    {
        _ = _channel.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _ = _consumer.Wait(1000);
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        _cts.Dispose();
    }
}
