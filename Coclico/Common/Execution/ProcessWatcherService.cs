using System.Diagnostics;
using System.IO;
using System.Management;

namespace Coclico.Services;

/// <summary>
/// Watches process start/stop via WMI (__InstanceCreation/DeletionEvent, 3 s polling).
/// NOTE: events are raised on a WMI thread-pool thread; UI consumers must marshal.
/// </summary>
public sealed class ProcessWatcherService : IDisposable
{

    private ManagementEventWatcher? _startWatcher;
    private ManagementEventWatcher? _stopWatcher;
    private readonly object _watcherLock = new();

    private EventHandler<string>? _processStarted;
    public event EventHandler<string>? ProcessStarted
    {
        add
        {
            lock (_watcherLock)
            {
                _processStarted += value;
                EnsureStartWatcher();
            }
        }
        remove
        {
            lock (_watcherLock)
            {
                _processStarted -= value;
                if (_processStarted == null)
                {
                    StopStartWatcher();
                }
            }
        }
    }

    private EventHandler<string>? _processStopped;
    public event EventHandler<string>? ProcessStopped
    {
        add
        {
            lock (_watcherLock)
            {
                _processStopped += value;
                EnsureStopWatcher();
            }
        }
        remove
        {
            lock (_watcherLock)
            {
                _processStopped -= value;
                if (_processStopped == null)
                {
                    StopStopWatcher();
                }
            }
        }
    }

    public ProcessWatcherService()
    {
    }

    private void EnsureStartWatcher()
    {
        if (_startWatcher != null)
        {
            return;
        }

        try
        {
            _startWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'"));
            _startWatcher.EventArrived += OnProcessStarted;
            _startWatcher.Start();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ProcessWatcherService.StartWatcherInit");
        }
    }

    private void StopStartWatcher()
    {
        try
        {
            if (_startWatcher != null)
            {
                _startWatcher.EventArrived -= OnProcessStarted;
                _startWatcher.Stop();
                _startWatcher.Dispose();
                _startWatcher = null;
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
    }

    private void EnsureStopWatcher()
    {
        if (_stopWatcher != null)
        {
            return;
        }

        try
        {
            _stopWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 3 WHERE TargetInstance ISA 'Win32_Process'"));
            _stopWatcher.EventArrived += OnProcessStopped;
            _stopWatcher.Start();
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ProcessWatcherService.StopWatcherInit");
        }
    }

    private void StopStopWatcher()
    {
        try
        {
            if (_stopWatcher != null)
            {
                _stopWatcher.EventArrived -= OnProcessStopped;
                _stopWatcher.Stop();
                _stopWatcher.Dispose();
                _stopWatcher = null;
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
    }

    private void OnProcessStarted(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string? processName = targetInstance["Name"]?.ToString();
            if (processName != null)
            {
                _processStarted?.Invoke(this, processName);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ProcessWatcherService.OnProcessStarted");
        }
    }

    private void OnProcessStopped(object sender, EventArrivedEventArgs e)
    {
        try
        {
            using var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            string? processName = targetInstance["Name"]?.ToString();
            if (processName != null)
            {
                _processStopped?.Invoke(this, processName);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "ProcessWatcherService.OnProcessStopped");
        }
    }

    public void Dispose()
    {
        lock (_watcherLock)
        {
            StopStartWatcher();
            StopStopWatcher();
        }
        GC.SuppressFinalize(this);
    }
}
