using Quartz;
using Ray.BiliBiliTool.Application.Contracts;

namespace Ray.BiliBiliTool.Web.Jobs;

public class ProductCommentJob(
    ILogger<ProductCommentJob> logger,
    IProductCommentTaskAppService appService
) : BaseJob<ProductCommentJob>(logger)
{
    private readonly ILogger<ProductCommentJob> _logger = logger;
    public static readonly JobKey Key = new(nameof(ProductCommentJob), Constants.BiliJobGroup);

    protected override async Task DoExecuteAsync(IJobExecutionContext context)
    {
        _logger.LogInformation($"{nameof(ProductCommentJob)} started.");
        await appService.DoTaskAsync();
    }
}
