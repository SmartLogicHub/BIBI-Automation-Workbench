namespace Ray.BiliBiliTool.Config.Options;

public class ContentAutomationBridgeOptions : BaseConfigOptions
{
    public override string SectionName => "ContentAutomationBridge";

    public string BaseUrl { get; set; } = "http://127.0.0.1:8123";

    public string ExecutorPath { get; set; } = "";

    public int RequestTimeoutSeconds { get; set; } = 60;

    public int DefaultSearchLimit { get; set; } = 10;

    public int DefaultMaxCount { get; set; } = 5;

    public int DefaultDailyLimit { get; set; } = 20;

    public int LogRetentionDays { get; set; } = 30;

    public ContentAutomationBridgeOptions()
    {
        IsEnable = true;
    }

    public override Dictionary<string, string> ToConfigDictionary()
    {
        return MergeConfigDictionary(
            new Dictionary<string, string>
            {
                { $"{SectionName}:{nameof(BaseUrl)}", BaseUrl },
                { $"{SectionName}:{nameof(ExecutorPath)}", ExecutorPath },
                {
                    $"{SectionName}:{nameof(RequestTimeoutSeconds)}",
                    RequestTimeoutSeconds.ToString()
                },
                { $"{SectionName}:{nameof(DefaultSearchLimit)}", DefaultSearchLimit.ToString() },
                { $"{SectionName}:{nameof(DefaultMaxCount)}", DefaultMaxCount.ToString() },
                { $"{SectionName}:{nameof(DefaultDailyLimit)}", DefaultDailyLimit.ToString() },
                { $"{SectionName}:{nameof(LogRetentionDays)}", LogRetentionDays.ToString() },
            }
        );
    }
}
