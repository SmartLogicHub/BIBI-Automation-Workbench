namespace Ray.BiliBiliTool.Application.Contracts.ContentAutomation;

public interface IContentAutomationBridge
{
    Task<ContentAutomationHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ContentAutomationAccount>> GetAccountsAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationAccountUpdateResult> CreateAccountAsync(
        ContentAutomationAccountCreateRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationAccountUpdateResult> UpdateAccountAsync(
        string accountId,
        ContentAutomationAccountUpdateRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> CheckAccountLoginAsync(
        string accountId,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> OpenLoginWindowAsync(
        string accountId,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default
    );

    Task<string> GetAccountCookieAsync(
        string accountId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ContentAutomationVideo>> GetVideosAsync(
        string status = "",
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationDeleteResult> DeleteVideoAsync(
        string videoId,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationClearResult> ClearVideosAsync(
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ContentAutomationLedgerRecord>> GetLedgerAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationLedgerExportResult> ExportLedgerAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationDeleteResult> DeleteLedgerRecordAsync(
        string ledgerId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ContentAutomationLibrary>> GetLibrariesAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationLibrarySaveResult> SaveLibraryAsync(
        ContentAutomationLibrarySaveRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationLibraryImportResult> ImportLibrariesFromDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationLibraryImportResult> ImportLibrariesFromFilesAsync(
        IReadOnlyList<ContentAutomationLibraryImportFile> files,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationDeleteResult> DeleteAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationDeleteResult> DeleteLibraryAsync(
        string libraryId,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationClearResult> ClearLedgerAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> ImportVideosAsync(
        ContentAutomationImportVideosRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> SearchVideosAsync(
        ContentAutomationSearchRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> StartIntegratedWorkflowAsync(
        ContentAutomationWorkflowRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobStatus> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<ContentAutomationJobEvent>> GetJobEventsAsync(
        string jobId,
        int maxCount = 50,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobStatus> StopJobAsync(
        string jobId,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationAiSettings> GetAiSettingsAsync(
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationAiSettingsSaveResult> SaveAiSettingsAsync(
        ContentAutomationAiSettings settings,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationVerifyResult> VerifyDeepSeekAsync(
        ContentAutomationVerifyRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationVerifyResult> VerifyQwenAsync(
        ContentAutomationVerifyRequest request,
        CancellationToken cancellationToken = default
    );

    Task<string> GenerateAiCommentPreviewAsync(
        string provider,
        string videoTitle,
        string rules,
        CancellationToken cancellationToken = default
    );

    Task<ContentAutomationJobResult> RefreshAccountCookieAsync(
        string accountId,
        CancellationToken cancellationToken = default
    );
}
