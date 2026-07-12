namespace Ray.BiliBiliTool.Config.Options;

public class LocalWorkbenchOptions : BaseConfigOptions
{
    public override string SectionName => "LocalWorkbench";

    public string FrontendApiBaseUrl { get; set; } = "";

    public string BackendBaseUrl { get; set; } = "";

    public string BrowserPath { get; set; } = "";

    public string BrowserUserDataRoot { get; set; } = "./data/profiles";

    public string DatabasePath { get; set; } = "./data/BiliBiliTool.db";

    public string LogDirectory { get; set; } = "./data/Logs";

    public string TemplateDirectory { get; set; } = "./data/templates";

    public string TaskResultDirectory { get; set; } = "./data/outputs";

    public string WorkflowStorePath { get; set; } = "./data/maintenance-workflows.json";

    public string CommentStrategyStorePath { get; set; } = "./data/comment-strategy.json";

    public int WebPort { get; set; } = 5101;

    public int ExecutorPort { get; set; } = 8123;

    public bool IsInitialized { get; set; }

    public override Dictionary<string, string> ToConfigDictionary()
    {
        return MergeConfigDictionary(
            new Dictionary<string, string>
            {
                { $"{SectionName}:{nameof(FrontendApiBaseUrl)}", FrontendApiBaseUrl },
                { $"{SectionName}:{nameof(BackendBaseUrl)}", BackendBaseUrl },
                { $"{SectionName}:{nameof(BrowserPath)}", BrowserPath },
                { $"{SectionName}:{nameof(BrowserUserDataRoot)}", BrowserUserDataRoot },
                { $"{SectionName}:{nameof(DatabasePath)}", DatabasePath },
                { $"{SectionName}:{nameof(LogDirectory)}", LogDirectory },
                { $"{SectionName}:{nameof(TemplateDirectory)}", TemplateDirectory },
                { $"{SectionName}:{nameof(TaskResultDirectory)}", TaskResultDirectory },
                { $"{SectionName}:{nameof(WorkflowStorePath)}", WorkflowStorePath },
                { $"{SectionName}:{nameof(CommentStrategyStorePath)}", CommentStrategyStorePath },
                { $"{SectionName}:{nameof(WebPort)}", WebPort.ToString() },
                { $"{SectionName}:{nameof(ExecutorPort)}", ExecutorPort.ToString() },
                {
                    $"{SectionName}:{nameof(IsInitialized)}",
                    IsInitialized.ToString().ToLowerInvariant()
                },
            }
        );
    }
}
