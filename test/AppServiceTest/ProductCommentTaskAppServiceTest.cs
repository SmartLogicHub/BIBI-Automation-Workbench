using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;
using Ray.BiliBiliTool.Infrastructure;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace AppServiceTest;

public class ProductCommentTaskAppServiceTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;
    private readonly IServiceProvider? _previousServiceProvider;
    private readonly ServiceProvider _serviceProvider;

    public ProductCommentTaskAppServiceTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bibi-product-comment-app-" + Guid.NewGuid());
        _storagePath = Path.Combine(_tempDir, "product-comment.json");
        _previousServiceProvider = Global.ServiceProviderRoot;
        _serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance))
            .BuildServiceProvider();
        Global.ServiceProviderRoot = _serviceProvider;
    }

    [Fact]
    public async Task DoTaskAsync_ShouldExitWhenDisabledWithoutSearch()
    {
        var search = new FakeProductCommentSearchDomainService();
        var store = CreateStore(
            new ProductCommentTaskOptions
            {
                IsEnable = false,
                StoragePath = _storagePath,
                Keywords = ["原子豆ANC"],
                Templates = ["看完{产品名}"],
            }
        );
        var appService = CreateAppService(
            search,
            store,
            new ProductCommentTaskOptions
            {
                IsEnable = false,
                StoragePath = _storagePath,
                Keywords = ["原子豆ANC"],
                Templates = ["看完{产品名}"],
            }
        );

        await appService.DoTaskAsync();

        Assert.Empty(search.Calls);
        Assert.Empty(await store.GetCandidatesAsync());
        Assert.Empty(await store.GetLedgerAsync());
    }

    [Fact]
    public async Task DoTaskAsync_DryRunShouldSearchStorePlanAndWriteLedger()
    {
        var options = CreateEnabledDryRunOptions();
        var search = new FakeProductCommentSearchDomainService([
            new SearchVideoItemDto
            {
                Aid = 1001,
                Bvid = "BV001",
                Title = "降噪耳机体验",
                Author = "科技UP",
            },
        ]);
        var store = CreateStore(options);
        var appService = CreateAppService(search, store, options);

        await appService.DoTaskAsync();

        Assert.Collection(search.Calls, x => Assert.Equal("原子豆ANC", x.Keyword));

        var candidate = Assert.Single(await store.GetCandidatesAsync());
        Assert.Equal("BV001", candidate.Bvid);
        Assert.Equal("原子豆ANC", candidate.Keyword);
        Assert.Equal("降噪耳机体验", candidate.Title);

        var ledger = Assert.Single(await store.GetLedgerAsync());
        Assert.Equal("10001", ledger.AccountUserId);
        Assert.Equal("BV001", ledger.Bvid);
        Assert.Equal("dry_run", ledger.Status);
        Assert.True(ledger.DryRun);
        Assert.Equal("看完原子豆ANC：降噪耳机体验：科技UP", ledger.CommentText);

        var accountState = await store.GetAccountStateAsync("10001");
        Assert.Null(accountState);
    }

    [Fact]
    public async Task DoTaskAsync_ShouldExitWhenKeywordsMissing()
    {
        var options = CreateEnabledDryRunOptions();
        options.Keywords = [];
        var search = new FakeProductCommentSearchDomainService();
        var store = CreateStore(options);
        var appService = CreateAppService(search, store, options);

        await appService.DoTaskAsync();

        Assert.Empty(search.Calls);
        Assert.Empty(await store.GetCandidatesAsync());
        Assert.Empty(await store.GetLedgerAsync());
    }

    [Fact]
    public async Task DoTaskAsync_ShouldExitWhenTemplatesMissing()
    {
        var options = CreateEnabledDryRunOptions();
        options.Templates = [];
        var search = new FakeProductCommentSearchDomainService([
            new SearchVideoItemDto
            {
                Aid = 1001,
                Bvid = "BV001",
                Title = "降噪耳机体验",
                Author = "科技UP",
            },
        ]);
        var store = CreateStore(options);
        var appService = CreateAppService(search, store, options);

        await appService.DoTaskAsync();

        Assert.Empty(search.Calls);
        Assert.Empty(await store.GetCandidatesAsync());
        Assert.Empty(await store.GetLedgerAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        Global.ServiceProviderRoot = _previousServiceProvider;
        _serviceProvider.Dispose();
    }

    private static ProductCommentTaskAppService CreateAppService(
        IProductCommentSearchDomainService search,
        IProductCommentStore store,
        ProductCommentTaskOptions options
    )
    {
        var optionsMonitor = new StaticOptionsMonitor<ProductCommentTaskOptions>(options);
        var planner = new ProductCommentPlannerDomainService(optionsMonitor, store);

        return new ProductCommentTaskAppService(
            NullLogger<ProductCommentTaskAppService>.Instance,
            optionsMonitor,
            CreateCookieFactory(),
            search,
            store,
            planner
        );
    }

    private IProductCommentStore CreateStore(ProductCommentTaskOptions options)
    {
        options.StoragePath = _storagePath;
        return new ProductCommentJsonStore(
            new StaticOptionsMonitor<ProductCommentTaskOptions>(options)
        );
    }

    private ProductCommentTaskOptions CreateEnabledDryRunOptions()
    {
        return new ProductCommentTaskOptions
        {
            IsEnable = true,
            DryRun = true,
            EnableAutoPublish = false,
            PublishMode = "DryRun",
            StoragePath = _storagePath,
            Keywords = ["原子豆ANC"],
            Templates = ["看完{产品名}：{视频标题}：{UP主}"],
            MaxVideosPerKeyword = 1,
            MaxPublishPerRun = 1,
        };
    }

    private static CookieStrFactory<BiliCookie> CreateCookieFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BiliBiliCookies:0"] =
                        "DedeUserID=10001; SESSDATA=sess; bili_jct=jct; buvid3=buvid",
                }
            )
            .Build();

        return new CookieStrFactory<BiliCookie>(configuration);
    }

    private sealed class FakeProductCommentSearchDomainService(
        IReadOnlyList<SearchVideoItemDto>? results = null
    ) : IProductCommentSearchDomainService
    {
        public List<(string Keyword, string UserId)> Calls { get; } = [];

        public Task<IReadOnlyList<SearchVideoItemDto>> SearchVideosAsync(
            string keyword,
            BiliCookie ck
        )
        {
            Calls.Add((keyword, ck.UserId));
            return Task.FromResult(results ?? []);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
