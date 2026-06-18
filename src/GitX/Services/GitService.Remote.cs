using GitX.Core.Logging;
using LibGit2Sharp;
using System.Diagnostics;
using System.IO;

namespace GitX.Services;

/// <summary>
/// Git 远端同步能力：fetch、凭据获取和 git.exe 查找。
/// </summary>
public partial class GitService
{
    private readonly Dictionary<string, Credentials?> _credCache = new();
    private readonly object _credLock = new();

    /// <summary>
    /// 同步远程分支（Fetch + Prune）。
    /// 直接调用系统 git.exe fetch，完整复用 SSH key / Git Credential Manager / credential.helper
    /// 等系统级认证机制，避免 LibGit2Sharp 的 "remote authentication required but no callback set"。
    /// LibGit2Sharp 读取 Branches/refs 是实时的，git fetch 完即可看到新引用。
    /// 用带超时的 Task 包裹，慢网络下回退到本地缓存。
    /// </summary>
    public void FetchRemote(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(60);
        try
        {
            var gitPath = FindGitExecutable();
            if (gitPath == null)
            {
                // 没装 git.exe，回退到 LibGit2Sharp（仅对公开仓库或已缓存凭据的仓库有效）
                AppLog.Warn("未找到 git.exe，回退 LibGit2Sharp Fetch（可能因无凭据失败）");
                FetchViaLibGit2(timeout.Value);
                return;
            }

            bool completed = Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = gitPath,
                    Arguments = "fetch --all --prune",
                    WorkingDirectory = _repoPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                var stderr = proc.StandardError.ReadToEnd();
                proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                {
                    AppLog.Warn("git fetch 退出码 {Code}: {Err}", proc.ExitCode, stderr.Trim());
                }
            }).Wait(timeout.Value);

            if (!completed)
            {
                AppLog.Warn("Fetch 超时（{Sec}s），使用本地缓存", timeout.Value.TotalSeconds);
                return;
            }
            AppLog.Info("远程分支同步完成（git fetch --all --prune）");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Fetch 远程分支失败，使用本地缓存");
        }
    }

    /// <summary>
    /// LibGit2Sharp 兜底 Fetch：当系统未安装 git.exe 时使用。
    /// 通过 git credential fill 尝试获取 HTTPS 凭据；SSH 仓库通常无法处理。
    /// </summary>
    private void FetchViaLibGit2(TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(() =>
            {
                lock (_repoLock)
                {
                    foreach (var remote in _repo.Network.Remotes)
                    {
                        var refSpecs = remote.FetchRefSpecs.Select(rs => rs.Specification).ToList();
                        var options = new FetchOptions
                        {
                            CredentialsProvider = (url, userFromUrl, types) => QueryCredential(url)
                        };
                        Commands.Fetch(_repo, remote.Name, refSpecs, options, null);
                    }
                }
            });
            task.Wait(timeout);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "LibGit2Sharp Fetch 失败");
        }
    }

    /// <summary>
    /// 通过 `git credential fill` 从系统 Git Credential Manager 获取 HTTPS 凭据。
    /// 仅当安装了 git.exe 时有效；按 url 缓存，避免重复弹 GCM 窗口。
    /// </summary>
    private Credentials? QueryCredential(string url)
    {
        lock (_credLock)
        {
            if (_credCache.TryGetValue(url, out var cached)) return cached;
        }

        Credentials? result = null;
        try
        {
            var gitPath = FindGitExecutable();
            if (gitPath == null) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = gitPath,
                Arguments = "credential fill",
                WorkingDirectory = _repoPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            // GCM 协议：输入 protocol + host，输出含 username/password
            proc.StandardInput.Write($"protocol={uri.Scheme}\n");
            proc.StandardInput.Write($"host={uri.Host}\n");
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var user = uri.UserInfo.Split(':')[0];
                if (!string.IsNullOrEmpty(user))
                    proc.StandardInput.Write($"username={user}\n");
            }
            proc.StandardInput.Write("\n");
            proc.StandardInput.Flush();

            string? username = null, password = null;
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = proc.StandardOutput.ReadLine();
                if (line == null || line.Length == 0) break;
                if (line.StartsWith("username=")) username = line.Substring(9);
                else if (line.StartsWith("password=")) password = line.Substring(9);
            }
            proc.WaitForExit(8000);

            if (!string.IsNullOrEmpty(password))
            {
                result = new UsernamePasswordCredentials
                {
                    Username = string.IsNullOrEmpty(username) ? "git" : username,
                    Password = password
                };
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "git credential fill 失败: {Url}", url);
        }

        lock (_credLock)
        {
            _credCache[url] = result;
        }
        return result;
    }

    /// <summary>
    /// 查找系统 git.exe（优先 Program Files，其次 PATH）。TortoiseGit 用户必然装了 Git for Windows。
    /// </summary>
    private static string? FindGitExecutable()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files\Git\bin\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe",
            @"C:\Program Files (x86)\Git\bin\git.exe"
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), "git.exe");
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        return null;
    }
}
