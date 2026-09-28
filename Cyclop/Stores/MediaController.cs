using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Cyclop.Stores;

/// What is playing, from whichever app is playing it.
///
/// Windows' own media sessions — the same feed behind the overlay that appears
/// with the volume keys. Every browser publishes one for a playing tab, so a
/// YouTube video in Chrome, Edge, Firefox or Yandex shows up here with its
/// title, channel and thumbnail, and answers play/pause, next, previous and
/// seek, with nothing to install or configure in the browser.
public sealed class MediaController : Observable
{
    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    /// Only runs while the pane is on screen: the scrubber is the one thing
    /// that needs a clock, and nobody sees it otherwise.
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };

    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    /// Chosen by hand with the source switch; kept for as long as that app
    /// has a session, so a second app starting to play does not steal the pane.
    string? pinnedSource;

    TimeSpan anchorPosition;
    DateTimeOffset anchorAt;
    double rate = 1;
    /// A seek just sent. The player reports its old position for a moment
    /// afterwards, and following that would snap the bar back.
    (TimeSpan Target, DateTime At)? pendingSeek;
    string? mediaKey;

    public MediaController()
    {
        ticker.Tick += (_, _) => Raise(nameof(Position));
    }

    // MARK: - State

    bool hasSession;
    public bool HasSession { get => hasSession; private set => Set(ref hasSession, value); }

    string title = "";
    public string Title { get => title; private set => Set(ref title, value); }

    string subtitle = "";
    public string Subtitle { get => subtitle; private set => Set(ref subtitle, value); }

    string source = "";
    public string Source { get => source; private set => Set(ref source, value); }

    int sessionCount;
    public int SessionCount { get => sessionCount; private set { if (Set(ref sessionCount, value)) Raise(nameof(CanSwitchSource)); } }
    public bool CanSwitchSource => sessionCount > 1;

    ImageSource? artwork;
    public ImageSource? Artwork { get => artwork; private set => Set(ref artwork, value); }

    bool artworkIsSquare = true;
    /// A square cover fills its box; a 16:9 video thumbnail is fitted into it
    /// instead, because cropping it to a square loses the edges that say which
    /// video it is.
    public bool ArtworkIsSquare { get => artworkIsSquare; private set => Set(ref artworkIsSquare, value); }

    bool isPlaying;
    public bool IsPlaying { get => isPlaying; private set => Set(ref isPlaying, value); }

    bool canPrevious, canNext, canSeek;
    public bool CanPrevious { get => canPrevious; private set => Set(ref canPrevious, value); }
    public bool CanNext { get => canNext; private set => Set(ref canNext, value); }
    public bool CanSeek { get => canSeek; private set => Set(ref canSeek, value); }

    TimeSpan duration;
    public TimeSpan Duration { get => duration; private set => Set(ref duration, value); }

    /// The last reported position carried forward by the clock, since players
    /// report it only now and then.
    public TimeSpan Position
    {
        get
        {
            var position = anchorPosition;
            if (isPlaying)
                position += TimeSpan.FromTicks((long)((DateTimeOffset.Now - anchorAt).Ticks * rate));
            if (position < TimeSpan.Zero) return TimeSpan.Zero;
            return duration > TimeSpan.Zero && position > duration ? duration : position;
        }
    }

    // MARK: - Lifecycle

    public async void Start()
    {
        if (manager != null) return;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception error)
        {
            Support.Log($"media sessions unavailable: {error.Message}");
            return;
        }
        manager.SessionsChanged += (_, _) => dispatcher.BeginInvoke(Choose);
        manager.CurrentSessionChanged += (_, _) => dispatcher.BeginInvoke(Choose);
        Choose();
    }

    /// The pane is on screen or not.
    public void SetActive(bool active)
    {
        if (active)
        {
            Raise(nameof(Position));
            ticker.Start();
        }
        else ticker.Stop();
    }

    // MARK: - Commands

    public async void TogglePlayPause()
    {
        if (session == null) return;
        // Flipped at once: waiting for the round trip makes the button feel dead.
        anchorPosition = Position;
        anchorAt = DateTimeOffset.Now;
        IsPlaying = !IsPlaying;
        await Try(() => session.TryTogglePlayPauseAsync().AsTask());
    }

    public async void Next()
    {
        if (session != null) await Try(() => session.TrySkipNextAsync().AsTask());
    }

    public async void Previous()
    {
        if (session != null) await Try(() => session.TrySkipPreviousAsync().AsTask());
    }

    public void SkipBy(double seconds) => Seek(Position + TimeSpan.FromSeconds(seconds));

    public async void Seek(TimeSpan target)
    {
        if (session == null || !CanSeek) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && target > Duration) target = Duration;
        anchorPosition = target;
        anchorAt = DateTimeOffset.Now;
        pendingSeek = (target, DateTime.UtcNow);
        Raise(nameof(Position));
        var start = session.GetTimelineProperties().StartTime;
        await Try(() => session.TryChangePlaybackPositionAsync((start + target).Ticks).AsTask());
    }

    /// Cycles through the apps that have a session, and sticks to the choice.
    public void SwitchSource()
    {
        if (manager == null) return;
        var sessions = manager.GetSessions();
        if (sessions.Count < 2) return;
        int index = 0;
        for (int i = 0; i < sessions.Count; i++)
            if (sessions[i].SourceAppUserModelId == session?.SourceAppUserModelId) index = i;
        var next = sessions[(index + 1) % sessions.Count];
        pinnedSource = next.SourceAppUserModelId;
        Attach(next);
    }

    static async Task Try(Func<Task<bool>> command)
    {
        try { await command(); }
        catch (Exception error) { Support.Log($"media command failed: {error.Message}"); }
    }

    // MARK: - Sessions

    void Choose()
    {
        if (manager == null) return;
        var sessions = manager.GetSessions();
        SessionCount = sessions.Count;

        var pinned = sessions.FirstOrDefault(s => s.SourceAppUserModelId == pinnedSource);
        if (pinned == null) pinnedSource = null;
        var current = manager.GetCurrentSession();
        // The hand-picked one; else whatever is actually playing, the one
        // Windows calls current first; else whatever is there at all.
        var pick = pinned
            ?? (IsSessionPlaying(current) ? current : null)
            ?? sessions.FirstOrDefault(IsSessionPlaying)
            ?? current
            ?? sessions.FirstOrDefault();
        Attach(pick);
    }

    static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession? s)
    {
        try { return s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    void Attach(GlobalSystemMediaTransportControlsSession? next)
    {
        if (next?.SourceAppUserModelId == session?.SourceAppUserModelId && next != null && session != null)
        {
            // Same app, possibly a fresh object: refresh without flicker.
            if (!ReferenceEquals(next, session)) Rebind(next);
            return;
        }
        Rebind(next);
        mediaKey = null;
        Artwork = null;
        RefreshAll();
    }

    void Rebind(GlobalSystemMediaTransportControlsSession? next)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnMediaChanged;
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        session = next;
        HasSession = session != null;
        if (session == null) return;
        session.MediaPropertiesChanged += OnMediaChanged;
        session.PlaybackInfoChanged += OnPlaybackChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        Source = FriendlyName(session.SourceAppUserModelId);
    }

    // Session events arrive on a thread pool thread.
    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) RefreshMedia(); });
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) { RefreshPlayback(); RefreshTimeline(); } });
    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) RefreshTimeline(); });

    void RefreshAll()
    {
        if (session == null)
        {
            Title = Subtitle = Source = "";
            IsPlaying = CanNext = CanPrevious = CanSeek = false;
            Duration = anchorPosition = TimeSpan.Zero;
            Raise(nameof(Position));
            return;
        }
        RefreshMedia();
        RefreshPlayback();
        RefreshTimeline();
    }

    async void RefreshMedia()
    {
        var current = session;
        if (current == null) return;
        GlobalSystemMediaTransportControlsSessionMediaProperties properties;
        try { properties = await current.TryGetMediaPropertiesAsync(); }
        catch (Exception error) { Support.Log($"media properties: {error.Message}"); return; }
        if (!ReferenceEquals(current, session) || properties == null) return;

        Title = properties.Title ?? "";
        // A browser repeats the title as the album; "Channel — Title" twice
        // reads like a bug.
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(properties.Artist)) parts.Add(properties.Artist);
        if (!string.IsNullOrWhiteSpace(properties.AlbumTitle) && properties.AlbumTitle != properties.Title) parts.Add(properties.AlbumTitle);
        Subtitle = string.Join(" — ", parts);

        var key = $"{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
        if (key != mediaKey)
        {
            mediaKey = key;
            Artwork = null;
        }
        if (properties.Thumbnail == null) return;

        try
        {
            using var stream = await properties.Thumbnail.OpenReadAsync();
            using var managed = stream.AsStreamForRead();
            var copy = new MemoryStream();
            await managed.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            // The track may have changed while the picture was on its way.
            if (key != mediaKey || !ReferenceEquals(current, session)) return;
            ArtworkIsSquare = image.PixelHeight == 0 || Math.Abs((double)image.PixelWidth / image.PixelHeight - 1) < 0.02;
            Artwork = image;
        }
        catch (Exception error)
        {
            Support.Log($"artwork: {error.Message}");
        }
    }

    void RefreshPlayback()
    {
        if (session == null) return;
        var info = session.GetPlaybackInfo();
        if (info == null) return;
        // Carry the clock forward before the state flips, so pausing freezes
        // the bar where it visibly is.
        anchorPosition = Position;
        anchorAt = DateTimeOffset.Now;
        IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        rate = info.PlaybackRate is double r && r > 0 ? r : 1;
        CanNext = info.Controls.IsNextEnabled;
        CanPrevious = info.Controls.IsPreviousEnabled;
        CanSeek = info.Controls.IsPlaybackPositionEnabled;
    }

    void RefreshTimeline()
    {
        if (session == null) return;
        var timeline = session.GetTimelineProperties();
        if (timeline == null) return;
        Duration = timeline.EndTime - timeline.StartTime;

        var reported = timeline.Position - timeline.StartTime;
        // Some apps never stamp the update time; "now" is the honest fallback.
        var at = timeline.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : timeline.LastUpdatedTime;
        if (isPlaying)
            reported += TimeSpan.FromTicks((long)((DateTimeOffset.Now - at).Ticks * rate));

        if (pendingSeek is var (target, sent))
        {
            bool settled = (reported - target).Duration() < TimeSpan.FromSeconds(2.5);
            bool expired = DateTime.UtcNow - sent > TimeSpan.FromSeconds(1.5);
            if (!settled && !expired) return;
            pendingSeek = null;
        }
        anchorPosition = reported;
        anchorAt = DateTimeOffset.Now;
        Raise(nameof(Position));
    }

    /// "Chrome", "MSEdge", "Spotify.exe", "SpotifyAB.SpotifyMusic_…!Spotify",
    /// or a Firefox hash — made readable.
    static string FriendlyName(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("msedge") || lower.Contains("edge")) return "Edge";
        if (lower.Contains("firefox") || lower == "308046b0af4a39cb") return "Firefox";
        if (lower.Contains("yandex")) return "Yandex Browser";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("brave")) return "Brave";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("zunemusic") || lower.Contains("media")) return "Media Player";
        var name = id.Split('!').Last();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }
}
