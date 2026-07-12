using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService;

public class ProductCommentSearchDomainService(
    ILogger<ProductCommentSearchDomainService> logger,
    IOptionsMonitor<ProductCommentTaskOptions> options,
    ISearchApi searchApi
) : IProductCommentSearchDomainService
{
    public async Task<IReadOnlyList<SearchVideoItemDto>> SearchVideosAsync(
        string keyword,
        BiliCookie ck
    )
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            logger.LogWarning("[ProductComment] Search keyword is empty, skip search.");
            return [];
        }

        var taskOptions = options.CurrentValue;
        var maxPages = Math.Max(1, taskOptions.MaxSearchPagesPerKeyword);
        var maxVideos = Math.Max(0, taskOptions.MaxVideosPerKeyword);
        if (maxVideos == 0)
        {
            logger.LogWarning("[ProductComment] MaxVideosPerKeyword is 0, skip search.");
            return [];
        }

        var result = new List<SearchVideoItemDto>();
        var seenBvids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= maxPages && result.Count < maxVideos; page++)
        {
            var response = await SearchPageAsync(keyword, page, taskOptions.SearchOrder, ck);
            if (response.Code != 0)
            {
                logger.LogWarning(
                    "[ProductComment] Search API failed. Keyword={keyword}, Page={page}, Code={code}, Message={message}",
                    keyword,
                    page,
                    response.Code,
                    response.Message
                );
                return [];
            }

            foreach (var item in response.Data.Result)
            {
                if (result.Count >= maxVideos)
                    break;

                if (string.IsNullOrWhiteSpace(item.Bvid))
                    continue;

                if (!seenBvids.Add(item.Bvid))
                    continue;

                item.Title = WebUtility.HtmlDecode(item.Title ?? "");

                if (item.Aid == 0)
                    item.Aid = await GetAidByBvidAsync(item.Bvid, ck);

                if (item.Aid == 0)
                    continue;

                result.Add(item);
            }
        }

        return result;
    }

    private Task<BiliApiResponse<SearchVideoResultDto>> SearchPageAsync(
        string keyword,
        int page,
        string order,
        BiliCookie ck
    )
    {
        var request = new SearchVideoRequestDto
        {
            keyword = keyword,
            page = page,
            order = string.IsNullOrWhiteSpace(order) ? "pubdate" : order,
        };

        return searchApi.SearchVideosAsync(request, ck.ToString());
    }

    private async Task<long> GetAidByBvidAsync(string bvid, BiliCookie ck)
    {
        var response = await searchApi.GetVideoDetailByBvidAsync(bvid, ck.ToString());
        if (response.Code != 0)
        {
            logger.LogWarning(
                "[ProductComment] Get video detail failed. Bvid={bvid}, Code={code}, Message={message}",
                bvid,
                response.Code,
                response.Message
            );
            return 0;
        }

        return response.Data.Aid;
    }
}
