using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TGame.ServerConsole;

internal partial class MainWindow
{
    private readonly ObservableCollection<LogEntry> rows = [];
    private ListCollectionView logs = null!;
    private ProcessPage? active;
    private long readThrough;
    private int matchCount;
    private bool switching;
    private ScrollViewer? logScroller;

    private void InitializeLogs()
    {
        logs = new ListCollectionView(rows);
        logs.Filter = item => active is not { MatchesOnly: true, Keyword.Length: > 0 } || ((LogEntry)item).Matches(active.Keyword);
        LogList.ItemsSource = logs;
    }

    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessTabs.SelectedItem is not ProcessPage page)
        {
            return;
        }

        if (active != null)
        {
            active.TopLine = ViewportLine();
            active.SelectedLine = (LogList.SelectedItem as LogEntry)?.Number;
        }
        active = page;
        switching = true;
        ActiveHeading.DataContext = page;
        FilterBox.Text = page.Keyword;
        OnlyMatches.IsChecked = page.MatchesOnly;
        Follow.IsChecked = page.Following;
        switching = false;
        ResetLogRows();
        UpdateLogs();
        if (page.SelectedLine.HasValue)
        {
            LogList.SelectedItem = rows.FirstOrDefault(row => row.Number == page.SelectedLine && logs.Contains(row));
        }

        if (!page.Following)
        {
            RestoreViewport(page.TopLine);
        }
    }

    private void ResetLogRows()
    {
        rows.Clear();
        matchCount = 0;
        readThrough = active?.ClearedThrough ?? 0;
        UpdateCounts();
    }

    private void UpdateLogs()
    {
        if (active is null)
        {
            return;
        }

        var source = active.Instance;
        if (source.LineCount < readThrough)
        {
            active.ClearedThrough = 0;
            active.TopLine = active.SelectedLine = null;
            ResetLogRows();
        }

        if (readThrough == source.LineCount)
        {
            return;
        }

        var first = source.LineCount - source.Lines.Count + 1;
        var viewport = !active.Following && rows.Count > 0 && rows[0].Number < first ? ViewportLine() : null;
        if (rows.Count != 0 && rows[^1].Number < first)
        {
            rows.Clear();
            matchCount = 0;
        }
        else
        {
            while (rows.Count != 0 && rows[0].Number < first)
            {
                if (IsMatch(rows[0]))
                {
                    matchCount--;
                }

                rows.RemoveAt(0);
            }
        }

        for (var n = Math.Max(first, readThrough + 1); n <= source.LineCount; n++)
        {
            var row = LogEntry.Parse(n, source.Lines[(int)(n - first)]);
            if (IsMatch(row))
            {
                matchCount++;
            }

            rows.Add(row);
        }
        readThrough = source.LineCount;
        UpdateCounts();
        if (active.Following)
        {
            FollowLatest();
        }
        else if (viewport.HasValue)
        {
            RestoreViewport(viewport);
        }
    }

    private bool IsMatch(LogEntry entry) => active is { Keyword.Length: > 0 } && entry.Matches(active.Keyword);

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (switching || active is null)
        {
            return;
        }

        var top = active.Following ? null : ViewportLine();
        active.Keyword = FilterBox.Text;
        active.MatchesOnly = OnlyMatches.IsChecked == true;
        matchCount = rows.Count(IsMatch);
        var selected = LogList.SelectedItem;
        logs.Refresh();
        if (selected != null && logs.Contains(selected))
        {
            LogList.SelectedItem = selected;
        }

        UpdateCounts();
        if (active.Following)
        {
            FollowLatest();
        }
        else
        {
            RestoreViewport(top);
        }
    }

    private void FollowChanged(object sender, RoutedEventArgs e)
    {
        if (active is null)
        {
            return;
        }

        active.Following = Follow.IsChecked == true;
        if (active.Following)
        {
            FollowLatest();
        }
    }

    private void FollowLatest()
    {
        if (logs.Count > 0)
        {
            LogList.ScrollIntoView(logs.GetItemAt(logs.Count - 1));
        }
    }

    private void UpdateCounts()
    {
        bool searching = active is { Keyword.Length: > 0 };
        ShownCount.Text = searching ? $"{matchCount} 匹配  /  {rows.Count} 行" : $"{rows.Count} 行";
        EmptyLog.Text = rows.Count > 0 ? "没有匹配的日志" : "暂无日志";
        EmptyLog.Visibility = logs.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        PreviousMatch.IsEnabled = NextMatch.IsEnabled = matchCount > 0;
    }

    private void PreviousClicked(object sender, RoutedEventArgs e) => FindMatch(-1);

    private void NextClicked(object sender, RoutedEventArgs e) => FindMatch(1);

    private void FindMatch(int direction)
    {
        if (active is null || matchCount == 0)
        {
            return;
        }

        var index = LogList.SelectedIndex;
        if (index < 0)
        {
            index = direction == 1 ? -1 : 0;
        }

        for (int i = 0; i < logs.Count; i++)
        {
            index = (index + direction + logs.Count) % logs.Count;
            var row = (LogEntry)logs.GetItemAt(index);
            if (!IsMatch(row))
            {
                continue;
            }

            active.Following = false;
            Follow.IsChecked = false;
            LogList.SelectedItem = row;
            LogList.ScrollIntoView(row);
            break;
        }
    }

    private void ClearLog(object sender, RoutedEventArgs e)
    {
        if (active is null)
        {
            return;
        }

        active.ClearedThrough = active.Instance.LineCount;
        active.TopLine = active.SelectedLine = null;
        ResetLogRows();
    }

    private ScrollViewer? LogScroller()
    {
        if (logScroller is null)
        {
            LogList.ApplyTemplate();
            logScroller = FindScrollViewer(LogList);
        }

        return logScroller;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private long? ViewportLine()
    {
        var index = (int)(LogScroller()?.VerticalOffset ?? 0);
        return index < logs.Count ? ((LogEntry)logs.GetItemAt(index)).Number : null;
    }

    private void RestoreViewport(long? top)
    {
        if (!top.HasValue || logs.Count == 0)
        {
            return;
        }

        int index = 0;
        while (index < logs.Count - 1 && ((LogEntry)logs.GetItemAt(index)).Number < top.Value)
        {
            index++;
        }

        LogScroller()?.ScrollToVerticalOffset(index);
    }
}
