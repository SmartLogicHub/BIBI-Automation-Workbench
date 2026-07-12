using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Application;
using Ray.BiliBiliTool.Application.Contracts.ContentAutomation;
using Ray.BiliBiliTool.Config.Options;

namespace AppServiceTest;

public class ContentAutomationBridgeTest
{
    [Fact]
    public void DefaultRequestTimeout_ShouldAllowAiResponsesToFinish()
    {
        var options = new ContentAutomationBridgeOptions();

        Assert.Equal(60, options.RequestTimeoutSeconds);
    }

    [Fact]
    public void Constructor_ShouldApplyConfiguredRequestTimeout()
    {
        using var httpClient = new HttpClient(
            new RecordingHandler(_ => JsonResponse("""{"ok":true}"""))
        );

        _ = new ContentAutomationBridge(
            httpClient,
            new StaticOptionsMonitor<ContentAutomationBridgeOptions>(
                new ContentAutomationBridgeOptions { RequestTimeoutSeconds = 7 }
            )
        );

        Assert.Equal(TimeSpan.FromSeconds(7), httpClient.Timeout);
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnOfflineWhenExecutorCannotBeReached()
    {
        var bridge = CreateBridge(
            new ThrowingHandler(new HttpRequestException("connection refused"))
        );

        var result = await bridge.CheckHealthAsync();

        Assert.False(result.IsOnline);
        Assert.Equal("http://127.0.0.1:8123", result.BaseUrl);
        Assert.Contains("connection refused", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartIntegratedWorkflowAsync_ShouldValidateRequiredFieldsBeforeHttpRequest()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse("""{"jobId":"job-1","status":"started"}""")
        );
        var bridge = CreateBridge(handler);

        var result = await bridge.StartIntegratedWorkflowAsync(
            new ContentAutomationWorkflowRequest
            {
                Keywords = [],
                LibraryId = "7",
                MaxCount = 3,
            }
        );

        Assert.False(result.Success);
        Assert.Contains("keyword", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StartIntegratedWorkflowAsync_ShouldPostCompatiblePayloadAndParseJobId()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/actions/run-integrated-workflow", request.RequestUri?.AbsolutePath);

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.False(root.GetProperty("dryRun").GetBoolean());
            Assert.Equal("2026", root.GetProperty("year").GetString());
            Assert.Equal(12, root.GetProperty("limit").GetInt32());
            Assert.Equal(3, root.GetProperty("maxCount").GetInt32());
            Assert.Equal("7", root.GetProperty("libraryId").GetString());
            Assert.Equal("mouse", root.GetProperty("keywords")[0].GetString());
            Assert.Equal("1", root.GetProperty("accountIds")[0].GetString());
            Assert.Equal("BV1xx411c7mD", root.GetProperty("manualUrls")[0].GetString());
            Assert.Equal(
                "https://www.bilibili.com/video/BV1Q541167Qg",
                root.GetProperty("manualUrls")[1].GetString()
            );
            Assert.Equal("09:00-18:00", root.GetProperty("timeWindow").GetString());
            Assert.Equal(15, root.GetProperty("accountIntervalMinSeconds").GetInt32());
            Assert.Equal(45, root.GetProperty("accountIntervalMaxSeconds").GetInt32());
            Assert.Equal(60, root.GetProperty("sameAccountCooldownMinSeconds").GetInt32());
            Assert.Equal(180, root.GetProperty("sameAccountCooldownMaxSeconds").GetInt32());
            Assert.Equal(75, root.GetProperty("cycleIntervalSeconds").GetInt32());

            return JsonResponse("""{"jobId":"job-42","status":"started","message":"任务已启动"}""");
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.StartIntegratedWorkflowAsync(
            new ContentAutomationWorkflowRequest
            {
                DryRun = false,
                Keywords = ["mouse"],
                Year = "2026",
                Limit = 12,
                Rules = "2026 only",
                MaxCount = 3,
                AccountIds = ["1", "2"],
                ManualUrls = ["BV1xx411c7mD", "https://www.bilibili.com/video/BV1Q541167Qg"],
                TimeWindow = "09:00-18:00",
                DurationMinutes = 60,
                CycleIntervalMinutes = 30,
                CycleIntervalSeconds = 75,
                AccountIntervalMinSeconds = 15,
                AccountIntervalMaxSeconds = 45,
                SameAccountCooldownMinSeconds = 60,
                SameAccountCooldownMaxSeconds = 180,
                LibraryId = "7",
            }
        );

        Assert.True(result.Success);
        Assert.Equal("job-42", result.JobId);
        Assert.Equal("started", result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAccountsAsync_ShouldReadNestedAccountsPayload()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(
                """
                {
                  "ok": true,
                  "accounts": [
                    {
                      "id": 1,
                      "name": "company_bili_1",
                      "enabled": true,
                      "user_data_dir": "accounts/company_bili_1",
                      "daily_limit": 20,
                      "today_count": 3,
                      "status": "idle",
                      "login_status": "logged_in",
                      "next_available_at": "2026-07-08T12:00:00+08:00",
                      "login_note": ""
                    }
                  ]
                }
                """
            )
        );
        var bridge = CreateBridge(handler);

        var accounts = await bridge.GetAccountsAsync();

        var account = Assert.Single(accounts);
        Assert.Equal("1", account.Id);
        Assert.Equal("company_bili_1", account.Name);
        Assert.True(account.Enabled);
        Assert.Equal(20, account.DailyLimit);
        Assert.Equal(3, account.TodayCount);
        Assert.Equal("logged_in", account.LoginStatus);
        Assert.Equal("accounts/company_bili_1", account.UserDataDir);
    }

    [Fact]
    public async Task GetLedgerAsync_ShouldMapStoredTemplateAndFailureFields()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse(
                """
                {
                  "ok": true,
                  "ledger": [
                    {
                      "id": 9,
                      "status": "skipped",
                      "account_name": "测试账号",
                      "bvid": "BV1TEST",
                      "url": "https://www.bilibili.com/video/BV1TEST",
                      "video_title": "凭证测试视频",
                      "template_text": "实际使用的评论模板",
                      "error_reason": "超过今日限制",
                      "verification_method": "comment_visible",
                      "proof_text": "评论已出现在视频评论区",
                      "platform_comment_id": "987654321",
                      "comment_url": "https://www.bilibili.com/video/BV1TEST?comment_on=1",
                      "verified_at": "2026-07-11T10:00:02+08:00",
                      "published_at": "2026-07-11T10:00:01+08:00",
                      "created_at": "2026-07-11T10:00:00+08:00"
                    }
                  ]
                }
                """
            )
        );
        var bridge = CreateBridge(handler);

        var ledger = await bridge.GetLedgerAsync();

        var record = Assert.Single(ledger);
        Assert.Equal("实际使用的评论模板", record.CommentText);
        Assert.Equal("超过今日限制", record.ErrorMessage);
        Assert.Equal("凭证测试视频", record.VideoTitle);
        Assert.Equal("https://www.bilibili.com/video/BV1TEST", record.VideoUrl);
        Assert.Equal("comment_visible", record.VerificationMethod);
        Assert.Equal("评论已出现在视频评论区", record.ProofText);
        Assert.Equal("987654321", record.PlatformCommentId);
        Assert.Contains("comment_on=1", record.CommentUrl);
        Assert.Equal("2026-07-11T10:00:02+08:00", record.VerifiedAt);
        Assert.Equal("2026-07-11T10:00:01+08:00", record.PublishedAt);
    }

    [Fact]
    public async Task SaveAiSettingsAsync_ShouldSendExplicitKeyRemovalFlag()
    {
        var handler = new RecordingHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var deepseek = document.RootElement.GetProperty("deepseek");
            Assert.True(deepseek.GetProperty("clearApiKey").GetBoolean());
            Assert.Equal("", deepseek.GetProperty("apiKey").GetString());
            return JsonResponse("""{"ok":true,"settings":{}}""");
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.SaveAiSettingsAsync(
            new ContentAutomationAiSettings
            {
                Deepseek = new ContentAutomationProviderConfig
                {
                    ClearApiKey = true,
                    Model = "deepseek-v4-flash",
                    BaseUrl = "https://api.deepseek.com",
                },
            }
        );

        Assert.True(result.Success);
    }

    [Fact]
    public async Task UpdateAccountAsync_ShouldPatchAccountFieldsAndParseUpdatedAccount()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/api/accounts/1", request.RequestUri?.AbsolutePath);

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.False(root.GetProperty("enabled").GetBoolean());
            Assert.Equal(9, root.GetProperty("dailyLimit").GetInt32());

            return JsonResponse(
                """
                {
                  "ok": true,
                  "account": {
                    "id": 1,
                    "name": "company_bili_1",
                    "enabled": false,
                    "daily_limit": 9,
                    "today_count": 3
                  }
                }
                """
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.UpdateAccountAsync(
            "1",
            new ContentAutomationAccountUpdateRequest { Enabled = false, DailyLimit = 9 }
        );

        Assert.True(result.Success);
        Assert.Equal("company_bili_1", result.Account.Name);
        Assert.False(result.Account.Enabled);
        Assert.Equal(9, result.Account.DailyLimit);
    }

    [Fact]
    public async Task UpdateAccountAsync_ShouldClearCookieAndPersistLoginState()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/api/accounts/2", request.RequestUri?.AbsolutePath);

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.Equal("", root.GetProperty("cookie").GetString());
            Assert.Equal("login_required", root.GetProperty("loginStatus").GetString());

            return JsonResponse(
                """
                {
                  "ok": true,
                  "account": {
                    "id": 2,
                    "name": "expired-account",
                    "enabled": true,
                    "login_status": "login_required",
                    "cookie": ""
                  }
                }
                """
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.UpdateAccountAsync(
            "2",
            new ContentAutomationAccountUpdateRequest
            {
                ClearCookie = true,
                LoginStatus = "login_required",
            }
        );

        Assert.True(result.Success);
        Assert.Equal("login_required", result.Account.LoginStatus);
        Assert.Equal("", result.Account.Cookie);
    }

    [Fact]
    public async Task CreateAccountAsync_ShouldPostNewAccountAndParseCreatedAccount()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/accounts", request.RequestUri?.AbsolutePath);

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.Equal("marketing_3", root.GetProperty("name").GetString());
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal(20, root.GetProperty("dailyLimit").GetInt32());

            return JsonResponse(
                """
                {
                  "ok": true,
                  "account": {
                    "id": 7,
                    "name": "marketing_3",
                    "enabled": true,
                    "daily_limit": 20,
                    "today_count": 0,
                    "user_data_dir": "accounts/marketing_3"
                  }
                }
                """
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.CreateAccountAsync(
            new ContentAutomationAccountCreateRequest
            {
                Name = "marketing_3",
                Enabled = true,
                DailyLimit = 20,
            }
        );

        Assert.True(result.Success);
        Assert.Equal("7", result.Account.Id);
        Assert.Equal("marketing_3", result.Account.Name);
        Assert.True(result.Account.Enabled);
    }

    [Fact]
    public async Task CreateAccountAsync_ShouldRejectStructuredErrorWithoutOkFlag()
    {
        var handler = new RecordingHandler(_ =>
            JsonResponse("""{"status":"error","error":"账号名称已存在"}""")
        );
        var bridge = CreateBridge(handler);

        var result = await bridge.CreateAccountAsync(
            new ContentAutomationAccountCreateRequest { Name = "duplicate" }
        );

        Assert.False(result.Success);
        Assert.Contains("账号名称已存在", result.Message);
    }

    [Fact]
    public async Task CreateAccountAsync_ShouldRejectSuccessResponseWithoutAccountIdentity()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""{"ok":true}"""));
        var bridge = CreateBridge(handler);

        var result = await bridge.CreateAccountAsync(
            new ContentAutomationAccountCreateRequest { Name = "missing-id" }
        );

        Assert.False(result.Success);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task CheckAccountLoginAsync_ShouldStartCheckLoginJob()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/actions/check-account-login", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            Assert.Equal("1", document.RootElement.GetProperty("accountId").GetString());
            return JsonResponse(
                """{"jobId":"job-login-check","status":"started","message":"任务已启动"}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.CheckAccountLoginAsync("1");

        Assert.True(result.Success);
        Assert.Equal("job-login-check", result.JobId);
    }

    [Fact]
    public async Task OpenLoginWindowAsync_ShouldStartLoginAccountJobWithTimeout()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/actions/login-account", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            Assert.Equal("1", document.RootElement.GetProperty("accountId").GetString());
            Assert.Equal(120, document.RootElement.GetProperty("timeoutSeconds").GetInt32());
            return JsonResponse(
                """{"jobId":"job-login","status":"started","message":"任务已启动"}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.OpenLoginWindowAsync("1", timeoutSeconds: 120);

        Assert.True(result.Success);
        Assert.Equal("job-login", result.JobId);
    }

    [Fact]
    public async Task ExportLedgerAsync_ShouldParseCountAndFile()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/ledger/export", request.RequestUri?.AbsolutePath);
            return JsonResponse("""{"ok":true,"count":5,"file":"G:\\exports\\ledger.xlsx"}""");
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.ExportLedgerAsync();

        Assert.True(result.Success);
        Assert.Equal(5, result.Count);
        Assert.Equal(@"G:\exports\ledger.xlsx", result.File);
    }

    [Fact]
    public async Task ImportLibrariesFromDirectoryAsync_ShouldPostExecutorTemplateDirectory()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/api/comment-libraries/import-directory",
                request.RequestUri?.AbsolutePath
            );
            using var document = JsonDocument.Parse(request.Body);
            Assert.Equal(
                @"G:\桌面\评论词",
                document.RootElement.GetProperty("directory").GetString()
            );
            return JsonResponse(
                """{"ok":true,"imported":2,"libraries":[{"id":1,"name":"产品A","template_count":12}]}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.ImportLibrariesFromDirectoryAsync(@"G:\桌面\评论词");

        Assert.True(result.Success);
        Assert.Equal(2, result.Imported);
        Assert.Single(result.Libraries);
        Assert.Equal("产品A", result.Libraries[0].Name);
    }

    [Fact]
    public async Task ImportLibrariesFromFilesAsync_ShouldUploadBrowserSelectedTextFiles()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/comment-libraries/import-files", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            var files = document.RootElement.GetProperty("files");
            Assert.Equal(2, files.GetArrayLength());
            Assert.Equal("atomanc.txt", files[0].GetProperty("name").GetString());
            Assert.Equal("第一条\n第二条", files[0].GetProperty("content").GetString());
            Assert.Equal("atomevo.txt", files[1].GetProperty("name").GetString());
            return JsonResponse(
                """{"ok":true,"imported":2,"libraries":[{"id":1,"name":"atomanc","template_count":2}]}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.ImportLibrariesFromFilesAsync([
            new ContentAutomationLibraryImportFile
            {
                RelativeName = "atomanc.txt",
                Content = "第一条\n第二条",
            },
            new ContentAutomationLibraryImportFile
            {
                RelativeName = "atomevo.txt",
                Content = "第三条",
            },
        ]);

        Assert.True(result.Success);
        Assert.Equal(2, result.Imported);
        Assert.Single(result.Libraries);
        Assert.Equal("atomanc", result.Libraries[0].Name);
    }

    [Fact]
    public async Task SaveLibraryAsync_ShouldPostTemplatesAndParseUpdatedLibraries()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/comment-libraries", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.Equal("产品A", root.GetProperty("name").GetString());
            Assert.True(root.GetProperty("enabled").GetBoolean());
            Assert.Equal("第一条", root.GetProperty("templates")[0].GetString());
            return JsonResponse(
                """{"ok":true,"library":{"id":7,"name":"产品A","enabled":true,"template_count":2},"libraries":[{"id":7,"name":"产品A","template_count":2}]}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.SaveLibraryAsync(
            new ContentAutomationLibrarySaveRequest
            {
                Name = "产品A",
                Enabled = true,
                Templates = ["第一条", "第二条"],
            }
        );

        Assert.True(result.Success);
        Assert.Equal("7", result.Library.Id);
        Assert.Equal(2, result.Library.TemplateCount);
        Assert.Single(result.Libraries);
    }

    [Fact]
    public async Task ImportVideosAsync_ShouldStartExecutorImportJob()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/actions/import-videos", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            Assert.False(document.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal("BV1xx411c7mD", document.RootElement.GetProperty("urls")[0].GetString());
            return JsonResponse("""{"jobId":"job-import","status":"started"}""");
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.ImportVideosAsync(
            new ContentAutomationImportVideosRequest { DryRun = false, Urls = ["BV1xx411c7mD"] }
        );

        Assert.True(result.Success);
        Assert.Equal("job-import", result.JobId);
    }

    [Fact]
    public async Task VerifyDeepSeekAsync_ShouldPostModelAndBaseUrl()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/settings/ai/verify-deepseek", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.Equal("sk-test", root.GetProperty("apiKey").GetString());
            Assert.Equal("deepseek-chat", root.GetProperty("model").GetString());
            Assert.Equal("https://gateway.example.com/v1", root.GetProperty("baseUrl").GetString());
            return JsonResponse(
                """{"ok":true,"provider":"deepseek","model":"deepseek-chat","result":{"ok":true}}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.VerifyDeepSeekAsync(
            new ContentAutomationVerifyRequest
            {
                ApiKey = "sk-test",
                Model = "deepseek-chat",
                BaseUrl = "https://gateway.example.com/v1",
            }
        );

        Assert.True(result.Success);
        Assert.Equal("deepseek-chat", result.Model);
    }

    [Fact]
    public async Task VerifyQwenAsync_ShouldPostModelAndBaseUrl()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/settings/ai/verify-qwen", request.RequestUri?.AbsolutePath);
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            Assert.Equal("qwen-key", root.GetProperty("apiKey").GetString());
            Assert.Equal("qwen-plus", root.GetProperty("model").GetString());
            Assert.Equal(
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                root.GetProperty("baseUrl").GetString()
            );
            return JsonResponse(
                """{"ok":true,"provider":"qwen","model":"qwen-plus","result":{"ok":true}}"""
            );
        });
        var bridge = CreateBridge(handler);

        var result = await bridge.VerifyQwenAsync(
            new ContentAutomationVerifyRequest
            {
                ApiKey = "qwen-key",
                Model = "qwen-plus",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            }
        );

        Assert.True(result.Success);
        Assert.Equal("qwen-plus", result.Model);
    }

    [Fact]
    public async Task GetJobEventsAsync_ShouldParseServerSentEventsPayload()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/events/job-1", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: progress
                    data: {"jobId":"job-1","action":"run_integrated_workflow","status":"running","progress":35,"message":"searching"}

                    event: done
                    data: {"jobId":"job-1","status":"done","progress":100,"message":"completed"}

                    """,
                    Encoding.UTF8,
                    "text/event-stream"
                ),
            };
        });
        var bridge = CreateBridge(handler);

        var events = await bridge.GetJobEventsAsync("job-1");

        Assert.Equal(2, events.Count);
        Assert.Equal("progress", events[0].EventType);
        Assert.Equal("job-1", events[0].JobId);
        Assert.Equal("run_integrated_workflow", events[0].Action);
        Assert.Equal("running", events[0].Status);
        Assert.Equal(35, events[0].Progress);
        Assert.Equal("searching", events[0].Message);
        Assert.Equal("done", events[1].EventType);
        Assert.Equal(100, events[1].Progress);
    }

    private static ContentAutomationBridge CreateBridge(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new ContentAutomationBridge(
            httpClient,
            new StaticOptionsMonitor<ContentAutomationBridgeOptions>(
                new ContentAutomationBridgeOptions()
            )
        );
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri, body);
            Requests.Add(recorded);
            return responder(recorded);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw exception;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string Body);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
