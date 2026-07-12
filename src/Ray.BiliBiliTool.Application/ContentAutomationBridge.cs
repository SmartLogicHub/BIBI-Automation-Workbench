using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Config.Options;

namespace Ray.BiliBiliTool.Application;

public class ContentAutomationBridge : IContentAutomationBridge
{
    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<ContentAutomationBridgeOptions> _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ContentAutomationBridge(
        HttpClient httpClient,
        IOptionsMonitor<ContentAutomationBridgeOptions> options
    )
    {
        _httpClient = httpClient;
        _options = options;
        ApplyTimeout();
    }

    public async Task<ContentAutomationHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken = default
    )
    {
        var baseUrl = GetBaseUrl();
        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri("/api/health"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            var ok = response.IsSuccessStatusCode && GetBool(root, true, "ok");
            var status = GetString(root, "status");

            return new ContentAutomationHealthResult
            {
                IsOnline = ok,
                BaseUrl = baseUrl,
                Status = status,
                Version = GetString(root, "version"),
                Message = ok
                    ? "Executor online"
                    : GetErrorMessage(root, response.ReasonPhrase ?? "Executor offline"),
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationHealthResult
            {
                IsOnline = false,
                BaseUrl = baseUrl,
                Status = "offline",
                Message = ex.Message,
            };
        }
    }

    public async Task<IReadOnlyList<ContentAutomationAccount>> GetAccountsAsync(
        CancellationToken cancellationToken = default
    )
    {
        var root = await GetRootAsync("/api/accounts", cancellationToken);
        if (
            !TryGetProperty(root, out var accounts, "accounts")
            || accounts.ValueKind != JsonValueKind.Array
        )
            return [];

        return accounts.EnumerateArray().Select(MapAccount).ToList();
    }

    public async Task<ContentAutomationAccountUpdateResult> CreateAccountAsync(
        ContentAutomationAccountCreateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var name = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return FailedAccountUpdate("Account name is required.");

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["enabled"] = request.Enabled,
            ["dailyLimit"] = request.DailyLimit,
        };

        if (!string.IsNullOrWhiteSpace(request.Note))
            payload["note"] = request.Note.Trim();

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri("/api/accounts"),
                payload,
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || IsErrorPayload(root))
                return FailedAccountUpdate(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Account create failed.")
                );

            if (
                !TryGetProperty(root, out var accountElement, "account")
                || accountElement.ValueKind != JsonValueKind.Object
            )
            {
                return FailedAccountUpdate(
                    GetErrorMessage(root, "Account create response was incomplete.")
                );
            }

            var account = MapAccount(accountElement);
            if (string.IsNullOrWhiteSpace(account.Id))
                return FailedAccountUpdate("Account create response did not contain an identity.");

            return new ContentAutomationAccountUpdateResult
            {
                Success = true,
                Message = GetString(root, "message"),
                Account = account,
            };
        }
        catch (Exception ex)
        {
            return FailedAccountUpdate(ex.Message);
        }
    }

    public async Task<ContentAutomationAccountUpdateResult> UpdateAccountAsync(
        string accountId,
        ContentAutomationAccountUpdateRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return FailedAccountUpdate("Account id is required.");

        var payload = new Dictionary<string, object?>();
        if (request.Enabled.HasValue)
            payload["enabled"] = request.Enabled.Value;
        if (request.DailyLimit.HasValue)
            payload["dailyLimit"] = request.DailyLimit.Value;
        if (!string.IsNullOrWhiteSpace(request.Status))
            payload["status"] = request.Status.Trim();
        if (!string.IsNullOrWhiteSpace(request.Note))
            payload["note"] = request.Note.Trim();
        if (!string.IsNullOrWhiteSpace(request.Proxy))
            payload["proxy"] = request.Proxy.Trim();
        if (request.ClearCookie)
            payload["cookie"] = "";
        else if (!string.IsNullOrWhiteSpace(request.Cookie))
            payload["cookie"] = request.Cookie.Trim();
        if (!string.IsNullOrWhiteSpace(request.LoginStatus))
            payload["loginStatus"] = request.LoginStatus.Trim();

        if (payload.Count == 0)
            return FailedAccountUpdate("No account fields were provided.");

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Patch,
                BuildUri($"/api/accounts/{Uri.EscapeDataString(accountId.Trim())}")
            )
            {
                Content = JsonContent.Create(payload, options: JsonOptions),
            };
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedAccountUpdate(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Account update failed.")
                );

            var account = TryGetProperty(root, out var accountElement, "account")
                ? MapAccount(accountElement)
                : new ContentAutomationAccount { Id = accountId.Trim() };

            return new ContentAutomationAccountUpdateResult
            {
                Success = true,
                Message = GetString(root, "message"),
                Account = account,
            };
        }
        catch (Exception ex)
        {
            return FailedAccountUpdate(ex.Message);
        }
    }

    public Task<ContentAutomationJobResult> CheckAccountLoginAsync(
        string accountId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return Task.FromResult(FailedJob("Account id is required."));

        var payload = new Dictionary<string, object?> { ["accountId"] = accountId.Trim() };
        return StartActionAsync("/api/actions/check-account-login", payload, cancellationToken);
    }

    public Task<ContentAutomationJobResult> OpenLoginWindowAsync(
        string accountId,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return Task.FromResult(FailedJob("Account id is required."));

        var payload = new Dictionary<string, object?>
        {
            ["accountId"] = accountId.Trim(),
            ["timeoutSeconds"] = Math.Max(30, timeoutSeconds),
        };
        return StartActionAsync("/api/actions/login-account", payload, cancellationToken);
    }

    public async Task<string> GetAccountCookieAsync(
        string accountId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return "";

        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri($"/api/accounts/{Uri.EscapeDataString(accountId.Trim())}/cookie"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (response.IsSuccessStatusCode && TryGetProperty(root, out var cookieProp, "cookie"))
            {
                return cookieProp.GetString() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    public async Task<IReadOnlyList<ContentAutomationVideo>> GetVideosAsync(
        string status = "",
        CancellationToken cancellationToken = default
    )
    {
        var path = string.IsNullOrWhiteSpace(status)
            ? "/api/videos"
            : $"/api/videos?status={Uri.EscapeDataString(status.Trim())}";
        var root = await GetRootAsync(path, cancellationToken);
        if (
            !TryGetProperty(root, out var videos, "videos")
            || videos.ValueKind != JsonValueKind.Array
        )
            return [];

        return videos.EnumerateArray().Select(MapVideo).ToList();
    }

    public async Task<ContentAutomationDeleteResult> DeleteVideoAsync(
        string videoId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return FailedDelete("Video id is required.");

        try
        {
            using var response = await _httpClient.DeleteAsync(
                BuildUri($"/api/videos/{Uri.EscapeDataString(videoId.Trim())}"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedDelete(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Video delete failed.")
                );

            return new ContentAutomationDeleteResult
            {
                Success = true,
                Deleted = GetInt(root, "deleted"),
                Message = GetString(root, "message"),
            };
        }
        catch (Exception ex)
        {
            return FailedDelete(ex.Message);
        }
    }

    public Task<ContentAutomationClearResult> ClearVideosAsync(
        CancellationToken cancellationToken = default
    ) => ClearCollectionAsync("/api/videos/clear", "Video pool clear failed.", cancellationToken);

    public async Task<IReadOnlyList<ContentAutomationLedgerRecord>> GetLedgerAsync(
        CancellationToken cancellationToken = default
    )
    {
        var root = await GetRootAsync("/api/ledger", cancellationToken);
        if (
            !TryGetProperty(root, out var ledger, "ledger")
            || ledger.ValueKind != JsonValueKind.Array
        )
            return [];

        return ledger.EnumerateArray().Select(MapLedger).ToList();
    }

    public async Task<ContentAutomationDeleteResult> DeleteLedgerRecordAsync(
        string ledgerId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(ledgerId))
            return FailedDelete("Ledger id is required.");

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri($"/api/ledger/{Uri.EscapeDataString(ledgerId.Trim())}/delete"),
                new { },
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedDelete(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Ledger record delete failed.")
                );

            return new ContentAutomationDeleteResult
            {
                Success = true,
                Deleted = GetInt(root, "deleted"),
                Message = GetString(root, "message"),
            };
        }
        catch (Exception ex)
        {
            return FailedDelete(ex.Message);
        }
    }

    public async Task<ContentAutomationLedgerExportResult> ExportLedgerAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri("/api/ledger/export"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedLedgerExport(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Ledger export failed.")
                );

            return new ContentAutomationLedgerExportResult
            {
                Success = true,
                Count = GetInt(root, "count"),
                File = GetString(root, "file"),
                Message = GetString(root, "message"),
            };
        }
        catch (Exception ex)
        {
            return FailedLedgerExport(ex.Message);
        }
    }

    public async Task<IReadOnlyList<ContentAutomationLibrary>> GetLibrariesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var root = await GetRootAsync("/api/comment-libraries", cancellationToken);
        if (
            !TryGetProperty(root, out var libraries, "libraries")
            || libraries.ValueKind != JsonValueKind.Array
        )
            return [];

        return libraries.EnumerateArray().Select(MapLibrary).ToList();
    }

    public async Task<ContentAutomationLibrarySaveResult> SaveLibraryAsync(
        ContentAutomationLibrarySaveRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var name = request.Name?.Trim() ?? "";
        var templates = NormalizeList(request.Templates);
        if (string.IsNullOrWhiteSpace(name))
            return FailedLibrarySave("Library name is required.");

        if (templates.Count == 0)
            return FailedLibrarySave("At least one template is required.");

        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["enabled"] = request.Enabled,
            ["templates"] = templates,
        };
        if (!string.IsNullOrWhiteSpace(request.Id))
            payload["id"] = request.Id.Trim();

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri("/api/comment-libraries"),
                payload,
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedLibrarySave(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Library save failed.")
                );

            return new ContentAutomationLibrarySaveResult
            {
                Success = true,
                Message = GetString(root, "message"),
                Library = TryGetProperty(root, out var library, "library")
                    ? MapLibrary(library)
                    : new ContentAutomationLibrary(),
                Libraries = MapLibraries(root),
            };
        }
        catch (Exception ex)
        {
            return FailedLibrarySave(ex.Message);
        }
    }

    public async Task<ContentAutomationLibraryImportResult> ImportLibrariesFromDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ContentAutomationLibraryImportResult
            {
                Success = false,
                Message = "Template directory is required.",
            };
        }

        var payload = new Dictionary<string, object?> { ["directory"] = directory.Trim() };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri("/api/comment-libraries/import-directory"),
                payload,
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationLibraryImportResult
                {
                    Success = false,
                    Message = GetErrorMessage(
                        root,
                        response.ReasonPhrase ?? "Library import failed."
                    ),
                };
            }

            return new ContentAutomationLibraryImportResult
            {
                Success = true,
                Imported = GetInt(root, "imported"),
                Message = GetString(root, "message"),
                Libraries = MapLibraries(root),
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationLibraryImportResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public async Task<ContentAutomationLibraryImportResult> ImportLibrariesFromFilesAsync(
        IReadOnlyList<ContentAutomationLibraryImportFile> files,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedFiles = (files ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativeName))
            .Select(file => new { name = file.RelativeName.Trim(), content = file.Content ?? "" })
            .Take(200)
            .ToList();
        if (normalizedFiles.Count == 0)
        {
            return new ContentAutomationLibraryImportResult
            {
                Success = false,
                Message = "请选择包含文本文件的文件夹。",
            };
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri("/api/comment-libraries/import-files"),
                new { files = normalizedFiles },
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationLibraryImportResult
                {
                    Success = false,
                    Message = GetErrorMessage(root, response.ReasonPhrase ?? "词库导入失败。"),
                };
            }

            return new ContentAutomationLibraryImportResult
            {
                Success = true,
                Imported = GetInt(root, "imported"),
                Message = GetString(root, "message"),
                Libraries = MapLibraries(root),
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationLibraryImportResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public async Task<ContentAutomationDeleteResult> DeleteLibraryAsync(
        string libraryId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(libraryId))
            return FailedDelete("Library id is required.");

        try
        {
            using var response = await _httpClient.DeleteAsync(
                BuildUri($"/api/comment-libraries/{Uri.EscapeDataString(libraryId.Trim())}"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedDelete(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Library delete failed.")
                );

            return new ContentAutomationDeleteResult
            {
                Success = true,
                Deleted = GetInt(root, "deleted"),
                Message = GetString(root, "message"),
                Libraries = MapLibraries(root),
            };
        }
        catch (Exception ex)
        {
            return FailedDelete(ex.Message);
        }
    }

    public async Task<ContentAutomationDeleteResult> DeleteAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return FailedDelete("Account id is required.");

        try
        {
            using var response = await _httpClient.DeleteAsync(
                BuildUri($"/api/accounts/{Uri.EscapeDataString(accountId.Trim())}"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
                return FailedDelete(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Account delete failed.")
                );

            return new ContentAutomationDeleteResult
            {
                Success = true,
                Deleted = GetBool(root, "deleted") ? 1 : 0,
                Message = GetString(root, "message"),
            };
        }
        catch (Exception ex)
        {
            return FailedDelete(ex.Message);
        }
    }

    public async Task<ContentAutomationClearResult> ClearLedgerAsync(
        CancellationToken cancellationToken = default
    ) => await ClearCollectionAsync("/api/ledger/clear", "Ledger clear failed.", cancellationToken);

    private async Task<ContentAutomationClearResult> ClearCollectionAsync(
        string path,
        string fallbackMessage,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri(path),
                new { },
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationClearResult
                {
                    Success = false,
                    Message = GetErrorMessage(root, response.ReasonPhrase ?? fallbackMessage),
                };
            }

            return new ContentAutomationClearResult
            {
                Success = true,
                Deleted = GetInt(root, "deleted"),
                Message = GetString(root, "message"),
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationClearResult { Success = false, Message = ex.Message };
        }
    }

    public Task<ContentAutomationJobResult> ImportVideosAsync(
        ContentAutomationImportVideosRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var urls = NormalizeList(request.Urls);
        if (urls.Count == 0)
            return Task.FromResult(FailedJob("At least one video URL or BV id is required."));

        var payload = new Dictionary<string, object?>
        {
            ["dryRun"] = request.DryRun,
            ["urls"] = urls,
        };

        return StartActionAsync("/api/actions/import-videos", payload, cancellationToken);
    }

    public Task<ContentAutomationJobResult> SearchVideosAsync(
        ContentAutomationSearchRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var keywords = NormalizeList(request.Keywords);
        if (keywords.Count == 0)
            return Task.FromResult(FailedJob("At least one keyword is required."));

        if (request.Limit <= 0)
            return Task.FromResult(FailedJob("Search limit must be greater than 0."));

        var payload = new Dictionary<string, object?>
        {
            ["dryRun"] = request.DryRun,
            ["keywords"] = keywords,
            ["year"] = NormalizeYear(request.Year),
            ["limit"] = request.Limit,
            ["rules"] = request.Rules?.Trim() ?? "",
        };

        return StartActionAsync("/api/actions/search-videos", payload, cancellationToken);
    }

    public Task<ContentAutomationJobResult> StartIntegratedWorkflowAsync(
        ContentAutomationWorkflowRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var keywords = NormalizeList(request.Keywords);
        if (keywords.Count == 0)
            return Task.FromResult(FailedJob("At least one keyword is required."));

        var commentMode = request.CommentMode?.Trim().ToLowerInvariant() ?? "template";
        if (commentMode is not ("template" or "deepseek" or "qwen"))
            commentMode = "template";

        if (commentMode == "template" && string.IsNullOrWhiteSpace(request.LibraryId))
            return Task.FromResult(FailedJob("A comment library must be selected."));

        if (request.Limit <= 0)
            return Task.FromResult(FailedJob("Search limit must be greater than 0."));

        if (request.MaxCount <= 0)
            return Task.FromResult(FailedJob("Max count must be greater than 0."));

        var payload = new Dictionary<string, object?>
        {
            ["dryRun"] = request.DryRun,
            ["keywords"] = keywords,
            ["commentMode"] = commentMode,
            ["year"] = NormalizeYear(request.Year),
            ["limit"] = request.Limit,
            ["rules"] = request.Rules?.Trim() ?? "",
            ["maxCount"] = request.MaxCount,
            ["accountIds"] = NormalizeList(request.AccountIds),
            ["manualUrls"] = NormalizeList(request.ManualUrls),
            ["timeWindow"] = request.TimeWindow?.Trim() ?? "",
            ["durationMinutes"] = Math.Max(0, request.DurationMinutes),
            ["cycleIntervalMinutes"] = Math.Max(1, request.CycleIntervalMinutes),
            ["cycleIntervalSeconds"] =
                request.CycleIntervalSeconds > 0
                    ? request.CycleIntervalSeconds
                    : Math.Max(1, request.CycleIntervalMinutes) * 60,
            ["accountIntervalMinSeconds"] = Math.Max(1, request.AccountIntervalMinSeconds),
            ["accountIntervalMaxSeconds"] = Math.Max(
                Math.Max(1, request.AccountIntervalMinSeconds),
                request.AccountIntervalMaxSeconds
            ),
            ["sameAccountCooldownMinSeconds"] = Math.Max(1, request.SameAccountCooldownMinSeconds),
            ["sameAccountCooldownMaxSeconds"] = Math.Max(
                Math.Max(1, request.SameAccountCooldownMinSeconds),
                request.SameAccountCooldownMaxSeconds
            ),
            ["libraryId"] =
                commentMode == "template" ? request.LibraryId.Trim() : $"__AI__:{commentMode}",
            ["fallbackLibraryId"] = request.LibraryId?.Trim() ?? "",
        };

        return StartActionAsync("/api/actions/run-integrated-workflow", payload, cancellationToken);
    }

    public async Task<ContentAutomationAiSettings> GetAiSettingsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var root = await GetRootAsync("/api/settings/ai", cancellationToken);
            var settings = new ContentAutomationAiSettings();

            if (root.TryGetProperty("settings", out var settingsProp))
            {
                if (settingsProp.TryGetProperty("deepseek", out var ds))
                {
                    settings.Deepseek.Configured = GetBool(ds, "configured");
                    settings.Deepseek.MaskedKey = GetString(ds, "maskedKey");
                    settings.Deepseek.ApiKey = GetString(ds, "apiKey");
                    settings.Deepseek.Model = GetString(ds, "model");
                    settings.Deepseek.BaseUrl = GetString(ds, "baseUrl");
                }
                if (settingsProp.TryGetProperty("qwen", out var qw))
                {
                    settings.Qwen.Configured = GetBool(qw, "configured");
                    settings.Qwen.MaskedKey = GetString(qw, "maskedKey");
                    settings.Qwen.ApiKey = GetString(qw, "apiKey");
                    settings.Qwen.Model = GetString(qw, "model");
                    settings.Qwen.BaseUrl = GetString(qw, "baseUrl");
                }
            }
            return settings;
        }
        catch
        {
            return new ContentAutomationAiSettings();
        }
    }

    public async Task<ContentAutomationAiSettingsSaveResult> SaveAiSettingsAsync(
        ContentAutomationAiSettings settings,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["deepseek"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = settings.Deepseek.ApiKey?.Trim() ?? "",
                    ["clearApiKey"] = settings.Deepseek.ClearApiKey,
                    ["model"] = settings.Deepseek.Model?.Trim() ?? "",
                    ["baseUrl"] = settings.Deepseek.BaseUrl?.Trim() ?? "",
                },
                ["qwen"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = settings.Qwen.ApiKey?.Trim() ?? "",
                    ["clearApiKey"] = settings.Qwen.ClearApiKey,
                    ["model"] = settings.Qwen.Model?.Trim() ?? "",
                    ["baseUrl"] = settings.Qwen.BaseUrl?.Trim() ?? "",
                },
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            using var response = await _httpClient.PostAsync(
                BuildUri("/api/settings/ai"),
                content,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationAiSettingsSaveResult
                {
                    Success = false,
                    Message = GetErrorMessage(
                        root,
                        response.ReasonPhrase ?? "Save AI settings failed."
                    ),
                };
            }

            return new ContentAutomationAiSettingsSaveResult
            {
                Success = true,
                Message = "保存成功",
                Settings = settings,
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationAiSettingsSaveResult
            {
                Success = false,
                Message = ex.Message,
            };
        }
    }

    public async Task<ContentAutomationVerifyResult> VerifyDeepSeekAsync(
        ContentAutomationVerifyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["apiKey"] = request.ApiKey?.Trim() ?? "",
                ["model"] = request.Model?.Trim() ?? "",
                ["baseUrl"] = request.BaseUrl?.Trim() ?? "",
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            using var response = await _httpClient.PostAsync(
                BuildUri("/api/settings/ai/verify-deepseek"),
                content,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationVerifyResult
                {
                    Success = false,
                    Message = GetErrorMessage(
                        root,
                        response.ReasonPhrase ?? "Verify DeepSeek connection failed."
                    ),
                };
            }

            return new ContentAutomationVerifyResult
            {
                Success = true,
                Provider = "deepseek",
                Model = GetString(root, "model"),
                Result = GetString(root, "result"),
                Message = "DeepSeek 联通性测试成功",
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationVerifyResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<ContentAutomationVerifyResult> VerifyQwenAsync(
        ContentAutomationVerifyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["apiKey"] = request.ApiKey?.Trim() ?? "",
                ["model"] = request.Model?.Trim() ?? "",
                ["baseUrl"] = request.BaseUrl?.Trim() ?? "",
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            using var response = await _httpClient.PostAsync(
                BuildUri("/api/settings/ai/verify-qwen"),
                content,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return new ContentAutomationVerifyResult
                {
                    Success = false,
                    Message = GetErrorMessage(
                        root,
                        response.ReasonPhrase ?? "Verify Qwen connection failed."
                    ),
                };
            }

            return new ContentAutomationVerifyResult
            {
                Success = true,
                Provider = "qwen",
                Model = GetString(root, "model"),
                Result = GetString(root, "result"),
                Message = "千问联通性测试成功",
            };
        }
        catch (Exception ex)
        {
            return new ContentAutomationVerifyResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<string> GenerateAiCommentPreviewAsync(
        string provider,
        string videoTitle,
        string rules,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["provider"] = provider?.Trim() ?? "deepseek",
                ["videoTitle"] = videoTitle?.Trim() ?? "",
                ["rules"] = rules?.Trim() ?? "",
            };

            var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"
            );

            using var response = await _httpClient.PostAsync(
                BuildUri("/api/ai/generate-comment"),
                content,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return $"生成失败: {GetErrorMessage(root, response.ReasonPhrase ?? "Generate comment failed.")}";
            }

            return GetString(root, "comment");
        }
        catch (Exception ex)
        {
            return $"接口调用异常: {ex.Message}";
        }
    }

    public async Task<ContentAutomationJobResult> RefreshAccountCookieAsync(
        string accountId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return FailedJob("Account id is required.");

        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri($"/api/accounts/{accountId}/cookie"),
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return FailedJob(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Refresh cookie failed.")
                );
            }

            var cookie = GetString(root, "cookie");
            if (string.IsNullOrWhiteSpace(cookie))
            {
                return FailedJob(
                    "浏览器本地会话已失效，无法实现免密重签。请先使用二维码重新登录一次以保存会话。"
                );
            }

            return new ContentAutomationJobResult
            {
                Success = true,
                Message = "Cookie 免扫码重签成功！凭证已同步更新。",
                JobId = "refresh_cookie_" + accountId,
            };
        }
        catch (Exception ex)
        {
            return FailedJob($"刷新接口调用异常: {ex.Message}");
        }
    }

    public async Task<ContentAutomationJobStatus> GetJobAsync(
        string jobId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new ContentAutomationJobStatus { Message = "Job id is required." };

        try
        {
            var root = await GetRootAsync(
                $"/api/jobs/{Uri.EscapeDataString(jobId.Trim())}",
                cancellationToken
            );
            return MapJob(root);
        }
        catch (Exception ex)
        {
            return new ContentAutomationJobStatus { JobId = jobId.Trim(), Message = ex.Message };
        }
    }

    public async Task<IReadOnlyList<ContentAutomationJobEvent>> GetJobEventsAsync(
        string jobId,
        int maxCount = 50,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return [];

        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri($"/api/events/{Uri.EscapeDataString(jobId.Trim())}"),
                cancellationToken
            );
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return
                [
                    FailedEvent(
                        jobId.Trim(),
                        response.ReasonPhrase ?? "Executor event request failed."
                    ),
                ];

            var events = ParseServerSentEvents(text);
            var take = Math.Clamp(maxCount, 1, 200);
            return events.Count <= take ? events : events.Skip(events.Count - take).ToList();
        }
        catch (Exception ex)
        {
            return [FailedEvent(jobId.Trim(), ex.Message)];
        }
    }

    public async Task<ContentAutomationJobStatus> StopJobAsync(
        string jobId,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new ContentAutomationJobStatus { Message = "Job id is required." };

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri($"/api/jobs/{Uri.EscapeDataString(jobId.Trim())}/stop"),
                new { },
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            return MapJob(document.RootElement);
        }
        catch (Exception ex)
        {
            return new ContentAutomationJobStatus { JobId = jobId.Trim(), Message = ex.Message };
        }
    }

    private async Task<ContentAutomationJobResult> StartActionAsync(
        string path,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri(path),
                payload,
                JsonOptions,
                cancellationToken
            );
            var document = await ReadJsonAsync(response, cancellationToken);
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || GetBool(root, true, "ok") == false)
            {
                return FailedJob(
                    GetErrorMessage(root, response.ReasonPhrase ?? "Executor request failed.")
                );
            }

            var jobId = GetString(root, "jobId", "job_id", "id");
            return new ContentAutomationJobResult
            {
                Success = !string.IsNullOrWhiteSpace(jobId),
                JobId = jobId,
                Status = GetString(root, "status"),
                Message = GetString(root, "message", "error"),
            };
        }
        catch (Exception ex)
        {
            return FailedJob(ex.Message);
        }
    }

    private async Task<JsonElement> GetRootAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildUri(path), cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        return document.RootElement.Clone();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private Uri BuildUri(string path)
    {
        var baseUrl = GetBaseUrl();
        var root = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(root), path.TrimStart('/'));
    }

    private string GetBaseUrl()
    {
        var value = _options.CurrentValue.BaseUrl?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:8123" : value.TrimEnd('/');
    }

    private void ApplyTimeout()
    {
        var seconds = Math.Clamp(_options.CurrentValue.RequestTimeoutSeconds, 1, 600);
        _httpClient.Timeout = TimeSpan.FromSeconds(seconds);
    }

    private static ContentAutomationAccount MapAccount(JsonElement item)
    {
        return new ContentAutomationAccount
        {
            Id = GetString(item, "id"),
            Name = GetString(item, "name"),
            Uid = GetString(item, "uid", "mid"),
            Nickname = GetString(item, "nickname", "uname"),
            AvatarUrl = GetString(item, "avatar_url", "avatarUrl", "face"),
            Enabled = GetBool(item, "enabled"),
            UserDataDir = GetString(item, "user_data_dir", "userDataDir"),
            DailyLimit = GetInt(item, "daily_limit", "dailyLimit"),
            TodayCount = GetInt(item, "today_count", "todayCount"),
            Status = GetString(item, "status"),
            LoginStatus = GetString(item, "login_status", "loginStatus"),
            LastUsedAt = GetString(item, "last_used_at", "lastUsedAt"),
            LastLoginCheckedAt = GetString(item, "last_login_checked_at", "lastLoginCheckedAt"),
            NextAvailableAt = GetString(item, "next_available_at", "nextAvailableAt"),
            Note = GetString(item, "login_note", "note"),
            Proxy = GetString(item, "proxy"),
            Cookie = GetString(item, "cookie"),
        };
    }

    private static ContentAutomationVideo MapVideo(JsonElement item)
    {
        return new ContentAutomationVideo
        {
            Id = GetString(item, "id"),
            Bvid = GetString(item, "bvid", "Bvid"),
            Title = GetString(item, "title"),
            Author = GetString(item, "author", "up", "owner"),
            Keyword = GetString(item, "keyword"),
            Url = GetString(item, "url"),
            Status = GetString(item, "status"),
            CommentText = GetString(item, "comment_text", "commentText", "comment"),
            LastError = GetString(item, "last_error", "lastError", "error"),
            CreatedAt = GetString(item, "created_at", "createdAt"),
        };
    }

    private static ContentAutomationLedgerRecord MapLedger(JsonElement item)
    {
        return new ContentAutomationLedgerRecord
        {
            Id = GetString(item, "id"),
            Status = GetString(item, "status"),
            AccountName = GetString(item, "account_name", "accountName"),
            AccountId = GetString(item, "account_id", "accountId"),
            Bvid = GetString(item, "bvid"),
            VideoTitle = GetString(item, "video_title", "videoTitle", "title"),
            VideoUrl = GetString(item, "url", "video_url", "videoUrl"),
            CommentText = GetString(
                item,
                "comment_text",
                "commentText",
                "comment",
                "template_text",
                "templateText"
            ),
            ErrorMessage = GetString(
                item,
                "error_message",
                "errorMessage",
                "error",
                "error_reason",
                "errorReason"
            ),
            VerificationMethod = GetString(item, "verification_method", "verificationMethod"),
            ProofText = GetString(item, "proof_text", "proofText"),
            PlatformCommentId = GetString(item, "platform_comment_id", "platformCommentId"),
            CommentUrl = GetString(item, "comment_url", "commentUrl"),
            VerifiedAt = GetString(item, "verified_at", "verifiedAt"),
            PublishedAt = GetString(item, "published_at", "publishedAt"),
            CreatedAt = GetString(item, "created_at", "createdAt"),
        };
    }

    private static ContentAutomationLibrary MapLibrary(JsonElement item)
    {
        return new ContentAutomationLibrary
        {
            Id = GetString(item, "id"),
            Name = GetString(item, "name"),
            Enabled = GetBool(item, "enabled"),
            TemplateCount = GetInt(item, "template_count", "templateCount"),
            ProductName = GetString(item, "product_name", "productName"),
            Templates =
                TryGetProperty(item, out var templates, "templates")
                && templates.ValueKind == JsonValueKind.Array
                    ? templates.EnumerateArray().Select(MapTemplate).ToList()
                    : [],
        };
    }

    private static ContentAutomationTemplate MapTemplate(JsonElement item)
    {
        return new ContentAutomationTemplate
        {
            Id = GetString(item, "id"),
            Text = GetString(item, "text", "content"),
            Enabled = GetBool(item, true, "enabled"),
            SortOrder = GetInt(item, "sort_order", "sortOrder"),
        };
    }

    private static IReadOnlyList<ContentAutomationLibrary> MapLibraries(JsonElement root)
    {
        return
            TryGetProperty(root, out var libraries, "libraries")
            && libraries.ValueKind == JsonValueKind.Array
            ? libraries.EnumerateArray().Select(MapLibrary).ToList()
            : [];
    }

    private static ContentAutomationJobStatus MapJob(JsonElement root)
    {
        return new ContentAutomationJobStatus
        {
            Found =
                GetBool(root, true, "ok") || !string.IsNullOrWhiteSpace(GetString(root, "jobId")),
            JobId = GetString(root, "jobId", "job_id", "id"),
            Action = GetString(root, "action"),
            Status = GetString(root, "status"),
            Progress = GetInt(root, "progress"),
            Message = GetString(root, "message", "error"),
            Error = GetString(root, "error"),
            Category = GetString(root, "category"),
            Suggestion = GetString(root, "suggestion"),
            StopRequested = GetBool(root, "stop_requested", "stopRequested"),
        };
    }

    private static List<ContentAutomationJobEvent> ParseServerSentEvents(string text)
    {
        var result = new List<ContentAutomationJobEvent>();
        var eventType = "";
        var data = new StringBuilder();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                FlushEvent(result, eventType, data.ToString());
                eventType = "";
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line["data:".Length..].Trim());
            }
        }

        FlushEvent(result, eventType, data.ToString());
        return result;
    }

    private static void FlushEvent(
        ICollection<ContentAutomationJobEvent> result,
        string eventType,
        string rawData
    )
    {
        if (string.IsNullOrWhiteSpace(eventType) && string.IsNullOrWhiteSpace(rawData))
            return;

        if (!string.IsNullOrWhiteSpace(rawData))
        {
            try
            {
                using var document = JsonDocument.Parse(rawData);
                result.Add(MapEvent(eventType, document.RootElement, rawData));
                return;
            }
            catch (JsonException)
            {
                // Fall through and preserve the unparseable payload as a visible log line.
            }
        }

        result.Add(
            new ContentAutomationJobEvent
            {
                EventType = string.IsNullOrWhiteSpace(eventType) ? "message" : eventType,
                Message = rawData,
                RawData = rawData,
            }
        );
    }

    private static ContentAutomationJobEvent MapEvent(
        string eventType,
        JsonElement data,
        string rawData
    )
    {
        return new ContentAutomationJobEvent
        {
            EventType = string.IsNullOrWhiteSpace(eventType) ? "message" : eventType,
            JobId = GetString(data, "jobId", "job_id", "id"),
            Action = GetString(data, "action"),
            Status = GetString(data, "status"),
            Progress = GetInt(data, "progress"),
            Message = GetString(data, "message", "error"),
            Error = GetString(data, "error"),
            Category = GetString(data, "category"),
            Recoverable = GetBool(data, true, "recoverable"),
            Suggestion = GetString(data, "suggestion"),
            RawData = rawData,
        };
    }

    private static List<string> NormalizeList(IEnumerable<string> values)
    {
        return values
            .Select(x => x?.Trim() ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeYear(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? DateTime.Now.Year.ToString() : value.Trim();
    }

    private static ContentAutomationJobResult FailedJob(string message)
    {
        return new ContentAutomationJobResult
        {
            Success = false,
            Status = "failed",
            Message = message,
        };
    }

    private static ContentAutomationAccountUpdateResult FailedAccountUpdate(string message)
    {
        return new ContentAutomationAccountUpdateResult { Success = false, Message = message };
    }

    private static ContentAutomationLedgerExportResult FailedLedgerExport(string message)
    {
        return new ContentAutomationLedgerExportResult { Success = false, Message = message };
    }

    private static ContentAutomationLibrarySaveResult FailedLibrarySave(string message)
    {
        return new ContentAutomationLibrarySaveResult { Success = false, Message = message };
    }

    private static ContentAutomationDeleteResult FailedDelete(string message)
    {
        return new ContentAutomationDeleteResult { Success = false, Message = message };
    }

    private static ContentAutomationJobEvent FailedEvent(string jobId, string message)
    {
        return new ContentAutomationJobEvent
        {
            EventType = "error",
            JobId = jobId,
            Status = "error",
            Message = message,
            Error = message,
        };
    }

    private static string GetErrorMessage(JsonElement root, string fallback)
    {
        return
            GetString(root, "message", "error", "detail", "suggestion") is { Length: > 0 } message
            ? message
            : fallback;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }

    private static bool IsErrorPayload(JsonElement element)
    {
        if (GetBool(element, true, "ok") == false)
            return true;

        return string.Equals(
                GetString(element, "status"),
                "error",
                StringComparison.OrdinalIgnoreCase
            ) || !string.IsNullOrWhiteSpace(GetString(element, "error"));
    }

    private static int GetInt(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result))
            return result;

        return
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result)
            ? result
            : 0;
    }

    private static bool GetBool(JsonElement element, params string[] names)
    {
        return GetBool(element, false, names);
    }

    private static bool GetBool(JsonElement element, bool defaultValue, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
            return defaultValue;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var result) && result != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var result)
                ? result
                : value.GetString() == "1",
            _ => defaultValue,
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        out JsonElement value,
        params string[] names
    )
    {
        foreach (var name in names)
        {
            if (
                element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out value)
            )
                return true;
        }

        value = default;
        return false;
    }
}
