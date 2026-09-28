using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyclop.Stores;

public sealed class Note : Observable
{
    // Swift's JSONEncoder writes a UUID in upper case; kept that way so a
    // notes.json can move between the Mac and Windows versions.
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString().ToUpperInvariant();

    string text = "";
    [JsonPropertyName("text")]
    public string Text
    {
        get => text;
        set { if (Set(ref text, value)) Raise(nameof(Title)); }
    }

    [JsonPropertyName("edited")]
    [JsonConverter(typeof(ReferenceDateConverter))]
    public DateTime Edited { get; set; } = DateTime.UtcNow;

    bool isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    [JsonIgnore]
    public string Title
    {
        get
        {
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return line ?? "New note";
        }
    }

    [JsonIgnore] public bool IsBlank => string.IsNullOrWhiteSpace(text);
}

/// Swift's default date encoding: seconds since 2001-01-01 UTC, as a number.
public sealed class ReferenceDateConverter : JsonConverter<DateTime>
{
    static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Reference.AddSeconds(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value.ToUniversalTime() - Reference).TotalSeconds);
}

/// Scratch notes: somewhere to put a thought down for an hour. Created empty
/// and instantly, deleted in one click, and the blank ones sweep themselves
/// out when the tab is left.
public sealed class NoteStore : Observable
{
    public ObservableCollection<Note> Notes { get; } = [];

    static string FilePath => Support.File("notes.json");

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly DebouncedWrite saves = new(TimeSpan.FromMilliseconds(600));

    Note? selected;
    public Note? Selected
    {
        get => selected;
        set
        {
            if (selected != null) selected.IsSelected = false;
            if (!Set(ref selected, value)) return;
            if (selected != null) selected.IsSelected = true;
        }
    }

    public NoteStore() => Load();

    /// Newest on top, and the order never changes afterwards: a list that
    /// reshuffles itself on every edit loses the reader's place.
    public void Add()
    {
        var note = new Note();
        Notes.Insert(0, note);
        Selected = note;
        ScheduleSave();
    }

    public void Update(Note note, string text)
    {
        if (note.Text == text) return;
        note.Text = text;
        note.Edited = DateTime.UtcNow;
        ScheduleSave();
    }

    public void Remove(Note note)
    {
        int index = Notes.IndexOf(note);
        Notes.Remove(note);
        if (Selected == note)
            Selected = Notes.Count == 0 ? null : Notes[Math.Min(index, Notes.Count - 1)];
        ScheduleSave();
    }

    /// Called when the tab is left or the panel collapses.
    public void Leave()
    {
        foreach (var blank in Notes.Where(n => n.IsBlank).ToList()) Notes.Remove(blank);
        if (Selected != null && !Notes.Contains(Selected)) Selected = Notes.FirstOrDefault();
        ScheduleSave();
        Flush();
    }

    public void Flush() => saves.Flush();

    void Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(FilePath), Json);
            foreach (var note in stored ?? []) Notes.Add(note);
        }
        catch (FileNotFoundException) { }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Support.Log($"cannot read notes.json: {error.Message}");
        }
        Selected = Notes.FirstOrDefault();
    }

    void ScheduleSave() => saves.Schedule(Persist);

    void Persist()
    {
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(Notes, Json)); }
        catch (IOException error) { Support.Log($"cannot write notes.json: {error.Message}"); }
    }
}
