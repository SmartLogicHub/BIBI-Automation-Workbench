using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Infrastructure.EF;

namespace Ray.BiliBiliTool.Web.Services;

public interface IDataMaintenanceService
{
    Task<DataMaintenanceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<DataMaintenanceActionResult> CleanExpiredExecutionLogsAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> CleanExpiredSystemLogsAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> CleanExpiredLogFilesAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> CreateBackupAsync(
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> ExportBusinessDataAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<DataMaintenanceFile>> GetGeneratedFilesAsync(
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> DeleteGeneratedFileAsync(
        string id,
        CancellationToken cancellationToken = default
    );

    Task<DataMaintenanceActionResult> ClearGeneratedFilesAsync(
        CancellationToken cancellationToken = default
    );

    Task<GeneratedDataFileDownload?> OpenGeneratedFileAsync(
        string id,
        CancellationToken cancellationToken = default
    );
}

public sealed class DataMaintenanceService(
    IDbContextFactory<BiliDbContext> dbContextFactory,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IOptionsMonitor<ProductCommentTaskOptions> productCommentOptions,
    IOptionsMonitor<LocalWorkbenchOptions> localWorkbenchOptions
) : IDataMaintenanceService
{
    public async Task<DataMaintenanceSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var databasePath = GetDatabasePath();
        var logFiles = GetLogFiles().ToList();
        var contentData = GetContentAutomationDataSummary();
        var appSettingsCount = await CountTableAsync(
            dbContext,
            "bili_appsettings",
            cancellationToken
        );
        var quartzJobCount = await CountTableAsync(
            dbContext,
            "QRTZ_JOB_DETAILS",
            cancellationToken
        );

        return new DataMaintenanceSnapshot
        {
            GeneratedAt = DateTimeOffset.Now,
            Areas =
            [
                new DataMaintenanceArea
                {
                    Name = "本地数据库",
                    Description = "SQLite 主数据库，包含调度、日志、账号和配置表。",
                    ItemCount = await CountKnownDatabaseRowsAsync(dbContext, cancellationToken),
                    SizeBytes = GetFileSize(databasePath),
                    LastUpdated = GetLastWriteTime(databasePath),
                    Location = databasePath,
                    CanClean = false,
                    SafetyNote = "不能整体删除。只允许清理其中的日志和历史记录。",
                },
                new DataMaintenanceArea
                {
                    Name = "执行历史",
                    Description = "任务运行历史，用于查看每次调度是否成功。",
                    ItemCount = await dbContext.ExecutionLogs.CountAsync(cancellationToken),
                    SizeBytes = 0,
                    LastUpdated = await GetLatestExecutionLogTimeAsync(
                        dbContext,
                        cancellationToken
                    ),
                    Location = "SQLite 表：bili_execution_logs",
                    CanClean = true,
                    SafetyNote = "只清理过期历史，不影响任务配置和登录态。",
                },
                new DataMaintenanceArea
                {
                    Name = "系统日志",
                    Description = "Web 服务写入 SQLite 的运行日志。",
                    ItemCount = await dbContext.BiliLogs.CountAsync(cancellationToken),
                    SizeBytes = 0,
                    LastUpdated = await GetLatestSystemLogTimeAsync(dbContext, cancellationToken),
                    Location = "SQLite 表：bili_logs",
                    CanClean = true,
                    SafetyNote = "只清理过期日志，不删除业务数据。",
                },
                new DataMaintenanceArea
                {
                    Name = "文件日志",
                    Description = "Web 和控制台落盘的 .txt / .log 文件。",
                    ItemCount = logFiles.Count,
                    SizeBytes = logFiles.Sum(x => x.Length),
                    LastUpdated = logFiles.Count == 0 ? null : logFiles.Max(x => x.LastWriteTime),
                    Location = string.Join("；", GetLogDirectories()),
                    CanClean = true,
                    SafetyNote = "只清理过期日志文件，不删除数据库。",
                },
                new DataMaintenanceArea
                {
                    Name = "内容自动化数据",
                    Description = "候选池、评论模板和发布台账 JSON 数据。",
                    ItemCount = contentData.ItemCount,
                    SizeBytes = GetFileSize(contentData.Location),
                    LastUpdated = GetLastWriteTime(contentData.Location),
                    Location = contentData.Location,
                    CanClean = false,
                    SafetyNote = "这是业务数据，不提供一键清空，避免误删台账。",
                },
                new DataMaintenanceArea
                {
                    Name = "配置与账号",
                    Description = "本地配置、Web 用户和登录相关资料。",
                    ItemCount =
                        appSettingsCount + await dbContext.Users.CountAsync(cancellationToken),
                    SizeBytes = 0,
                    LastUpdated = null,
                    Location =
                        $"配置 {appSettingsCount} 条，Web 用户 {await dbContext.Users.CountAsync(cancellationToken)} 个",
                    CanClean = false,
                    SafetyNote = "受保护数据。不会被日志清理操作删除。",
                },
                new DataMaintenanceArea
                {
                    Name = "调度定义",
                    Description = "Quartz 任务和触发器定义。",
                    ItemCount = quartzJobCount,
                    SizeBytes = 0,
                    LastUpdated = null,
                    Location = "SQLite 表：QRTZ_*",
                    CanClean = false,
                    SafetyNote = "任务定义不属于缓存，不能在数据清理中删除。",
                },
            ],
        };
    }

    public async Task<DataMaintenanceActionResult> CleanExpiredExecutionLogsAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    )
    {
        var safeDays = Math.Max(1, daysToKeep);
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-(safeDays + 1));

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var expiredLogs = await dbContext
            .ExecutionLogs.Where(x => x.DateAddedUtc < cutoff)
            .ToListAsync(cancellationToken);

        dbContext.ExecutionLogs.RemoveRange(expiredLogs);
        var deleted = await dbContext.SaveChangesAsync(cancellationToken);

        return new DataMaintenanceActionResult(
            true,
            $"已清理 {deleted} 条 {safeDays} 天前的执行历史。",
            deleted
        );
    }

    public async Task<DataMaintenanceActionResult> CleanExpiredSystemLogsAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    )
    {
        var safeDays = Math.Max(1, daysToKeep);
        var cutoff = DateTime
            .UtcNow.AddDays(-safeDays)
            .ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await dbContext.Database.ExecuteSqlRawAsync(
            "DELETE FROM bili_logs WHERE timeStamp < {0}",
            [cutoff],
            cancellationToken
        );

        return new DataMaintenanceActionResult(
            true,
            $"已清理 {deleted} 条 {safeDays} 天前的系统日志。",
            deleted
        );
    }

    public Task<DataMaintenanceActionResult> CleanExpiredLogFilesAsync(
        int daysToKeep,
        CancellationToken cancellationToken = default
    )
    {
        var safeDays = Math.Max(1, daysToKeep);
        var cutoff = DateTime.Now.AddDays(-safeDays);
        var deleted = 0;
        long deletedBytes = 0;
        var failed = 0;

        foreach (var file in GetLogFiles().Where(x => x.LastWriteTime < cutoff))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                deletedBytes += file.Length;
                file.Delete();
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        var message =
            failed == 0
                ? $"已清理 {deleted} 个过期日志文件，释放 {FormatBytes(deletedBytes)}。"
                : $"已清理 {deleted} 个过期日志文件，{failed} 个文件被占用或无权限，稍后可重试。";

        return Task.FromResult(new DataMaintenanceActionResult(failed == 0, message, deleted));
    }

    public async Task<DataMaintenanceActionResult> CreateBackupAsync(
        CancellationToken cancellationToken = default
    )
    {
        var backupDirectory = Path.Combine(environment.ContentRootPath, "data", "backups");
        Directory.CreateDirectory(backupDirectory);
        var destination = Path.Combine(
            backupDirectory,
            $"BIBI-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        );
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["main.db"] = GetDatabasePath(),
            ["workflows.json"] = ResolveWorkbenchPath(
                localWorkbenchOptions.CurrentValue.WorkflowStorePath
            ),
            ["comment-strategy.json"] = ResolveWorkbenchPath(
                localWorkbenchOptions.CurrentValue.CommentStrategyStorePath
            ),
            ["accounts.json"] = Path.Combine(environment.ContentRootPath, "data", "cookies.json"),
            ["comment-service.db"] = Path.Combine(
                AppContext.BaseDirectory,
                "data",
                "comment-service",
                "data",
                "app.db"
            ),
        };

        await using var archiveStream = File.Create(destination);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);
        var added = 0;
        foreach (
            var pair in files.Where(pair =>
                !string.IsNullOrWhiteSpace(pair.Value) && File.Exists(pair.Value)
            )
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await using var source = new FileStream(
                pair.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            await source.CopyToAsync(target, cancellationToken);
            added++;
        }

        return new DataMaintenanceActionResult(
            added > 0,
            added > 0 ? "本地备份已完成。" : "当前没有可备份的数据。",
            added
        );
    }

    public async Task<DataMaintenanceActionResult> ExportBusinessDataAsync(
        CancellationToken cancellationToken = default
    )
    {
        var exportDirectory = Path.Combine(environment.ContentRootPath, "data", "exports");
        Directory.CreateDirectory(exportDirectory);
        var destination = Path.Combine(
            exportDirectory,
            $"BIBI-data-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        );
        var payload = new Dictionary<string, object?>
        {
            ["exportedAt"] = DateTimeOffset.Now,
            ["commentStrategy"] = ReadJsonValue(
                ResolveWorkbenchPath(localWorkbenchOptions.CurrentValue.CommentStrategyStorePath)
            ),
            ["workflows"] = ReadJsonValue(
                ResolveWorkbenchPath(localWorkbenchOptions.CurrentValue.WorkflowStorePath)
            ),
            ["commentLedger"] = await ReadCommentLedgerAsync(cancellationToken),
        };

        await using var stream = File.Create(destination);
        await JsonSerializer.SerializeAsync(
            stream,
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
            cancellationToken
        );
        return new DataMaintenanceActionResult(
            true,
            "运营数据已导出。",
            ((IReadOnlyList<Dictionary<string, object?>>)payload["commentLedger"]!).Count
        );
    }

    public Task<IReadOnlyList<DataMaintenanceFile>> GetGeneratedFilesAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<DataMaintenanceFile> files = GetGeneratedDataFileStore().List();
        return Task.FromResult(files);
    }

    public Task<DataMaintenanceActionResult> DeleteGeneratedFileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = GetGeneratedDataFileStore().Delete(id);
        return Task.FromResult(
            new DataMaintenanceActionResult(
                deleted,
                deleted ? "文件已删除。" : "文件不存在或不允许删除。",
                deleted ? 1 : 0
            )
        );
    }

    public Task<DataMaintenanceActionResult> ClearGeneratedFilesAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deleted = GetGeneratedDataFileStore().Clear();
        return Task.FromResult(
            new DataMaintenanceActionResult(
                true,
                deleted == 0 ? "没有需要清理的文件。" : $"已删除 {deleted} 个本地文件。",
                deleted
            )
        );
    }

    public Task<GeneratedDataFileDownload?> OpenGeneratedFileAsync(
        string id,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetGeneratedDataFileStore().Open(id));
    }

    private async Task<long> CountKnownDatabaseRowsAsync(
        BiliDbContext dbContext,
        CancellationToken cancellationToken
    )
    {
        var knownTables = new[]
        {
            "QRTZ_JOB_DETAILS",
            "QRTZ_TRIGGERS",
            "bili_execution_logs",
            "bili_logs",
            "bili_user",
            "bili_appsettings",
        };

        long total = 0;
        foreach (var table in knownTables)
        {
            total += await CountTableAsync(dbContext, table, cancellationToken);
        }

        return total;
    }

    private static async Task<long> CountTableAsync(
        BiliDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<DateTimeOffset?> GetLatestExecutionLogTimeAsync(
        BiliDbContext dbContext,
        CancellationToken cancellationToken
    )
    {
        return await dbContext
            .ExecutionLogs.OrderByDescending(x => x.DateAddedUtc)
            .Select(x => (DateTimeOffset?)x.DateAddedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<DateTimeOffset?> GetLatestSystemLogTimeAsync(
        BiliDbContext dbContext,
        CancellationToken cancellationToken
    )
    {
        var latest = await dbContext
            .BiliLogs.OrderByDescending(x => x.Timestamp)
            .Select(x => x.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        return latest == default ? null : latest;
    }

    private ContentAutomationDataSummary GetContentAutomationDataSummary()
    {
        var path = Path.GetFullPath(productCommentOptions.CurrentValue.StoragePath);
        if (!File.Exists(path))
            return new ContentAutomationDataSummary(path, 0);

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var count =
                CountJsonArray(root, "candidates")
                + CountJsonArray(root, "templates")
                + CountJsonArray(root, "ledger");
            return new ContentAutomationDataSummary(path, count);
        }
        catch
        {
            return new ContentAutomationDataSummary(path, 0);
        }
    }

    private static int CountJsonArray(JsonElement root, string propertyName)
    {
        return
            root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
    }

    private string GetDatabasePath()
    {
        var connectionString = configuration.GetConnectionString("Sqlite") ?? "";
        if (string.IsNullOrWhiteSpace(connectionString))
            return "";

        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource))
            return "";

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, dataSource));
    }

    private string ResolveWorkbenchPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return "";
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private static object? ReadJsonValue(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadCommentLedgerAsync(
        CancellationToken cancellationToken
    )
    {
        var databasePath = Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "comment-service",
            "data",
            "app.db"
        );
        if (!File.Exists(databasePath))
            return [];

        var result = new List<Dictionary<string, object?>>();
        try
        {
            await using var connection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly"
            );
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT account_name,bvid,url,template_text,status,error_reason,published_at,created_at FROM ledger ORDER BY id DESC";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(
                    new Dictionary<string, object?>
                    {
                        ["account"] = reader.GetString(0),
                        ["bvid"] = reader.GetString(1),
                        ["url"] = reader.GetString(2),
                        ["comment"] = reader.GetString(3),
                        ["status"] = reader.GetString(4),
                        ["reason"] = reader.GetString(5),
                        ["publishedAt"] = reader.GetString(6),
                        ["createdAt"] = reader.GetString(7),
                    }
                );
            }
        }
        catch
        {
            return [];
        }
        return result;
    }

    private GeneratedDataFileStore GetGeneratedDataFileStore()
    {
        return new GeneratedDataFileStore(Path.Combine(environment.ContentRootPath, "data"));
    }

    private IEnumerable<FileInfo> GetLogFiles()
    {
        foreach (var directory in GetLogDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (
                var file in Directory
                    .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(x =>
                        x.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                        || x.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    )
            )
            {
                yield return new FileInfo(file);
            }
        }
    }

    private IEnumerable<string> GetLogDirectories()
    {
        var configuredLogDirectory = localWorkbenchOptions.CurrentValue.LogDirectory;
        if (!string.IsNullOrWhiteSpace(configuredLogDirectory))
        {
            yield return Path.IsPathRooted(configuredLogDirectory)
                ? configuredLogDirectory
                : Path.GetFullPath(
                    Path.Combine(environment.ContentRootPath, configuredLogDirectory)
                );
        }

        yield return Path.Combine(environment.ContentRootPath, "Logs");
        var parent = Directory.GetParent(environment.ContentRootPath)?.Parent?.Parent?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "Logs");
    }

    private static long GetFileSize(string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? 0
            : new FileInfo(path).Length;
    }

    private static DateTimeOffset? GetLastWriteTime(string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? null
            : new FileInfo(path).LastWriteTime;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private sealed record ContentAutomationDataSummary(string Location, long ItemCount);
}

public sealed class DataMaintenanceSnapshot
{
    public DateTimeOffset GeneratedAt { get; set; }

    public IReadOnlyList<DataMaintenanceArea> Areas { get; set; } = [];
}

public sealed class DataMaintenanceArea
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public long ItemCount { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset? LastUpdated { get; set; }

    public string Location { get; set; } = "";

    public bool CanClean { get; set; }

    public string SafetyNote { get; set; } = "";
}

public sealed record DataMaintenanceActionResult(bool Success, string Message, long AffectedCount);
