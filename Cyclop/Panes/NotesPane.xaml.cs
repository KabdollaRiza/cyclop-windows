using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cyclop.Stores;

namespace Cyclop.Panes;

public partial class NotesPane : UserControl
{
    NoteStore? store;
    /// Set while the editor is being filled from the store, so that filling
    /// it is not mistaken for typing.
    bool loading;

    public NotesPane() => InitializeComponent();

    public void Attach(NoteStore notes)
    {
        store = notes;
        List.ItemsSource = notes.Notes;
        notes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NoteStore.Selected)) ShowSelected();
        };
        notes.Notes.CollectionChanged += (_, _) => ShowSelected();
        ShowSelected();
    }

    void ShowSelected()
    {
        var note = store!.Selected;
        bool has = note != null;
        Editor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Delete.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has || Editor.Text == note!.Text) return;
        loading = true;
        Editor.Text = note.Text;
        Editor.CaretIndex = Editor.Text.Length;
        loading = false;
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        store!.Add();
        Window.GetWindow(this)?.Activate();
        Editor.Focus();
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Note note) return;
        store!.Selected = note;
        Window.GetWindow(this)?.Activate();
        Editor.Focus();
    }

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || store?.Selected is not Note note) return;
        store.Update(note, Editor.Text);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (store!.Selected is Note note) store.Remove(note);
    }
}
