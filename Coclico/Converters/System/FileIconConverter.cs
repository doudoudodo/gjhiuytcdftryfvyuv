using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Coclico.Converters;

public class FileIconConverter : IValueConverter
{
    // LRU cache: once full, the least recently used icon is evicted so newly
    // listed programs still get their icon (previously entries beyond 500 were
    // silently never cached).
    private static readonly LruIconCache _cache = new(MaxCacheSize);
    private const int MaxCacheSize = 500;

    private static readonly BitmapSource _placeholder = CreatePlaceholder();

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static BitmapSource CreatePlaceholder()
    {
        var bmp = new WriteableBitmap(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        bmp.Freeze();
        return bmp;
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
        {
            return _placeholder;
        }

        if (_cache.TryGet(path, out BitmapSource? cached))
        {
            return cached;
        }

        _ = Task.Run(() => LoadIconAsync(path));
        return _placeholder;
    }

    private static async Task LoadIconAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || _cache.TryGet(path, out _))
            {
                return;
            }

            BitmapSource? bitmap = await Task.Run(() => ExtractIcon(path));
            if (bitmap != null)
            {
                _cache.Add(path, bitmap);
            }
        }
        catch (Exception ex) { Services.LoggingService.LogException(ex, "FileIconConverter.LoadIconAsync"); }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public static Task PreloadAllAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var list = new List<string>(paths);
        if (list.Count == 0)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            foreach (string path in list)
            {
                if (string.IsNullOrEmpty(path) || _cache.TryGet(path, out _))
                {
                    continue;
                }

                try
                {
                    BitmapSource? bitmap = ExtractIcon(path);
                    if (bitmap != null)
                    {
                        _cache.Add(path, bitmap);
                    }
                }
                catch (Exception exSwallow) { Coclico.Services.LoggingService.LogException(exSwallow, "SwallowedException"); }
            }
            tcs.SetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>
    /// Extracts the associated icon of a file into a frozen WPF bitmap.
    /// Every GDI handle (Icon, HBITMAP) is released before returning.
    /// </summary>
    private static BitmapSource? ExtractIcon(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using Icon? icon = Icon.ExtractAssociatedIcon(path);
        if (icon == null)
        {
            return null;
        }

        using var bmp = icon.ToBitmap();
        IntPtr hBitmap = bmp.GetHbitmap();
        try
        {
            BitmapSource src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            // CreateBitmapSourceFromHBitmap copies the pixels: the GDI bitmap
            // must be released or it leaks one handle per extracted icon.
            _ = DeleteObject(hBitmap);
        }
    }

    private sealed class LruIconCache(int capacity)
    {
        private readonly LinkedList<string> _lru = [];
        private readonly Dictionary<string, (LinkedListNode<string> Node, BitmapSource Value)> _map = new();
        private readonly object _lock = new();

        public bool TryGet(string key, out BitmapSource? value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                    value = entry.Value;
                    return true;
                }

                value = null;
                return false;
            }
        }

        public void Add(string key, BitmapSource value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    _map[key] = (entry.Node, value);
                    _lru.Remove(entry.Node);
                    _lru.AddFirst(entry.Node);
                    return;
                }

                var node = new LinkedListNode<string>(key);
                _lru.AddFirst(node);
                _map[key] = (node, value);

                while (_map.Count > capacity && _lru.Last != null)
                {
                    string oldest = _lru.Last.Value;
                    _ = _map.Remove(oldest);
                    _lru.RemoveLast();
                }
            }
        }
    }
}
