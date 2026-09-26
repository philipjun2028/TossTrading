using DevExpress.Xpf.Core;
using TossTrading.App.ViewModels;

namespace TossTrading.App.Views;

public partial class BacktestWindow : ThemedWindow
{
    public BacktestWindow(BacktestViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // 실행 중에 닫으면 취소
        Closing += (_, _) => { if (vm.IsRunning) vm.CancelCommand.Execute(null); };
    }
}
