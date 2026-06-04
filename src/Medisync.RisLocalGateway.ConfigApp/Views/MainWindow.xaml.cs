using System.Windows;
using System.Windows.Controls;
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
            PasswordBoxControl.Password = vm.Configuration.PlainPassword;
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

    private void PasswordBoxControl_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
        {
            vm.Configuration.PlainPassword = pb.Password;
        }
    }

    private void OnLogLinesAppended(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.LogViewer.AutoScroll && vm.LogViewer.Lines.Count > 0)
        {
            LogListBox.ScrollIntoView(vm.LogViewer.Lines[vm.LogViewer.Lines.Count - 1]);
        }
    }
}
