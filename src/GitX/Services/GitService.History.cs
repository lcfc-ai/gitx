using GitX.Core.Logging;
using GitX.Core.Models;
using LibGit2Sharp;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GitX.Services;

public partial class GitService
{
    /// <summary>
    /// 获取目标分支上某个路径的最近提交记录。
    /// 文件路径会尽量跟随重命名；文件夹路径则直接按路径前缀统计相关提交。
    /// </summary>
    public IReadOnlyList<CommitHistoryItem> GetPathHistory(
        string branchName,
        string path,
        bool isFile,
        int maxCount = 20,
        string? authorFilter = null,
        int? recentDays = null)
    {
        if (string.IsNullOrWhiteSpace(path) || maxCount <= 0)
        {
            return Array.Empty<CommitHistoryItem>();
        }

        lock (_repoLock)
        {
            var normalizedPath = NormalizeHistoryPath(path);
            var gitPath = FindGitExecutable();
            if (gitPath != null)
            {
                var viaCli = GetPathHistoryViaGitCli(gitPath, branchName, normalizedPath, isFile, maxCount, authorFilter, recentDays);
                if (viaCli != null)
                {
                    return viaCli;
                }
            }

            return GetPathHistoryViaLibGit2(branchName, normalizedPath, isFile, maxCount, authorFilter, recentDays);
        }
    }

    /// <summary>
    /// 获取某次提交的详细变更。
    /// </summary>
    public CommitDetailResult? GetCommitDetail(string sha)
    {
        sha = sha.Trim();
        if (string.IsNullOrWhiteSpace(sha))
        {
            return null;
        }

        lock (_repoLock)
        {
            var commit = _repo.Lookup<Commit>(sha);
            if (commit == null)
            {
                return null;
            }

            var files = new List<CommitFileChangeItem>();
            if (!commit.Parents.Any())
            {
                foreach (var entry in EnumerateTreeEntries(commit.Tree))
                {
                    if (entry.TargetType == TreeEntryTargetType.Blob)
                    {
                        files.Add(new CommitFileChangeItem
                        {
                            Path = entry.Path,
                            Status = ChangeKind.Added
                        });
                    }
                }
            }
            else
            {
                foreach (var parent in commit.Parents)
                {
                    var changes = _repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);
                    foreach (var change in changes)
                    {
                        files.Add(new CommitFileChangeItem
                        {
                            Path = change.Path,
                            OldPath = change.OldPath,
                            Status = change.Status
                        });
                    }
                }
            }

            return new CommitDetailResult
            {
                Commit = new CommitHistoryItem
                {
                    Sha = commit.Sha,
                    Author = commit.Author.Name,
                    CommitTime = commit.Committer.When,
                    Message = commit.Message?.TrimEnd() ?? string.Empty
                },
                Files = files
                    .DistinctBy(x => $"{x.Status}:{x.OldPath}:{x.Path}")
                    .ToList()
            };
        }
    }

    private IReadOnlyList<CommitHistoryItem>? GetPathHistoryViaGitCli(
        string gitPath,
        string branchName,
        string path,
        bool isFile,
        int maxCount,
        string? authorFilter,
        int? recentDays)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            psi.ArgumentList.Add("log");
            psi.ArgumentList.Add(branchName);
            psi.ArgumentList.Add($"--max-count={maxCount}");
            psi.ArgumentList.Add("--date=iso-strict");
            psi.ArgumentList.Add("--pretty=format:%H%x1f%an%x1f%ad%x1f%s%x1e");
            if (recentDays is > 0)
            {
                psi.ArgumentList.Add($"--since={recentDays.Value}.days");
            }
            if (!string.IsNullOrWhiteSpace(authorFilter))
            {
                psi.ArgumentList.Add($"--author={authorFilter}");
            }
            if (isFile)
            {
                psi.ArgumentList.Add("--follow");
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(isFile ? path : path.TrimEnd('/') + "/");

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (!proc.HasExited)
            {
                try { proc.Kill(); } catch { }
                return null;
            }

            if (proc.ExitCode != 0)
            {
                AppLog.Warn("git log 退出码 {Code}: {Err}", proc.ExitCode, stderr.Trim());
                return null;
            }

            return ParseHistoryOutput(stdout);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "git log CLI 执行失败: {Path}", path);
            return null;
        }
    }

    private IReadOnlyList<CommitHistoryItem> GetPathHistoryViaLibGit2(
        string branchName,
        string path,
        bool isFile,
        int maxCount,
        string? authorFilter,
        int? recentDays)
    {
        var branchCommit = GetBranchCommitInternal(branchName);
        if (branchCommit == null)
        {
            return Array.Empty<CommitHistoryItem>();
        }

        var result = new List<CommitHistoryItem>();
        var filter = new CommitFilter
        {
            IncludeReachableFrom = branchCommit,
            SortBy = CommitSortStrategies.Time
        };

        foreach (var commit in _repo.Commits.QueryBy(filter))
        {
            if (!CommitTouchesPath(commit, path, isFile))
            {
                continue;
            }

            if (recentDays is > 0)
            {
                var cutoff = DateTimeOffset.Now.AddDays(-recentDays.Value);
                if (commit.Committer.When < cutoff)
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(authorFilter) &&
                commit.Author.Name.IndexOf(authorFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            result.Add(ToHistoryItem(commit));
            if (result.Count >= maxCount)
            {
                break;
            }
        }

        return result;
    }

    private static IReadOnlyList<CommitHistoryItem> ParseHistoryOutput(string stdout)
    {
        var result = new List<CommitHistoryItem>();
        foreach (var record in stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split('\x1f');
            if (fields.Length < 4)
            {
                continue;
            }

            var sha = fields[0].Trim();
            var author = fields[1].Trim();
            var message = fields[3].Trim();
            if (!DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                when = DateTimeOffset.MinValue;
            }

            result.Add(new CommitHistoryItem
            {
                Sha = sha,
                Author = author,
                CommitTime = when,
                Message = message
            });
        }

        return result;
    }

    private static CommitHistoryItem ToHistoryItem(Commit commit)
    {
        return new CommitHistoryItem
        {
            Sha = commit.Sha,
            Author = commit.Author.Name,
            CommitTime = commit.Committer.When,
            Message = commit.MessageShort ?? string.Empty
        };
    }

    private static IEnumerable<TreeEntry> EnumerateTreeEntries(Tree tree)
    {
        foreach (var entry in tree)
        {
            yield return entry;
            if (entry.TargetType == TreeEntryTargetType.Tree && entry.Target is Tree subtree)
            {
                foreach (var child in EnumerateTreeEntries(subtree))
                {
                    yield return child;
                }
            }
        }
    }

    private bool CommitTouchesPath(Commit commit, string path, bool isFile)
    {
        if (commit.Parents == null || !commit.Parents.Any())
        {
            return TreeContainsPath(commit.Tree, path, isFile);
        }

        foreach (var parent in commit.Parents)
        {
            var changes = _repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);
            foreach (var change in changes)
            {
                if (PathMatches(change.Path, path, isFile) ||
                    (!string.IsNullOrWhiteSpace(change.OldPath) && PathMatches(change.OldPath, path, isFile)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TreeContainsPath(Tree tree, string path, bool isFile)
    {
        var normalized = NormalizeHistoryPath(path);
        if (isFile)
        {
            return tree[normalized] != null;
        }

        var prefix = normalized + "/";
        return tree.Any(entry =>
            entry.TargetType == TreeEntryTargetType.Blob &&
            PathMatches(entry.Path, normalized, false));
    }

    private static bool PathMatches(string candidate, string path, bool isFile)
    {
        var normalizedCandidate = NormalizeHistoryPath(candidate);
        var normalizedPath = NormalizeHistoryPath(path);

        if (isFile)
        {
            return string.Equals(normalizedCandidate, normalizedPath, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(normalizedCandidate, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHistoryPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
    }
}
