namespace Ray.BiliBiliTool.Application.Contracts.ContentAutomation;

public class ContentAutomationStrategy
{
    public int Version { get; set; } = 1;

    public string CommentMode { get; set; } = "template";

    public string LibraryId { get; set; } = "";

    public IReadOnlyList<string> Keywords { get; set; } = [];

    public IReadOnlyList<string> AccountIds { get; set; } = [];

    public string Rules { get; set; } = "";

    public string Year { get; set; } = DateTime.Now.Year.ToString();

    public int SearchLimit { get; set; } = 10;

    public int PublishLimit { get; set; } = 5;

    public int DurationMinutes { get; set; } = 60;

    public int CycleIntervalMinutes { get; set; } = 30;

    public int CycleIntervalSeconds { get; set; }

    public int AccountIntervalMinSeconds { get; set; } = 480;

    public int AccountIntervalMaxSeconds { get; set; } = 900;

    public int SameAccountCooldownMinSeconds { get; set; } = 1200;

    public int SameAccountCooldownMaxSeconds { get; set; } = 2400;
}

public interface IContentAutomationStrategyService
{
    Task<ContentAutomationStrategy> GetAsync(CancellationToken cancellationToken = default);

    Task<ContentAutomationStrategy> SaveAsync(
        ContentAutomationStrategy strategy,
        CancellationToken cancellationToken = default
    );

    Task<bool> RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default);
}
