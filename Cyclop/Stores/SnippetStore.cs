using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Data;

namespace Cyclop.Stores;

public sealed class Snippet
{
    /// Optional; written without the key when empty, like on the Mac.
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    /// Name and value together: two snippets sharing a name are two snippets.
    [JsonIgnore] public string Id => $"{Label}\0{Text}";

    [JsonIgnore] public bool HasLabel => !string.IsNullOrEmpty(Label);

    [JsonIgnore]
    public string Preview => string.Join(' ', Text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// Guessed from the value, so a row is recognisable before it is read.
    [JsonIgnore]
    public string Glyph
    {
        get
        {
            var value = Text.Trim();
            if (value.Contains('@') && !value.Contains(' ')) return "";            // Mail
            if (value.StartsWith("http://") || value.StartsWith("https://")) return ""; // Link
            if (value.Count(char.IsDigit) >= 7 && value.All(c => char.IsDigit(c) || " +-()".Contains(c)))
                return "";                                                           // Phone
            return "";
        }
    }
}

/// A hand-kept list of things worth not retyping, in
/// `%APPDATA%\Cyclop\snippets.json` — a plain array of
/// `{"label": "...", "text": "..."}` where `label` may be left out.
/// The same file format as on the Mac.
public sealed class SnippetStore : Observable
{
    public ObservableCollection<Snippet> Items { get; } = [];
    public ICollectionView View { get; }

    public static string FilePath => Support.File("snippets.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // The file is meant to be edited by hand: no \/ in URLs, no ж
        // in place of Cyrillic.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    string query = "";
    public string Query
    {
        get => query;
        set { if (Set(ref query, value)) View.Refresh(); }
    }

    bool fileBroken;
    /// The file exists but cannot be parsed. The one state in which writing is
    /// forbidden: "could not read" and "read as it is" are different answers,
    /// and only the second makes writing back safe.
    public bool FileBroken
    {
        get => fileBroken;
        private set => Set(ref fileBroken, value);
    }

    public SnippetStore()
    {
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Matches;
    }

    /// Re-read on every visit: the file is edited from outside the app.
    public void Reload()
    {
        string json;
        try { json = File.ReadAllText(FilePath); }
        catch (FileNotFoundException) { Replace([]); FileBroken = false; return; }
        catch (IOException error) { Support.Log($"cannot read snippets.json: {error.Message}"); return; }

        try
        {
            Replace(JsonSerializer.Deserialize<List<Snippet>>(json, Json) ?? []);
            FileBroken = false;
        }
        catch (JsonException error)
        {
            // Keep what is on screen and say so: silence here is what turns a
            // stray comma into a lost file.
            FileBroken = true;
            Support.Log($"snippets.json is not readable: {error.Message}");
        }
    }

    /// Re-reads first, so an entry added in an editor meanwhile is not
    /// written over.
    public void Add(string label, string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;
        var name = label.Trim();
        var snippet = new Snippet { Label = name.Length == 0 ? null : name, Text = value };

        Reload();
        if (FileBroken) return;
        var duplicate = Items.FirstOrDefault(s => s.Id == snippet.Id);
        if (duplicate != null) Items.Remove(duplicate);
        Items.Insert(0, snippet);
        Persist();
    }

    public void Remove(Snippet snippet)
    {
        Items.Remove(snippet);
        Persist();
    }

    /// Not marked internal: a snippet pasted from here belongs in clipboard
    /// history like any other copy.
    public void Copy(Snippet snippet)
    {
        var data = new DataObject();
        data.SetText(snippet.Text, TextDataFormat.UnicodeText);
        Support.SetClipboard(data);
    }

    /// Selecting a file that does not exist yet opens nothing, so an empty
    /// list is written first — the state `Reload` treats as valid and empty.
    public static void Reveal()
    {
        if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "[]");
        Support.Reveal(FilePath);
    }

    void Persist()
    {
        if (FileBroken) return;
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(Items, Json)); }
        catch (IOException error) { Support.Log($"cannot write snippets.json: {error.Message}"); }
    }

    void Replace(IEnumerable<Snippet> snippets)
    {
        Items.Clear();
        foreach (var snippet in snippets) Items.Add(snippet);
    }

    /// Case- and accent-blind, over the name and the value alike.
    bool Matches(object item)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return true;
        var snippet = (Snippet)item;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return compare.IndexOf(snippet.Label ?? "", needle, options) >= 0
            || compare.IndexOf(snippet.Text, needle, options) >= 0;
    }
}
