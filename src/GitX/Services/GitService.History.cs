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
    /// 优先 git CLI（git show --name-status -z），失败回退 LibGit2Sharp。
    /// CLI 路径在历史 commit 涉及大量文件时显著快于 LibGit2Sharp.Diff.Compare。
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

            // 文件列表：CLI 优先，失败回退 LibGit2Sharp
            var files = GetCommitFilesPreferCli(sha);
            if (files == null)
            {
                files = new List<CommitFileChangeItem>();
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
                    files = files
                        .DistinctBy(x => $"{x.Status}:{x.OldPath}:{x.Path}")
                        .ToList();
                }
            }

            return new CommitDetailResult
            {
                Commit = new CommitHistoryItem
                {
                    Sha = commit.Sha,
                    Author = commit.Author.Name,
                    AuthorEmail = commit.Author.Email ?? string.Empty,
                    CommitTime = commit.Committer.When,
                    Message = commit.Message?.TrimEnd() ?? string.Empty,
                    ParentShas = commit.Parents.Select(p => p.Sha).ToArray()
                },
                Files = files
            };
        }
    }

    /// <summary>
    /// 用 git show --name-status -z &lt;sha&gt; 快速获取文件列表。
    /// 失败返回 null（调用方应回退到 LibGit2Sharp）。
    /// </summary>
    private List<CommitFileChangeItem>? GetCommitFilesPreferCli(string sha)
    {
        try
        {
            var gitPath = FindGitExecutable();
            if (gitPath == null) return null;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = $"show --name-status -z --no-renames --format= {sha}",
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            if (!proc.HasExited) { try { proc.Kill(); } catch { } return null; }
            if (proc.ExitCode != 0) return null;

            // 解析 NUL 分隔的 status+path 列表
            // 格式：M\0path\0A\0newpath\0D\0path\0R<score>\0oldpath\0newpath\0
            // --no-renames 后没有 R 行，避免 old/new 配对复杂度
            var tokens = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<CommitFileChangeItem>(tokens.Length / 2 + 1);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (string.IsNullOrEmpty(token) || token.Length < 1) continue;

                // R<score>/C<score> 是 2 段，旧路径+新路径
                var head = token[0];
                if (head == 'R' || head == 'C')
                {
                    if (i + 2 >= tokens.Length) break;
                    var oldPath = tokens[++i];
                    var newPath = tokens[++i];
                    result.Add(new CommitFileChangeItem
                    {
                        Path = newPath,
                        OldPath = oldPath,
                        Status = head == 'R' ? ChangeKind.Renamed : ChangeKind.Copied
                    });
                }
                else
                {
                    if (i + 1 >= tokens.Length) break;
                    var path = tokens[++i];
                    var kind = head switch
                    {
                        'A' => ChangeKind.Added,
                        'D' => ChangeKind.Deleted,
                        'M' => ChangeKind.Modified,
                        'T' => ChangeKind.TypeChanged,
                        _ => ChangeKind.Modified
                    };
                    result.Add(new CommitFileChangeItem
                    {
                        Path = path,
                        Status = kind
                    });
                }
            }
            return result;
        }
        catch
        {
            return null;
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
            psi.ArgumentList.Add("--pretty=format:%H%x1f%an%x1f%ae%x1f%ad%x1f%P%x1f%s%x1e");
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
            var authorEmail = fields.Length > 2 ? fields[2].Trim() : string.Empty;
            var message = fields.Length > 5 ? fields[5].Trim() : (fields.Length > 3 ? fields[3].Trim() : string.Empty);
            var parents = fields.Length > 4
                ? fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();
            if (!DateTimeOffset.TryParse(fields.Length > 3 ? fields[3] : string.Empty, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                when = DateTimeOffset.MinValue;
            }

            result.Add(new CommitHistoryItem
            {
                Sha = sha,
                Author = author,
                AuthorEmail = authorEmail,
                CommitTime = when,
                Message = message,
                ParentShas = parents
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
            AuthorEmail = commit.Author.Email ?? string.Empty,
            CommitTime = commit.Committer.When,
            Message = commit.MessageShort ?? string.Empty,
            ParentShas = commit.Parents.Select(p => p.Sha).ToArray()
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
