using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cyclop.Stores;

public sealed class ShelfItem : Observable
{
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico" };

    public ShelfItem(string path) => Path = path;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;
    public bool IsImage => ImageExtensions.Contains(System.IO.Path.GetExtension(Path));
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    ImageSource? thumbnail;
    /// Starts empty and is filled in off the UI thread: a shelf of identical
    /// PNG icons is useless when what it holds is screenshots.
    public ImageSource? Thumbnail
    {
        get => thumbnail;
        set => Set(ref thumbnail, value);
    }

    bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }
}

/// Files kept at hand. Referenced, never copied — the shelf is a holding
/// area, so a file moved away simply leaves it. The exception is clipboard
/// screenshots, which have no file until the vault writes one.
public sealed class ShelfStore : Observable
{
    public ObservableCollection<ShelfItem> Items { get; } = [];

    /// Generous, because screenshots accumulate here. Cards past the limit
    /// leave the shelf; their files stay where they are.
    const int Limit = 60;

    static string FilePath => Support.File("shelf.json");

    public ShelfStore() => Load();

    public int SelectedCount => Items.Count(i => i.IsSelected);

    /// Newest first. Several files dropped at once keep their own order.
    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Reverse())
        {
            var existing = Items.FirstOrDefault(i => SamePath(i.Path, path));
            if (existing != null)
            {
                Items.Move(Items.IndexOf(existing), 0);
                continue;
            }
            var item = new ShelfItem(path);
            Items.Insert(0, item);
            LoadThumbnail(item);
        }
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
        Persist();
    }

    /// Swaps a card's file for another in place — a vault copy for the file
    /// the system saved for the same screenshot.
    public void Replace(string oldPath, string newPath)
    {
        var old = Items.FirstOrDefault(i => SamePath(i.Path, oldPath));
        if (old == null)
        {
            Add([newPath]);
            return;
        }
        var item = new ShelfItem(newPath) { Thumbnail = old.Thumbnail };
        Items[Items.IndexOf(old)] = item;
        LoadThumbnail(item);
        Persist();
    }

    public void Remove(ShelfItem item)
    {
        Items.Remove(item);
        Raise(nameof(SelectedCount));
        Persist();
    }

    public void Clear()
    {
        Items.Clear();
        Raise(nameof(SelectedCount));
        Persist();
    }

    /// Called when the shelf comes into view: files deleted or moved away
    /// since leave it.
    public void RefreshFromDisk()
    {
        var gone = Items.Where(i => !i.Exists).ToList();
        if (gone.Count == 0) return;
        foreach (var item in gone) Items.Remove(item);
        Raise(nameof(SelectedCount));
        Persist();
    }

    // MARK: - Selection

    /// A plain click selects one card; Ctrl adds or removes, like Explorer.
    /// The clipboard then mirrors whatever is selected — picking a card is the
    /// whole gesture of handing a file onward: click, then Ctrl+V anywhere.
    public void Click(ShelfItem item, bool additive)
    {
        if (additive)
            item.IsSelected = !item.IsSelected;
        else
        {
            bool only = item.IsSelected && SelectedCount == 1;
            foreach (var other in Items) other.IsSelected = false;
            item.IsSelected = !only;
        }
        Raise(nameof(SelectedCount));
        // Clearing the selection leaves the clipboard alone: an empty write
        // would take back what was copied and hand over nothing.
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count > 0) Copy(selected);
    }

    public void ClearSelection()
    {
        foreach (var item in Items) item.IsSelected = false;
        Raise(nameof(SelectedCount));
    }

    /// Files a drag started on `item` carries: the whole selection when the
    /// grabbed card belongs to it, otherwise just that card.
    public string[] DragPaths(ShelfItem item) =>
        item.IsSelected ? Items.Where(i => i.IsSelected).Select(i => i.Path).ToArray() : [item.Path];

    /// Files for Explorer, the paths as text, and — for a single picture — the
    /// picture itself, so Ctrl+V works in a chat or an editor as well as in a
    /// folder. The picture only goes along for a single card: an app that finds
    /// an image takes it and looks no further, which would turn "four
    /// screenshots" into "the first screenshot".
    public void Copy(IReadOnlyList<ShelfItem> items)
    {
        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(items.Select(i => i.Path).ToArray());
        data.SetFileDropList(files);
        data.SetText(string.Join(Environment.NewLine, files.Cast<string>()), TextDataFormat.UnicodeText);
        data.SetData(ClipboardStore.InternalFormat, "1");

        if (items.Count == 1 && items[0].IsImage)
        {
            try
            {
                var bytes = File.ReadAllBytes(items[0].Path);
                var frame = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                data.SetImage(frame);
                if (Path.GetExtension(items[0].Path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    data.SetData("PNG", new MemoryStream(bytes));
            }
            catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
            {
                Support.Log($"cannot read {items[0].Path} for the clipboard: {error.Message}");
            }
        }
        Support.SetClipboard(data);
    }

    public void Open(ShelfItem item) => Start(item.Path);

    public void Reveal(ShelfItem item) => Support.Reveal(item.Path);

    static void Start(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Support.Log($"cannot open {path}: {error.Message}");
        }
    }

    // MARK: - Thumbnails

    void LoadThumbnail(ShelfItem item)
    {
        if (item.IsImage)
        {
            var path = item.Path;
            Task.Run(() =>
            {
                try
                {
                    // Decoded small, and read fully up front so the file is
                    // not held open (and locked) for as long as the card lives.
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    image.DecodePixelWidth = 160;
                    image.UriSource = new Uri(path);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception) { return null; }
            }).ContinueWith(t => { if (t.Result != null) item.Thumbnail = t.Result; },
                TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        try
        {
            if (!File.Exists(item.Path)) return;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(item.Path);
            if (icon == null) return;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            item.Thumbnail = source;
        }
        catch (Exception error) when (error is ArgumentException or IOException or System.ComponentModel.Win32Exception)
        {
            // A folder or an unreadable file: the card falls back to a glyph.
        }
    }

    // MARK: - Persistence

    void Load()
    {
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
            foreach (var path in paths)
            {
                var item = new ShelfItem(path);
                if (!item.Exists) continue;
                Items.Add(item);
                LoadThumbnail(item);
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Support.Log($"cannot read shelf.json: {error.Message}");
        }
    }

    void Persist()
    {
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(Items.Select(i => i.Path), new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException error) { Support.Log($"cannot write shelf.json: {error.Message}"); }
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
