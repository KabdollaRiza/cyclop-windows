using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cyclop.Stores;

namespace Cyclop.Panes;

public partial class ClipboardPane : UserControl
{
    ClipboardStore? store;
    Action<string>? toast;

    public ClipboardPane() => InitializeComponent();

    public void Attach(ClipboardStore clipboard, Action<string> showToast)
    {
        store = clipboard;
        toast = showToast;
        List.ItemsSource = clipboard.Items;
        clipboard.Items.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    void Refresh()
    {
        bool empty = store!.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ClipItem item) return;
        store!.Copy(item);
        toast?.Invoke("Copied");
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ClipItem item) store!.Remove(item);
    }

    void Clear_Click(object sender, RoutedEventArgs e) => store!.Clear();
}
