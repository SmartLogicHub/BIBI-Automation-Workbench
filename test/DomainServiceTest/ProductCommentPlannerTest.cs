using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace DomainServiceTest;

public class ProductCommentPlannerTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;
    private readonly DateTimeOffset _now = new(2026, 7, 8, 10, 0, 0, TimeSpan.Zero);

    public ProductCommentPlannerTest()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "bibi-product-comment-planner-" + Guid.NewGuid()
        );
        _storagePath = Path.Combine(_tempDir, "product-comment.json");
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldRenderConfiguredPlaceholders()
    {
        var (store, planner) = CreatePlanner(
            templates: ["看完{产品名}，{视频标题}，{UP主}讲得不错"]
        );
        await store.AddCandidateAsync(
            CreateCandidate(
                "BV001",
                aid: 1001,
                title: "降噪耳机体验",
                author: "科技UP",
                keyword: "原子豆ANC"
            )
        );

        var plan = await planner.BuildPlanAsync("10001", _now);

        var item = Assert.Single(plan);
        Assert.Equal("10001", item.AccountUserId);
        Assert.Equal("BV001", item.Bvid);
        Assert.Equal(1001, item.Aid);
        Assert.Equal("https://www.bilibili.com/video/BV001", item.Url);
        Assert.Equal("原子豆ANC", item.Keyword);
        Assert.Equal("降噪耳机体验", item.VideoTitle);
        Assert.Equal("科技UP", item.Author);
        Assert.Equal("看完{产品名}，{视频标题}，{UP主}讲得不错", item.TemplateText);
        Assert.Equal("看完原子豆ANC，降噪耳机体验，科技UP讲得不错", item.CommentText);
        Assert.True(item.DryRun);
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldNotProduceDuplicatePlanForSameBvid()
    {
        var (store, planner) = CreatePlanner();
        await store.AddCandidateAsync(CreateCandidate("BV001", title: "First"));
        await store.AddCandidateAsync(CreateCandidate("BV001", title: "Second"));

        var plan = await planner.BuildPlanAsync("10001", _now);

        var item = Assert.Single(plan);
        Assert.Equal("First", item.VideoTitle);
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldRespectMaxPublishPerRun()
    {
        var (store, planner) = CreatePlanner(maxPublishPerRun: 2);
        await store.AddCandidateAsync(CreateCandidate("BV001"));
        await store.AddCandidateAsync(CreateCandidate("BV002"));
        await store.AddCandidateAsync(CreateCandidate("BV003"));

        var plan = await planner.BuildPlanAsync("10001", _now);

        Assert.Collection(
            plan,
            x => Assert.Equal("BV001", x.Bvid),
            x => Assert.Equal("BV002", x.Bvid)
        );
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldSkipSuccessfulLedgerBvidWhenConfigured()
    {
        var (store, planner) = CreatePlanner(skipCommentedBvid: true);
        await store.AddCandidateAsync(CreateCandidate("BV001"));
        await store.AddCandidateAsync(CreateCandidate("BV002"));
        await store.AddLedgerRecordAsync(
            new ProductCommentLedgerRecord
            {
                AccountUserId = "10001",
                Bvid = "BV001",
                Aid = 1,
                Status = "success",
                CommentText = "done",
                CreatedAt = _now,
                PublishedAt = _now,
            }
        );

        var plan = await planner.BuildPlanAsync("10001", _now);

        var item = Assert.Single(plan);
        Assert.Equal("BV002", item.Bvid);
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldReturnEmptyWhenDailyLimitReached()
    {
        var (store, planner) = CreatePlanner(maxDailyCommentsPerAccount: 2);
        await store.AddCandidateAsync(CreateCandidate("BV001"));
        await store.SaveAccountStateAsync(
            new ProductCommentAccountState
            {
                AccountUserId = "10001",
                TodayCount = 2,
                StatDate = DateOnly.FromDateTime(_now.UtcDateTime),
            }
        );

        var plan = await planner.BuildPlanAsync("10001", _now);

        Assert.Empty(plan);
    }

    [Fact]
    public async Task BuildPlanAsync_ShouldReturnEmptyWhenAccountInCooldown()
    {
        var (store, planner) = CreatePlanner();
        await store.AddCandidateAsync(CreateCandidate("BV001"));
        await store.SaveAccountStateAsync(
            new ProductCommentAccountState
            {
                AccountUserId = "10001",
                TodayCount = 0,
                StatDate = DateOnly.FromDateTime(_now.UtcDateTime),
                NextAvailableAt = _now.AddMinutes(10),
            }
        );

        var plan = await planner.BuildPlanAsync("10001", _now);

        Assert.Empty(plan);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private (IProductCommentStore Store, IProductCommentPlannerDomainService Planner) CreatePlanner(
        List<string>? templates = null,
        int maxPublishPerRun = 5,
        int maxDailyCommentsPerAccount = 10,
        bool skipCommentedBvid = true
    )
    {
        var options = new ProductCommentTaskOptions
        {
            StoragePath = _storagePath,
            DryRun = true,
            SkipCommentedBvid = skipCommentedBvid,
            MaxPublishPerRun = maxPublishPerRun,
            MaxDailyCommentsPerAccount = maxDailyCommentsPerAccount,
            Templates = templates ?? ["template {产品名} {视频标题} {UP主}"],
        };
        var optionsMonitor = new StaticOptionsMonitor<ProductCommentTaskOptions>(options);
        var store = new ProductCommentJsonStore(optionsMonitor);
        var planner = new ProductCommentPlannerDomainService(optionsMonitor, store);
        return (store, planner);
    }

    private static ProductCommentCandidate CreateCandidate(
        string bvid,
        long aid = 1,
        string title = "First",
        string author = "UP",
        string keyword = "keyword"
    )
    {
        return new ProductCommentCandidate
        {
            Bvid = bvid,
            Aid = aid,
            Title = title,
            Author = author,
            Keyword = keyword,
        };
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
