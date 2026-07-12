namespace Ray.BiliBiliTool.Web.Services;

public sealed class GeneratedDataFileStore(string dataRoot)
{
    private static readonly IReadOnlyDictionary<string, GeneratedFileRule> Rules = new Dictionary<
        string,
        GeneratedFileRule
    >(StringComparer.OrdinalIgnoreCase)
    {
        ["backups"] = new("本地备份", ".zip", "application/zip"),
        ["exports"] = new("数据导出", ".json", "application/json"),
    };

    private readonly string _dataRoot = Path.GetFullPath(dataRoot);

    public IReadOnlyList<DataMaintenanceFile> List()
    {
        var files = new List<DataMaintenanceFile>();
        foreach (var pair in Rules)
        {
            var directory = Path.Combine(_dataRoot, pair.Key);
            if (!Directory.Exists(directory))
                continue;

            foreach (
                var path in Directory.EnumerateFiles(
                    directory,
                    $"*{pair.Value.Extension}",
                    SearchOption.TopDirectoryOnly
                )
            )
            {
                var file = new FileInfo(path);
                files.Add(
                    new DataMaintenanceFile
                    {
                        Id = $"{pair.Key}/{file.Name}",
                        Name = file.Name,
                        Kind = pair.Value.Kind,
                        SizeBytes = file.Length,
                        CreatedAt = file.LastWriteTime,
                    }
                );
            }
        }

        return files
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool Delete(string id)
    {
        if (!TryResolve(id, out var path) || !File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    public GeneratedDataFileDownload? Open(string id)
    {
        if (!TryResolve(id, out var path) || !File.Exists(path))
            return null;

        var folder = (id ?? "")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
        var rule = Rules[folder];
        return new GeneratedDataFileDownload
        {
            Content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            FileName = Path.GetFileName(path),
            ContentType = rule.ContentType,
        };
    }

    public int Clear()
    {
        var deleted = 0;
        foreach (var file in List())
        {
            if (Delete(file.Id))
                deleted++;
        }

        return deleted;
    }

    private bool TryResolve(string id, out string path)
    {
        path = "";
        var parts = (id ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !Rules.TryGetValue(parts[0], out var rule))
            return false;

        var fileName = parts[1];
        if (
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !string.Equals(
                Path.GetExtension(fileName),
                rule.Extension,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        var directory = Path.GetFullPath(Path.Combine(_dataRoot, parts[0]));
        var candidate = Path.GetFullPath(Path.Combine(directory, fileName));
        if (
            !candidate.StartsWith(
                directory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return false;

        path = candidate;
        return true;
    }

    private sealed record GeneratedFileRule(string Kind, string Extension, string ContentType);
}

public sealed class DataMaintenanceFile
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Kind { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GeneratedDataFileDownload : IDisposable
{
    public Stream Content { get; set; } = Stream.Null;

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "application/octet-stream";

    public void Dispose()
    {
        Content.Dispose();
    }
}
