using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Infrastructure;

namespace Ray.BiliBiliTool.Config.Extensions;

public static class ServiceCollectionExtension
{
    /// <summary>
    /// 注册配置
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddBiliBiliConfigs(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        //Options
        services
            .AddOptions()
            .Configure<JsonSerializerOptions>(o => o = JsonSerializerOptionsBuilder.DefaultOptions)
            .Configure<BiliBiliCookieOptions>(configuration.GetSection("BiliBiliCookie"))
            .Configure<DailyTaskOptions>(configuration.GetSection("DailyTaskConfig"))
            .Configure<MangaTaskOptions>(configuration.GetSection("MangaTaskConfig"))
            .Configure<MangaPrivilegeTaskOptions>(
                configuration.GetSection("MangaPrivilegeTaskConfig")
            )
            .Configure<Silver2CoinTaskOptions>(configuration.GetSection("Silver2CoinTaskConfig"))
            .Configure<ChargeTaskOptions>(configuration.GetSection("ChargeTaskConfig"))
            .Configure<LiveLotteryTaskOptions>(configuration.GetSection("LiveLotteryTaskConfig"))
            .Configure<UnfollowBatchedTaskOptions>(
                configuration.GetSection("UnfollowBatchedTaskConfig")
            )
            .Configure<VipBigPointOptions>(configuration.GetSection("VipBigPointConfig"))
            .Configure<SecurityOptions>(configuration.GetSection("Security"))
            .Configure<VipPrivilegeOptions>(configuration.GetSection("VipPrivilegeConfig"))
            .Configure<LiveFansMedalTaskOptions>(
                configuration.GetSection("LiveFansMedalTaskConfig")
            )
            .Configure<QingLongOptions>(configuration.GetSection("QingLongConfig"))
            .Configure<ContentAutomationBridgeOptions>(
                configuration.GetSection("ContentAutomationBridge")
            )
            .Configure<LocalWorkbenchOptions>(configuration.GetSection("LocalWorkbench"));

        // ProductCommentTaskOptions 安全校验
        services
            .AddOptions<ProductCommentTaskOptions>()
            .Bind(configuration.GetSection("ProductCommentTaskConfig"))
            .Validate(
                opts => opts.CommentIntervalMaxSeconds >= opts.CommentIntervalMinSeconds,
                "ProductCommentTaskConfig: CommentIntervalMaxSeconds must be >= CommentIntervalMinSeconds"
            )
            .Validate(
                opts => opts.CommentIntervalMinSeconds >= 0,
                "ProductCommentTaskConfig: CommentIntervalMinSeconds must be >= 0"
            )
            .Validate(
                opts => opts.CommentIntervalMaxSeconds >= 0,
                "ProductCommentTaskConfig: CommentIntervalMaxSeconds must be >= 0"
            );
        services
            .AddOptions<ContentAutomationBridgeOptions>()
            .Bind(configuration.GetSection("ContentAutomationBridge"))
            .Validate(
                opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _),
                "ContentAutomationBridge: BaseUrl must be an absolute URL"
            )
            .Validate(
                opts => opts.RequestTimeoutSeconds > 0,
                "ContentAutomationBridge: RequestTimeoutSeconds must be > 0"
            )
            .Validate(
                opts => opts.DefaultSearchLimit > 0,
                "ContentAutomationBridge: DefaultSearchLimit must be > 0"
            )
            .Validate(
                opts => opts.DefaultMaxCount > 0,
                "ContentAutomationBridge: DefaultMaxCount must be > 0"
            )
            .Validate(
                opts => opts.LogRetentionDays > 0,
                "ContentAutomationBridge: LogRetentionDays must be > 0"
            );

        services
            .AddOptions<LocalWorkbenchOptions>()
            .Bind(configuration.GetSection("LocalWorkbench"))
            .Validate(opts => opts.WebPort > 0, "LocalWorkbench: WebPort must be > 0")
            .Validate(opts => opts.ExecutorPort > 0, "LocalWorkbench: ExecutorPort must be > 0");

        return services;
    }
}
