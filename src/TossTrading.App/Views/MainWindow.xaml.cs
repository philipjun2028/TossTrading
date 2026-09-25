using System.ComponentModel;
using System.Windows;
using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class MainWindow : ThemedWindow
{
    private readonly MainViewModel _vm = new();
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing) return;
        if (_vm.IsRunning)
        {
            var r = DXMessageBox.Show("엔진이 실행 중입니다. 종료하면 봇의 청산 관리가 중단됩니다.\n종료할까요?",
                "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        e.Cancel = true;
        _closing = true;
        await _vm.ShutdownAsync();
        Close();
    }
}
