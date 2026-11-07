using System.Windows;
using System.Windows.Controls;

namespace TGame.ServerConsole;

internal partial class StopServersWindow : Window
{
    public IReadOnlyList<ServerInstance> SelectedServers => Choices.SelectedItems.Cast<ProcessPage>()
        .Where(page => page.CanStop).Select(page => page.Instance).ToArray();

    public StopServersWindow(IEnumerable<ProcessPage> processes)
    {
        InitializeComponent();
        Choices.ItemsSource = processes;
        UpdateCount();
    }

    private void SelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var page in Choices.Items.Cast<ProcessPage>().Where(page => page.CanStop))
        {
            if (!Choices.SelectedItems.Contains(page))
            {
                Choices.SelectedItems.Add(page);
            }
        }
    }

    private void DeselectAll(object sender, RoutedEventArgs e) => Choices.UnselectAll();

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var count = SelectedServers.Count;
        CountLabel.Text = $"已选 {count} 个";
        Confirm.IsEnabled = count > 0;
    }

    private void ConfirmStop(object sender, RoutedEventArgs e)
    {
        UpdateCount();
        if (Confirm.IsEnabled)
        {
            DialogResult = true;
        }
    }
}
