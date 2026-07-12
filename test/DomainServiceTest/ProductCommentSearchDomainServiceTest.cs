using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ProductComment;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace DomainServiceTest;

public class ProductCommentSearchDomainServiceTest
{
    [Fact]
    public async Task SearchVideosAsync_ShouldDecodeTitleDeduplicateAndFillMissingAid()
    {
        var api = new FakeSearchApi();
        api.SearchResponses.Enqueue(
            Success(
                new SearchVideoResultDto
                {
                    Result =
                    [
                        new SearchVideoItemDto
                        {
                            Aid = 0,
                            Bvid = "BV100",
                            Title = "原子豆 &amp; 耳机",
                            Author = "UP1",
                            Duration = "01:02",
                        },
                        new SearchVideoItemDto
                        {
                            Aid = 123,
                            Bvid = "BV100",
                            Title = "重复视频",
                            Author = "UP1",
                        },
                        new SearchVideoItemDto
                        {
                            Aid = 456,
                            Bvid = "",
                            Title = "缺少 BVID",
                        },
                    ],
                }
            )
        );
        api.DetailResponses["BV100"] = Success(
            new VideoDetailByBvidDto
            {
                Aid = 789,
                Bvid = "BV100",
                Title = "详情标题",
            }
        );

        var service = CreateService(api, maxPages: 1, maxVideos: 5);

        var result = await service.SearchVideosAsync("耳机", CreateCookie());

        var item = Assert.Single(result);
        Assert.Equal("BV100", item.Bvid);
        Assert.Equal(789, item.Aid);
        Assert.Equal("原子豆 & 耳机", item.Title);
        Assert.Equal("UP1", item.Author);
        Assert.Single(api.SearchRequests);
        Assert.Single(api.DetailRequests);
    }

    [Fact]
    public async Task SearchVideosAsync_ShouldStopWhenMaxVideosPerKeywordReached()
    {
        var api = new FakeSearchApi();
        api.SearchResponses.Enqueue(
            Success(
                new SearchVideoResultDto
                {
                    Result =
                    [
                        new SearchVideoItemDto
                        {
                            Aid = 1,
                            Bvid = "BV001",
                            Title = "标题1",
                        },
                        new SearchVideoItemDto
                        {
                            Aid = 2,
                            Bvid = "BV002",
                            Title = "标题2",
                        },
                    ],
                }
            )
        );
        api.SearchResponses.Enqueue(
            Success(
                new SearchVideoResultDto
                {
                    Result =
                    [
                        new SearchVideoItemDto
                        {
                            Aid = 3,
                            Bvid = "BV003",
                            Title = "标题3",
                        },
                    ],
                }
            )
        );

        var service = CreateService(api, maxPages: 3, maxVideos: 2);

        var result = await service.SearchVideosAsync("耳机", CreateCookie());

        Assert.Collection(
            result,
            x => Assert.Equal("BV001", x.Bvid),
            x => Assert.Equal("BV002", x.Bvid)
        );
        Assert.Single(api.SearchRequests);
        Assert.Equal(1, api.SearchRequests[0].page);
    }

    [Fact]
    public async Task SearchVideosAsync_ShouldReturnEmptyWhenBiliApiFails()
    {
        var api = new FakeSearchApi();
        api.SearchResponses.Enqueue(
            new BiliApiResponse<SearchVideoResultDto>
            {
                Code = -400,
                Message = "bad request",
                Data = new SearchVideoResultDto(),
            }
        );

        var service = CreateService(api, maxPages: 2, maxVideos: 5);

        var result = await service.SearchVideosAsync("耳机", CreateCookie());

        Assert.Empty(result);
    }

    private static IProductCommentSearchDomainService CreateService(
        ISearchApi api,
        int maxPages,
        int maxVideos
    )
    {
        var options = new ProductCommentTaskOptions
        {
            MaxSearchPagesPerKeyword = maxPages,
            MaxVideosPerKeyword = maxVideos,
        };

        return new ProductCommentSearchDomainService(
            NullLogger<ProductCommentSearchDomainService>.Instance,
            new StaticOptionsMonitor<ProductCommentTaskOptions>(options),
            api
        );
    }

    private static BiliCookie CreateCookie()
    {
        return new BiliCookie(
            new Dictionary<string, string>
            {
                ["DedeUserID"] = "1",
                ["SESSDATA"] = "sess",
                ["bili_jct"] = "csrf",
            }
        );
    }

    private static BiliApiResponse<T> Success<T>(T data)
    {
        return new BiliApiResponse<T>
        {
            Code = 0,
            Message = "0",
            Data = data,
        };
    }

    private sealed class FakeSearchApi : ISearchApi
    {
        public Queue<BiliApiResponse<SearchVideoResultDto>> SearchResponses { get; } = new();

        public Dictionary<string, BiliApiResponse<VideoDetailByBvidDto>> DetailResponses { get; } =
            new();

        public List<SearchVideoRequestDto> SearchRequests { get; } = [];

        public List<string> DetailRequests { get; } = [];

        public Task<BiliApiResponse<SearchVideoResultDto>> SearchVideosAsync(
            SearchVideoRequestDto request,
            string ck
        )
        {
            SearchRequests.Add(request);
            return Task.FromResult(SearchResponses.Dequeue());
        }

        public Task<BiliApiResponse<VideoDetailByBvidDto>> GetVideoDetailByBvidAsync(
            string bvid,
            string ck
        )
        {
            DetailRequests.Add(bvid);
            return Task.FromResult(DetailResponses[bvid]);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
