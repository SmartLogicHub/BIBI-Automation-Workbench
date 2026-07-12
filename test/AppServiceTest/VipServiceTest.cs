using Microsoft.Extensions.DependencyInjection;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.VipTask;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Application.Contracts;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.Infrastructure;

namespace AppServiceTest;

public sealed class LiveBilibiliFactAttribute : FactAttribute
{
    public LiveBilibiliFactAttribute()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("BIBI_RUN_LIVE_TESTS"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            Skip = "Set BIBI_RUN_LIVE_TESTS=1 to run tests that call the live Bilibili account.";
        }
    }
}

public class VipServiceTest
{
    public VipServiceTest()
    {
        Program.CreateHost(new[] { "--ENVIRONMENT=Development" });
    }

    [LiveBilibiliFact]
    public async Task CompleteV2Test()
    {
        using var scope = Global.ServiceProviderRoot.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IVipBigPointApi>();
        var res = await api.CompleteV2(new ReceiveOrCompleteTaskRequest("dress-view"), null);
        Assert.True(res.Code == 0);
    }

    [LiveBilibiliFact]
    public async Task ReceiveV2Test()
    {
        using var scope = Global.ServiceProviderRoot.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IVipBigPointApi>();
        var res = await api.ReceiveV2(new ReceiveOrCompleteTaskRequest("ogvwatchnew"), null);
        Assert.True(res.Code == 0);
    }
}
