using System.Windows;
using System.Windows.Threading;
using Cyclop.Stores;

namespace Cyclop;

public partial class App : Application
{
    Mutex? single;
    Tray? tray;
    PanelWindow? panel;
    NoteStore? notes;
    ScreenshotCatcher? catcher;
    EventWaitHandle? showSignal;
    RegisteredWaitHandle? showWait;

    /// Set by the running copy's listener; raised by a second launch.
    const string ShowSignalName = @"Local\Cyclop.Show";

    /// Passed by the autostart entry. Signing in should bring the bar up
    /// quietly; every other launch — the shortcut, the exe — is someone asking
    /// to see Cyclop, and gets the panel.
    public const string BackgroundArgument = "--background";

    protected override void OnStartup(StartupEventArgs e)
    {
        bool background = e.Args.Contains(BackgroundArgument);

        // One Cyclop per session: a second one would draw a second panel over
        // the first and record every copy twice. A second launch is still
        // somebody asking to see the panel, so it asks the running copy to
        // show it before stepping aside — otherwise the shortcut would seem
        // to do nothing at all.
        single = new Mutex(true, @"Local\Cyclop", out bool first);
        if (!first)
        {
            if (!background && EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Support.Log($"unhandled: {args.Exception}");
            args.Handled = true;
        };

        var settings = Settings.Load();
        notes = new NoteStore();
        var shelf = new ShelfStore();
        var media = new MediaController();
        media.Start();
        var clipboard = new ClipboardStore();
        catcher = new ScreenshotCatcher();

        panel = new PanelWindow(shelf, media, clipboard, new SnippetStore(), notes);
        clipboard.WantsImages = () => settings.CatchScreenshots;
        clipboard.OnImage = catcher.OnClipboardImage;
        catcher.Caught += path =>
        {
            shelf.Add([path]);
            panel.Caught();
        };
        catcher.Replaced += shelf.Replace;
        if (settings.CatchScreenshots) catcher.Start();

        panel.Show();
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => panel.Reveal()), null, Timeout.Infinite, executeOnlyOnce: false);
        if (!background)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(panel.Reveal));

        tray = new Tray(panel.Toggle, Quit, settings.CatchScreenshots, on =>
        {
            settings.CatchScreenshots = on;
            settings.Save();
            if (on) catcher.Start(); else catcher.Stop();
        });
    }

    void Quit()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        notes?.Flush();
        catcher?.Dispose();
        showWait?.Unregister(null);
        showSignal?.Dispose();
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
