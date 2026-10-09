using System.IO;
using System.Text.Json;
using Coclico.Models.Network;

namespace Coclico.Services.Network;

public sealed class NetworkMemoryService : INetworkMemoryService
{
    private static readonly string MemoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Coclico", "network_memory.json");

    private NetworkMemoryStore _store = new();
    private readonly object _lock = new();

    public NetworkMemoryService()
    {
        LoadStore();
    }

    private void LoadStore()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(MemoryFilePath))
                {
                    string json = File.ReadAllText(MemoryFilePath);
                    NetworkMemoryStore? loaded = JsonSerializer.Deserialize<NetworkMemoryStore>(json);
                    if (loaded != null)
                    {
                        _store = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.LogException(ex, "NetworkMemoryService.LoadStore");
                _store = new NetworkMemoryStore();
            }
        }
    }

    private async Task SaveStoreAsync(CancellationToken ct)
    {
        string json;
        lock (_lock)
        {
            _store.LastUpdatedUtc = DateTime.UtcNow;
            json = JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true });
        }

        try
        {
            string? dir = Path.GetDirectoryName(MemoryFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }
            string tmp = MemoryFilePath + ".tmp"; await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false); File.Move(tmp, MemoryFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "NetworkMemoryService.SaveStoreAsync");
        }
    }

    public HardwareKnowledgeProfile? GetProfile(string hardwareFingerprint)
    {
        if (string.IsNullOrWhiteSpace(hardwareFingerprint))
        {
            return null;
        }

        lock (_lock)
        {
            return _store.Profiles.TryGetValue(hardwareFingerprint, out HardwareKnowledgeProfile? p) ? p : null;
        }
    }

    public async Task RecordExperimentAsync(
        string hardwareFingerprint,
        string adapterDesc,
        string driverVer,
        ExperimentRecord experiment,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hardwareFingerprint))
        {
            return;
        }

        lock (_lock)
        {
            if (!_store.Profiles.TryGetValue(hardwareFingerprint, out HardwareKnowledgeProfile? profile))
            {
                profile = new HardwareKnowledgeProfile
                {
                    HardwareFingerprint = hardwareFingerprint,
                    AdapterDescription = adapterDesc,
                    DriverVersion = driverVer
                };
                _store.Profiles[hardwareFingerprint] = profile;
            }

            profile.LastOptimizedUtc = DateTime.UtcNow;
            profile.HistoricalExperiments.Add(experiment);

            if (profile.HistoricalExperiments.Count > 50)
            {
                profile.HistoricalExperiments.RemoveAt(0);
            }
        }

        await SaveStoreAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveProvenBestSettingAsync(
        string hardwareFingerprint,
        string keyword,
        string value,
        double score,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hardwareFingerprint))
        {
            return;
        }

        lock (_lock)
        {
            if (!_store.Profiles.TryGetValue(hardwareFingerprint, out HardwareKnowledgeProfile? profile))
            {
                profile = new HardwareKnowledgeProfile
                {
                    HardwareFingerprint = hardwareFingerprint,
                    LastOptimizedUtc = DateTime.UtcNow
                };
                _store.Profiles[hardwareFingerprint] = profile;
            }

            profile.ProvenBestSettings[keyword] = value;
            if (score > profile.BestScoreAchieved)
            {
                profile.BestScoreAchieved = score;
            }
        }

        await SaveStoreAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkKeywordHarmfulAsync(
        string hardwareFingerprint,
        string keyword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hardwareFingerprint))
        {
            return;
        }

        lock (_lock)
        {
            if (!_store.Profiles.TryGetValue(hardwareFingerprint, out HardwareKnowledgeProfile? profile))
            {
                profile = new HardwareKnowledgeProfile
                {
                    HardwareFingerprint = hardwareFingerprint,
                    LastOptimizedUtc = DateTime.UtcNow
                };
                _store.Profiles[hardwareFingerprint] = profile;
            }

            if (!profile.KnownHarmfulKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                profile.KnownHarmfulKeywords.Add(keyword);
            }
        }

        await SaveStoreAsync(ct).ConfigureAwait(false);
    }
}

