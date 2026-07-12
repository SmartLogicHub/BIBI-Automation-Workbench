using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Infrastructure.Cookie;

namespace AppServiceTest;

public class BaseMultiAccountsAppServiceTest
{
    [Fact]
    public async Task DoTaskForAccountAsync_ShouldExecuteOnlyTheRequestedAccount()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BiliBiliCookies:0"] = "DedeUserID=100; SESSDATA=first; bili_jct=first-csrf",
                    ["BiliBiliCookies:1"] = "DedeUserID=200; SESSDATA=second; bili_jct=second-csrf",
                }
            )
            .Build();
        var service = new RecordingMultiAccountService(
            NullLogger<RecordingMultiAccountService>.Instance,
            new CookieStrFactory<BiliCookie>(configuration)
        );
        var selectedAccount = CookieStrFactory<BiliCookie>.CreateNew(
            "DedeUserID=200; SESSDATA=second; bili_jct=second-csrf"
        );

        await service.DoTaskForAccountAsync(selectedAccount);

        Assert.Equal(["200"], service.ExecutedUserIds);
    }

    private sealed class RecordingMultiAccountService(
        ILogger<RecordingMultiAccountService> logger,
        CookieStrFactory<BiliCookie> cookieFactory
    ) : BaseMultiAccountsAppService(logger, cookieFactory)
    {
        public List<string> ExecutedUserIds { get; } = [];

        protected override Task DoTaskAccountAsync(
            BiliCookie ck,
            CancellationToken cancellationToken = default
        )
        {
            ExecutedUserIds.Add(ck.UserId);
            return Task.CompletedTask;
        }
    }
}
