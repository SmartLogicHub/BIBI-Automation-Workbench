using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using Ray.BiliBiliTool.Application.Attributes;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace Ray.BiliBiliTool.Application;

public class ProductCommentTaskAppService(
    ILogger<ProductCommentTaskAppService> logger,
    IOptionsMonitor<ProductCommentTaskOptions> options,
    CookieStrFactory<BiliCookie> cookieStrFactory,
    IProductCommentSearchDomainService searchDomainService,
    IProductCommentStore store,
    IProductCommentPlannerDomainService planner
) : BaseMultiAccountsAppService(logger, cookieStrFactory), IProductCommentTaskAppService
{
    private readonly ILogger<ProductCommentTaskAppService> _logger = logger;
    private readonly IOptionsMonitor<ProductCommentTaskOptions> _options = options;
    private readonly IProductCommentSearchDomainService _searchDomainService = searchDomainService;
    private readonly IProductCommentStore _store = store;
    private readonly IProductCommentPlannerDomainService _planner = planner;

    [TaskInterceptor("ProductComment", TaskLevel.One)]
    protected override async Task DoTaskAccountAsync(
        BiliCookie ck,
        CancellationToken cancellationToken = default
    )
    {
        var taskOptions = _options.CurrentValue;

        if (!taskOptions.IsEnable)
        {
            _logger.LogInformation("[ProductComment] IsEnable=false, skip.");
            return;
        }

        if (!IsCookieUsable(ck))
        {
            _logger.LogWarning(
                "[ProductComment] Cookie is missing required account fields, skip account."
            );
            return;
        }

        if (!taskOptions.DryRun)
        {
            _logger.LogWarning(
                "[ProductComment] Only dry-run flow is implemented in this task, skip real run."
            );
            return;
        }

        var keywords = NormalizeKeywords(taskOptions.Keywords).ToList();
        if (keywords.Count == 0)
        {
            _logger.LogWarning("[ProductComment] Keywords is empty, skip dry-run.");
            return;
        }

        var templates = await _store.GetTemplatesAsync();
        if (templates.Count == 0)
        {
            _logger.LogWarning("[ProductComment] Templates is empty, skip dry-run.");
            return;
        }

        _logger.LogInformation(
            "[ProductComment] Dry-run starts. Account={account}, Keywords={keywordCount}, Templates={templateCount}",
            ck.UserId,
            keywords.Count,
            templates.Count
        );

        await SearchAndStoreCandidatesAsync(ck, keywords, taskOptions, cancellationToken);
        var plan = await _planner.BuildPlanAsync(ck.UserId, DateTimeOffset.UtcNow);

        if (plan.Count == 0)
        {
            _logger.LogInformation("[ProductComment] No dry-run plan item generated.");
            return;
        }

        await WriteDryRunLedgerAsync(plan, cancellationToken);

        _logger.LogInformation(
            "[ProductComment] Dry-run completed. Account={account}, PlanCount={count}",
            ck.UserId,
            plan.Count
        );
    }

    private async Task SearchAndStoreCandidatesAsync(
        BiliCookie ck,
        IReadOnlyList<string> keywords,
        ProductCommentTaskOptions taskOptions,
        CancellationToken cancellationToken
    )
    {
        var maxCandidatesPerRun = Math.Max(0, taskOptions.MaxCandidatesPerRun);
        if (maxCandidatesPerRun == 0)
        {
            _logger.LogWarning("[ProductComment] MaxCandidatesPerRun is 0, skip search.");
            return;
        }

        var handledCandidates = 0;
        var insertedCandidates = 0;
        var duplicateCandidates = 0;

        foreach (var keyword in keywords)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (handledCandidates >= maxCandidatesPerRun)
                break;

            var videos = await _searchDomainService.SearchVideosAsync(keyword, ck);
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (handledCandidates >= maxCandidatesPerRun)
                    break;

                var inserted = await _store.AddCandidateAsync(ToCandidate(video, keyword));
                if (inserted)
                    insertedCandidates++;
                else
                    duplicateCandidates++;

                handledCandidates++;
            }
        }

        _logger.LogInformation(
            "[ProductComment] Candidate dry-run storage completed. Inserted={inserted}, Duplicated={duplicated}",
            insertedCandidates,
            duplicateCandidates
        );
    }

    private async Task WriteDryRunLedgerAsync(
        IReadOnlyList<ProductCommentPlanItem> plan,
        CancellationToken cancellationToken
    )
    {
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "[ProductComment] Dry-run plan. Account={account}, Bvid={bvid}, Title={title}, Comment={comment}",
                item.AccountUserId,
                item.Bvid,
                item.VideoTitle,
                item.CommentText
            );

            await _store.AddLedgerRecordAsync(
                new ProductCommentLedgerRecord
                {
                    AccountUserId = item.AccountUserId,
                    Bvid = item.Bvid,
                    Aid = item.Aid,
                    Url = item.Url,
                    Keyword = item.Keyword,
                    VideoTitle = item.VideoTitle,
                    Author = item.Author,
                    CommentText = item.CommentText,
                    Status = "dry_run",
                    DryRun = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                }
            );
        }
    }

    private static ProductCommentCandidate ToCandidate(SearchVideoItemDto video, string keyword)
    {
        return new ProductCommentCandidate
        {
            Bvid = video.Bvid,
            Aid = video.Aid,
            Url = video.Arcurl ?? "",
            Title = video.Title,
            Author = video.Author ?? "",
            Keyword = keyword,
            Status = "pending",
        };
    }

    private static IEnumerable<string> NormalizeKeywords(IEnumerable<string> keywords)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var keyword in keywords.Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x))
        )
        {
            if (seen.Add(keyword))
                yield return keyword;
        }
    }

    private static bool IsCookieUsable(BiliCookie ck)
    {
        return !string.IsNullOrWhiteSpace(ck.UserId)
            && !string.IsNullOrWhiteSpace(ck.SessData)
            && !string.IsNullOrWhiteSpace(ck.BiliJct);
    }
}
