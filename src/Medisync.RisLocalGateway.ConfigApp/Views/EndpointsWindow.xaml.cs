using System.ComponentModel;
using System.Windows;
using Medisync.RisLocalGateway.ConfigApp.ViewModels;

namespace Medisync.RisLocalGateway.ConfigApp.Views;

public partial class EndpointsWindow : Window
{
    private readonly EndpointsViewModel _viewModel;

    public EndpointsWindow(EndpointsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Load();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EndpointsViewModel.ShouldClose) && _viewModel.ShouldClose)
        {
            Close();
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }
}
