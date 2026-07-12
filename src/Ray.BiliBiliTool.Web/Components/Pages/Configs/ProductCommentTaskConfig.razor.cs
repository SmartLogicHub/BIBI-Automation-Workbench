using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Quartz;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Web.Jobs;

namespace Ray.BiliBiliTool.Web.Components.Pages.Configs;

public partial class ProductCommentTaskConfig : BaseConfigComponent<ProductCommentTaskOptions>
{
    private const string SafePublishMode = "DryRun";
    private const string DefaultDisabledCron = "0 0 0 1 1 ?";

    [Inject]
    private IOptionsMonitor<ProductCommentTaskOptions> ProductCommentTaskOptionsMonitor { get; set; } =
        null!;

    protected override IOptionsMonitor<ProductCommentTaskOptions> OptionsMonitor =>
        ProductCommentTaskOptionsMonitor;

    private string _keywordsText = "";
    private string _templatesText = "";

    protected override JobKey GetJobKey() => ProductCommentJob.Key;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        ApplySafeDefaults();
        SyncTextFieldsFromConfig();
    }

    protected override async Task HandleValidSubmitAsync()
    {
        ApplyTextFieldsToConfig();
        ApplySafeDefaults();
        await base.HandleValidSubmitAsync();
        SyncTextFieldsFromConfig();
    }

    private async Task ReloadConfigAsync()
    {
        await LoadConfigAsync();
        ApplySafeDefaults();
        SyncTextFieldsFromConfig();
    }

    private void ApplySafeDefaults()
    {
        _config.DryRun = true;
        _config.EnableAutoPublish = false;
        _config.PublishMode = SafePublishMode;
        _config.Cron = string.IsNullOrWhiteSpace(_config.Cron)
            ? DefaultDisabledCron
            : _config.Cron.Trim();
    }

    private void SyncTextFieldsFromConfig()
    {
        _keywordsText = string.Join(Environment.NewLine, _config.Keywords);
        _templatesText = string.Join(Environment.NewLine, _config.Templates);
    }

    private void ApplyTextFieldsToConfig()
    {
        _config.Keywords = NormalizeKeywords(_keywordsText).ToList();
        _config.Templates = NormalizeTemplates(_templatesText).ToList();
    }

    private static IEnumerable<string> NormalizeKeywords(string value)
    {
        return value
            .Split(
                ['\r', '\n', ',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> NormalizeTemplates(string value)
    {
        return value
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal);
    }
}
