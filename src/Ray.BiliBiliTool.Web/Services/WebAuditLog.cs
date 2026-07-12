namespace Ray.BiliBiliTool.Web.Services;

using System.Text.Json;

public interface IWebAuditLog
{
    Task AddAsync(string actor, string action, string detail, string result);

    Task<IReadOnlyList<WebAuditLogRecord>> ListAsync();
}

public class FileWebAuditLog(IWebHostEnvironment environment) : IWebAuditLog
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path = Path.Combine(
        environment.ContentRootPath,
        "Data",
        "web-audit-log.json"
    );
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task AddAsync(string actor, string action, string detail, string result)
    {
        var record = new WebAuditLogRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Actor = string.IsNullOrWhiteSpace(actor) ? "本地用户" : actor.Trim(),
            Action = action.Trim(),
            Detail = detail.Trim(),
            Result = result.Trim(),
        };

        await _lock.WaitAsync();
        try
        {
            var records = await ReadRecordsAsync();
            records.Insert(0, record);
            if (records.Count > 1000)
            {
                records.RemoveRange(1000, records.Count - 1000);
            }

            await WriteRecordsAsync(records);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WebAuditLogRecord>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadRecordsAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<WebAuditLogRecord>> ReadRecordsAsync()
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<WebAuditLogRecord>>(
                    stream,
                    JsonOptions
                ) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task WriteRecordsAsync(List<WebAuditLogRecord> records)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions);
        }

        File.Move(tempPath, _path, overwrite: true);
    }
}

public class WebAuditLogRecord
{
    public string Id { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public string Actor { get; set; } = "";

    public string Action { get; set; } = "";

    public string Detail { get; set; } = "";

    public string Result { get; set; } = "";
}
