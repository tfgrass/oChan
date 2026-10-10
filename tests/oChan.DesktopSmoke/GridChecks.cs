using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using oChan.Boards.FourChan;
using oChan.Desktop;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.DesktopSmoke;

internal static class GridChecks
{
    public static async Task RunAsync(MainWindow window, IImageBoard provider)
    {
        FourChanBoard firstBoard = new FourChanBoard(provider, new Uri("https://boards.4chan.org/g/"));
        FourChanBoard secondBoard = new FourChanBoard(provider, new Uri("https://boards.4chan.org/po/"));
        using ThreadRow two = CreateRow(firstBoard, "2", 2);
        using ThreadRow ten = CreateRow(firstBoard, "10", 10);
        using BoardRow first = new BoardRow(firstBoard);
        using BoardRow second = new BoardRow(secondBoard);
        window.UrlList.Add(ten);
        window.UrlList.Add(two);
        window.BoardsList.Add(first);
        window.BoardsList.Add(second);
        TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        DataGrid threadGrid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        DataGrid boardGrid = window.FindControl<DataGrid>("BoardsDataGrid")!;
        try
        {
            tabs.SelectedIndex = 0;
            await WaitForRowsAsync(threadGrid, two, ten);
            foreach (string header in new[] { "Thread ID", "Progress", "Queued", "Active" })
            {
                DataGridColumn column = threadGrid.Columns.Single(column => (string?)column.Header == header);
                threadGrid.CollectionView.SortDescriptions.Clear();
                column.Sort(ListSortDirection.Ascending);
                await Program.WaitUntilAsync(() => ReferenceEquals(threadGrid.CollectionView.Cast<ThreadRow>().First(), two));
                column.Sort(ListSortDirection.Descending);
                await Program.WaitUntilAsync(() => ReferenceEquals(threadGrid.CollectionView.Cast<ThreadRow>().First(), ten));
            }

            Console.WriteLine("SMOKE numeric thread ID, progress, queued and active sorting distinguishes 2 from 10.");
            await WaitForRowsAsync(threadGrid, two, ten);
            await CheckContextSelectionAsync(window, threadGrid, two, ten);
            await WaitForRowsAsync(threadGrid, two, ten);
            DataGridColumn activeColumn = threadGrid.Columns.Single(column => (string?)column.Header == "Active");
            DataGridCell activeCell = FindRow(threadGrid, ten).GetVisualDescendants().OfType<DataGridCell>()
                .Single(cell => ReferenceEquals(DataGridColumn.GetColumnContainingElement(cell), activeColumn));
            Point? activeEnd = activeCell.TranslatePoint(new Point(activeCell.Bounds.Width, 0), threadGrid);
            if (!activeCell.IsVisible || activeCell.Bounds.Width <= 0 || activeEnd == null
                || activeEnd.Value.X > threadGrid.Bounds.Width + 1)
                throw new InvalidOperationException("The default window hides the active-download column offscreen.");
            Program.CaptureWindow(window, "smoke-grid.png");

            tabs.SelectedIndex = 1;
            await WaitForRowsAsync(boardGrid, first, second);
            await CheckContextSelectionAsync(window, boardGrid, first, second);
            Console.WriteLine("SMOKE thread and board context menus select the pointed row and clear stale selection on blank space.");
        }
        finally
        {
            threadGrid.ContextMenu?.Close();
            boardGrid.ContextMenu?.Close();
            threadGrid.CollectionView.SortDescriptions.Clear();
            window.UrlList.Remove(two);
            window.UrlList.Remove(ten);
            window.BoardsList.Remove(first);
            window.BoardsList.Remove(second);
            tabs.SelectedIndex = 0;
        }
    }

    private static ThreadRow CreateRow(IBoard board, string id, int value)
    {
        FourChanThread thread = new FourChanThread(board, new Uri("https://boards.4chan.org/g/thread/" + id))
        {
            Title = "Grid fixture " + id,
            TotalMediaCount = 100,
            DownloadedMedia = new HashSet<string>(Enumerable.Range(1, value).Select(number => number.ToString()))
        };
        ThreadRow row = new ThreadRow(thread);
        row.UpdateSnapshot(thread.GetSnapshot(new DownloadCounts(value, value)));
        return row;
    }

    private static Task WaitForRowsAsync(DataGrid grid, object first, object second) => Program.WaitUntilAsync(() =>
        VisibleRows(grid, first).Count() == 1 && VisibleRows(grid, second).Count() == 1);

    private static IEnumerable<DataGridRow> VisibleRows(DataGrid grid, object item) => grid.GetVisualDescendants()
        .OfType<DataGridRow>().Where(row => row.IsVisible && row.Bounds.Height > 0 && ReferenceEquals(row.DataContext, item));

    private static DataGridRow FindRow(DataGrid grid, object item) => VisibleRows(grid, item).Single();

    private static async Task CheckContextSelectionAsync(MainWindow window, DataGrid grid, object first, object second)
    {
        DataGridRow target = FindRow(grid, second);
        Point position = target.TranslatePoint(new Point(20, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The context target is not attached to the window.");
        ContextMenu menu = grid.ContextMenu ?? throw new InvalidOperationException("The grid context menu is missing.");
        foreach (KeyModifiers modifiers in new[] { KeyModifiers.None, KeyModifiers.Control })
        {
            grid.SelectedItem = first;
            RightPress(window, position, modifiers);
            if (!ReferenceEquals(grid.SelectedItem, second))
                throw new InvalidOperationException("Right click left the previously selected row active: " + modifiers);
            menu.Open(grid);
            await Program.WaitUntilAsync(() => menu.IsOpen);
            if (!ReferenceEquals(grid.SelectedItem, second))
                throw new InvalidOperationException("Opening the context menu changed the selected row.");
            menu.Close();
        }

        Point blank = grid.TranslatePoint(new Point(20, grid.Bounds.Height - 30), window)
            ?? throw new InvalidOperationException("The grid is not attached to the window.");
        RightPress(window, blank, KeyModifiers.None);
        if (grid.SelectedItem != null)
            throw new InvalidOperationException("Right click on blank space retained a stale action target.");
    }

    private static void RightPress(MainWindow window, Point position, KeyModifiers modifiers)
    {
        using Pointer pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        PointerPointProperties properties = new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        InputElement hit = window.InputHitTest(position) as InputElement
            ?? throw new InvalidOperationException("The pointer target is not hit-testable.");
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, position, 0, properties, modifiers));
        pointer.Capture(null);
    }
}
