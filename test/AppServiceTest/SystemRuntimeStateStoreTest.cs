using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Application.Contracts.Runtime;

namespace AppServiceTest;

public class SystemRuntimeStateStoreTest
{
    [Fact]
    public void Update_ShouldPublishSnapshotWithoutLeakingMutableState()
    {
        var store = new SystemRuntimeStateStore();
        SystemRuntimeState? published = null;
        store.Changed += state => published = state;

        var input = new SystemRuntimeState
        {
            Status = SystemRuntimeStatus.Ready,
            Message = "运行正常",
            AvailableAccounts = 3,
            TodayComments = 8,
        };

        store.Update(input);
        input.Message = "已被外部修改";

        Assert.NotNull(published);
        Assert.Equal(SystemRuntimeStatus.Ready, store.Current.Status);
        Assert.Equal("运行正常", store.Current.Message);
        Assert.Equal(3, published!.AvailableAccounts);
        Assert.Equal(8, published.TodayComments);
    }

    [Fact]
    public void NewStore_ShouldExposeStartingSnapshotImmediately()
    {
        var store = new SystemRuntimeStateStore();

        Assert.Equal(SystemRuntimeStatus.Starting, store.Current.Status);
        Assert.False(store.Current.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(store.Current.Message));
    }
}
