using System.Text.Json;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Config.Options;

namespace Ray.BiliBiliTool.Application;

public class ContentAutomationStrategyService : IContentAutomationStrategyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storePath;

    public ContentAutomationStrategyService(string storePath)
    {
        _storePath = Path.GetFullPath(storePath);
    }

    public ContentAutomationStrategyService(IOptionsMonitor<LocalWorkbenchOptions> options)
        : this(ResolveStorePath(options.CurrentValue.CommentStrategyStorePath)) { }

    public async Task<ContentAutomationStrategy> GetAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnlockedAsync(cancellationToken);
        }
        catch (JsonException)
        {
            return Normalize(new ContentAutomationStrategy());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentAutomationStrategy> SaveAsync(
        ContentAutomationStrategy strategy,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(strategy);
        var normalized = Normalize(strategy);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteUnlockedAsync(normalized, cancellationToken);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedId = accountId?.Trim() ?? "";
        if (normalizedId.Length == 0)
            return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadUnlockedAsync(cancellationToken);
            var remaining = current
                .AccountIds.Where(id =>
                    !id.Equals(normalizedId, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            if (remaining.Count == current.AccountIds.Count)
                return false;

            current.AccountIds = remaining;
            await WriteUnlockedAsync(current, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ContentAutomationStrategy> ReadUnlockedAsync(
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(_storePath))
            return Normalize(new ContentAutomationStrategy());

        await using var stream = File.OpenRead(_storePath);
        var strategy = await JsonSerializer.DeserializeAsync<ContentAutomationStrategy>(
            stream,
            JsonOptions,
            cancellationToken
        );
        return Normalize(strategy ?? new ContentAutomationStrategy());
    }

    private async Task WriteUnlockedAsync(
        ContentAutomationStrategy strategy,
        CancellationToken cancellationToken
    )
    {
        var directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(strategy),
                    JsonOptions,
                    cancellationToken
                );
            }

            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static ContentAutomationStrategy Normalize(ContentAutomationStrategy strategy)
    {
        var mode = strategy.CommentMode?.Trim().ToLowerInvariant();
        if (mode is not ("template" or "deepseek" or "qwen"))
            mode = "template";

        var cycleIntervalSeconds =
            strategy.CycleIntervalSeconds > 0
                ? Math.Clamp(strategy.CycleIntervalSeconds, 1, 86_400)
                : Math.Clamp(strategy.CycleIntervalMinutes, 1, 1_440) * 60;
        var accountIntervalMinSeconds = Math.Clamp(strategy.AccountIntervalMinSeconds, 1, 86_400);
        var accountIntervalMaxSeconds = Math.Clamp(
            strategy.AccountIntervalMaxSeconds,
            accountIntervalMinSeconds,
            86_400
        );
        var sameAccountCooldownMinSeconds = Math.Clamp(
            strategy.SameAccountCooldownMinSeconds,
            1,
            86_400
        );
        var sameAccountCooldownMaxSeconds = Math.Clamp(
            strategy.SameAccountCooldownMaxSeconds,
            sameAccountCooldownMinSeconds,
            86_400
        );

        return new ContentAutomationStrategy
        {
            Version = Math.Max(1, strategy.Version),
            CommentMode = mode,
            LibraryId = strategy.LibraryId?.Trim() ?? "",
            Keywords = NormalizeList(strategy.Keywords),
            AccountIds = NormalizeList(strategy.AccountIds),
            Rules = strategy.Rules?.Trim() ?? "",
            Year = string.IsNullOrWhiteSpace(strategy.Year)
                ? DateTime.Now.Year.ToString()
                : strategy.Year.Trim(),
            SearchLimit = Math.Clamp(strategy.SearchLimit, 1, 50),
            PublishLimit = Math.Clamp(strategy.PublishLimit, 1, 50),
            DurationMinutes = Math.Clamp(strategy.DurationMinutes, 1, 1440),
            CycleIntervalMinutes = Math.Max(1, (int)Math.Ceiling(cycleIntervalSeconds / 60d)),
            CycleIntervalSeconds = cycleIntervalSeconds,
            AccountIntervalMinSeconds = accountIntervalMinSeconds,
            AccountIntervalMaxSeconds = accountIntervalMaxSeconds,
            SameAccountCooldownMinSeconds = sameAccountCooldownMinSeconds,
            SameAccountCooldownMaxSeconds = sameAccountCooldownMaxSeconds,
        };
    }

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(value => value?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveStorePath(string configuredPath)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath)
            ? "./data/comment-strategy.json"
            : configuredPath.Trim();
        return Path.GetFullPath(value, AppContext.BaseDirectory);
    }
}
