using System.Text.Json;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure;

namespace Ray.BiliBiliTool.DomainService;

public class ProductCommentJsonStore(IOptionsMonitor<ProductCommentTaskOptions> options)
    : IProductCommentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerOptionsBuilder.DefaultOptions
    )
    {
        WriteIndented = true,
    };

    public async Task<bool> AddCandidateAsync(ProductCommentCandidate candidate)
    {
        var data = await LoadAsync();
        if (data.Candidates.Any(x => SameBvid(x.Bvid, candidate.Bvid)))
            return false;

        NormalizeCandidate(candidate);
        data.Candidates.Add(candidate);
        await SaveAsync(data);
        return true;
    }

    public async Task<IReadOnlyList<ProductCommentCandidate>> GetCandidatesAsync()
    {
        var data = await LoadAsync();
        return data.Candidates;
    }

    public async Task<IReadOnlyList<ProductCommentCandidate>> GetPendingCandidatesAsync()
    {
        var data = await LoadAsync();
        var successfulBvids = data
            .Ledger.Where(x => IsSuccess(x.Status))
            .Select(x => x.Bvid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var query = data.Candidates.Where(x => IsPending(x.Status));
        if (options.CurrentValue.SkipCommentedBvid)
        {
            query = query.Where(x => !successfulBvids.Contains(x.Bvid));
        }

        return query.ToList();
    }

    public async Task<bool> UpdateCandidateStatusAsync(
        string bvid,
        string status,
        string lastError = ""
    )
    {
        if (string.IsNullOrWhiteSpace(bvid) || string.IsNullOrWhiteSpace(status))
            return false;

        var data = await LoadAsync();
        var candidate = data.Candidates.FirstOrDefault(x => SameBvid(x.Bvid, bvid.Trim()));
        if (candidate is null)
            return false;

        candidate.Status = status.Trim();
        candidate.LastError = lastError?.Trim() ?? "";
        candidate.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(data);
        return true;
    }

    public async Task AddLedgerRecordAsync(ProductCommentLedgerRecord record)
    {
        var data = await LoadAsync();
        NormalizeLedgerRecord(record);
        data.Ledger.Add(record);
        await SaveAsync(data);
    }

    public async Task<IReadOnlyList<ProductCommentLedgerRecord>> GetLedgerAsync()
    {
        var data = await LoadAsync();
        return data.Ledger;
    }

    public async Task SaveAccountStateAsync(ProductCommentAccountState state)
    {
        var data = await LoadAsync();
        var existingIndex = data.AccountStates.FindIndex(x =>
            string.Equals(x.AccountUserId, state.AccountUserId, StringComparison.OrdinalIgnoreCase)
        );

        if (existingIndex >= 0)
            data.AccountStates[existingIndex] = state;
        else
            data.AccountStates.Add(state);

        await SaveAsync(data);
    }

    public async Task<ProductCommentAccountState?> GetAccountStateAsync(string accountUserId)
    {
        var data = await LoadAsync();
        return data.AccountStates.FirstOrDefault(x =>
            string.Equals(x.AccountUserId, accountUserId, StringComparison.OrdinalIgnoreCase)
        );
    }

    public async Task<IReadOnlyList<ProductCommentAccountState>> GetAccountStatesAsync()
    {
        var data = await LoadAsync();
        return data
            .AccountStates.OrderBy(x => x.AccountUserId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveTemplateAsync(ProductCommentTemplate template)
    {
        var data = await LoadAsync();
        NormalizeTemplate(template);
        if (string.IsNullOrWhiteSpace(template.Text))
            return;

        var existingIndex = data.Templates.FindIndex(x =>
            string.Equals(x.Id, template.Id, StringComparison.OrdinalIgnoreCase)
        );

        if (existingIndex >= 0)
            data.Templates[existingIndex] = template;
        else
            data.Templates.Add(template);

        await SaveAsync(data);
    }

    public async Task<IReadOnlyList<ProductCommentTemplate>> GetTemplatesAsync()
    {
        var configTemplates = NormalizeTemplateTexts(options.CurrentValue.Templates).ToList();
        if (configTemplates.Count > 0)
        {
            return configTemplates
                .Select(
                    (text, index) =>
                        new ProductCommentTemplate
                        {
                            Id = $"config-{index + 1}",
                            Text = text,
                            IsEnabled = true,
                        }
                )
                .ToList();
        }

        var data = await LoadAsync();
        return NormalizeTemplateTexts(data.Templates.Where(x => x.IsEnabled).Select(x => x.Text))
            .Select(
                (text, index) =>
                    new ProductCommentTemplate
                    {
                        Id = $"stored-{index + 1}",
                        Text = text,
                        IsEnabled = true,
                    }
            )
            .ToList();
    }

    private async Task<ProductCommentStoreData> LoadAsync()
    {
        var path = GetStoragePath();
        if (!File.Exists(path))
            return new ProductCommentStoreData();

        var json = await File.ReadAllTextAsync(path);
        if (string.IsNullOrWhiteSpace(json))
            return new ProductCommentStoreData();

        return JsonSerializer.Deserialize<ProductCommentStoreData>(json, JsonOptions)
            ?? new ProductCommentStoreData();
    }

    private async Task SaveAsync(ProductCommentStoreData data)
    {
        var path = GetStoragePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private string GetStoragePath()
    {
        return Path.GetFullPath(options.CurrentValue.StoragePath);
    }

    private static void NormalizeCandidate(ProductCommentCandidate candidate)
    {
        var now = DateTimeOffset.UtcNow;
        candidate.Bvid = candidate.Bvid.Trim();
        candidate.Title = candidate.Title.Trim();
        candidate.Author = candidate.Author.Trim();
        candidate.Keyword = candidate.Keyword.Trim();
        candidate.Status = string.IsNullOrWhiteSpace(candidate.Status)
            ? "pending"
            : candidate.Status.Trim();
        candidate.Url = string.IsNullOrWhiteSpace(candidate.Url)
            ? $"https://www.bilibili.com/video/{candidate.Bvid}"
            : candidate.Url.Trim();
        candidate.CreatedAt = candidate.CreatedAt == default ? now : candidate.CreatedAt;
        candidate.UpdatedAt = now;
    }

    private static void NormalizeLedgerRecord(ProductCommentLedgerRecord record)
    {
        record.Id = string.IsNullOrWhiteSpace(record.Id)
            ? Guid.NewGuid().ToString("N")
            : record.Id.Trim();
        record.Bvid = record.Bvid.Trim();
        record.Url = string.IsNullOrWhiteSpace(record.Url)
            ? $"https://www.bilibili.com/video/{record.Bvid}"
            : record.Url.Trim();
        record.CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
    }

    private static void NormalizeTemplate(ProductCommentTemplate template)
    {
        template.Id = string.IsNullOrWhiteSpace(template.Id)
            ? Guid.NewGuid().ToString("N")
            : template.Id.Trim();
        template.Text = template.Text.Trim();
        template.CreatedAt =
            template.CreatedAt == default ? DateTimeOffset.UtcNow : template.CreatedAt;
    }

    private static IEnumerable<string> NormalizeTemplateTexts(IEnumerable<string> templates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var text in templates.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x))
        )
        {
            if (seen.Add(text))
                yield return text;
        }
    }

    private static bool SameBvid(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPending(string status)
    {
        return string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuccess(string status)
    {
        return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProductCommentStoreData
    {
        public List<ProductCommentCandidate> Candidates { get; set; } = [];

        public List<ProductCommentTemplate> Templates { get; set; } = [];

        public List<ProductCommentLedgerRecord> Ledger { get; set; } = [];

        public List<ProductCommentAccountState> AccountStates { get; set; } = [];
    }
}
