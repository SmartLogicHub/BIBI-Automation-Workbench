using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Console;
using Ray.BiliBiliTool.Infrastructure;
using Ray.BiliBiliTool.Infrastructure.Cookie;
using Xunit;

namespace BiliAgentTest;

public class VideoApiTest
{
    public VideoApiTest()
    {
        Program.CreateHost(new[] { "--ENVIRONMENT=Development" });
    }

    [LiveBilibiliFact]
    public void GetLiveWalletStatus_Normal_Success()
    {
        using var scope = Global.ServiceProviderRoot.CreateScope();

        var ck = scope.ServiceProvider.GetRequiredService<CookieStrFactory<BiliCookie>>();
        var api = scope.ServiceProvider.GetRequiredService<IVideoApi>();

        var req = new GetAlreadyDonatedCoinsRequest(248097491);
        BiliApiResponse<DonatedCoinsForVideo>? re = api.GetDonatedCoinsForVideo(req, null).Result;

        if (ck.Count > 0)
        {
            Assert.True(re.Code == 0 && re.Data.Multiply >= 0);
        }
        else
        {
            Assert.False(re.Code != 0);
        }
    }

    [LiveBilibiliFact]
    public async Task GetBangumiTest()
    {
        using var scope = Global.ServiceProviderRoot.CreateScope();

        var ck = scope.ServiceProvider.GetRequiredService<CookieStrFactory<BiliCookie>>();
        var api = scope.ServiceProvider.GetRequiredService<IVideoApi>();
        var req = await api.GetBangumiBySsid(46508, null);

        Assert.Equal(0, req.Code);
    }

    [LiveBilibiliFact]
    public async Task GetRandomVideoOfRanking()
    {
        using var scope = Global.ServiceProviderRoot.CreateScope();

        var ck = scope.ServiceProvider.GetRequiredService<CookieStrFactory<BiliCookie>>();
        var api = scope.ServiceProvider.GetRequiredService<IVideoWithoutCookieApi>();
        var req = await api.GetRegionRankingVideosV2();

        Assert.Equal(0, req.Code);
    }
}
