using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Coclico.Services;

public sealed class AppErrorManager : IAppErrorManager, IDisposable
{
    private const int MaxFailuresBeforeDegraded = 3;
    private const int MaxFailuresBeforeFailed = 10;

    private readonly ConcurrentDictionary<AppBlock, BlockState> _states = new();
    private readonly string _errorLogDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public event EventHandler<AppBlockError>? BlockError;
    public event EventHandler<string>? BlockRecovered;
    public event EventHandler<AppBlockError>? FatalError;

    public AppErrorManager()
    {
        _errorLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Coclico", "errors");
        _ = Directory.CreateDirectory(_errorLogDir);
    }

    public void Report(AppErrorCode code, Exception? ex = null, string? detail = null)
    {
        _ = Task.Run(() => ReportAsync(code, ex, detail));
    }

    public async Task ReportAsync(AppErrorCode code, Exception? ex = null, string? detail = null)
    {
        AppBlock block = GetBlock(code);
        BlockState state = _states.GetOrAdd(block, _ => new BlockState());

        int failures = Interlocked.Increment(ref state.FailureCount);
        string message = detail ?? ex?.Message ?? code.ToString();

        var error = new AppBlockError(
            Block: block,
            Code: code,
            Message: message,
            Exception: ex,
            Timestamp: DateTime.UtcNow,
            FailureCount: failures
        );

        if (failures >= MaxFailuresBeforeFailed)
        {
            state.Status = BlockStatus.Failed;
        }
        else if (failures >= MaxFailuresBeforeDegraded)
        {
            state.Status = BlockStatus.Degraded;
        }

        await WriteErrorLogAsync(error, state.Status);

        // NOTE: events are raised from a thread-pool thread; UI consumers must marshal.
        // Handler exceptions are logged instead of being silently lost (Report is fire-and-forget).
        if (state.Status == BlockStatus.Failed)
        {
            RaiseEvent(FatalError, this, error, nameof(FatalError));
        }
        else
        {
            RaiseEvent(BlockError, this, error, nameof(BlockError));
        }

        LoggingService.LogError($"[{GetPrefix(code)}] [{code}] {message}");
        if (ex != null)
        {
            LoggingService.LogException(ex, $"{block}.{code}");
        }
    }

    public bool IsBlockDegraded(AppBlock block)
    {
        return _states.TryGetValue(block, out BlockState? s) && s.Status != BlockStatus.Healthy;
    }

    public void MarkRecovered(AppBlock block)
    {
        if (_states.TryGetValue(block, out BlockState? state))
        {
            state.Status = BlockStatus.Healthy;
            _ = Interlocked.Exchange(ref state.FailureCount, 0);
            RaiseEvent(BlockRecovered, this, block.ToString(), nameof(BlockRecovered));
        }
    }

    public IReadOnlyDictionary<AppBlock, BlockStatus> GetBlockStatuses()
    {
        var result = new Dictionary<AppBlock, BlockStatus>();
        foreach (KeyValuePair<AppBlock, BlockState> kvp in _states)
        {
            result[kvp.Key] = kvp.Value.Status;
        }

        return result;
    }

    private async Task WriteErrorLogAsync(AppBlockError error, BlockStatus status)
    {
        try
        {
            await _writeLock.WaitAsync();
            string logFile = Path.Combine(_errorLogDir, $"errors-{DateTime.UtcNow:yyyy-MM-dd}.ndjson");
            string entry = JsonSerializer.Serialize(new
            {
                timestamp = error.Timestamp,
                block = error.Block.ToString(),
                code = (int)error.Code,
                codeName = error.Code.ToString(),
                prefix = GetPrefix(error.Code),
                message = error.Message,
                exception = error.Exception?.ToString(),
                failureCount = error.FailureCount,
                status = status.ToString()
            });
            await File.AppendAllTextAsync(logFile, entry + Environment.NewLine);
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    /// <summary>
    /// Invokes each subscriber separately so one failing handler neither breaks
    /// the others nor hides its exception (Report is fire-and-forget).
    /// </summary>
    private static void RaiseEvent<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, string eventName)
    {
        if (handler == null)
        {
            return;
        }

        foreach (Delegate d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)d).Invoke(sender, args);
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, $"AppErrorManager.{eventName} handler");
            }
        }
    }

    private static AppBlock GetBlock(AppErrorCode code)
    {
        return (int)code switch
        {
            >= 10000 and < 11000 => AppBlock.AI,
            >= 11000 and < 20000 => AppBlock.AIRag,
            >= 20000 and < 30000 => AppBlock.Cleaning,
            >= 30000 and < 40000 => AppBlock.HealthDefense,
            >= 40000 and < 50000 => AppBlock.RamCleaner,
            >= 50000 and < 60000 => AppBlock.Apps,
            >= 60000 and < 70000 => AppBlock.Installer,
            >= 70000 and < 80000 => AppBlock.Security,
            >= 80000 and < 90000 => AppBlock.Power,
            >= 90000 and < 100000 => AppBlock.Settings,
            >= 100000 and < 110000 => AppBlock.UI,
            >= 110000 and < 120000 => AppBlock.System,
            >= 120000 and < 130000 => AppBlock.Network,
            >= 130000 and < 140000 => AppBlock.Updates,
            >= 140000 and < 150000 => AppBlock.DiskAnalyzer,
            >= 150000 and < 160000 => AppBlock.Dashboard,
            _ => AppBlock.System
        };
    }

    private static string GetPrefix(AppErrorCode code)
    {
        return (int)code switch
        {
            >= 10000 and < 11000 => "E-AI",
            >= 11000 and < 20000 => "E-RAG",
            >= 20000 and < 30000 => "E-CLN",
            >= 30000 and < 40000 => "E-HLT",
            >= 40000 and < 50000 => "E-RAM",
            >= 50000 and < 60000 => "E-APP",
            >= 60000 and < 70000 => "E-INS",
            >= 70000 and < 80000 => "E-SEC",
            >= 80000 and < 90000 => "E-PWR",
            >= 90000 and < 100000 => "E-CFG",
            >= 100000 and < 110000 => "E-UI",
            >= 110000 and < 120000 => "E-SYS",
            >= 120000 and < 130000 => "E-NET",
            >= 130000 and < 140000 => "E-UPD",
            >= 140000 and < 150000 => "E-DSK",
            >= 150000 and < 160000 => "E-DSB",
            _ => "E-SYS"
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }

    private sealed class BlockState
    {
        public volatile BlockStatus Status = BlockStatus.Healthy;
        public int FailureCount;
    }
}
