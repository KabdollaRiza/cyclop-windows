using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cyclop.Stores;

/// Where clipboard screenshots are kept: `Pictures\Cyclop`.
///
/// A screenshot taken to the clipboard (PrtSc, Win+Shift+S) exists only in
/// memory: paste it once and copy something else, and it is gone. The vault
/// writes it to disk so the shelf can hold on to it. Nothing here is ever
/// deleted automatically — the folder is the user's.
public static class ScreenshotVault
{
    public static string Folder
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Cyclop");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string? Save(BitmapSource image, DateTime at)
    {
        var folder = Folder;
        var stem = $"Screenshot {at:yyyy-MM-dd} at {at:HH.mm.ss}";
        var path = Path.Combine(folder, stem + ".png");
        // Two screenshots inside one second would otherwise collide.
        for (int attempt = 2; File.Exists(path); attempt++)
            path = Path.Combine(folder, $"{stem} ({attempt}).png");

        try
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(path);
            png.Save(file);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Support.Log($"cannot save screenshot: {error.Message}");
            return null;
        }
    }
}

/// Reads a picture off the clipboard, whichever way it was put there.
static class ClipboardImage
{
    public static BitmapSource? Read(IDataObject data)
    {
        try
        {
            // Snipping Tool and browsers offer PNG: lossless and with the
            // alpha channel right, so it wins whenever it is there.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                return Decode(png);
            // PrtSc and most other apps offer a device-independent bitmap.
            if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dib)
                return FromDib(dib.ToArray());
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                return Plain(bitmap);
        }
        catch (Exception error) when (error is NotSupportedException or FileFormatException
                                          or ArgumentException or COMException or IndexOutOfRangeException)
        {
            Support.Log($"cannot read the clipboard image: {error.Message}");
        }
        return null;
    }

    /// Copied out into a plain bitmap: a decoded frame stays tied to its
    /// decoder, which belongs to the UI thread, and the PNG encode that saves
    /// it runs on another.
    static BitmapSource Decode(Stream stream) =>
        Plain(BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]);

    static BitmapSource Plain(BitmapSource frame)
    {
        int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY,
            frame.Format, frame.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// A DIB is a BMP file without its 14-byte file header. Putting the header
    /// back lets the stock BMP decoder read it, which is sturdier than WPF's
    /// `Clipboard.GetImage`: that one reads the unused fourth byte of a 32-bit
    /// DIB as alpha and hands back a fully transparent screenshot.
    static BitmapSource FromDib(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colorsUsed = BitConverter.ToInt32(dib, 32);
        const int BI_BITFIELDS = 3;
        int masks = compression == BI_BITFIELDS && headerSize == 40 ? 12 : 0;
        int palette = (bitCount <= 8 && colorsUsed == 0 ? 1 << bitCount : colorsUsed) * 4;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(file, 10);
        dib.CopyTo(file, 14);

        var decoded = BitmapDecoder.Create(new MemoryStream(file), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        // A screenshot has no transparency to lose; dropping the channel is
        // what keeps a zeroed "alpha" byte from blanking the picture.
        return Plain(new FormatConvertedBitmap(decoded, PixelFormats.Bgr32, null, 0));
    }
}

/// Catches screenshots from every place Windows puts them and hands each one
/// to the shelf exactly once.
///
/// Windows has two routes, and a single snip often takes both:
/// - the clipboard — PrtSc, Alt+PrtSc, Win+Shift+S, Snipping Tool, and most
///   third-party tools. Those pictures are saved to the vault.
/// - a folder — Win+PrtSc and Snipping Tool's auto-save write to
///   `Pictures\Screenshots`; Xbox Game Bar writes to `Videos\Captures`.
///
/// When both fire for one snip, the file the system already saved wins and
/// no second copy is made. Two arrivals count as one snip when they land
/// within a few seconds of each other *and* have the same pixel size — time
/// alone would merge a Win+Shift+S and a Win+PrtSc taken back to back. They
/// can arrive in either order, so a clipboard picture waits a moment before
/// it is written, and a folder file that turns up just after a vault save
/// replaces that save.
public sealed class ScreenshotCatcher : IDisposable
{
    /// A new screenshot file, ready to go on the shelf.
    public event Action<string>? Caught;
    /// A vault copy turned out to duplicate a file the system saved: the
    /// shelf should swap the first path for the second.
    public event Action<string, string>? Replaced;

    /// How far apart a clipboard picture and a folder file can land and still
    /// be taken for the same snip.
    static readonly TimeSpan Pairing = TimeSpan.FromSeconds(4);

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    readonly List<FileSystemWatcher> watchers = [];
    readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer pendingTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

    BitmapSource? pending;
    DateTime pendingAt;
    (DateTime At, int Width, int Height)? lastFolderFile;
    (string Path, DateTime At, int Width, int Height)? lastVault;

    public ScreenshotCatcher()
    {
        pendingTimer.Tick += (_, _) => SavePending();
    }

    /// The system's screenshot folders. `Pictures\Screenshots` is a known
    /// folder of its own and can be redirected (OneDrive does), so it is
    /// asked for by id rather than built from a path.
    public static IEnumerable<string> Folders
    {
        get
        {
            if (KnownFolder(new Guid("b7bede81-df94-4682-a7d8-57a52620b86f")) is string screenshots)
                yield return screenshots;
            var captures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
            if (Directory.Exists(captures))
                yield return captures;
        }
    }

    public bool IsRunning => watchers.Count > 0;

    public void Start()
    {
        if (IsRunning) return;
        foreach (var folder in Folders)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                };
                watcher.Created += (_, e) => Arrived(e.FullPath);
                watcher.Renamed += (_, e) => Arrived(e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception error) when (error is ArgumentException or IOException)
            {
                Support.Log($"cannot watch {folder}: {error.Message}");
            }
        }
    }

    public void Stop()
    {
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
        pending = null;
        pendingTimer.Stop();
    }

    public void Dispose() => Stop();

    /// From the clipboard listener, on the UI thread.
    public void OnClipboardImage(BitmapSource image)
    {
        var now = DateTime.UtcNow;
        // The folder already delivered this snip.
        if (lastFolderFile is var (at, width, height) && now - at < Pairing
            && width == image.PixelWidth && height == image.PixelHeight)
            return;
        pending = image;
        pendingAt = now;
        pendingTimer.Stop();
        pendingTimer.Start();
    }

    async void SavePending()
    {
        pendingTimer.Stop();
        var image = pending;
        pending = null;
        if (image == null) return;

        var at = DateTime.Now;
        // Encoding a 4K PNG takes a noticeable moment; not on the UI thread.
        var path = await Task.Run(() => ScreenshotVault.Save(image, at));
        if (path == null) return;
        lastVault = (path, DateTime.UtcNow, image.PixelWidth, image.PixelHeight);
        Caught?.Invoke(path);
    }

    /// From a watcher thread.
    void Arrived(string path)
    {
        if (!ImageExtensions.Contains(Path.GetExtension(path))) return;
        dispatcher.BeginInvoke(async () =>
        {
            if (!seen.Add(path)) return;
            if (!await WaitUntilWritten(path)) return;

            var now = DateTime.UtcNow;
            var (width, height) = PixelSize(path);
            lastFolderFile = (now, width, height);

            // The clipboard copy of this same snip is still waiting: drop it.
            if (pending != null && now - pendingAt < Pairing
                && pending.PixelWidth == width && pending.PixelHeight == height)
            {
                pending = null;
                pendingTimer.Stop();
                Caught?.Invoke(path);
                return;
            }
            // The clipboard copy was already written: the system's file takes
            // its place, and the vault copy — seconds old, and made by us — goes.
            if (lastVault is var (vaultPath, at, vaultWidth, vaultHeight) && now - at < Pairing
                && vaultWidth == width && vaultHeight == height)
            {
                lastVault = null;
                try { File.Delete(vaultPath); } catch (IOException) { }
                Replaced?.Invoke(vaultPath, path);
                return;
            }
            Caught?.Invoke(path);
        });
    }

    /// Read from the file header alone; the pixels are not decoded.
    static (int Width, int Height) PixelSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            return (-1, -1);
        }
    }

    /// The watcher reports a file the moment it is created, usually before
    /// the picture is in it. Waits for a steady, non-zero size that can be
    /// opened for reading.
    static async Task<bool> WaitUntilWritten(string path)
    {
        long last = -1;
        for (int attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(200);
            try
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == last)
                {
                    using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return true;
                }
                last = size;
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// KF_FLAG_CREATE: Windows makes `Pictures\Screenshots` on the first
    /// Win+PrtSc, and a watcher cannot be put on a folder that is not there yet.
    static string? KnownFolder(Guid id)
    {
        const uint KF_FLAG_CREATE = 0x8000;
        if (SHGetKnownFolderPath(id, KF_FLAG_CREATE, IntPtr.Zero, out var pointer) != 0) return null;
        try { return Marshal.PtrToStringUni(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
}
