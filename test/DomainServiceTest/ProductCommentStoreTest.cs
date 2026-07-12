using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace DomainServiceTest;

public class ProductCommentStoreTest : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storagePath;

    public ProductCommentStoreTest()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bibi-product-comment-" + Guid.NewGuid());
        _storagePath = Path.Combine(_tempDir, "product-comment.json");
    }

    [Fact]
    public async Task AddCandidateAsync_ShouldInsertNewBvid()
    {
        var store = CreateStore();

        var inserted = await store.AddCandidateAsync(CreateCandidate("BV001"));
        var candidates = await store.GetCandidatesAsync();

        Assert.True(inserted);
        var candidate = Assert.Single(candidates);
        Assert.Equal("BV001", candidate.Bvid);
        Assert.Equal("pending", candidate.Status);
        Assert.Equal("https://www.bilibili.com/video/BV001", candidate.Url);
    }

    [Fact]
    public async Task AddCandidateAsync_ShouldSkipDuplicateBvid()
    {
        var store = CreateStore();

        var first = await store.AddCandidateAsync(CreateCandidate("BV001", title: "First"));
        var second = await store.AddCandidateAsync(CreateCandidate("BV001", title: "Second"));
        var candidates = await store.GetCandidatesAsync();

        Assert.True(first);
        Assert.False(second);
        var candidate = Assert.Single(candidates);
        Assert.Equal("First", candidate.Title);
    }

    [Fact]
    public async Task Candidate_ShouldPersistAfterReloadingStore()
    {
        var store = CreateStore();
        await store.AddCandidateAsync(CreateCandidate("BV001"));

        var reloaded = CreateStore();
        var candidates = await reloaded.GetCandidatesAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal("BV001", candidate.Bvid);
    }

    [Fact]
    public async Task GetPendingCandidatesAsync_ShouldSkipSuccessfulLedgerBvidWhenEnabled()
    {
        var store = CreateStore(skipCommentedBvid: true);
        await store.AddCandidateAsync(CreateCandidate("BV001"));
        await store.AddCandidateAsync(CreateCandidate("BV002"));
        await store.AddLedgerRecordAsync(
            new ProductCommentLedgerRecord
            {
                Id = "ledger-1",
                Bvid = "BV001",
                Aid = 1,
                Status = "success",
                CommentText = "done",
                CreatedAt = DateTimeOffset.UtcNow,
                PublishedAt = DateTimeOffset.UtcNow,
            }
        );

        var pending = await store.GetPendingCandidatesAsync();

        var candidate = Assert.Single(pending);
        Assert.Equal("BV002", candidate.Bvid);
    }

    [Fact]
    public async Task AccountState_ShouldPersistTodayCountAndCooldown()
    {
        var nextAvailableAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var store = CreateStore();
        await store.SaveAccountStateAsync(
            new ProductCommentAccountState
            {
                AccountUserId = "10001",
                TodayCount = 3,
                StatDate = DateOnly.FromDateTime(DateTime.UtcNow),
                LastUsedAt = DateTimeOffset.UtcNow,
                NextAvailableAt = nextAvailableAt,
            }
        );

        var reloaded = CreateStore();
        var state = await reloaded.GetAccountStateAsync("10001");

        Assert.NotNull(state);
        Assert.Equal(3, state!.TodayCount);
        Assert.Equal(nextAvailableAt, state.NextAvailableAt);
    }

    [Fact]
    public async Task GetAccountStatesAsync_ShouldReturnAllSavedStates()
    {
        var store = CreateStore();
        await store.SaveAccountStateAsync(
            new ProductCommentAccountState
            {
                AccountUserId = "10001",
                TodayCount = 1,
                StatDate = DateOnly.FromDateTime(DateTime.UtcNow),
            }
        );
        await store.SaveAccountStateAsync(
            new ProductCommentAccountState
            {
                AccountUserId = "10002",
                TodayCount = 2,
                StatDate = DateOnly.FromDateTime(DateTime.UtcNow),
            }
        );

        var states = await store.GetAccountStatesAsync();

        Assert.Collection(
            states.OrderBy(x => x.AccountUserId),
            x => Assert.Equal("10001", x.AccountUserId),
            x => Assert.Equal("10002", x.AccountUserId)
        );
    }

    [Fact]
    public async Task UpdateCandidateStatusAsync_ShouldUpdateExistingCandidate()
    {
        var store = CreateStore();
        await store.AddCandidateAsync(CreateCandidate("BV001"));

        var updated = await store.UpdateCandidateStatusAsync("bv001", "skipped", "not relevant");
        var candidate = Assert.Single(await store.GetCandidatesAsync());

        Assert.True(updated);
        Assert.Equal("skipped", candidate.Status);
        Assert.Equal("not relevant", candidate.LastError);
        Assert.True(candidate.UpdatedAt >= candidate.CreatedAt);
    }

    [Fact]
    public async Task UpdateCandidateStatusAsync_ShouldReturnFalseWhenCandidateMissing()
    {
        var store = CreateStore();

        var updated = await store.UpdateCandidateStatusAsync("BV404", "skipped", "missing");

        Assert.False(updated);
        Assert.Empty(await store.GetCandidatesAsync());
    }

    [Fact]
    public async Task GetTemplatesAsync_ShouldPreferConfigTemplatesTrimAndDeduplicate()
    {
        var store = CreateStore(
            templates: ["  hello {product}  ", "", "hello {product}", "second template"]
        );

        await store.SaveTemplateAsync(
            new ProductCommentTemplate
            {
                Id = "stored",
                Text = "stored template",
                CreatedAt = DateTimeOffset.UtcNow,
            }
        );

        var templates = await store.GetTemplatesAsync();

        Assert.Collection(
            templates,
            x => Assert.Equal("hello {product}", x.Text),
            x => Assert.Equal("second template", x.Text)
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private IProductCommentStore CreateStore(
        bool skipCommentedBvid = true,
        List<string>? templates = null
    )
    {
        var options = new ProductCommentTaskOptions
        {
            StoragePath = _storagePath,
            SkipCommentedBvid = skipCommentedBvid,
            Templates = templates ?? [],
        };

        return new ProductCommentJsonStore(
            new StaticOptionsMonitor<ProductCommentTaskOptions>(options)
        );
    }

    private static ProductCommentCandidate CreateCandidate(string bvid, string title = "First")
    {
        return new ProductCommentCandidate
        {
            Bvid = bvid,
            Aid = 1,
            Title = title,
            Author = "UP",
            Keyword = "keyword",
        };
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
