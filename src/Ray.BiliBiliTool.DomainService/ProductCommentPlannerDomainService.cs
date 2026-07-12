using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService;

public class ProductCommentPlannerDomainService(
    IOptionsMonitor<ProductCommentTaskOptions> options,
    IProductCommentStore store
) : IProductCommentPlannerDomainService
{
    public async Task<IReadOnlyList<ProductCommentPlanItem>> BuildPlanAsync(
        string accountUserId,
        DateTimeOffset now
    )
    {
        if (string.IsNullOrWhiteSpace(accountUserId))
            return [];

        var taskOptions = options.CurrentValue;
        var maxPublishPerRun = Math.Max(0, taskOptions.MaxPublishPerRun);
        var maxDailyComments = Math.Max(0, taskOptions.MaxDailyCommentsPerAccount);
        if (maxPublishPerRun == 0 || maxDailyComments == 0)
            return [];

        var accountState = await store.GetAccountStateAsync(accountUserId);
        if (IsInCooldown(accountState, now))
            return [];

        var todayCount = GetTodayCount(accountState, now);
        if (todayCount >= maxDailyComments)
            return [];

        var templates = (await store.GetTemplatesAsync())
            .Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Text))
            .ToList();
        if (templates.Count == 0)
            return [];

        var limit = Math.Min(maxPublishPerRun, maxDailyComments - todayCount);
        var candidates = (await store.GetPendingCandidatesAsync())
            .Where(x => !string.IsNullOrWhiteSpace(x.Bvid))
            .GroupBy(x => x.Bvid, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(limit)
            .ToList();

        return candidates
            .Select(
                (candidate, index) =>
                    BuildPlanItem(
                        accountUserId,
                        candidate,
                        templates[index % templates.Count],
                        taskOptions.DryRun
                    )
            )
            .ToList();
    }

    private static ProductCommentPlanItem BuildPlanItem(
        string accountUserId,
        ProductCommentCandidate candidate,
        ProductCommentTemplate template,
        bool dryRun
    )
    {
        return new ProductCommentPlanItem
        {
            AccountUserId = accountUserId.Trim(),
            Bvid = candidate.Bvid,
            Aid = candidate.Aid,
            Url = string.IsNullOrWhiteSpace(candidate.Url)
                ? $"https://www.bilibili.com/video/{candidate.Bvid}"
                : candidate.Url,
            Keyword = candidate.Keyword,
            VideoTitle = candidate.Title,
            Author = candidate.Author,
            TemplateText = template.Text,
            CommentText = RenderTemplate(template.Text, candidate),
            DryRun = dryRun,
        };
    }

    private static string RenderTemplate(string template, ProductCommentCandidate candidate)
    {
        return template
            .Replace("{产品名}", candidate.Keyword)
            .Replace("{视频标题}", candidate.Title)
            .Replace("{UP主}", candidate.Author)
            .Replace("{ProductName}", candidate.Keyword)
            .Replace("{VideoTitle}", candidate.Title)
            .Replace("{Author}", candidate.Author);
    }

    private static bool IsInCooldown(ProductCommentAccountState? state, DateTimeOffset now)
    {
        return state?.NextAvailableAt is { } nextAvailableAt && nextAvailableAt > now;
    }

    private static int GetTodayCount(ProductCommentAccountState? state, DateTimeOffset now)
    {
        if (state is null)
            return 0;

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return state.StatDate == today ? state.TodayCount : 0;
    }
}
