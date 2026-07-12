namespace Ray.BiliBiliTool.Application.Contracts.ContentAutomation;

public class ContentAutomationHealthResult
{
    public bool IsOnline { get; set; }

    public string BaseUrl { get; set; } = "";

    public string Status { get; set; } = "";

    public string Message { get; set; } = "";

    public string Version { get; set; } = "";
}

public class ContentAutomationAccount
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Uid { get; set; } = "";

    public string Nickname { get; set; } = "";

    public string AvatarUrl { get; set; } = "";

    public bool Enabled { get; set; }

    public string UserDataDir { get; set; } = "";

    public int DailyLimit { get; set; }

    public int TodayCount { get; set; }

    public string Status { get; set; } = "";

    public string LoginStatus { get; set; } = "";

    public string LastUsedAt { get; set; } = "";

    public string LastLoginCheckedAt { get; set; } = "";

    public string NextAvailableAt { get; set; } = "";

    public string Note { get; set; } = "";

    public string Proxy { get; set; } = "";

    public string Cookie { get; set; } = "";
}

public class ContentAutomationAccountUpdateRequest
{
    public bool? Enabled { get; set; }

    public int? DailyLimit { get; set; }

    public string Status { get; set; } = "";

    public string Note { get; set; } = "";

    public string Proxy { get; set; } = "";

    public string Cookie { get; set; } = "";

    public bool ClearCookie { get; set; }

    public string LoginStatus { get; set; } = "";
}

public class ContentAutomationAccountCreateRequest
{
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int DailyLimit { get; set; } = 20;

    public string Note { get; set; } = "";
}

public class ContentAutomationAccountUpdateResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = "";

    public ContentAutomationAccount Account { get; set; } = new();
}

public class ContentAutomationVideo
{
    public string Id { get; set; } = "";

    public string Bvid { get; set; } = "";

    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    public string Keyword { get; set; } = "";

    public string Url { get; set; } = "";

    public string Status { get; set; } = "";

    public string CommentText { get; set; } = "";

    public string LastError { get; set; } = "";

    public string CreatedAt { get; set; } = "";
}

public class ContentAutomationLedgerRecord
{
    public string Id { get; set; } = "";

    public string Status { get; set; } = "";

    public string AccountName { get; set; } = "";

    public string AccountId { get; set; } = "";

    public string Bvid { get; set; } = "";

    public string VideoTitle { get; set; } = "";

    public string VideoUrl { get; set; } = "";

    public string CommentText { get; set; } = "";

    public string ErrorMessage { get; set; } = "";

    public string VerificationMethod { get; set; } = "";

    public string ProofText { get; set; } = "";

    public string PlatformCommentId { get; set; } = "";

    public string CommentUrl { get; set; } = "";

    public string VerifiedAt { get; set; } = "";

    public string PublishedAt { get; set; } = "";

    public string CreatedAt { get; set; } = "";
}

public class ContentAutomationLedgerExportResult
{
    public bool Success { get; set; }

    public int Count { get; set; }

    public string File { get; set; } = "";

    public string Message { get; set; } = "";
}

public class ContentAutomationLibrary
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    public int TemplateCount { get; set; }

    public string ProductName { get; set; } = "";

    public IReadOnlyList<ContentAutomationTemplate> Templates { get; set; } = [];
}

public class ContentAutomationTemplate
{
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
}

public class ContentAutomationLibrarySaveRequest
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<string> Templates { get; set; } = [];
}

public class ContentAutomationLibrarySaveResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = "";

    public ContentAutomationLibrary Library { get; set; } = new();

    public IReadOnlyList<ContentAutomationLibrary> Libraries { get; set; } = [];
}

public class ContentAutomationLibraryImportResult
{
    public bool Success { get; set; }

    public int Imported { get; set; }

    public string Message { get; set; } = "";

    public IReadOnlyList<ContentAutomationLibrary> Libraries { get; set; } = [];
}

public class ContentAutomationLibraryImportFile
{
    public string RelativeName { get; set; } = "";

    public string Content { get; set; } = "";
}

public class ContentAutomationDeleteResult
{
    public bool Success { get; set; }

    public int Deleted { get; set; }

    public string Message { get; set; } = "";

    public IReadOnlyList<ContentAutomationLibrary> Libraries { get; set; } = [];
}

public class ContentAutomationClearResult
{
    public bool Success { get; set; }

    public int Deleted { get; set; }

    public string Message { get; set; } = "";
}

public class ContentAutomationImportVideosRequest
{
    public bool DryRun { get; set; } = true;

    public IReadOnlyList<string> Urls { get; set; } = [];
}

public class ContentAutomationWorkflowRequest
{
    public bool DryRun { get; set; } = true;

    public IReadOnlyList<string> Keywords { get; set; } = [];

    public string CommentMode { get; set; } = "template";

    public string Year { get; set; } = "2026";

    public int Limit { get; set; } = 10;

    public string Rules { get; set; } = "";

    public int MaxCount { get; set; } = 5;

    public IReadOnlyList<string> AccountIds { get; set; } = [];

    public IReadOnlyList<string> ManualUrls { get; set; } = [];

    public string TimeWindow { get; set; } = "";

    public int DurationMinutes { get; set; } = 60;

    public int CycleIntervalMinutes { get; set; } = 30;

    public int CycleIntervalSeconds { get; set; }

    public int AccountIntervalMinSeconds { get; set; } = 480;

    public int AccountIntervalMaxSeconds { get; set; } = 900;

    public int SameAccountCooldownMinSeconds { get; set; } = 1200;

    public int SameAccountCooldownMaxSeconds { get; set; } = 2400;

    public string LibraryId { get; set; } = "";
}

public class ContentAutomationSearchRequest
{
    public bool DryRun { get; set; } = true;

    public IReadOnlyList<string> Keywords { get; set; } = [];

    public string Year { get; set; } = "2026";

    public int Limit { get; set; } = 10;

    public string Rules { get; set; } = "";
}

public class ContentAutomationJobResult
{
    public bool Success { get; set; }

    public string JobId { get; set; } = "";

    public string Status { get; set; } = "";

    public string Message { get; set; } = "";
}

public class ContentAutomationJobStatus
{
    public bool Found { get; set; }

    public string JobId { get; set; } = "";

    public string Action { get; set; } = "";

    public string Status { get; set; } = "";

    public int Progress { get; set; }

    public string Message { get; set; } = "";

    public string Error { get; set; } = "";

    public string Category { get; set; } = "";

    public string Suggestion { get; set; } = "";

    public bool StopRequested { get; set; }
}

public class ContentAutomationJobEvent
{
    public string EventType { get; set; } = "";

    public string JobId { get; set; } = "";

    public string Action { get; set; } = "";

    public string Status { get; set; } = "";

    public int Progress { get; set; }

    public string Message { get; set; } = "";

    public string Error { get; set; } = "";

    public string Category { get; set; } = "";

    public bool Recoverable { get; set; } = true;

    public string Suggestion { get; set; } = "";

    public string RawData { get; set; } = "";
}

public class ContentAutomationAiSettings
{
    public ContentAutomationProviderConfig Deepseek { get; set; } = new();
    public ContentAutomationProviderConfig Qwen { get; set; } = new();
}

public class ContentAutomationProviderConfig
{
    public bool Configured { get; set; }
    public string MaskedKey { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool ClearApiKey { get; set; }
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class ContentAutomationAiSettingsSaveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public ContentAutomationAiSettings Settings { get; set; } = new();
}

public class ContentAutomationVerifyRequest
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

public class ContentAutomationVerifyResult
{
    public bool Success { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
}
