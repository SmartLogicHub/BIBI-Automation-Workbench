using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;

namespace Ray.BiliBiliTool.DomainService.Interfaces;

public interface IProductCommentSearchDomainService : IDomainService
{
    Task<IReadOnlyList<SearchVideoItemDto>> SearchVideosAsync(string keyword, BiliCookie ck);
}
