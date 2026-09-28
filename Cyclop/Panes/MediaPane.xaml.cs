using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cyclop.Stores;

namespace Cyclop.Panes;

public partial class MediaPane : UserControl
{
    MediaController? media;
    /// Set while dragging the scrubber, so the bar follows the pointer
    /// instead of the clock.
    double? scrubbing;

    public MediaPane() => InitializeComponent();

    public void Attach(MediaController controller)
    {
        media = controller;
        DataContext = controller;
        controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaController.Position) || e.PropertyName == nameof(MediaController.Duration))
                UpdateTime();
            else
                UpdateState();
        };
        UpdateState();
        UpdateTime();
    }

    void UpdateState()
    {
        var m = media!;
        Player.Visibility = m.HasSession ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = m.HasSession ? Visibility.Collapsed : Visibility.Visible;

        PlayButton.Content = m.IsPlaying ? "" : "";
        // Dimmed, not hidden, when the player does not offer it: a button that
        // looks live and does nothing says "broken"; dim says "not here".
        PreviousButton.IsEnabled = m.CanPrevious;
        NextButton.IsEnabled = m.CanNext;
        Back10.IsEnabled = Forward10.IsEnabled = m.CanSeek;
        Track.IsEnabled = m.CanSeek;
        Track.Cursor = m.CanSeek ? Cursors.Hand : Cursors.Arrow;

        Art.Stretch = m.ArtworkIsSquare ? Stretch.UniformToFill : Stretch.Uniform;
        SwitchGlyph.Visibility = m.CanSwitchSource ? Visibility.Visible : Visibility.Collapsed;
        SourceButton.IsHitTestVisible = m.CanSwitchSource;
        SourceButton.ToolTip = m.CanSwitchSource ? "Switch to the next app that is playing" : null;
    }

    void UpdateTime()
    {
        var m = media!;
        double total = m.Duration.TotalSeconds;
        double progress = scrubbing ?? (total > 0 ? Math.Clamp(m.Position.TotalSeconds / total, 0, 1) : 0);
        Elapsed.Text = Format(TimeSpan.FromSeconds(progress * total));
        Total.Text = Format(m.Duration);

        double width = Track.ActualWidth;
        Fill.Width = width * progress;
        Knob.Margin = new Thickness(Math.Clamp(width * progress - 5.5, 0, Math.Max(0, width - 11)), 0, 0, 0);
    }

    static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    // MARK: - Scrubber

    double FractionAt(MouseEventArgs e) =>
        Track.ActualWidth > 0 ? Math.Clamp(e.GetPosition(Track).X / Track.ActualWidth, 0, 1) : 0;

    void Track_Down(object sender, MouseButtonEventArgs e)
    {
        if (!media!.CanSeek) return;
        scrubbing = FractionAt(e);
        Track.CaptureMouse();
        UpdateTime();
    }

    void Track_Move(object sender, MouseEventArgs e)
    {
        if (scrubbing == null) return;
        scrubbing = FractionAt(e);
        UpdateTime();
    }

    void Track_Up(object sender, MouseButtonEventArgs e)
    {
        if (scrubbing == null) return;
        var target = FractionAt(e);
        Track.ReleaseMouseCapture();
        // Seek first: clearing `scrubbing` beforehand would drop the bar back
        // to the old position for a moment before the new one lands.
        media!.Seek(TimeSpan.FromSeconds(media.Duration.TotalSeconds * target));
        scrubbing = null;
        UpdateTime();
    }

    void Track_Hover(object sender, MouseEventArgs e)
    {
        bool on = (Track.IsMouseOver || scrubbing != null) && media!.CanSeek;
        Rail.Height = Fill.Height = on ? 6 : 4;
        Rail.CornerRadius = Fill.CornerRadius = new CornerRadius(on ? 3 : 2);
        Knob.Visibility = on ? Visibility.Visible : Visibility.Hidden;
    }

    void Track_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTime();

    // MARK: - Buttons

    void Play_Click(object sender, RoutedEventArgs e) => media!.TogglePlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => media!.Next();
    void Previous_Click(object sender, RoutedEventArgs e) => media!.Previous();
    void Back10_Click(object sender, RoutedEventArgs e) => media!.SkipBy(-10);
    void Forward10_Click(object sender, RoutedEventArgs e) => media!.SkipBy(10);
    void Source_Click(object sender, RoutedEventArgs e) => media!.SwitchSource();
}
