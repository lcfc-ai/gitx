using GitX.Core.Logging;
using GitX.Services;
using GitX.ViewModels;
using GitX.Views;
using System.IO;
using System.Windows;

namespace GitX;

/// <summary>
/// 应用启动编排：参数校验、服务创建、窗口切换。
/// </summary>
internal sealed class AppBootstrapper
{
    private readonly Application _app;

    public AppBootstrapper(Application app)
    {
        _app = app;
    }

    public void Start(string[] args)
    {
        _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var repoPath = ParseRepoPath(args);
        if (string.IsNullOrEmpty(repoPath))
        {
            ShowInfo("请从 TortoiseGit 右键菜单启动工具，或传入 --repo=仓库路径 参数", "GitX 启动提示");
            _app.Shutdown();
            return;
        }

        repoPath = repoPath.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (!Directory.Exists(repoPath))
        {
            ShowError($"路径无效：{repoPath}", "GitX 错误");
            _app.Shutdown();
            return;
        }

        var gitDir = Path.Combine(repoPath, ".git");
        if (!Directory.Exists(gitDir))
        {
            ShowError("当前目录不是 Git 仓库（未找到 .git 文件夹）", "GitX 错误");
            _app.Shutdown();
            return;
        }

        try
        {
            var cacheLogService = new CacheLogService();
            var gitService = new GitService(repoPath);
            var diffService = new DiffService(gitService);
            var diffTreeFilterService = new DiffTreeFilterService();
            var fileService = new FileService(gitService, repoPath, cacheLogService);
            var textDiffService = new TextDiffService();

            var branchSelectVm = new BranchSelectViewModel(gitService);
            var branchSelectWindow = new BranchSelectWindow { DataContext = branchSelectVm };
            bool? result = branchSelectWindow.ShowDialog();

            if (result != true || branchSelectVm.SelectedBranch == null)
            {
                gitService.Dispose();
                cacheLogService.Dispose();
                _app.Shutdown();
                return;
            }

            var targetBranch = branchSelectVm.SelectedBranch;
            var currentBranch = gitService.GetCurrentBranchName();

            var mainVm = new MainWindowViewModel(
                gitService,
                diffService,
                diffTreeFilterService,
                fileService,
                cacheLogService,
                textDiffService,
                repoPath,
                currentBranch,
                targetBranch.FullName);

            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.Closed += (_, _) => _app.Shutdown();
            mainWindow.Show();

            _app.ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "启动失败");
            ShowError($"启动失败：{ex.Message}", "GitX 错误");
            _app.Shutdown();
        }
    }

    private static string? ParseRepoPath(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                return arg.Substring(7).Trim('"');
            }
        }

        if (args.Length > 0 && !args[0].StartsWith("-"))
        {
            return args[0].Trim('"');
        }

        return null;
    }

    private static void ShowInfo(string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ShowError(string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
