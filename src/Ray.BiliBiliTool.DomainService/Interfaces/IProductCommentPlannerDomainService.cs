using Ray.BiliBiliTool.DomainService.Dtos;

namespace Ray.BiliBiliTool.DomainService.Interfaces;

public interface IProductCommentPlannerDomainService : IDomainService
{
    Task<IReadOnlyList<ProductCommentPlanItem>> BuildPlanAsync(
        string accountUserId,
        DateTimeOffset now
    );
}
