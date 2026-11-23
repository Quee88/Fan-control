using System.ComponentModel;
using FanControl.ViewModels;
using Wpf.Ui.Controls;

namespace FanControl;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        ((App)System.Windows.Application.Current).OnWindowClosing(e);
        base.OnClosing(e);
    }
}
