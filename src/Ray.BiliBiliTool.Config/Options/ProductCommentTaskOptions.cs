using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ray.BiliBiliTool.Config.Options;

/// <summary>
/// 商品评论推广任务配置
/// </summary>
public class ProductCommentTaskOptions : BaseConfigOptions
{
    public override string SectionName => "ProductCommentTaskConfig";

    // ---- 安全默认值 ----
    // IsEnable 继承自 BaseConfigOptions（默认 true），
    // 构造函数中显式覆盖为 false，确保即使缺少 appsettings.json 配置节也不会误启用。

    /// <summary>预演模式（默认 true，不产生真实发布）</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>是否允许自动发布（默认 false）</summary>
    public bool EnableAutoPublish { get; set; } = false;

    /// <summary>发布模式: DryRun | Manual | AutoApi</summary>
    public string PublishMode { get; set; } = "DryRun";

    // ---- 搜索配置 ----

    /// <summary>搜索关键词列表</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>搜索排序方式（pubdate=按发布时间）</summary>
    public string SearchOrder { get; set; } = "pubdate";

    /// <summary>每个关键词最多搜索的页数</summary>
    public int MaxSearchPagesPerKeyword { get; set; } = 2;

    /// <summary>每个关键词最多保留的视频数</summary>
    public int MaxVideosPerKeyword { get; set; } = 5;

    /// <summary>每次运行最多处理的候选视频数</summary>
    public int MaxCandidatesPerRun { get; set; } = 20;

    // ---- 评论模板 ----

    /// <summary>评论模板列表（支持占位符 {产品名}、{视频标题}、{UP主}）</summary>
    public List<string> Templates { get; set; } = [];

    // ---- 发布限流 ----

    /// <summary>每次运行最多发布的评论数</summary>
    public int MaxPublishPerRun { get; set; } = 5;

    /// <summary>每个账号每天最大评论数</summary>
    public int MaxDailyCommentsPerAccount { get; set; } = 10;

    /// <summary>评论间隔最小秒数</summary>
    public int CommentIntervalMinSeconds { get; set; } = 60;

    /// <summary>评论间隔最大秒数</summary>
    public int CommentIntervalMaxSeconds { get; set; } = 180;

    /// <summary>同一账号冷却时间（分钟）</summary>
    public int AccountCooldownMinutes { get; set; } = 30;

    // ---- 持久化 ----

    /// <summary>本地存储路径</summary>
    public string StoragePath { get; set; } = "Data/product-comment.json";

    /// <summary>跳过已处理的 BVID</summary>
    public bool SkipAlreadyProcessedBvid { get; set; } = true;

    /// <summary>跳过已评论的 BVID</summary>
    public bool SkipCommentedBvid { get; set; } = true;

    // ---- 构造函数 ----

    public ProductCommentTaskOptions()
    {
        // 安全默认值：覆盖 BaseConfigOptions.IsEnable 的 true 默认值
        // 即使 appsettings.json 缺少 ProductCommentTaskConfig 节也不会误启用
        IsEnable = false;
    }

    // ---- 序列化到 SQLite 配置字典 ----
    // 被 Web 项目的 BaseConfigComponent 通过 sqliteProvider.BatchSet() 调用

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public override Dictionary<string, string> ToConfigDictionary()
    {
        return MergeConfigDictionary(
            new Dictionary<string, string>
            {
                { $"{SectionName}:{nameof(DryRun)}", DryRun.ToString().ToLower() },
                {
                    $"{SectionName}:{nameof(EnableAutoPublish)}",
                    EnableAutoPublish.ToString().ToLower()
                },
                { $"{SectionName}:{nameof(PublishMode)}", PublishMode },
                {
                    $"{SectionName}:{nameof(Keywords)}",
                    JsonSerializer.Serialize(Keywords, _jsonOptions)
                },
                { $"{SectionName}:{nameof(SearchOrder)}", SearchOrder },
                {
                    $"{SectionName}:{nameof(MaxSearchPagesPerKeyword)}",
                    MaxSearchPagesPerKeyword.ToString()
                },
                { $"{SectionName}:{nameof(MaxVideosPerKeyword)}", MaxVideosPerKeyword.ToString() },
                { $"{SectionName}:{nameof(MaxCandidatesPerRun)}", MaxCandidatesPerRun.ToString() },
                {
                    $"{SectionName}:{nameof(Templates)}",
                    JsonSerializer.Serialize(Templates, _jsonOptions)
                },
                { $"{SectionName}:{nameof(MaxPublishPerRun)}", MaxPublishPerRun.ToString() },
                {
                    $"{SectionName}:{nameof(MaxDailyCommentsPerAccount)}",
                    MaxDailyCommentsPerAccount.ToString()
                },
                {
                    $"{SectionName}:{nameof(CommentIntervalMinSeconds)}",
                    CommentIntervalMinSeconds.ToString()
                },
                {
                    $"{SectionName}:{nameof(CommentIntervalMaxSeconds)}",
                    CommentIntervalMaxSeconds.ToString()
                },
                {
                    $"{SectionName}:{nameof(AccountCooldownMinutes)}",
                    AccountCooldownMinutes.ToString()
                },
                { $"{SectionName}:{nameof(StoragePath)}", StoragePath },
                {
                    $"{SectionName}:{nameof(SkipAlreadyProcessedBvid)}",
                    SkipAlreadyProcessedBvid.ToString().ToLower()
                },
                {
                    $"{SectionName}:{nameof(SkipCommentedBvid)}",
                    SkipCommentedBvid.ToString().ToLower()
                },
            }
        );
    }
}
