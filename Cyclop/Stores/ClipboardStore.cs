using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Cyclop.Stores;

public sealed class ClipItem
{
    public string? Text { get; init; }
    public string? FilePath { get; init; }

    public string Preview
    {
        get
        {
            if (FilePath != null) return Path.GetFileName(FilePath);
            var line = string.Join(' ', Text!.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > 300 ? line[..300] : line;
        }
    }

    public string Glyph
    {
        get
        {
            if (FilePath != null) return "";                 // Document
            if (Text!.StartsWith("http://") || Text.StartsWith("https://")) return ""; // Link
            return "";                                        // AlignLeft
        }
    }

    public bool SamePayload(ClipItem other) => Text == other.Text && FilePath == other.FilePath;
}

/// Clipboard history: the last 40 copies, in memory only.
///
/// Unlike the Mac version this does not poll: Windows delivers
/// WM_CLIPBOARDUPDATE to any window that asks, so nothing runs until
/// something is actually copied.
public sealed class ClipboardStore
{
    public ObservableCollection<ClipItem> Items { get; } = [];

    const int Limit = 40;

    /// Marks our own writes, so putting an entry back is not recorded again —
    /// and a screenshot copied back out of the shelf is not saved twice.
    public const string InternalFormat = "Cyclop.Internal";

    /// Raised for a picture on the clipboard — a screenshot taken straight to
    /// the clipboard, which would otherwise vanish after one paste.
    public Action<BitmapSource>? OnImage;

    /// Asked before the picture is read at all, so with screenshot catching
    /// off a copied picture is not decoded only to be thrown away.
    public Func<bool> WantsImages = () => true;

    /// Formats apps set to keep a copy out of history tools — password
    /// managers set the first; the second is the older convention.
    static readonly uint ExcludeFormat = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint ViewerIgnoreFormat = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");

    HwndSource? source;
    bool readPending;

    public void Start(HwndSource window)
    {
        if (source != null) return;
        source = window;
        source.AddHook(Hook);
        if (!Native.AddClipboardFormatListener(source.Handle))
            Support.Log($"AddClipboardFormatListener failed: {Marshal.GetLastWin32Error()}");
    }

    public void Stop()
    {
        if (source == null) return;
        Native.RemoveClipboardFormatListener(source.Handle);
        source.RemoveHook(Hook);
        source = null;
    }

    public void Clear() => Items.Clear();

    public void Remove(ClipItem item) => Items.Remove(item);

    /// Puts an entry back on the clipboard without re-recording it, and
    /// moves it to the top: freshly used entries bubble up.
    public void Copy(ClipItem item)
    {
        var data = new DataObject();
        if (item.FilePath != null)
            data.SetFileDropList(new StringCollection { item.FilePath });
        else
            data.SetText(item.Text!, TextDataFormat.UnicodeText);
        data.SetData(InternalFormat, "1");
        Support.SetClipboard(data);

        int index = Items.IndexOf(item);
        if (index > 0) Items.Move(index, 0);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) ScheduleRead();
        return IntPtr.Zero;
    }

    /// Several updates can arrive for one copy (an app sets text, then HTML,
    /// then its own format). They are folded into a single read shortly after
    /// the last one, which also gives the copying app time to let go of the
    /// clipboard lock.
    async void ScheduleRead()
    {
        if (readPending) return;
        readPending = true;
        await Task.Delay(60);
        readPending = false;
        await Read();
    }

    async Task Read()
    {
        if (Native.IsClipboardFormatAvailable(ExcludeFormat) || Native.IsClipboardFormatAvailable(ViewerIgnoreFormat))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                ReadOnce();
                return;
            }
            catch (ExternalException)
            {
                await Task.Delay(50);
            }
        }
        Support.Log("clipboard stayed locked; a copy was not recorded");
    }

    void ReadOnce()
    {
        var data = Clipboard.GetDataObject();
        if (data == null || data.GetDataPresent(InternalFormat)) return;

        // Windows' own history opt-out: a DWORD that is zero means "keep out".
        if (data.GetDataPresent("CanIncludeInClipboardHistory")
            && data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag
            && flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
            return;

        // A copied file arrives as a file list, not as text, so files win first.
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            Record(new ClipItem { FilePath = files[0] });
            return;
        }

        // A picture with no text beside it. Office puts a rendered image of
        // every cell range and paragraph on the clipboard next to the text;
        // those are text copies, and saving each one as a screenshot would
        // bury the real ones.
        bool hasText = data.GetDataPresent(DataFormats.UnicodeText);
        if (!hasText && OnImage != null && WantsImages() && ClipboardImage.Read(data) is BitmapSource image)
        {
            OnImage(image);
            return;
        }

        if (hasText
            && data.GetData(DataFormats.UnicodeText) is string text
            && !string.IsNullOrWhiteSpace(text))
            Record(new ClipItem { Text = text });
    }

    void Record(ClipItem item)
    {
        var existing = Items.FirstOrDefault(i => i.SamePayload(item));
        if (existing != null) Items.Remove(existing);
        Items.Insert(0, item);
        while (Items.Count > Limit) Items.RemoveAt(Items.Count - 1);
    }
}
