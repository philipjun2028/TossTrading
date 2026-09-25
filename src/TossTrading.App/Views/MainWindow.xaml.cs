using System.ComponentModel;
using System.Windows;
using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class MainWindow : ThemedWindow
{
    private readonly MainViewModel _vm = new();
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownDone || e.Cancel) return;

        // 엔진이 없으면(시작 전·창 생성 실패 등) 바로 닫는다
        if (!_vm.HasEngine)
        {
            _vm.StopRefresh();
            _shutdownDone = true;
            return;
        }

        var r = DXMessageBox.Show("엔진이 실행 중입니다. 종료하면 봇의 청산 관리가 중단됩니다.\n종료할까요?",
            "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        e.Cancel = true;
        if (r != MessageBoxResult.Yes) return;
        _ = ShutdownThenCloseAsync();
    }

    /// <summary>엔진을 비동기로 정리한 뒤, 현재 Closing 처리가 끝난 다음 틱에 다시 닫는다.</summary>
    private async Task ShutdownThenCloseAsync()
    {
        try { await _vm.ShutdownAsync(); }
        catch { /* 종료 중 오류는 무시하고 창은 닫는다 */ }
        finally
        {
            _shutdownDone = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
