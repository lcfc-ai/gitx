using GitX.Core.Logging;
using GitX.Core.Models;
using LibGit2Sharp;
using System.Diagnostics;
using System.IO;

namespace GitX.Services;

/// <summary>
/// Git 差异计算能力：读取本地工作区与分支之间的文件变更。
/// </summary>
public partial class GitService
{
    /// <summary>
    /// 计算「本地工作区」与「目标分支最新 commit」之间的文件变更列表。
    /// 语义：本地磁盘上的代码相对目标分支的差异，只显示真正不一致的文件。
    /// 这意味着：当用户执行“下载全部”之后，如果本地工作区已经完全对齐目标分支，
    /// 左侧差异列表应当收敛为空。
    ///
    /// 实现策略：
    /// 1. 优先调用系统 git.exe diff --name-status &lt;targetSha&gt;，完整复用
    ///    .gitattributes 的 text 规范化、core.autocrlf 等 git 原生 diff 配置，
    ///    结果与命令行/IDE 看到的完全一致。
    ///    LibGit2Sharp 的 Diff.Compare 不完整应用 .gitattributes，会把 CRLF vs LF 误判为差异。
    /// 2. 未安装 git.exe 时回退 LibGit2Sharp（目标 commit 树对工作区）。
    /// 3. 额外把未跟踪文件补成 Added，避免本地新建文件被漏掉。
    /// 4. 使用 `-z` NUL 分隔，避免特殊文件名和转义解析出错。
    /// </summary>
    public IReadOnlyList<FileChange> GetTreeChanges(string currentBranchName, string targetBranchName)
    {
        Commit? targetCommit;
        lock (_repoLock)
        {
            targetCommit = GetBranchCommitInternal(targetBranchName);
        }
        if (targetCommit == null)
            throw new InvalidOperationException($"无法获取分支 {targetBranchName} 对应的 Commit");

        // 优先 git CLI：结果权威，与命令行一致
        var gitPath = FindGitExecutable();
        if (gitPath != null)
        {
            var viaCli = GetWorkingTreeChangesViaGitCli(gitPath, targetBranchName, targetCommit.Sha);
            if (viaCli != null)
            {
                AppLog.Info("差异计算（git CLI）: 本地工作区 vs {Tgt} = {Count} 个变更",
                    targetCommit.Sha.Substring(0, 7), viaCli.Count);
                return viaCli;
            }
            AppLog.Warn("git CLI diff 失败，回退 LibGit2Sharp");
        }

        // 回退 LibGit2Sharp：目标 commit 树对比工作区
        List<FileChange> list;
        List<string> untrackedPaths;
        lock (_repoLock)
        {
            var treeChanges = _repo.Diff.Compare<TreeChanges>(targetCommit.Tree, DiffTargets.WorkingDirectory);
            list = new List<FileChange>();
            foreach (var c in treeChanges.Where(c => c.Status != ChangeKind.Unmodified))
            {
                list.Add(new FileChange(c.Path, c.OldPath, c.Status));
            }

            var status = _repo.RetrieveStatus();
            untrackedPaths = new List<string>();
            foreach (var entry in status.Untracked)
            {
                untrackedPaths.Add(entry.FilePath);
            }
        }

        list = NormalizeWorkingTreeChanges(list, targetBranchName);

        // 补上未跟踪文件，避免本地新建文件漏显示
        foreach (var path in untrackedPaths)
        {
            if (WorkingFileMatchesTarget(targetBranchName, path))
            {
                continue;
            }

            if (!list.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(CreateUntrackedChange(targetBranchName, path));
            }
        }

        AppLog.Info("差异计算（LibGit2Sharp 兜底）: 本地工作区 vs {Target} = {Count} 个变更",
            targetCommit.Sha.Substring(0, 7), list.Count);
        return list;
    }

    /// <summary>
    /// 通过 `git diff --name-status &lt;targetSha&gt;` 获取工作区相对目标 commit 的变更列表。
    /// --name-status 输出格式（tab 分隔）：
    ///   M\tpath/to/file
    ///   A\tpath/to/new
    ///   D\tpath/to/deleted
    ///   R100\told/path\tnew/path
    ///   C100\told/path\tnew/path
    /// 返回 null 表示执行失败，调用方应回退。
    /// </summary>
    private IReadOnlyList<FileChange>? GetWorkingTreeChangesViaGitCli(string gitPath, string targetBranchName, string targetSha)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                // --name-status: 显示状态字母 + 路径
                // 不加 --ignore-cr-at-eol 等：让 git 按 .gitattributes/autocrlf 原生判断，
                // 与用户在命令行/IDE 看到的差异完全一致
                Arguments = $"diff --name-status -z -M -C --diff-filter=ACMRTD {targetSha}",
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (!proc.HasExited) { try { proc.Kill(); } catch { } return null; }
            if (proc.ExitCode != 0)
            {
                AppLog.Warn("git diff 退出码 {Code}", proc.ExitCode);
                return null;
            }

            var result = ParseNameStatusOutput(stdout);

            // 补充未跟踪文件
            var untracked = RunGitCommand(gitPath, "ls-files --others --exclude-standard -z");
            if (untracked != null)
            {
                foreach (var path in SplitNullSeparated(untracked))
                {
                    if (WorkingFileMatchesTarget(targetBranchName, path))
                    {
                        continue;
                    }

                    if (!result.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(CreateUntrackedChange(targetBranchName, path));
                    }
                }
            }

            // 兜底扫描本地磁盘：有些本地文件可能因为目录状态、忽略规则或 git 状态差异没有进到上面的结果里，
            // 但只要它真实存在且相对目标分支需要同步，就应该显示出来。
            AddFilesystemOnlyChanges(result, targetBranchName);
            return NormalizeWorkingTreeChanges(result, targetBranchName);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "git diff CLI 执行失败");
            return null;
        }
    }

    private string? RunGitCommand(string gitPath, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = arguments,
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (!proc.HasExited) { try { proc.Kill(); } catch { } return null; }
            if (proc.ExitCode != 0) return null;

            return stdout;
        }
        catch
        {
            return null;
        }
    }

    private static ChangeKind ParseChangeKind(char c) => c switch
    {
        'A' => ChangeKind.Added,
        'D' => ChangeKind.Deleted,
        'R' => ChangeKind.Renamed,
        'C' => ChangeKind.Copied,
        'T' => ChangeKind.TypeChanged,
        'M' => ChangeKind.Modified,
        _ => ChangeKind.Modified
    };

    /// <summary>
    /// 将工作区结果规范化为更适合 UI 展示的状态：
    /// 本地实际存在但内容与目标分支不同的文件，优先标成修改而不是删除。
    /// </summary>
    private List<FileChange> NormalizeWorkingTreeChanges(IEnumerable<FileChange> rawChanges, string targetBranchName)
    {
            var normalized = new List<FileChange>();
            foreach (var change in rawChanges)
            {
                if (change.Status is ChangeKind.Renamed or ChangeKind.Copied or ChangeKind.TypeChanged)
                {
                    normalized.Add(change);
                    continue;
                }

                var fullPath = Path.Combine(_repoPath, change.Path);
                var localExists = File.Exists(fullPath);
                var targetBlob = GetFileBlobInternal(targetBranchName, change.Path);

            if (localExists && targetBlob != null)
            {
                if (WorkingFileMatchesTarget(targetBranchName, change.Path))
                {
                    continue;
                }

                normalized.Add(new FileChange(change.Path, change.OldPath, ChangeKind.Modified));
                continue;
            }

            if (localExists && targetBlob == null)
            {
                normalized.Add(new FileChange(change.Path, change.OldPath, ChangeKind.Deleted));
                continue;
            }

            if (!localExists && targetBlob != null)
            {
                normalized.Add(new FileChange(change.Path, change.OldPath, ChangeKind.Added));
            }
        }

        return normalized;
    }

    private FileChange CreateUntrackedChange(string targetBranchName, string path)
    {
        var targetBlob = GetFileBlobInternal(targetBranchName, path);
        if (targetBlob == null)
        {
            return new FileChange(path, null, ChangeKind.Deleted);
        }

        return WorkingFileMatchesTarget(targetBranchName, path)
            ? new FileChange(path, null, ChangeKind.Added)
            : new FileChange(path, null, ChangeKind.Modified);
    }

    private void AddFilesystemOnlyChanges(List<FileChange> result, string targetBranchName)
    {
        var knownPaths = new HashSet<string>(result.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var change in result)
        {
            if (!string.IsNullOrWhiteSpace(change.OldPath))
            {
                knownPaths.Add(change.OldPath);
            }
        }

        // 优先用 `git ls-files` 拿到仓库内文件列表（避免 Directory.EnumerateFiles 扫到 .git/ 或 node_modules/ 等大目录）
        // gitPath 为空（无 git.exe）时回退到本地磁盘扫描
        var gitPath = FindGitExecutable();
        var enumerated = gitPath != null
            ? TryEnumerateTrackedAndUntrackedViaGit(gitPath)
            : null;

        if (enumerated == null)
        {
            // 无 git.exe：退化成本地扫描，跳过 .git/ 即可
            foreach (var relativePath in EnumerateLocalFiles())
            {
                if (knownPaths.Contains(relativePath)) continue;
                if (IsIgnoredLocalPath(relativePath)) continue;
                if (WorkingFileMatchesTarget(targetBranchName, relativePath)) continue;

                var targetBlob = GetFileBlobInternal(targetBranchName, relativePath);
                var kind = targetBlob == null ? ChangeKind.Deleted : ChangeKind.Modified;
                result.Add(new FileChange(relativePath, null, kind));
                knownPaths.Add(relativePath);
            }
            return;
        }

        foreach (var relativePath in enumerated)
        {
            if (knownPaths.Contains(relativePath)) continue;

            // 仍走 .gitignore 检查，因为 ls-files --others --exclude-standard 理论已过滤，
            // 但 ls-files（tracked）不过滤忽略规则，需要再过一遍
            if (IsIgnoredLocalPath(relativePath)) continue;

            if (WorkingFileMatchesTarget(targetBranchName, relativePath)) continue;

            var targetBlob = GetFileBlobInternal(targetBranchName, relativePath);
            var kind = targetBlob == null ? ChangeKind.Deleted : ChangeKind.Modified;
            result.Add(new FileChange(relativePath, null, kind));
            knownPaths.Add(relativePath);
        }
    }

    /// <summary>
    /// 一次 git 调用同时拿到 tracked + untracked 文件列表。
    /// git 输出的 NUL 结尾，tracked 用 `ls-files -z`，untracked 用 `ls-files --others --exclude-standard -z`。
    /// 大仓库下比 Directory.EnumerateFiles 快一个数量级。
    /// </summary>
    private List<string>? TryEnumerateTrackedAndUntrackedViaGit(string gitPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = "ls-files -z --others --exclude-standard",
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(15000);
            if (!proc.HasExited) { try { proc.Kill(); } catch { } return null; }
            if (proc.ExitCode != 0) return null;

            var result = new List<string>();
            foreach (var path in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = path.Replace('\\', '/');
                if (ShouldSkipLocalPath(normalized)) continue;
                result.Add(normalized);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateLocalFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_repoPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(_repoPath, file);
            if (ShouldSkipLocalPath(relativePath))
            {
                continue;
            }

            yield return relativePath.Replace('\\', '/');
        }
    }

    private bool IsIgnoredLocalPath(string relativePath)
    {
        lock (_repoLock)
        {
            return _repo.Ignore.IsPathIgnored(relativePath);
        }
    }

    private static bool ShouldSkipLocalPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, ".git", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    private static List<FileChange> ParseNameStatusOutput(string output)
    {
        var tokens = SplitNullSeparated(output);
        var result = new List<FileChange>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            var statusChar = token[0];
            var kind = ParseChangeKind(statusChar);

            string path;
            string? oldPath = null;

            if (kind is ChangeKind.Renamed or ChangeKind.Copied)
            {
                if (i + 2 >= tokens.Count)
                {
                    break;
                }

                oldPath = tokens[++i];
                path = tokens[++i];
            }
            else
            {
                if (i + 1 >= tokens.Count)
                {
                    break;
                }

                path = tokens[++i];
            }

            result.Add(new FileChange(path, oldPath, kind));
        }

        return result;
    }

    private static List<string> SplitNullSeparated(string text)
    {
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
