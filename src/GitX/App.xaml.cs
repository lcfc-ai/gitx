using GitX.Core.Logging;
using System.IO;
using System.Windows;

namespace GitX;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var bootstrapper = new AppBootstrapper(this);
        bootstrapper.Start(e.Args);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error(e.Exception, "UI 线程未处理异常");
        MessageBox.Show($"程序发生异常：{e.Exception.Message}", "GitX 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        AppLog.Error(ex, "全局未处理异常");
        try
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "GitX_CrashLog.txt");
            File.WriteAllText(logPath, $"[{DateTime.Now}] {ex?.ToString() ?? "Unknown"}");
            MessageBox.Show($"程序崩溃，日志已导出到桌面：{logPath}", "GitX 致命错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error(e.Exception, "Task 未观察异常");
        e.SetObserved();
    }
}
