using System.IO;
using System.Text.Json;

namespace Coclico.Services;

public sealed class CacheService : ICacheService
{
    private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Coclico", "cache");

    private static readonly JsonSerializerOptions _serializeOptions = new() { WriteIndented = false };

    private const int LockStripeCount = 32;
    private static readonly SemaphoreSlim[] _stripedLocks =
        System.Linq.Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim GetLock(string key)
    {
        return _stripedLocks[Math.Abs(key.GetHashCode() % LockStripeCount)];
    }

    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();
    private static string SanitizeKey(string key)
    {
        return string.Concat(key.Select(c => Array.IndexOf(_invalidChars, c) >= 0 ? '_' : c));
    }

    public CacheService()
    {
        _ = Directory.CreateDirectory(CacheDir);
    }

    /// <summary>
    /// Writes a cache file atomically (.tmp then File.Move) so a crash can never
    /// leave a half-written JSON entry behind.
    /// </summary>
    private static void WriteAtomically(string path, Action<FileStream> write)
    {
        string tmpPath = path + ".tmp";
        try
        {
            using (FileStream stream = File.Create(tmpPath))
            {
                write(stream);
            }

            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) { File.Delete(tmpPath); } } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            throw;
        }
    }

    private static async Task WriteAtomicallyAsync(string path, Func<FileStream, Task> write)
    {
        string tmpPath = path + ".tmp";
        try
        {
            await using (FileStream stream = File.Create(tmpPath))
            {
                await write(stream).ConfigureAwait(false);
            }

            File.Move(tmpPath, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmpPath)) { File.Delete(tmpPath); } } catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
            throw;
        }
    }

    public void Set<T>(string key, T value, TimeSpan? ttl = null)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            var entry = new CacheEntry<T>
            {
                Value = value,
                ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue,
                CreatedAt = DateTime.UtcNow
            };
            WriteAtomically(path, stream => JsonSerializer.Serialize(stream, entry, _serializeOptions));
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Set");
        }
        finally { _ = sem.Release(); }
    }

    public T? Get<T>(string key)
    {
        return TryGet<T>(key, out T? value) ? value : default;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            if (!File.Exists(path))
            {
                value = default;
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            CacheEntry<T>? entry = JsonSerializer.Deserialize<CacheEntry<T>>(stream, _serializeOptions);
            if (entry == null)
            {
                value = default;
                return false;
            }

            if (DateTime.UtcNow > entry.ExpiresAt)
            {
                File.Delete(path);
                value = default;
                return false;
            }
            value = entry.Value;
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.TryGet");
            value = default;
            return false;
        }
        finally { _ = sem.Release(); }
    }

    public bool Has(string key)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("ExpiresAt", out JsonElement expiryEl))
            {
                if (DateTime.TryParse(expiryEl.GetString(), out DateTime expiry))
                {
                    return DateTime.UtcNow <= expiry;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Has");
            return false;
        }
        finally { _ = sem.Release(); }
    }

    public void Set<T>(string subdir, string key, T value, TimeSpan? ttl = null)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            _ = Directory.CreateDirectory(GetSubdirPath(subdir));
            var entry = new CacheEntry<T>
            {
                Value = value,
                ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue,
                CreatedAt = DateTime.UtcNow
            };
            WriteAtomically(path, stream => JsonSerializer.Serialize(stream, entry, _serializeOptions));
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Set(subdir)");
        }
        finally { _ = sem.Release(); }
    }

    public T? Get<T>(string subdir, string key)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            using FileStream stream = File.OpenRead(path);
            CacheEntry<T>? entry = JsonSerializer.Deserialize<CacheEntry<T>>(stream, _serializeOptions);
            if (entry == null)
            {
                return default;
            }

            if (DateTime.UtcNow > entry.ExpiresAt) { File.Delete(path); return default; }
            return entry.Value;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Get(subdir)");
            return default;
        }
        finally { _ = sem.Release(); }
    }

    public bool Has(string subdir, string key)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using FileStream stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("ExpiresAt", out JsonElement expiryEl))
            {
                if (DateTime.TryParse(expiryEl.GetString(), out DateTime expiry))
                {
                    return DateTime.UtcNow <= expiry;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Has(subdir)");
            return false;
        }
        finally { _ = sem.Release(); }
    }

    public void Invalidate(string subdir, string key)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try { File.Delete(path); }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.Invalidate(subdir)"); }
        finally { _ = sem.Release(); }
    }

    public void ClearSubdir(string subdir)
    {
        try
        {
            string dir = GetSubdirPath(subdir);
            if (!Directory.Exists(dir))
            {
                return;
            }

            // Delete each entry under its own stripe lock to avoid racing a concurrent Get/Set.
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                SemaphoreSlim sem = GetLock(f);
                sem.Wait();
                try { File.Delete(f); }
                catch { /* best effort */ }
                finally { _ = sem.Release(); }
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.ClearSubdir");
        }
    }

    public void Invalidate(string key)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        sem.Wait();
        try { File.Delete(path); }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.Invalidate(key)"); }
        finally { _ = sem.Release(); }
    }

    public void Clear()
    {
        try
        {
            foreach (string f in Directory.GetFiles(CacheDir, "*.json", SearchOption.AllDirectories))
            {
                SemaphoreSlim sem = GetLock(f);
                sem.Wait();
                try { File.Delete(f); }
                catch { /* best effort */ }
                finally { _ = sem.Release(); }
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.Clear");
        }
    }

    public long GetCacheSizeBytes()
    {
        try
        {
            long total = 0;
            foreach (string f in Directory.GetFiles(CacheDir, "*.json", SearchOption.AllDirectories))
            {
                total += new FileInfo(f).Length;
            }

            return total;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.GetCacheSizeBytes");
            return 0;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = new CacheEntry<T>
            {
                Value = value,
                ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue,
                CreatedAt = DateTime.UtcNow
            };
            await WriteAtomicallyAsync(path, stream => JsonSerializer.SerializeAsync(stream, entry, _serializeOptions)).ConfigureAwait(false);
        }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.SetAsync"); }
        finally { _ = sem.Release(); }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        (bool found, T? value) = await TryGetAsync<T>(key).ConfigureAwait(false);
        return found ? value : default;
    }

    public async Task<(bool Found, T? Value)> TryGetAsync<T>(string key)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return (false, default);
            }

            await using FileStream stream = File.OpenRead(path);
            CacheEntry<T>? entry = await JsonSerializer.DeserializeAsync<CacheEntry<T>>(stream, _serializeOptions).ConfigureAwait(false);
            if (entry == null)
            {
                return (false, default);
            }

            if (DateTime.UtcNow > entry.ExpiresAt) { File.Delete(path); return (false, default); }
            return (true, entry.Value);
        }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.TryGetAsync"); return (false, default); }
        finally { _ = sem.Release(); }
    }

    public async Task<bool> HasAsync(string key)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            await using FileStream stream = File.OpenRead(path);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("ExpiresAt", out JsonElement expiryEl))
            {
                if (DateTime.TryParse(expiryEl.GetString(), out DateTime expiry))
                {
                    return DateTime.UtcNow <= expiry;
                }
            }
            return true;
        }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.HasAsync"); return false; }
        finally { _ = sem.Release(); }
    }

    public async Task InvalidateAsync(string key)
    {
        string path = GetPath(key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.InvalidateAsync"); }
        finally { _ = sem.Release(); }
    }

    public async Task ClearAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (string f in Directory.GetFiles(CacheDir, "*.json", SearchOption.AllDirectories))
                {
                    SemaphoreSlim sem = GetLock(f);
                    sem.Wait();
                    try { File.Delete(f); }
                    catch { /* best effort */ }
                    finally { _ = sem.Release(); }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { LoggingService.LogException(ex, "CacheService.ClearAsync"); }
    }

    public async Task SetAsync<T>(string subdir, string key, T value, TimeSpan? ttl = null)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            _ = Directory.CreateDirectory(GetSubdirPath(subdir));
            var entry = new CacheEntry<T>
            {
                Value = value,
                ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : DateTime.MaxValue,
                CreatedAt = DateTime.UtcNow
            };
            await WriteAtomicallyAsync(path, stream => JsonSerializer.SerializeAsync(stream, entry, _serializeOptions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.SetAsync(subdir)");
        }
        finally { _ = sem.Release(); }
    }

    public async Task<T?> GetAsync<T>(string subdir, string key)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            await using FileStream stream = File.OpenRead(path);
            CacheEntry<T>? entry = await JsonSerializer.DeserializeAsync<CacheEntry<T>>(stream, _serializeOptions).ConfigureAwait(false);
            if (entry == null)
            {
                return default;
            }

            if (DateTime.UtcNow > entry.ExpiresAt)
            {
                File.Delete(path);
                return default;
            }

            return entry.Value;
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.GetAsync(subdir)");
            return default;
        }
        finally { _ = sem.Release(); }
    }

    public async Task InvalidateAsync(string subdir, string key)
    {
        string path = GetPath(subdir, key);
        SemaphoreSlim sem = GetLock(path);
        await sem.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.InvalidateAsync(subdir)");
        }
        finally { _ = sem.Release(); }
    }

    public async Task ClearSubdirAsync(string subdir)
    {
        try
        {
            string dir = GetSubdirPath(subdir);
            if (!Directory.Exists(dir))
            {
                return;
            }

            await Task.Run(() =>
            {
                foreach (string f in Directory.GetFiles(dir, "*.json"))
                {
                    SemaphoreSlim sem = GetLock(f);
                    sem.Wait();
                    try { File.Delete(f); }
                    catch { /* best effort */ }
                    finally { _ = sem.Release(); }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggingService.LogException(ex, "CacheService.ClearSubdirAsync");
        }
    }

    private string GetSubdirPath(string subdir)
    {
        return Path.Combine(CacheDir, SanitizeKey(subdir));
    }

    private string GetPath(string subdir, string key)
    {
        return Path.Combine(GetSubdirPath(subdir), SanitizeKey(key) + ".cache.json");
    }

    private string GetPath(string key)
    {
        return Path.Combine(CacheDir, SanitizeKey(key) + ".cache.json");
    }

    private class CacheEntry<T>
    {
        public T? Value { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
