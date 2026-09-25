using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using TossTrading.App.Services;
using TossTrading.App.Views;

namespace TossTrading.App;

public partial class App : Application
{
    // 웹소켓은 계정당 2연결 제한 → 프로그램 중복 실행 방지 (설계 문서 3.5)
    private static Mutex? _singleInstance;

    public static string CrashLogPath => Path.Combine(SettingsStore.DataDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "TossTrading.SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("TossTrading 이 이미 실행 중입니다.", "TossTrading", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            ReportException(args.Exception, "UI 스레드");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ReportException(ex, "백그라운드 스레드", showDialog: false);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportException(args.Exception, "비동기 작업", showDialog: false);
            args.SetObserved();
        };

        base.OnStartup(e);

        // 창 생성 실패 시 앱이 바로 종료되지 않도록 시작 중에는 명시적 종료 모드
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (TryShowMainWindow(Theme.Win11DarkName, out var first)) return;

        // Win11Dark 테마에서 실패하면 Core 패키지에 포함된 기본 테마로 한 번 더 시도
        ReportException(first!, "메인 창 생성 (Win11Dark 테마)", showDialog: false);
        if (TryShowMainWindow(Theme.Office2019ColorfulName, out var second))
        {
            MessageBox.Show($"Win11Dark 테마로 화면을 여는 중 오류가 나서 기본 테마로 실행했습니다.\n원인은 다음 파일에 기록했습니다:\n{CrashLogPath}",
                "TossTrading", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReportException(second!, "메인 창 생성 (기본 테마)");
        Shutdown();
    }

    private bool TryShowMainWindow(string theme, out Exception? error)
    {
        MainWindow? window = null;
        try
        {
            ApplicationThemeHelper.ApplicationThemeName = theme;
            window = new MainWindow();
            window.Show();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            try { window?.Close(); } catch { /* 반쯤 생성된 창 정리 실패는 무시 */ }
            error = ex;
            return false;
        }
    }

    /// <summary>
    /// 예외 전체(내부 예외·호출 스택 포함)를 crash.log 에 남기고 클립보드에 복사한 뒤,
    /// TargetInvocationException 같은 포장 예외를 벗긴 실제 원인을 보여준다.
    /// </summary>
    public static void ReportException(Exception ex, string where, bool showDialog = true)
    {
        var detail = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(CrashLogPath, detail);
        }
        catch
        {
            // 로그 기록 실패는 무시
        }

        if (!showDialog) return;

        var root = ex;
        while (root is TargetInvocationException or AggregateException or System.Windows.Markup.XamlParseException
               && root.InnerException is not null)
            root = root.InnerException;

        try { Clipboard.SetText(detail); } catch { /* 클립보드 사용 불가 시 무시 */ }

        MessageBox.Show(
            $"{root.GetType().Name}: {root.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"위치: {FirstFrames(root)}{Environment.NewLine}{Environment.NewLine}" +
            $"전체 내용은 클립보드에 복사했고 다음 파일에도 기록했습니다:{Environment.NewLine}{CrashLogPath}",
            "예기치 않은 오류", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FirstFrames(Exception ex)
    {
        var lines = (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "(호출 스택 없음)" : string.Join(Environment.NewLine, lines.Take(4));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
