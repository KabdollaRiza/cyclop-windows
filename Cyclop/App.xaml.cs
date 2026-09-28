using System.Windows;
using Cyclop.Stores;

namespace Cyclop;

public partial class App : Application
{
    Mutex? single;
    Tray? tray;
    PanelWindow? panel;
    NoteStore? notes;
    ScreenshotCatcher? catcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One Cyclop per session: a second one would draw a second panel over
        // the first and record every copy twice.
        single = new Mutex(true, @"Local\Cyclop", out bool first);
        if (!first)
        {
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
        tray?.Dispose();
        single?.Dispose();
        base.OnExit(e);
    }
}
