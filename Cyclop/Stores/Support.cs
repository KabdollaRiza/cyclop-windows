using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Cyclop.Stores;

static class Support
{
    /// `%APPDATA%\Cyclop` — the Windows counterpart of
    /// `~/Library/Application Support/Cyclop`. The JSON files in it use the
    /// same format as on the Mac, so they can be carried across by hand.
    public static string Folder
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cyclop");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string File(string name) => Path.Combine(Folder, name);

    public static void Log(string message)
    {
        Debug.WriteLine($"Cyclop: {message}");
        try { System.IO.File.AppendAllText(File("cyclop.log"), $"{DateTime.Now:u} {message}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    /// Writes next to the target and swaps it in, so a crash mid-write never
    /// leaves half a file behind.
    public static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, contents);
        System.IO.File.Move(temp, path, overwrite: true);
    }

    public static void Reveal(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// The clipboard is a shared lock: another app holding it for a moment
    /// makes every call throw. A few short retries are the documented cure.
    public static void SetClipboard(IDataObject data)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(30);
            }
            catch (ExternalException error)
            {
                Log($"cannot write the clipboard: {error.Message}");
                return;
            }
        }
    }
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// Saves a moment after edits pause rather than on every keystroke.
public sealed class DebouncedWrite
{
    readonly DispatcherTimer timer;
    Action? pending;

    public DebouncedWrite(TimeSpan delay)
    {
        timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => Flush();
    }

    public void Schedule(Action write)
    {
        pending = write;
        timer.Stop();
        timer.Start();
    }

    public void Flush()
    {
        timer.Stop();
        var write = pending;
        pending = null;
        write?.Invoke();
    }
}
