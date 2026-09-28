using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cyclop.Stores;

namespace Cyclop.Panes;

public partial class ShelfPane : UserControl
{
    ShelfStore? store;
    Action<string>? toast;

    ShelfItem? pressed;
    Point pressedAt;

    public ShelfPane() => InitializeComponent();

    public void Attach(ShelfStore shelf, Action<string> showToast)
    {
        store = shelf;
        toast = showToast;
        List.ItemsSource = shelf.Items;
        shelf.Items.CollectionChanged += (_, _) => Refresh();
        shelf.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    /// Lights the drop zone while a file is dragged over the panel.
    public void SetTargeted(bool on)
    {
        DropOutline.Stroke = (System.Windows.Media.Brush)FindResource(on ? "Secondary" : "Hairline");
        DropOutline.Fill = on ? (System.Windows.Media.Brush)FindResource("Surface") : System.Windows.Media.Brushes.Transparent;
    }

    void Refresh()
    {
        bool empty = store!.Items.Count == 0;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Footer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        int selected = store.SelectedCount;
        SelectedText.Text = selected > 0 ? $"Selected: {selected} · copied" : $"{store.Items.Count} on the shelf";
        DeselectButton.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static ShelfItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ShelfItem;

    // MARK: - Click, double click, drag

    void Card_Down(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not ShelfItem item) return;
        if (e.ClickCount == 2)
        {
            store!.Open(item);
            pressed = null;
            e.Handled = true;
            return;
        }
        pressed = item;
        pressedAt = e.GetPosition(this);
    }

    void Card_Move(object sender, MouseEventArgs e)
    {
        if (pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - pressedAt;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;

        var item = pressed;
        pressed = null;
        var paths = store!.DragPaths(item);
        var data = new DataObject(DataFormats.FileDrop, paths);
        // Out of the panel the file goes wherever it is dropped: a folder on
        // the same drive moves it, anything else copies. Modal until the drop.
        var result = DragDrop.DoDragDrop((DependencyObject)sender, data,
            DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        if (result.HasFlag(DragDropEffects.Move)) store.RefreshFromDisk();
    }

    void Card_Up(object sender, MouseButtonEventArgs e)
    {
        if (pressed == null || ItemOf(sender) != pressed) return;
        var item = pressed;
        pressed = null;
        store!.Click(item, additive: Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (item.IsSelected) toast?.Invoke("Copied — paste anywhere");
    }

    void Scroller_Wheel(object sender, MouseWheelEventArgs e)
    {
        // The strip is horizontal; a mouse wheel only turns one way.
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    // MARK: - Menu and footer

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not ShelfItem item) return;
        store!.Copy([item]);
        toast?.Invoke("Copied");
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ShelfItem item) store!.Open(item);
    }

    void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ShelfItem item) store!.Reveal(item);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is ShelfItem item) store!.Remove(item);
    }

    void Deselect_Click(object sender, RoutedEventArgs e) => store!.ClearSelection();

    void Clear_Click(object sender, RoutedEventArgs e) => store!.Clear();

    void Folder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ScreenshotVault.Folder}\"") { UseShellExecute = true });
}
