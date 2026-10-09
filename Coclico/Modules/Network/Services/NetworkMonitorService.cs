using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Coclico.Services;

public record NetworkStats(
    DateTime Timestamp,
    long TotalBytesSent,
    long TotalBytesReceived,
    double UploadKbps,
    double DownloadKbps,
    int ActiveTcpConnections,
    double PingMs);

public class NetworkMonitorService : IDisposable
{

    private readonly BehaviorSubject<NetworkStats> _subject;
    private readonly System.Timers.Timer _timer;
    private DateTime _lastSnapshot = DateTime.UtcNow;

    private readonly ConcurrentDictionary<string, (long Sent, long Received)> _lastInterfaceBytes = new();

    private long _prevTotalSent;
    private long _prevTotalReceived;

    public IObservable<NetworkStats> StatsStream => _subject.AsObservable();
    public NetworkStats CurrentStats => _subject.Value;

    private int _isRefreshing = 0;

    private async void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _isRefreshing, 1, 0) != 0)
        {
            return;
        }

        try { await RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex) { LoggingService.LogException(ex, nameof(NetworkMonitorService)); }
        finally { _ = Interlocked.Exchange(ref _isRefreshing, 0); }
    }

    public NetworkMonitorService()
    {
        _subject = new BehaviorSubject<NetworkStats>(new NetworkStats(
            DateTime.UtcNow, 0, 0, 0, 0, 0, 0));

        _timer = new System.Timers.Timer(10000)
        {
            AutoReset = true,
            Enabled = true
        };
        _timer.Elapsed += OnTimerElapsed;

        _ = RefreshAsync();
    }

    private async Task<double> ProbeLatencyAsync()
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync("1.1.1.1", 1000).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        return -1;
    }

    private async Task RefreshAsync()
    {
        try
        {
            DateTime now = DateTime.UtcNow;

            long totalSent = 0, totalReceived = 0;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;

                _ = _lastInterfaceBytes.AddOrUpdate(ni.Id,
                    _ => (stats.BytesSent, stats.BytesReceived),
                    (_, _) => (stats.BytesSent, stats.BytesReceived));
            }

            double elapsed = Math.Max((now - _lastSnapshot).TotalSeconds, 0.001);

            long prevSent = Interlocked.Exchange(ref _prevTotalSent, totalSent);
            long prevReceived = Interlocked.Exchange(ref _prevTotalReceived, totalReceived);

            double uploadKbps = Math.Max(0, (totalSent - prevSent) * 8.0 / 1024.0 / elapsed);
            double downloadKbps = Math.Max(0, (totalReceived - prevReceived) * 8.0 / 1024.0 / elapsed);

            _lastSnapshot = now;

            int tcpConnections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Length;
            double pingMs = await ProbeLatencyAsync().ConfigureAwait(false);

            var snapshot = new NetworkStats(now, totalSent, totalReceived, uploadKbps, downloadKbps, tcpConnections, pingMs);
            _subject.OnNext(snapshot);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "NetworkMonitorService.RefreshAsync");
        }
    }

    public void Dispose()
    {
        try { _timer.Stop(); _timer.Dispose(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        try { _subject.OnCompleted(); _subject.Dispose(); } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        GC.SuppressFinalize(this);
    }
}
