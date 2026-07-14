using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Medisync.RisLocalGateway.ConfigApp.ViewModels;

namespace Medisync.RisLocalGateway.ConfigApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Initialize();
            vm.LogViewer.LinesAppended += OnLogLinesAppended;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.LogViewer.LinesAppended -= OnLogLinesAppended;
            vm.Shutdown();
        }
    }

    private void OnLogLinesAppended(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.LogViewer.AutoScroll && vm.LogViewer.Lines.Count > 0)
        {
            LogListBox.ScrollIntoView(vm.LogViewer.Lines[vm.LogViewer.Lines.Count - 1]);
        }
    }

    // Chọn ngày trong lịch popup → đóng popup (bỏ check ToggleButton). Dùng ToggleButton thay
    // WPF DatePicker vì control đó render ô ngày lỗi trên máy này.
    private void OnFromDateSelected(object sender, SelectionChangedEventArgs e) => FromToggle.IsChecked = false;
    private void OnToDateSelected(object sender, SelectionChangedEventArgs e) => ToToggle.IsChecked = false;

    /// <summary>
    /// Sort DataGrid "Danh sách study": tự điều khiển hoàn toàn để LẦN ĐẦU click 1 cột = DESCENDING
    /// (mặc định của WPF là ascending). Click tiếp trên cùng cột thì đảo asc↔desc.
    /// </summary>
    private void OnStudiesSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var grid = (DataGrid)sender;
        var path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;

        // Đang desc → chuyển asc; còn lại (null/asc) → desc ⇒ lần đầu luôn desc.
        var newDir = e.Column.SortDirection == ListSortDirection.Descending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        foreach (var c in grid.Columns) c.SortDirection = c == e.Column ? newDir : null;

        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view is null) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(path, newDir));
        view.Refresh();
    }
}
