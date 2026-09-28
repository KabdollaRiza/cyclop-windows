using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cyclop.Stores;

namespace Cyclop.Panes;

public partial class SnippetsPane : UserControl
{
    SnippetStore? store;
    Action<string>? toast;

    public SnippetsPane() => InitializeComponent();

    public void Attach(SnippetStore snippets, Action<string> showToast)
    {
        store = snippets;
        toast = showToast;
        DataContext = snippets;
        snippets.Items.CollectionChanged += (_, _) => Refresh();
        snippets.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    void Refresh() =>
        Empty.Visibility = store!.Items.Count == 0 && !store.FileBroken ? Visibility.Visible : Visibility.Collapsed;

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Snippet snippet) return;
        store!.Copy(snippet);
        toast?.Invoke("Copied");
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Snippet snippet) store!.Remove(snippet);
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        NewLabel.Text = "";
        NewText.Text = "";
        Form.Visibility = Visibility.Visible;
        Window.GetWindow(this)?.Activate();
        NewText.Focus();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewText.Text))
        {
            NewText.Focus();
            return;
        }
        store!.Add(NewLabel.Text, NewText.Text);
        if (store.FileBroken)
        {
            toast?.Invoke("Not saved: snippets.json is broken");
            return;
        }
        Form.Visibility = Visibility.Collapsed;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Form.Visibility = Visibility.Collapsed;

    void Reveal_Click(object sender, RoutedEventArgs e) => SnippetStore.Reveal();
}
