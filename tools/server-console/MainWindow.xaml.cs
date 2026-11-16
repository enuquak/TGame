using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TGame.ServerConsole;

internal partial class MainWindow : Window
{
    private const int RefreshIntervalMilliseconds = 500;
    private const int MaxOutputBatchCharacters = 65536;
    private const int MaxOutputCharacters = 160000;
    private const int RetainedOutputCharacters = 100000;
    private const double ScrollEndTolerance = 2;

    private sealed record ServerCommand(string Name, string Icon, Func<Task>? Execute = null);
    private readonly ConsoleSession session;
    private readonly UserSettings settings;
    private ProcessPage[] pages = [];
    private ListCollectionView pickerView = null!;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(RefreshIntervalMilliseconds) };
    private readonly ConcurrentQueue<string> output = new();
    private bool busy;
    private bool maximized;
    private string? refreshError;

    public MainWindow(ConsoleSession session, UserSettings userSettings)
    {
        this.session = session;
        settings = userSettings;
        InitializeComponent();
        RestoreWindow();
        MachineLabel.Text = Environment.MachineName;
        EnvironmentLabel.Text = session.ProjectSettings.EnvironmentName;
        EnvironmentLabel.ToolTip = EnvironmentLabel.Text;
        pages = CreatePages();
        pickerView = new ListCollectionView(pages);
        PickerResults.ItemsSource = pickerView;
        Commands.ItemsSource = session.ProjectSettings.Commands.Select(action => action switch
        {
            ConsoleAction.Start => new ServerCommand("启动服务器", "\uE768", session.StartAsync),
            ConsoleAction.Stop => new ServerCommand("关闭服务器", "\uE71A"),
            ConsoleAction.ExportProtobuf => new ServerCommand("导出 Protobuf", "\uE74E", session.ExportAsync),
            ConsoleAction.Build => new ServerCommand("编译服务器 C#", "\uE90F", () => session.BuildAsync(false)),
            ConsoleAction.BuildAndRestart => new ServerCommand("编译并重启", "\uE72C", () => session.BuildAsync(true)),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的服务器命令")
        }).ToArray();
        InitializeLogs();
        ProcessTabs.ItemsSource = pages;
        ProcessTabs.SelectedItem = pages.FirstOrDefault(page => page.Role == settings.SelectedRole) ?? pages.FirstOrDefault();
        if (settings.Warning is { Length: > 0 } warning)
        {
            output.Enqueue(warning);
            OperationStatus.Text = warning;
            SetOutputOpen(true);
        }

        session.Output += output.Enqueue;
        session.InstancesChanged += ReloadInstances;
        timer.Tick += (_, _) => RefreshWindow();
        Loaded += (_, _) =>
        {
            RefreshWindow();
            timer.Start();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            session.Output -= output.Enqueue;
            session.InstancesChanged -= ReloadInstances;
        };
    }

    private void ReloadInstances()
    {
        SaveSettings();
        pages = CreatePages();
        pickerView = new ListCollectionView(pages);
        PickerResults.ItemsSource = pickerView;
        ProcessTabs.ItemsSource = pages;
        ProcessTabs.SelectedItem = pages.FirstOrDefault(page => page.Role == settings.SelectedRole) ?? pages.FirstOrDefault();
    }

    private ProcessPage[] CreatePages()
    {
        var result = session.Instances.Select(instance => new ProcessPage(instance)).ToArray();
        foreach (var page in result)
        {
            if (!settings.Logs.TryGetValue(page.Role, out var preference))
            {
                continue;
            }

            page.Keyword = preference.Keyword;
            page.MatchesOnly = preference.MatchesOnly;
            page.Following = preference.Following;
        }

        return result;
    }

    private void RestoreWindow()
    {
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width);
        MinHeight = Math.Min(MinHeight, work.Height);
        Width = Math.Clamp(double.IsFinite(settings.Width) && settings.Width > 0 ? settings.Width : Width, MinWidth, work.Width);
        Height = Math.Clamp(double.IsFinite(settings.Height) && settings.Height > 0 ? settings.Height : Height, MinHeight, work.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        maximized = settings.Maximized;
        if (maximized)
        {
            WindowState = WindowState.Maximized;
        }

        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                maximized = WindowState == WindowState.Maximized;
            }
        };
    }

    private void SaveSettings()
    {
        var bounds = RestoreBounds;
        settings.Width = bounds.IsEmpty ? Width : bounds.Width;
        settings.Height = bounds.IsEmpty ? Height : bounds.Height;
        settings.Maximized = maximized;
        settings.SelectedRole = active?.Role;
        settings.Logs = pages.ToDictionary(page => page.Role, page => new LogPreference(page.Keyword, page.MatchesOnly, page.Following));
        try
        {
            settings.Save();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"用户偏好保存失败 · {error.Message}");
        }
    }

    private void RefreshWindow()
    {
        try
        {
            session.Refresh();
            refreshError = null;
        }
        catch (Exception error)
        {
            if (refreshError != error.Message)
            {
                refreshError = error.Message;
                ReportError("读取进程状态失败", error);
            }
        }

        foreach (var page in pages)
        {
            if (page.Refresh() && page == active)
            {
                ResetLogRows();
            }
        }

        ProcessSummary.Text = $"{pages.Length} 个实例  ·  {pages.Count(page => page.Status == ServerStatus.Ready)} 个就绪";
        UpdateLogs();
        FlushOutput();
    }

    private async void CommandDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject origin &&
            ItemsControl.ContainerFromElement(Commands, origin) is ListBoxItem { DataContext: ServerCommand command })
        {
            await RunCommand(command);
        }
    }

    private async void CommandKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Commands.SelectedItem is ServerCommand command)
        {
            e.Handled = true;
            await RunCommand(command);
        }
    }

    private async Task RunCommand(ServerCommand command)
    {
        if (busy)
        {
            return;
        }

        var execute = command.Execute;
        if (execute == null)
        {
            var dialog = new StopServersWindow(pages) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var selected = dialog.SelectedServers;
            execute = () => session.StopAsync(selected);
        }
        busy = true;
        Commands.IsEnabled = false;
        SetOutputOpen(true);
        OperationStatus.Text = $"{command.Name}…";
        OperationStatus.Foreground = (Brush)FindResource("Muted");
        output.Enqueue($"[{DateTime.Now:HH:mm:ss}] {command.Name}");
        try
        {
            await execute();
            OperationStatus.Text = $"已完成 · {command.Name}";
        }
        catch (Exception error)
        {
            ReportError(command.Name + "失败", error);
        }
        finally
        {
            busy = false;
            Commands.IsEnabled = true;
            RefreshWindow();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            OperationStatus.Text = "命令执行中，完成后可关闭控制台。";
        }

        base.OnClosing(e);
        if (!e.Cancel && IsLoaded)
        {
            SaveSettings();
        }
    }

    private void ReportError(string title, Exception error)
    {
        OperationStatus.Text = title + " · " + error.Message;
        OperationStatus.Foreground = Brushes.Firebrick;
        output.Enqueue($"[{DateTime.Now:HH:mm:ss}] {title} · {error.Message}");
        SetOutputOpen(true);
    }

    private void ToggleOutput(object sender, RoutedEventArgs e) => SetOutputOpen(!OutputBox.IsVisible);

    private void SetOutputOpen(bool open)
    {
        OutputBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OutputToggle.Content = open ? "▾  命令输出" : "▸  命令输出";
    }

    private void FlushOutput()
    {
        if (output.IsEmpty)
        {
            return;
        }

        var batch = new StringBuilder();
        while (batch.Length < MaxOutputBatchCharacters && output.TryDequeue(out var line))
        {
            batch.AppendLine(line);
        }

        var atEnd = OutputBox.VerticalOffset >= OutputBox.ExtentHeight - OutputBox.ViewportHeight - ScrollEndTolerance;
        if (OutputBox.Text.Length > MaxOutputCharacters)
        {
            var text = OutputBox.Text;
            var split = text.IndexOf('\n', text.Length - RetainedOutputCharacters);
            OutputBox.Text = split >= 0 ? text[(split + 1)..] : text[^RetainedOutputCharacters..];
        }

        OutputBox.AppendText(batch.ToString());
        if (atEnd)
        {
            OutputBox.ScrollToEnd();
        }
    }

    private void OpenPicker(object sender, RoutedEventArgs e) => Picker.IsOpen = true;

    private void PickerOpened(object? sender, EventArgs e)
    {
        PickerSearch.Clear();
        PickerResults.SelectedItem = active;
        PickerResults.ScrollIntoView(active);
        PickerSearch.Focus();
    }

    private void PickerSearchChanged(object sender, TextChangedEventArgs e)
    {
        var term = PickerSearch.Text.Trim();
        pickerView.Filter = item => item is ProcessPage page && (page.Role.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (page.Pid?.ToString().Contains(term, StringComparison.Ordinal) ?? false));
        PickerResults.SelectedIndex = pickerView.Count > 0 ? 0 : -1;
    }

    private void PickerSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && PickerResults.SelectedItem is { } selected)
        {
            PickerResults.ScrollIntoView(selected);
            (PickerResults.ItemContainerGenerator.ContainerFromItem(selected) as ListBoxItem)?.Focus();
            e.Handled = true;
            return;
        }

        PickerResultsKeyDown(sender, e);
    }

    private void PickerResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        if (e.Key == Key.Enter && PickerResults.SelectedItem is ProcessPage page)
        {
            ChoosePage(page);
        }

        Picker.IsOpen = false;
        e.Handled = true;
        PickerButton.Focus();
    }

    private void ChooseProcess(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject origin &&
            ItemsControl.ContainerFromElement(PickerResults, origin) is ListBoxItem { DataContext: ProcessPage page })
        {
            ChoosePage(page);
            Picker.IsOpen = false;
        }
    }

    private void ChoosePage(ProcessPage page)
    {
        ProcessTabs.SelectedItem = page;
        ProcessTabs.ScrollIntoView(page);
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Picker.IsOpen)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }
        else if (e.Key == Key.F3)
        {
            FindMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (e.Key == Key.Enter && FilterBox.IsKeyboardFocused)
        {
            FindMatch(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && LogList.IsKeyboardFocusWithin)
        {
            CopyLog(sender, e);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void CopyLog(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not LogEntry row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Raw);
        }
        catch (COMException error)
        {
            ReportError("复制日志失败", error);
        }
    }
}
