using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TossTrading.App.Views;

namespace TossTrading.App;

public partial class App : Application
{
    // 웹소켓은 계정당 2연결 제한 → 프로그램 중복 실행 방지 (설계 문서 3.5)
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "TossTrading.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("TossTrading 이 이미 실행 중입니다.", "TossTrading", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "예기치 않은 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
