using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;

namespace AppServiceTest;

public class ContentAutomationStrategyServiceTest
{
    [Fact]
    public async Task SaveAsync_ShouldPersistNormalizedStrategyAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"bibi-strategy-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "comment-strategy.json");

        try
        {
            var writer = new ContentAutomationStrategyService(path);
            var saved = await writer.SaveAsync(
                new ContentAutomationStrategy
                {
                    CommentMode = " deepseek ",
                    LibraryId = " library-1 ",
                    Keywords = [" 耳机 ", "耳机", "", "降噪"],
                    AccountIds = [" 1001 ", "1001", "1002"],
                    Rules = " 只生成自然短评 ",
                    SearchLimit = 999,
                    PublishLimit = 0,
                    DurationMinutes = -5,
                    CycleIntervalMinutes = 0,
                    CycleIntervalSeconds = 75,
                    AccountIntervalMinSeconds = 15,
                    AccountIntervalMaxSeconds = 45,
                    SameAccountCooldownMinSeconds = 60,
                    SameAccountCooldownMaxSeconds = 180,
                }
            );

            var reader = new ContentAutomationStrategyService(path);
            var loaded = await reader.GetAsync();

            Assert.Equal("deepseek", saved.CommentMode);
            Assert.Equal(["耳机", "降噪"], loaded.Keywords);
            Assert.Equal(["1001", "1002"], loaded.AccountIds);
            Assert.Equal("library-1", loaded.LibraryId);
            Assert.Equal("只生成自然短评", loaded.Rules);
            Assert.InRange(loaded.SearchLimit, 1, 50);
            Assert.InRange(loaded.PublishLimit, 1, 50);
            Assert.True(loaded.DurationMinutes >= 1);
            Assert.True(loaded.CycleIntervalMinutes >= 1);
            Assert.Equal(75, loaded.CycleIntervalSeconds);
            Assert.Equal(15, loaded.AccountIntervalMinSeconds);
            Assert.Equal(45, loaded.AccountIntervalMaxSeconds);
            Assert.Equal(60, loaded.SameAccountCooldownMinSeconds);
            Assert.Equal(180, loaded.SameAccountCooldownMaxSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_ShouldReturnUsableDefaultsWhenStoreDoesNotExist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "strategy.json");
        var service = new ContentAutomationStrategyService(path);

        var strategy = await service.GetAsync();

        Assert.Equal("template", strategy.CommentMode);
        Assert.True(strategy.SearchLimit > 0);
        Assert.True(strategy.PublishLimit > 0);
        Assert.True(strategy.DurationMinutes > 0);
        Assert.True(strategy.CycleIntervalMinutes > 0);
        Assert.True(strategy.CycleIntervalSeconds > 0);
        Assert.True(strategy.AccountIntervalMinSeconds > 0);
        Assert.True(strategy.AccountIntervalMaxSeconds >= strategy.AccountIntervalMinSeconds);
        Assert.True(strategy.SameAccountCooldownMinSeconds > 0);
        Assert.True(
            strategy.SameAccountCooldownMaxSeconds >= strategy.SameAccountCooldownMinSeconds
        );
    }

    [Fact]
    public async Task RemoveAccountAsync_ShouldPersistentlyPruneOnlyTheDeletedAccount()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"bibi-strategy-remove-{Guid.NewGuid():N}"
        );
        var path = Path.Combine(directory, "comment-strategy.json");

        try
        {
            var service = new ContentAutomationStrategyService(path);
            await service.SaveAsync(
                new ContentAutomationStrategy { AccountIds = ["account-1", "account-2"] }
            );

            var changed = await service.RemoveAccountAsync("account-1");
            var loaded = await new ContentAutomationStrategyService(path).GetAsync();

            Assert.True(changed);
            Assert.Equal(["account-2"], loaded.AccountIds);
            Assert.False(await service.RemoveAccountAsync("missing-account"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
