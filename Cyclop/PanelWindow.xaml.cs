using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Cyclop.Stores;

namespace Cyclop;

public enum Tab { Shelf, Music, Clipboard, Snippets, Notes }

/// The panel: a pill at the top centre of the screen that unfolds downwards
/// when the pointer reaches it and folds back when the pointer leaves.
///
/// Windows has no notch, so the pill stands in for one — the same idea as the
/// Mac version's 180 × 24 pt stand-in on displays without a notch.
public partial class PanelWindow : Window
{
    // Sizes in device-independent pixels, matching NotchGeometry.swift.
    const double WindowWidth = 700, WindowHeight = 252;
    static readonly Size CollapsedSize = new(180, 8);
    static readonly Size ExpandedSize = new(620, 208);

    /// Opening waits a little longer than on the Mac: the top edge of a Windows
    /// screen is where maximised title bars live, and a pointer on its way to
    /// one should pass without unfolding anything.
    static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(110);
    static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(120);
    /// Hover switches tabs only once the pointer has come to rest on an icon:
    /// one passing through the rail switches nothing.
    static readonly TimeSpan TabDwell = TimeSpan.FromMilliseconds(150);
    /// A file dragged to the top edge opens the panel straight on the shelf.
    /// A little slower than a plain hover: a held button can also be a text
    /// selection or a scrollbar that happened to travel up there.
    static readonly TimeSpan DragOpenDelay = TimeSpan.FromMilliseconds(200);

    readonly ShelfStore shelf;
    readonly MediaController media;
    readonly ClipboardStore clipboard;
    readonly SnippetStore snippets;
    readonly NoteStore notes;

    readonly DispatcherTimer pointer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    readonly DispatcherTimer dwell = new() { Interval = TabDwell };
    RadioButton? dwellTarget;

    IntPtr hwnd;
    IntPtr monitor;
    bool isOpen;
    /// Opened from the tray: stays open until the pointer has been inside and
    /// left again, since it did not arrive by hovering.
    bool pinned;
    DateTime? enteredAt, leftAt;
    IntPtr previousForeground;
    Tab tab = Tab.Shelf;
    /// Context menus live in popups outside the panel; while one is open the
    /// pointer is expected to leave.
    int openMenus;

    public PanelWindow(ShelfStore shelf, MediaController media, ClipboardStore clipboard, SnippetStore snippets, NoteStore notes)
    {
        this.shelf = shelf;
        this.media = media;
        this.clipboard = clipboard;
        this.snippets = snippets;
        this.notes = notes;
        InitializeComponent();

        ShelfPane.Attach(shelf, ShowToast);
        MediaPane.Attach(media);
        ClipboardPane.Attach(clipboard, ShowToast);
        SnippetsPane.Attach(snippets, ShowToast);
        NotesPane.Attach(notes);

        foreach (var (button, target) in new[] { (ShelfTab, Tab.Shelf), (MusicTab, Tab.Music), (ClipboardTab, Tab.Clipboard), (SnippetsTab, Tab.Snippets), (NotesTab, Tab.Notes) })
        {
            button.Checked += (_, _) => Select(target);
            button.MouseEnter += (_, _) => { dwellTarget = button; dwell.Stop(); dwell.Start(); };
            button.MouseLeave += (_, _) => { if (dwellTarget == button) dwell.Stop(); };
        }
        dwell.Tick += (_, _) =>
        {
            dwell.Stop();
            if (dwellTarget != null && isOpen) dwellTarget.IsChecked = true;
        };

        pointer.Tick += (_, _) => Track();
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((_, _) => openMenus++));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, _) => openMenus = Math.Max(0, openMenus - 1)));

        // Files dropped anywhere on the panel go on the shelf. Preview events,
        // so a text field under the pointer does not get to claim them first.
        Shell.PreviewDragEnter += FilesOver;
        Shell.PreviewDragOver += FilesOver;
        Shell.PreviewDragLeave += (_, _) => ShelfPane.SetTargeted(false);
        Shell.PreviewDrop += FilesDropped;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => Place(monitor, force: true));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: kept out of Alt+Tab and the taskbar.
        int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT);

        Native.GetCursorPos(out var p);
        Place(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST), force: true);

        clipboard.Start(HwndSource.FromHwnd(hwnd));
        pointer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        pointer.Stop();
        clipboard.Stop();
        base.OnClosed(e);
    }

    /// From the tray icon.
    public void Toggle()
    {
        if (isOpen)
            Collapse();
        else
            Reveal();
    }

    /// Opens the panel without a hover — from the tray, or from launching the
    /// app. It did not arrive under the pointer, so it stays open until the
    /// pointer has been inside and left again.
    public void Reveal()
    {
        if (!isOpen)
        {
            Native.GetCursorPos(out var p);
            // The panel goes to the display the pointer is on, which is the
            // one being looked at — not the one the tray or the shortcut is on.
            Place(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));
            Open();
        }
        pinned = true;
    }

    // MARK: - Pointer

    /// Sampled on a timer rather than through mouse hooks: a low-level hook
    /// would put Cyclop in the path of every mouse event on the system, and
    /// one cursor read every 50 ms costs nothing.
    void Track()
    {
        Native.GetCursorPos(out var p);
        if (!isOpen)
            Place(Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST));

        var now = DateTime.UtcNow;
        bool inside = Contains(isOpen ? ExpandedSize : CollapsedSize, isOpen ? 6 : 4, p);

        if (!isOpen)
        {
            // A held button is a drag. A window dragged to the top edge to be
            // maximised must not unfold the panel; anything else — a file,
            // most likely — opens it on the shelf, ready for the drop.
            bool dragging = Native.LeftButtonDown;
            if (inside && !Native.UserIsBusy() && !(dragging && Native.IsMovingWindow()))
            {
                enteredAt ??= now;
                if (now - enteredAt >= (dragging ? DragOpenDelay : OpenDelay))
                {
                    Open();
                    if (dragging) ShelfTab.IsChecked = true;
                }
            }
            else enteredAt = null;
            return;
        }

        if (inside)
        {
            leftAt = null;
            pinned = false;
            return;
        }
        // A held button here is a text selection that overshot the panel.
        if (pinned || openMenus > 0 || Native.LeftButtonDown) return;
        leftAt ??= now;
        if (now - leftAt >= CloseDelay) Collapse();
    }

    /// Whether the physical-pixel point is inside the panel at `size`,
    /// grown by `slack` DIPs on the sides and bottom.
    bool Contains(Size size, double slack, Native.POINT p)
    {
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        double scale = r.Width / WindowWidth;
        double left = r.Left + ((WindowWidth - size.Width) / 2 - slack) * scale;
        double right = r.Left + ((WindowWidth + size.Width) / 2 + slack) * scale;
        double bottom = r.Top + (size.Height + slack) * scale;
        return p.X >= left && p.X < right && p.Y >= r.Top && p.Y < bottom;
    }

    /// Centres the window at the top of `target`, sized for that display's scale.
    void Place(IntPtr target, bool force = false)
    {
        if (target == IntPtr.Zero || (target == monitor && !force)) return;
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(target, ref info)) return;
        if (Native.GetDpiForMonitor(target, Native.MDT_EFFECTIVE_DPI, out uint dpi, out _) != 0) dpi = 96;

        monitor = target;
        int width = (int)Math.Round(WindowWidth * dpi / 96);
        int height = (int)Math.Round(WindowHeight * dpi / 96);
        var screen = info.rcMonitor;
        int x = screen.Left + (screen.Width - width) / 2;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, screen.Top, width, height, Native.SWP_NOACTIVATE);
    }

    // MARK: - Open and close

    void Open()
    {
        if (isOpen) return;
        isOpen = true;
        enteredAt = null;
        leftAt = null;
        previousForeground = Native.GetForegroundWindow();
        SetClickThrough(false);
        if (tab == Tab.Snippets) snippets.Reload();
        if (tab == Tab.Shelf) shelf.RefreshFromDisk();
        media.SetActive(tab == Tab.Music);
        Animate(opening: true);
    }

    void Collapse()
    {
        if (!isOpen) return;
        isOpen = false;
        pinned = false;
        enteredAt = null;
        leftAt = null;
        dwell.Stop();
        SetClickThrough(true);
        if (tab == Tab.Notes) notes.Leave();
        media.SetActive(false);

        // Typing in the panel made it the foreground window. Hand the keyboard
        // back to whatever had it, or keystrokes would go to a folded panel.
        if (IsActive && Native.IsWindow(previousForeground))
            Native.SetForegroundWindow(previousForeground);
        Keyboard.ClearFocus();
        Animate(opening: false);
    }

    void SetClickThrough(bool on)
    {
        int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        style = on ? style | Native.WS_EX_TRANSPARENT : style & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style);
    }

    void Animate(bool opening)
    {
        var size = opening ? ExpandedSize : CollapsedSize;
        var duration = TimeSpan.FromMilliseconds(opening ? 270 : 200);
        // A slight overshoot when unfolding: the stand-in for the Mac's spring.
        IEasingFunction ease = opening
            ? new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var width = new DoubleAnimation(size.Width, duration) { EasingFunction = ease };
        var height = new DoubleAnimation(size.Height, duration) { EasingFunction = ease };
        if (opening)
            Shell.CornerRadius = new CornerRadius(0, 0, 22, 22);
        else
            height.Completed += (_, _) => { if (!isOpen) Shell.CornerRadius = new CornerRadius(0, 0, 4, 4); };
        Shell.BeginAnimation(WidthProperty, width);
        Shell.BeginAnimation(HeightProperty, height);

        Body.IsHitTestVisible = opening;
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(opening ? 1 : 0, TimeSpan.FromMilliseconds(opening ? 160 : 90))
        {
            BeginTime = TimeSpan.FromMilliseconds(opening ? 60 : 0),
        });
        Shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(opening ? 0.5 : 0, duration));
    }

    // MARK: - Tabs

    void Select(Tab target)
    {
        if (target == tab) return;
        if (tab == Tab.Notes) notes.Leave();
        tab = target;
        if (tab == Tab.Snippets) snippets.Reload();
        if (tab == Tab.Shelf) shelf.RefreshFromDisk();

        ShelfPane.Visibility = tab == Tab.Shelf ? Visibility.Visible : Visibility.Collapsed;
        MediaPane.Visibility = tab == Tab.Music ? Visibility.Visible : Visibility.Collapsed;
        media.SetActive(isOpen && tab == Tab.Music);
        ClipboardPane.Visibility = tab == Tab.Clipboard ? Visibility.Visible : Visibility.Collapsed;
        SnippetsPane.Visibility = tab == Tab.Snippets ? Visibility.Visible : Visibility.Collapsed;
        NotesPane.Visibility = tab == Tab.Notes ? Visibility.Visible : Visibility.Collapsed;
        Heading.Text = tab.ToString();
    }

    // MARK: - Drop and catch

    void FilesOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        // Copy, never Move: answering Move to Explorer would have it delete
        // the original once the drop lands. The shelf only points at files.
        e.Effects = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.Link;
        e.Handled = true;
        if (tab != Tab.Shelf) ShelfTab.IsChecked = true;
        ShelfPane.SetTargeted(true);
    }

    void FilesDropped(object sender, DragEventArgs e)
    {
        ShelfPane.SetTargeted(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        shelf.Add(files);
        ShowToast(files.Length == 1 ? "Added to the shelf" : $"{files.Length} added to the shelf");
    }

    /// A screenshot landed on the shelf. With the panel open that is visible
    /// by itself; folded, the pill gives a short nod so the catch is not silent.
    public void Caught()
    {
        if (isOpen)
        {
            ShowToast("Screenshot saved");
            return;
        }
        var nod = new DoubleAnimationUsingKeyFrames();
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(CollapsedSize.Width + 50, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        nod.KeyFrames.Add(new EasingDoubleKeyFrame(CollapsedSize.Width, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        Shell.BeginAnimation(WidthProperty, nod);
    }

    void ShowToast(string text)
    {
        Toast.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }
}
