using GitX.Core.Logging;
using GitX.Core.Models;
using System.IO;
using System.Text;

namespace GitX.Services;

/// <summary>
/// 文件操作服务，实现覆盖本地核心能力
/// </summary>
public class FileService
{
    private readonly GitService _gitService;
    private readonly string _repoPath;
    private readonly CacheLogService _cacheLog;

    public FileService(GitService gitService, string repoPath, CacheLogService cacheLog)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _cacheLog = cacheLog;
    }

    /// <summary>
    /// 单文件覆盖：将目标分支的文件内容写入本地工作区。
    /// 文本文件用 UTF-8(无 BOM) 写入；二进制文件通过 Blob 流式复制字节，避免编码损坏。
    /// 目标分支中不存在该文件时（删除场景）删除本地文件。
    /// </summary>
    public (bool success, string? error) OverwriteFile(string filePath, string targetBranch, string currentBranch)
    {
        try
        {
            var blob = _gitService.GetFileBlob(targetBranch, filePath);
            var fullPath = Path.Combine(_repoPath, filePath);

            // 确保目录存在
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (blob == null)
            {
                // 目标分支里确实没有该文件，才视为删除场景
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                RemoveEmptyParentDirectories(fullPath);
            }
            else if (blob.IsBinary)
            {
                // 二进制文件：通过 Blob 流式写入字节，绝不能用 WriteAllText（会写成空文本损坏文件）
                using var src = blob.GetContentStream();
                using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                src.CopyTo(fs);
            }
            else
            {
                // 文本文件：即使内容为空，也要写成空文件，而不是误判为删除
                var content = blob.GetContentText() ?? string.Empty;
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            }

            _cacheLog.LogFileOverwrite(_repoPath, currentBranch, targetBranch, filePath, true);
            AppLog.Info("文件覆盖成功: {File}", filePath);
            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            AppLog.Warn("文件权限不足: {File}", filePath);
            return (false, "文件权限不足，跳过覆盖");
        }
        catch (IOException ex)
        {
            AppLog.Warn(ex, "文件 IO 异常: {File}", filePath);
            return (false, $"文件被占用或 IO 异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "文件覆盖失败: {File}", filePath);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 从指定文件路径向上清理空目录，直到仓库根目录为止。
    /// </summary>
    private void RemoveEmptyParentDirectories(string filePath)
    {
        var currentDir = Path.GetDirectoryName(filePath);
        var repoRoot = Path.GetFullPath(_repoPath);

        while (!string.IsNullOrWhiteSpace(currentDir))
        {
            var fullDir = Path.GetFullPath(currentDir);
            if (!fullDir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.Equals(fullDir, repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!Directory.Exists(fullDir))
            {
                currentDir = Path.GetDirectoryName(fullDir);
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(fullDir).Any())
            {
                break;
            }

            Directory.Delete(fullDir);
            currentDir = Path.GetDirectoryName(fullDir);
        }
    }

    /// <summary>
    /// 批量覆盖文件夹下所有变更文件
    /// </summary>
    public (int successCount, int failCount, List<string> errors) OverwriteFolder(DiffTreeModel folderNode, string targetBranch, string currentBranch)
    {
        int success = 0, fail = 0;
        var errors = new List<string>();

        foreach (var fileNode in EnumerateFileNodes(folderNode))
        {
            var (ok, err) = OverwriteFile(fileNode.FullPath, targetBranch, currentBranch);
            if (ok)
                success++;
            else
            {
                fail++;
                errors.Add($"{fileNode.FullPath}: {err}");
            }
        }

        return (success, fail, errors);
    }

    /// <summary>
    /// 获取所有差异文件路径
    /// </summary>
    public List<string> GetAllFilePaths(DiffTreeModel root)
    {
        return EnumerateFileNodes(root).Select(n => n.FullPath).ToList();
    }

    private IEnumerable<DiffTreeModel> EnumerateFileNodes(DiffTreeModel node)
    {
        if (node.IsFile)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var file in EnumerateFileNodes(child))
            {
                yield return file;
            }
        }
    }
}
