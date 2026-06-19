using GitX.Core.Logging;
using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;

namespace GitX.Services;

/// <summary>
/// 本地日志 & 缓存服务。
/// SQLite 连接不是线程安全的，本服务会被 UI 线程与覆盖任务线程并发访问，
/// 故所有数据库操作通过 _dbLock 串行化。内存 Blob 缓存同样加锁并设置 LRU 上限，
/// 避免大仓库持续吃内存。
/// </summary>
public class CacheLogService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _dbLock = new();

    // LRU 缓存：LinkedList 记录访问顺序，Dictionary 存内容
    private readonly LinkedList<string> _lruKeys = new();
    private readonly Dictionary<string, string> _blobCache = new();
    private readonly object _cacheLock = new();
    private const int MaxCacheEntries = 256;

    private readonly string _dbPath;

    public CacheLogService()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitX");
        Directory.CreateDirectory(appData);
        _dbPath = Path.Combine(appData, "log.db");

        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();
        InitializeTables();
    }

    private void InitializeTables()
    {
        lock (_dbLock)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS BranchCompareLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RepoPath TEXT NOT NULL,
                    CurrentBranch TEXT NOT NULL,
                    TargetBranch TEXT NOT NULL,
                    CompareTime TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS FileOverWriteLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RepoPath TEXT NOT NULL,
                    CurrentBranch TEXT NOT NULL,
                    TargetBranch TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    IsSuccess INTEGER NOT NULL,
                    OverWriteTime TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 记录分支对比
    /// </summary>
    public void LogBranchCompare(string repoPath, string currentBranch, string targetBranch)
    {
        try
        {
            lock (_dbLock)
            {
                var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO BranchCompareLog (RepoPath, CurrentBranch, TargetBranch, CompareTime) VALUES (@repo, @cur, @tgt, @time)";
                cmd.Parameters.AddWithValue("@repo", repoPath);
                cmd.Parameters.AddWithValue("@cur", currentBranch);
                cmd.Parameters.AddWithValue("@tgt", targetBranch);
                cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "记录分支对比日志失败");
        }
    }

    /// <summary>
    /// 记录文件覆盖
    /// </summary>
    public void LogFileOverwrite(string repoPath, string currentBranch, string targetBranch, string filePath, bool isSuccess)
    {
        try
        {
            lock (_dbLock)
            {
                var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO FileOverWriteLog (RepoPath, CurrentBranch, TargetBranch, FilePath, IsSuccess, OverWriteTime) VALUES (@repo, @cur, @tgt, @file, @ok, @time)";
                cmd.Parameters.AddWithValue("@repo", repoPath);
                cmd.Parameters.AddWithValue("@cur", currentBranch);
                cmd.Parameters.AddWithValue("@tgt", targetBranch);
                cmd.Parameters.AddWithValue("@file", filePath);
                cmd.Parameters.AddWithValue("@ok", isSuccess ? 1 : 0);
                cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "记录文件覆盖日志失败");
        }
    }

    /// <summary>
    /// 缓存 Blob 内容（LRU，超出上限淘汰最久未使用项）
    /// </summary>
    public void CacheBlob(string key, string content)
    {
        lock (_cacheLock)
        {
            if (_blobCache.ContainsKey(key))
            {
                _lruKeys.Remove(key);
            }
            else if (_blobCache.Count >= MaxCacheEntries && _lruKeys.First is { } oldestNode)
            {
                var oldest = oldestNode.Value;
                _lruKeys.RemoveFirst();
                _blobCache.Remove(oldest);
            }
            _blobCache[key] = content;
            _lruKeys.AddLast(key);
        }
    }

    public string? GetCachedBlob(string key)
    {
        lock (_cacheLock)
        {
            if (!_blobCache.TryGetValue(key, out var content)) return null;
            // 命中后移到末尾，标记为最近使用
            _lruKeys.Remove(key);
            _lruKeys.AddLast(key);
            return content;
        }
    }

    /// <summary>
    /// 删除单条 Blob 缓存。
    /// </summary>
    public void RemoveCachedBlob(string key)
    {
        lock (_cacheLock)
        {
            if (_blobCache.Remove(key))
            {
                _lruKeys.Remove(key);
            }
        }
    }

    /// <summary>
    /// 清空工作区内容缓存，避免覆盖后继续读到旧内容。
    /// </summary>
    public void ClearWorkingTreeBlobCache()
    {
        lock (_cacheLock)
        {
            var keys = _blobCache.Keys.Where(k => k.StartsWith("worktree:", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys)
            {
                _blobCache.Remove(key);
                _lruKeys.Remove(key);
            }
        }
    }

    /// <summary>
    /// 清空全部 Blob 缓存：用于 fetch 远端后，远端 refs 已变化，旧的 Blob 内容可能 stale。
    /// </summary>
    public void ClearBlobCache()
    {
        lock (_cacheLock)
        {
            _blobCache.Clear();
            _lruKeys.Clear();
        }
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            _blobCache.Clear();
            _lruKeys.Clear();
        }
        lock (_dbLock)
        {
            _db?.Close();
            _db?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
