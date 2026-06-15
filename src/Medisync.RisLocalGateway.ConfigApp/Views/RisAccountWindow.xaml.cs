using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Medisync.RisLocalGateway.ConfigApp.ViewModels;

namespace Medisync.RisLocalGateway.ConfigApp.Views;

public partial class RisAccountWindow : Window
{
    private readonly RisAccountViewModel _viewModel;

    public RisAccountWindow(RisAccountViewModel viewModel)
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
        // PasswordBox không bind được → set thủ công sau khi VM Load xong.
        PasswordBoxControl.Password = _viewModel.PlainPassword;
    }

    private void PasswordBoxControl_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
        {
            _viewModel.PlainPassword = pb.Password;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RisAccountViewModel.ShouldClose) && _viewModel.ShouldClose)
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
