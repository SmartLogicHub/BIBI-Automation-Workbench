# BiliBiliToolPro 商品搜索 + 自动评论模块 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `G:\桌面\自动化项目\BIBI自动化项目` 中基于 `BiliBiliToolPro-main` 新建/改造项目，新增 `ProductComment` 模块，使项目同时保留原有自动养号能力，并支持按关键词搜索 B站商品相关视频、生成评论计划、按配置自动发表评论、记录台账和执行限额控制。

**Architecture:** 以 `G:\桌面\自动化项目\BIBI自动化项目` 作为最终项目目录和运行入口；从 `BiliBiliToolPro-main` 复制/迁移基础工程后，复用其 Cookie、WBI、API Client、Console 任务、Web Quartz、多账号基础设施；以 `B站互助台-Codex继续开发源码包-20260628-175845` 作为业务流程参考，迁移其“关键词搜索 -> 候选池 -> 评论库 -> 评论计划 -> 执行台账 -> 账号限额/冷却 -> dry-run”的模型。不要把 Python 项目直接嵌入 C# 项目，不要让两个项目共享运行状态。

**Tech Stack:** C# / .NET 8, WebApiClientCore, Microsoft.Extensions.Options, Scrutor DI, Quartz, System.Text.Json, BiliBiliToolPro existing Cookie/WBI infrastructure, local JSON/SQLite-style persistent storage depending on existing project conventions.

---

## 0. 项目背景和边界

### 0.1 现有项目

目标工程目录：

`G:\桌面\自动化项目\BIBI自动化项目`

改造来源工程：

`G:\桌面\自动化项目\BiliBiliToolPro-main\BiliBiliToolPro-main`

参考工程：

`G:\桌面\B站互助台-Codex继续开发源码包-20260628-175845`

计划书目录：

`G:\桌面\自动化项目\BiBi计划书`

### 0.2 最终目标

改造后的 `BIBI自动化项目` 应同时具备两类能力：

1. 原有自动养号能力继续可用，例如 `Daily`、`LiveLottery`、`VipBigPoint`、`UnfollowBatched` 等任务。
2. 新增 `ProductComment` 任务：
   - 按配置关键词搜索 B站视频。
   - 将搜索结果写入候选池。
   - 对候选视频去重。
   - 从评论模板库选择模板。
   - 替换 `{产品名}`、`{视频标题}`、`{UP主}` 等占位符。
   - 生成评论计划。
   - 按配置决定 dry-run、手动确认或自动发布。
   - 自动发布时通过接口执行评论动作。
   - 记录每条结果到台账。
   - 支持账号每日上限、账号冷却、失败记录、任务停止和可审计日志。

### 0.3 必须从互助台复用的业务思想

不要只复用“评论模板库”这个概念。互助台已经证明需要整套业务闭环：

- `accounts`: 账号启用状态、每日上限、今日计数、冷却时间、登录状态。
- `videos`: 候选视频队列，按 `bvid` 去重。
- `comment_libraries` / `comment_library_templates`: 评论库和模板。
- `ledger`: 发布台账。
- `daily_stats`: 每日统计。
- `dryRun`: 预演模式。
- `build_comment_plan`: 账号 + 视频 + 模板组合成评论计划。
- `record_publish_result`: 结果落账。
- `mark_account_cooldown`: 账号冷却。

### 0.4 BiliBiliToolPro 现有扩展点

DS 实施前必须先理解这些文件：

- API Client 注册：`src/Ray.BiliBiliTool.Agent/Extensions/ServiceCollectionExtension.cs`
- WBI Handler：`src/Ray.BiliBiliTool.Agent/BiliBiliAgent/WridEncryptionDelegatingHandler.cs`
- WBI 参数特性：`src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Attributes/WbiParameterAttribute.cs`
- Cookie 对象：`src/Ray.BiliBiliTool.Agent/BiliCookie.cs`
- 视频 API：`src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/IVideoApi.cs`
- 视频 DomainService：`src/Ray.BiliBiliTool.DomainService/VideoDomainService.cs`
- 多账号任务基类：`src/Ray.BiliBiliTool.Application/BaseMultiAccountsAppService.cs`
- Console 任务白名单：`src/Ray.BiliBiliTool.Application.Contracts/TaskTypeFactory.cs`
- 配置注册：`src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs`
- 命令行映射：`src/Ray.BiliBiliTool.Config/Constants.cs`
- Web Quartz 注册：`src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs`

关键事实：

- `BiliCookie` 已经提供 `BiliJct`，评论接口需要的 `csrf` 直接从 `ck.BiliJct` 取，不要另写 nav 获取 CSRF 流程。
- `WridEncryptionDelegatingHandler` 只有在 query/form 中存在 `w_rid` 时才会签名。
- 项目也有 `[WbiParameter]`，可显式为实现 `IWrid` 的参数生成 WBI。
- `BaseMultiAccountsAppService` 只负责遍历 Cookie，不负责每日限额、冷却、历史去重。
- `TaskTypeFactory` 是 Console 任务识别的硬白名单，新任务必须加入。

---

## 1. 总体设计

### 1.1 模块名称

任务代码：`ProductComment`

配置节：`ProductCommentTaskConfig`

任务合约：`IProductCommentTaskAppService`

任务实现：`ProductCommentTaskAppService`

### 1.2 核心流程

```text
ProductCommentTaskAppService.DoTaskAsync
  -> 读取 ProductCommentTaskOptions
  -> 如果 IsEnable=false，退出
  -> 如果 Keywords 或 Templates/TemplateLibrary 为空，退出并记录原因
  -> 对每个账号执行 DoTaskAccountAsync
       -> 检查账号 ProductComment 状态：今日上限、冷却、Cookie
       -> 搜索关键词对应的视频
       -> 候选池去重入库
       -> 读取待处理候选视频
       -> 生成评论计划
       -> 如果 DryRun=true，只记录计划并输出日志
       -> 如果 EnableAutoPublish=true，调用发布执行器
       -> 记录 ledger
       -> 更新账号今日计数和冷却
  -> 输出汇总
```

### 1.3 发布模式

必须支持三种模式，但分阶段实现：

1. `DryRun`
   - 默认值必须为 `true`。
   - 搜索、入队、生成计划、输出日志。
   - 不调用评论发布接口。
   - 不增加账号今日发布计数。

2. `Manual`
   - 生成评论计划和评论文本。
   - 输出目标视频 URL 和评论内容，供人工复制或外部流程确认。
   - 第一版可以只做 Console 日志，不做 Web UI。

3. `AutoApi`
   - 显式配置 `DryRun=false` 且 `EnableAutoPublish=true` 才允许执行。
   - 通过 B站评论接口发布。
   - 每条结果必须写入台账。
   - 出现登录失效、CSRF 缺失、频率限制、评论区关闭、内容失败等错误时必须记录失败原因。

### 1.4 配置建议

新增 `ProductCommentTaskOptions`，建议字段：

```jsonc
"ProductCommentTaskConfig": {
  "Cron": "0 0 10 * * ?",
  "IsEnable": false,
  "DryRun": true,
  "EnableAutoPublish": false,
  "PublishMode": "DryRun",

  "Keywords": [ "原子豆ANC", "蓝牙耳机推荐", "降噪耳机" ],
  "SearchOrder": "pubdate",
  "MaxSearchPagesPerKeyword": 2,
  "MaxVideosPerKeyword": 5,
  "MaxCandidatesPerRun": 20,

  "Templates": [
    "最近在看{产品名}，这个视频讲得挺细，准备再对比一下。",
    "{UP主}讲得比较实在，{产品名}这个点我之前还没注意到。",
    "看完这个视频，对{产品名}的实际体验更有概念了。"
  ],

  "MaxPublishPerRun": 5,
  "MaxDailyCommentsPerAccount": 10,
  "CommentIntervalMinSeconds": 60,
  "CommentIntervalMaxSeconds": 180,
  "AccountCooldownMinutes": 30,

  "StoragePath": "Data/product-comment.json",
  "SkipAlreadyProcessedBvid": true,
  "SkipCommentedBvid": true
}
```

说明：

- `IsEnable=false` 防止新模块默认运行。
- `DryRun=true` 防止未确认时产生真实发布。
- `EnableAutoPublish=false` 防止误触发自动发布。
- 只有 `IsEnable=true && DryRun=false && EnableAutoPublish=true && PublishMode=AutoApi` 时才允许自动发布。

---

## 2. 建议文件结构

### 2.1 Agent 层

Create:

- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/ISearchApi.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/IReplyApi.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/SearchVideoRequestDto.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/SearchVideoResultDto.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/VideoDetailByBvidDto.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/AddReplyRequest.cs`
- `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/AddReplyResultDto.cs`

Modify:

- `src/Ray.BiliBiliTool.Agent/Extensions/ServiceCollectionExtension.cs`
- optionally `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/IVideoApi.cs`

### 2.2 Config 层

Create:

- `src/Ray.BiliBiliTool.Config/Options/ProductCommentTaskOptions.cs`

Modify:

- `src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs`
- `src/Ray.BiliBiliTool.Config/Constants.cs`
- `src/Ray.BiliBiliTool.Console/appsettings.json`

### 2.3 DomainService 层

Create:

- `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentSearchDomainService.cs`
- `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentPlannerDomainService.cs`
- `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentStore.cs`
- `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentPublisher.cs`
- `src/Ray.BiliBiliTool.DomainService/ProductCommentSearchDomainService.cs`
- `src/Ray.BiliBiliTool.DomainService/ProductCommentPlannerDomainService.cs`
- `src/Ray.BiliBiliTool.DomainService/ProductCommentJsonStore.cs`
- `src/Ray.BiliBiliTool.DomainService/ProductCommentDryRunPublisher.cs`
- `src/Ray.BiliBiliTool.DomainService/ProductCommentApiPublisher.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentCandidate.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentTemplate.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentPlanItem.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentLedgerRecord.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentAccountState.cs`
- `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentPublishResult.cs`

备注：

- 第一阶段可以用 JSON 文件存储，路径来自 `StoragePath`。
- 如果 DS 认为项目里已有 EF/SQLite 更适合，可在 Task 0 写清楚替代方案，但必须先让 GPT 审核后再实施。

### 2.4 Application 层

Create:

- `src/Ray.BiliBiliTool.Application.Contracts/IProductCommentTaskAppService.cs`
- `src/Ray.BiliBiliTool.Application/ProductCommentTaskAppService.cs`

Modify:

- `src/Ray.BiliBiliTool.Application.Contracts/TaskTypeFactory.cs`

### 2.5 Web 层

Create:

- `src/Ray.BiliBiliTool.Web/Jobs/ProductCommentJob.cs`

Modify:

- `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs`

Web 层放在后期任务，不要第一轮实现。

---

## 3. 数据模型 Spec

### 3.1 ProductCommentCandidate

字段：

```csharp
public class ProductCommentCandidate
{
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string LastError { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

状态建议：

- `pending`
- `planned`
- `published`
- `failed`
- `skipped`

### 3.2 ProductCommentPlanItem

```csharp
public class ProductCommentPlanItem
{
    public string AccountUserId { get; set; } = "";
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public string Url { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string VideoTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string TemplateText { get; set; } = "";
    public string CommentText { get; set; } = "";
    public bool DryRun { get; set; }
}
```

### 3.3 ProductCommentLedgerRecord

```csharp
public class ProductCommentLedgerRecord
{
    public string Id { get; set; } = "";
    public string AccountUserId { get; set; } = "";
    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public string Url { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string VideoTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string CommentText { get; set; } = "";
    public string Status { get; set; } = "";
    public int? ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = "";
    public bool DryRun { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}
```

状态建议：

- `dry_run`
- `success`
- `failed`
- `skipped`

### 3.4 ProductCommentAccountState

```csharp
public class ProductCommentAccountState
{
    public string AccountUserId { get; set; } = "";
    public int TodayCount { get; set; }
    public DateOnly StatDate { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? NextAvailableAt { get; set; }
}
```

验收要求：

- 任务重启后，历史候选、台账、今日计数、冷却状态仍然存在。
- 同一 `Bvid` 默认不能重复入队。
- 已经 `success` 的 `Bvid` 默认不能再次发布。

---

## 4. Bilibili API Spec

### 4.1 搜索 API

Endpoint:

```text
GET https://api.bilibili.com/x/web-interface/wbi/search/type
```

参数：

- `search_type=video`
- `keyword`
- `page`
- `order=pubdate`
- `w_rid`
- `wts`

DTO 必须实现 `IWrid`。

注意：

- 现有 `WridEncryptionDelegatingHandler` 只有看到 `w_rid` 才会签名。
- DS 必须通过测试或日志确认最终请求包含有效 `w_rid` 和 `wts`。
- 如果 `[PathQuery]` 不能稳定触发签名，则改用 `[WbiParameter]`。

### 4.2 视频详情 API

Endpoint:

```text
GET https://api.bilibili.com/x/web-interface/view?bvid={bvid}
```

用途：

- 当搜索结果没有 `aid` 或 `aid=0` 时，用 `bvid` 查询 `aid`。

### 4.3 评论发布 API

Endpoint:

```text
POST https://api.bilibili.com/x/v2/reply/add
Content-Type: application/x-www-form-urlencoded
```

参数：

- `oid`: 视频 `aid`
- `type=1`
- `message`: 评论内容
- `plat=1`
- `csrf`: `ck.BiliJct`

要求：

- 缺少 `ck.BiliJct` 时立即失败并写台账，不要发送请求。
- Referer 应按视频构造：`https://www.bilibili.com/video/{bvid}`。
- 失败时记录 B站返回 `code` 和 `message`。

---

## 5. Task-by-Task 实施计划

每个 Task 完成后停止，把 diff、构建结果、测试结果交给 GPT 审核。GPT 审核通过后再做下一个 Task。

### Task -1: 初始化目标工程目录

**Files:**

- Source: `G:\桌面\自动化项目\BiliBiliToolPro-main\BiliBiliToolPro-main`
- Target: `G:\桌面\自动化项目\BIBI自动化项目`

- [ ] **Step 1: Confirm target directory**

Run:

```powershell
Get-ChildItem -Force -LiteralPath 'G:\桌面\自动化项目\BIBI自动化项目'
```

Expected:

- If the directory is empty, copy the source project into it.
- If it already contains files, stop and ask GPT/user before overwriting or merging.

- [ ] **Step 2: Copy source project into target**

Run only if target directory is empty:

```powershell
Copy-Item -LiteralPath 'G:\桌面\自动化项目\BiliBiliToolPro-main\BiliBiliToolPro-main\*' -Destination 'G:\桌面\自动化项目\BIBI自动化项目' -Recurse -Force
```

Expected:

- `G:\桌面\自动化项目\BIBI自动化项目\Ray.BiliBiliTool.sln` exists.
- `G:\桌面\自动化项目\BIBI自动化项目\src` exists.
- Original `BiliBiliToolPro-main` remains unchanged.

- [ ] **Step 3: Copy this implementation plan into target docs**

Create directory:

```powershell
New-Item -ItemType Directory -Force -LiteralPath 'G:\桌面\自动化项目\BIBI自动化项目\docs\plans'
```

Copy:

```powershell
Copy-Item -LiteralPath 'G:\桌面\自动化项目\BiBi计划书\BiliBiliToolPro-商品评论模块-详细Plan+Spec.md' -Destination 'G:\桌面\自动化项目\BIBI自动化项目\docs\plans\ProductComment-Plan-Spec.md' -Force
```

- [ ] **Step 4: GPT review gate**

Send GPT:

- Target directory listing.
- Confirmation source project was copied, not moved.
- Confirmation original source directory still exists.

Do not edit code until GPT confirms the project directory is initialized correctly.

---

### Task 0: 项目确认和基线检查

**Files:**

- Read only:
  - `src/Ray.BiliBiliTool.Agent/Extensions/ServiceCollectionExtension.cs`
  - `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/WridEncryptionDelegatingHandler.cs`
  - `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Attributes/WbiParameterAttribute.cs`
  - `src/Ray.BiliBiliTool.Agent/BiliCookie.cs`
  - `src/Ray.BiliBiliTool.Application.Contracts/TaskTypeFactory.cs`
  - `src/Ray.BiliBiliTool.Console/appsettings.json`
  - `G:\桌面\B站互助台-Codex继续开发源码包-20260628-175845\app_store.py`
  - `G:\桌面\B站互助台-Codex继续开发源码包-20260628-175845\server.py`

- [ ] **Step 1: Run baseline build**

Run:

```powershell
dotnet build Ray.BiliBiliTool.sln
```

Expected:

- Build succeeds, or existing unrelated build failures are documented before changing code.

- [ ] **Step 2: Record baseline task list**

Run:

```powershell
dotnet run --project src/Ray.BiliBiliTool.Console -- --runTasks=Test
```

Expected:

- If Cookie is missing, task may fail at runtime, but project should start and resolve `Test`.
- Document whether local Cookie/config is available.

- [ ] **Step 3: GPT review gate**

Send GPT:

- Build output summary.
- Any existing failures.
- Confirmation that no code was changed.

Do not proceed until GPT acknowledges baseline.

---

### Task 1: ProductComment 配置和任务骨架

**Files:**

- Create: `src/Ray.BiliBiliTool.Config/Options/ProductCommentTaskOptions.cs`
- Create: `src/Ray.BiliBiliTool.Application.Contracts/IProductCommentTaskAppService.cs`
- Create: `src/Ray.BiliBiliTool.Application/ProductCommentTaskAppService.cs`
- Modify: `src/Ray.BiliBiliTool.Config/Extensions/ServiceCollectionExtension.cs`
- Modify: `src/Ray.BiliBiliTool.Application.Contracts/TaskTypeFactory.cs`
- Modify: `src/Ray.BiliBiliTool.Console/appsettings.json`
- Optional test: `test/ConfigTest/TestProductCommentOptions.cs`

- [ ] **Step 1: Add failing config binding test**

Expected behavior:

- `ProductCommentTaskConfig` binds to `ProductCommentTaskOptions`.
- Defaults are safe:
  - `IsEnable=false`
  - `DryRun=true`
  - `EnableAutoPublish=false`
  - `PublishMode="DryRun"`

- [ ] **Step 2: Implement ProductCommentTaskOptions**

Requirements:

- Inherit `BaseConfigOptions`.
- `SectionName` returns `ProductCommentTaskConfig`.
- Include fields listed in section 1.4.
- Validate interval: max seconds must not be less than min seconds.

- [ ] **Step 3: Register options**

Modify `AddBiliBiliConfigs`:

```csharp
.Configure<ProductCommentTaskOptions>(
    configuration.GetSection("ProductCommentTaskConfig")
)
```

- [ ] **Step 4: Add task contract**

Create:

```csharp
using System.ComponentModel;

namespace Ray.BiliBiliTool.Application.Contracts;

[Description("ProductComment")]
public interface IProductCommentTaskAppService : IAppService;
```

- [ ] **Step 5: Add minimal AppService**

Requirements:

- Inherit `BaseMultiAccountsAppService`.
- Inject `ILogger<ProductCommentTaskAppService>`, `IOptionsMonitor<ProductCommentTaskOptions>`, `CookieStrFactory<BiliCookie>`.
- If `IsEnable=false`, log and return.
- If enabled, log that task is in skeleton mode.
- Do not search.
- Do not publish.
- Do not call B站 comment API.

- [ ] **Step 6: Register in TaskTypeFactory**

Add `typeof(IProductCommentTaskAppService)` to `TypeList`.

- [ ] **Step 7: Add appsettings section**

Add `ProductCommentTaskConfig` to `src/Ray.BiliBiliTool.Console/appsettings.json`.

Defaults:

```jsonc
"ProductCommentTaskConfig": {
  "Cron": "0 0 10 * * ?",
  "IsEnable": false,
  "DryRun": true,
  "EnableAutoPublish": false,
  "PublishMode": "DryRun",
  "Keywords": [],
  "SearchOrder": "pubdate",
  "MaxSearchPagesPerKeyword": 2,
  "MaxVideosPerKeyword": 5,
  "MaxCandidatesPerRun": 20,
  "Templates": [],
  "MaxPublishPerRun": 5,
  "MaxDailyCommentsPerAccount": 10,
  "CommentIntervalMinSeconds": 60,
  "CommentIntervalMaxSeconds": 180,
  "AccountCooldownMinutes": 30,
  "StoragePath": "Data/product-comment.json",
  "SkipAlreadyProcessedBvid": true,
  "SkipCommentedBvid": true
}
```

- [ ] **Step 8: Run tests/build**

Run:

```powershell
dotnet build Ray.BiliBiliTool.sln
dotnet test test/ConfigTest/ConfigTest.csproj
```

Expected:

- Build passes.
- Config test passes.

- [ ] **Step 9: GPT review gate**

Send GPT:

- Changed files.
- Config defaults.
- Build/test output.
- Confirm no search or publish logic was added.

---

### Task 2: 商品视频搜索 API 和 SearchDomainService

**Files:**

- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/ISearchApi.cs`
- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/SearchVideoRequestDto.cs`
- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/SearchVideoResultDto.cs`
- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/VideoDetailByBvidDto.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentSearchDomainService.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/ProductCommentSearchDomainService.cs`
- Modify: `src/Ray.BiliBiliTool.Agent/Extensions/ServiceCollectionExtension.cs`
- Optional modify: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/IVideoApi.cs`

- [ ] **Step 1: Add DTO tests**

Test:

- `SearchVideoRequestDto` implements `IWrid`.
- Default values:
  - `search_type="video"`
  - `order="pubdate"`
  - `page=1`
  - `w_rid` present as property.

- [ ] **Step 2: Add ISearchApi**

Required methods:

```csharp
Task<BiliApiResponse<SearchVideoResultDto>> SearchVideosAsync(
    [PathQuery] SearchVideoRequestDto request,
    [Header("Cookie")] string ck
);

Task<BiliApiResponse<VideoDetailByBvidDto>> GetVideoDetailByBvidAsync(
    string bvid,
    [Header("Cookie")] string ck
);
```

If `[PathQuery]` does not reliably trigger WBI, use existing `[WbiParameter]` pattern.

- [ ] **Step 3: Register ISearchApi**

Modify Agent service registration:

```csharp
services.AddBiliBiliClientApi<ISearchApi>(BiliHosts.Api, config);
```

Do not set `ignorWrid: true`.

- [ ] **Step 4: Implement SearchDomainService**

Responsibilities:

- Search each page up to `MaxSearchPagesPerKeyword`.
- Stop once `MaxVideosPerKeyword` valid candidates are collected.
- Normalize title with HTML decode.
- Deduplicate by `Bvid`.
- If `Aid` is 0, call `GetVideoDetailByBvidAsync`.
- Do not write storage yet in this Task.

- [ ] **Step 5: Error handling**

Requirements:

- Bili API `Code != 0` returns empty result with warning, or throws a controlled domain exception.
- Missing `Bvid` items are skipped.
- Empty result is not failure.

- [ ] **Step 6: Run tests/build**

Run:

```powershell
dotnet build Ray.BiliBiliTool.sln
dotnet test test/DomainServiceTest/DomainServiceTest.csproj
```

- [ ] **Step 7: GPT review gate**

Send GPT:

- API interface.
- DTOs.
- SearchDomainService implementation.
- WBI handling explanation.
- Tests/build output.

---

### Task 3: ProductComment 本地存储、候选池和评论模板

**Files:**

- Create: `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentStore.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/ProductCommentJsonStore.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentCandidate.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentTemplate.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentLedgerRecord.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentAccountState.cs`
- Test: `test/DomainServiceTest/ProductCommentStoreTest.cs`

- [ ] **Step 1: Write failing store tests**

Required tests:

- Add candidate inserts new BVID.
- Add duplicate BVID does not insert duplicate.
- Candidate persists after reloading store.
- Ledger success record causes `SkipCommentedBvid` behavior.
- Account state persists `TodayCount` and `NextAvailableAt`.

- [ ] **Step 2: Implement JSON storage**

Storage shape:

```json
{
  "candidates": [],
  "templates": [],
  "ledger": [],
  "accountStates": []
}
```

Requirements:

- Create directory if missing.
- Use atomic-ish write pattern: write temp file then replace.
- Keep methods small and testable.

- [ ] **Step 3: Implement template loading**

Rules:

- Prefer config `Templates`.
- Trim blank templates.
- Deduplicate exact text.
- Disabled template support can be added later; not required for Task 3.

- [ ] **Step 4: Run tests/build**

Run:

```powershell
dotnet test test/DomainServiceTest/DomainServiceTest.csproj
dotnet build Ray.BiliBiliTool.sln
```

- [ ] **Step 5: GPT review gate**

Send GPT:

- Store interface.
- JSON schema.
- Tests.
- Build output.

---

### Task 4: 评论计划生成和占位符替换

**Files:**

- Create: `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentPlannerDomainService.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/ProductCommentPlannerDomainService.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Dtos/ProductCommentPlanItem.cs`
- Test: `test/DomainServiceTest/ProductCommentPlannerTest.cs`

- [ ] **Step 1: Write failing planner tests**

Required tests:

- `{产品名}` -> candidate keyword.
- `{视频标题}` -> candidate title.
- `{UP主}` -> candidate author.
- Same BVID does not produce two plan items.
- Respects `MaxPublishPerRun`.
- Skips candidates already in successful ledger when `SkipCommentedBvid=true`.
- Does not select account whose `TodayCount >= MaxDailyCommentsPerAccount`.
- Does not select account whose `NextAvailableAt` is in the future.

- [ ] **Step 2: Implement planner**

Responsibilities:

- Select pending candidates.
- Select eligible account state.
- Select template.
- Render comment text.
- Return `ProductCommentPlanItem` list.

Do not publish.

- [ ] **Step 3: Run tests/build**

Run:

```powershell
dotnet test test/DomainServiceTest/DomainServiceTest.csproj
dotnet build Ray.BiliBiliTool.sln
```

- [ ] **Step 4: GPT review gate**

Send GPT:

- Planner code.
- Test output.
- Example generated plan.

---

### Task 5: ProductCommentTaskAppService dry-run 全链路

**Files:**

- Modify: `src/Ray.BiliBiliTool.Application/ProductCommentTaskAppService.cs`
- Use existing:
  - `IProductCommentSearchDomainService`
  - `IProductCommentStore`
  - `IProductCommentPlannerDomainService`

- [ ] **Step 1: Add AppService tests or minimal integration tests**

Required behavior:

- `IsEnable=false` exits.
- `DryRun=true` runs search, candidate storage, plan generation, logs plan, but does not publish.
- Missing keywords exits with clear log.
- Missing templates exits with clear log.

- [ ] **Step 2: Implement dry-run flow**

Flow:

```text
for each account:
  validate account Cookie
  search keywords
  upsert candidates
  build plan
  write dry-run ledger records or log-only plans depending on store design
  do not call publisher
```

Requirement:

- Dry-run must never call `IReplyApi`.

- [ ] **Step 3: Run Console dry-run**

Use a safe local config:

```jsonc
"ProductCommentTaskConfig": {
  "IsEnable": true,
  "DryRun": true,
  "EnableAutoPublish": false,
  "Keywords": [ "原子豆ANC" ],
  "Templates": [ "看完这个视频，对{产品名}更有概念了。" ],
  "MaxVideosPerKeyword": 1,
  "MaxPublishPerRun": 1
}
```

Run:

```powershell
dotnet run --project src/Ray.BiliBiliTool.Console -- --runTasks=ProductComment
```

Expected:

- Task starts.
- It logs search/plan result.
- It does not publish.

- [ ] **Step 4: GPT review gate**

Send GPT:

- AppService diff.
- Console output.
- Storage file sample with sensitive values removed.
- Confirmation `IReplyApi` does not exist yet or is not called.

---

### Task 6: 评论发布 API 和 ApiPublisher

**Files:**

- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Interfaces/IReplyApi.cs`
- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/AddReplyRequest.cs`
- Create: `src/Ray.BiliBiliTool.Agent/BiliBiliAgent/Dtos/ProductComment/AddReplyResultDto.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/Interfaces/IProductCommentPublisher.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/ProductCommentDryRunPublisher.cs`
- Create: `src/Ray.BiliBiliTool.DomainService/ProductCommentApiPublisher.cs`
- Modify: `src/Ray.BiliBiliTool.Agent/Extensions/ServiceCollectionExtension.cs`

- [ ] **Step 1: Write publisher tests with mocked IReplyApi**

Required tests:

- DryRun publisher returns `dry_run`.
- Api publisher refuses to run if `DryRun=true`.
- Api publisher refuses to run if `EnableAutoPublish=false`.
- Api publisher refuses when `ck.BiliJct` is empty.
- Successful API response returns success with `rpid`.
- Non-zero API response returns failed with code/message.

- [ ] **Step 2: Add IReplyApi**

Method:

```csharp
Task<BiliApiResponse<AddReplyResultDto>> AddReplyAsync(
    [FormContent] AddReplyRequest request,
    [Header("Cookie")] string ck,
    [Header("Referer")] string referer
);
```

Do not hardcode a generic Referer in the interface. Construct:

```text
https://www.bilibili.com/video/{bvid}
```

- [ ] **Step 3: Implement AddReplyRequest**

Fields:

- `oid`
- `type=1`
- `message`
- `plat=1`
- `csrf`

- [ ] **Step 4: Register IReplyApi**

```csharp
services.AddBiliBiliClientApi<IReplyApi>(BiliHosts.Api, config);
```

Do not add extra WBI requirements unless verified necessary.

- [ ] **Step 5: Implement ApiPublisher**

Guard condition:

```text
Only publish when:
IsEnable=true
DryRun=false
EnableAutoPublish=true
PublishMode=AutoApi
```

Update store:

- success -> ledger success, candidate published, account today count +1, cooldown set
- failed -> ledger failed, candidate failed or pending depending error type

- [ ] **Step 6: Run tests/build**

Run:

```powershell
dotnet test test/DomainServiceTest/DomainServiceTest.csproj
dotnet build Ray.BiliBiliTool.sln
```

- [ ] **Step 7: GPT review gate**

Send GPT:

- IReplyApi.
- Publisher code.
- Tests.
- Confirmation guard condition exists.

Do not run real auto publishing before GPT review.

---

### Task 7: Controlled AutoApi end-to-end

**Files:**

- Modify: `src/Ray.BiliBiliTool.Application/ProductCommentTaskAppService.cs`
- Modify: store/publisher files as needed

- [ ] **Step 1: Add integration guard tests**

Required tests:

- `DryRun=true` never publishes.
- `EnableAutoPublish=false` never publishes.
- `PublishMode != AutoApi` never publishes.
- `MaxPublishPerRun` limits actual published items.
- `MaxDailyCommentsPerAccount` limits actual published items.
- Cooldown prevents same account reuse.

- [ ] **Step 2: Wire publisher into AppService**

AppService chooses:

- DryRun mode -> `ProductCommentDryRunPublisher`
- AutoApi mode -> `ProductCommentApiPublisher`

- [ ] **Step 3: Add explicit logs**

Before each publish, log:

- account user id
- bvid
- title
- rendered comment preview
- dry-run or auto mode

Do not log raw Cookie.

- [ ] **Step 4: Manual real-run checklist**

Before any real run:

- Use one test account.
- Use one keyword.
- Use one template.
- `MaxVideosPerKeyword=1`
- `MaxPublishPerRun=1`
- `MaxDailyCommentsPerAccount=1`
- Verify `DryRun=false`
- Verify `EnableAutoPublish=true`
- Verify `PublishMode=AutoApi`

Run:

```powershell
dotnet run --project src/Ray.BiliBiliTool.Console -- --runTasks=ProductComment
```

- [ ] **Step 5: GPT review gate**

Send GPT:

- Full config used with secrets removed.
- Console output.
- Ledger record.
- Whether comment succeeded or failed.

---

### Task 8: Web Quartz Job

**Files:**

- Create: `src/Ray.BiliBiliTool.Web/Jobs/ProductCommentJob.cs`
- Modify: `src/Ray.BiliBiliTool.Web/Extensions/ServiceCollectionQuartzConfiguratorExtensions.cs`

- [ ] **Step 1: Add ProductCommentJob**

Pattern must match existing `DailyJob`.

- [ ] **Step 2: Register Quartz job**

Use:

```csharp
configuration["ProductCommentTaskConfig:Cron"] ?? DefaultCron
```

- [ ] **Step 3: Build Web**

Run:

```powershell
dotnet build src/Ray.BiliBiliTool.Web/Ray.BiliBiliTool.Web.csproj
```

- [ ] **Step 4: GPT review gate**

Send GPT:

- Job file.
- Quartz registration diff.
- Build output.

---

## 6. GPT Review Checklist

GPT 每轮审查按以下标准看。

### Blocking

- Build 不通过。
- 新任务没有注册到 `TaskTypeFactory`。
- 配置没有注册到 `AddBiliBiliConfigs`。
- 默认配置会触发真实发布。
- Dry-run 下调用了 `IReplyApi`。
- Cookie 被输出到日志。
- 每日上限、冷却、历史去重只存在内存里，没有持久化。
- `csrf` 没有从 `BiliCookie.BiliJct` 获取。
- WBI 搜索接口没有确认签名机制。
- 自动发布没有 `DryRun=false && EnableAutoPublish=true && PublishMode=AutoApi` 三重条件。

### Needs Fix

- 文件职责过大。
- AppService 包含过多纯业务逻辑，应下沉到 DomainService。
- DTO 字段过于严格导致搜索结果易解析失败。
- 错误码没有落台账。
- 失败候选无法重试或无法跳过。
- 测试只测 happy path。

### Acceptable

- 第一阶段没有 Web UI。
- 第一阶段只用 JSON 存储。
- 第一阶段不实现 AI 筛选。
- 第一阶段只支持 Console dry-run。

---

## 7. 非目标

以下内容不进入第一轮实施：

- AI 自动生成自由评论。
- Web UI 管理评论库。
- 从 Python 互助台直接迁移代码。
- 两个项目共享同一个数据库。
- OCR 识别评论区。
- 浏览器自动化发布作为主流程。
- 复杂账号分组策略。

---

## 8. 推荐 DS 执行方式

DS 每次只做一个 Task。完成后必须提交：

1. 修改文件列表。
2. 核心 diff 摘要。
3. 执行过的命令。
4. 命令结果。
5. 是否触发真实发布。
6. 遇到的疑问。

GPT 审核通过后，再做下一个 Task。

不要跳 Task。不要一次性实现搜索、存储、发布、Web 调度。

---

## 9. 第一条给 DS 的指令建议

```text
请按《BiliBiliToolPro 商品搜索 + 自动评论模块 Implementation Plan》执行 Task -1、Task 0 和 Task 1。

范围：
1. 先把 `G:\桌面\自动化项目\BiliBiliToolPro-main\BiliBiliToolPro-main` 复制到 `G:\桌面\自动化项目\BIBI自动化项目`，不要移动或污染原始项目。
2. 在 `G:\桌面\自动化项目\BIBI自动化项目` 内完成后续改造。
3. 只做 ProductComment 配置和任务骨架。
4. 让 --runTasks=ProductComment 能被识别。
5. 默认 IsEnable=false、DryRun=true、EnableAutoPublish=false。
6. 不做搜索 API。
7. 不做评论发布 API。
8. 不调用 B站评论接口。
9. dotnet build 必须通过。
10. 能写测试就写配置绑定测试。

完成后停止，把 diff、build/test 输出发给 GPT 审核。
```
