# BIBI Automation Workbench

面向 B 站账号运营的本地工作台，将多账号登录、评论任务、养号流程、执行记录和 Windows 便携发布集中到一个界面中。

> [!IMPORTANT]
> 本项目不是哔哩哔哩官方产品。请只操作本人拥有或已获授权的账号，并遵守平台规则、服务条款和当地法律。登录验证、验证码与平台风控需要人工处理；自动化并不代表可以绕过平台安全机制。

## 项目来源

本仓库在开源项目 [BiliBiliToolPro](https://github.com/RayWangQvQ/BiliBiliToolPro) 的基础上继续开发，保留了其部分模块、工程结构和提交历史，并增加了面向本地运营的工作台能力：

- 统一的账号生命周期与独立浏览器资料；
- 评论发现、模板选择、发布限制与结果台账；
- 可视化养号流程编辑器与动作执行记录；
- 本地日志、数据维护和 Windows 免环境便携包。

请不要将本仓库理解为完全从零开发的独立项目。原项目与本仓库均依照 GPL-3.0 许可证提供。

## 核心能力

### 多账号管理

- 每个账号使用独立浏览器资料，登录会话互不混用；
- 支持登录、重新登录、状态检查、启停、删除和每日额度展示；
- 删除账号时同步清理该账号的本地自动化资料，避免遗留孤立数据。

### 评论任务

- 按关键词或指定视频发现候选内容；
- 支持多个评论模板库、随机选取和 AI 辅助生成；
- AI 不可用时回退到模板策略，不发布固定的万能评论；
- 可设置参与账号、切换等待、账号冷却、单次上限和下一轮发现周期；
- 记录每次操作的账号、目标、时间、结果与可验证的平台反馈。

### 养号流程

- 创建、复制、删除、启停和运行流程；
- 在可视化画布中拖入节点、连线、缩放、整理、撤销和重做；
- 编排随机观看、点赞、分享、投币、关注、直播互动、每日任务、等待与结果记录；
- 通过执行概率、次数区间、等待区间、每日上限和失败处理控制动作；
- 保留目标内容、执行时间、账号、结果和失败原因。

### 运行记录与维护

- 分别查看评论任务和养号流程的历史记录；
- 支持筛选、导出和删除本地数据；
- 集中管理 AI 模型、运行状态、备份、日志清理和数据导出。

## Windows 便携版

适用于目标电脑不安装 .NET 或 Python 开发环境的场景。

1. 保留完整的 `BIBI-Portable-Windows-x64` 文件夹；
2. 将整个文件夹复制到 Windows x64 电脑；
3. 双击 `BIBI.exe`；
4. 程序会启动本地工作台并打开默认浏览器。

便携包包含 .NET 运行时与精简 Python 运行时，浏览器自动化优先使用系统 Chrome，也可使用 Windows 自带的 Edge。不要单独移动或删除 `Runtime`、`Executor` 或 `data` 目录。

创建便携包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

产物位于 `output\BIBI-Portable-Windows-x64`。

## 本地开发

环境要求：

- Windows x64；
- .NET SDK 8；
- Python 3.11；
- Chrome 或 Edge。

启动 Web 工作台：

```powershell
dotnet run --project .\src\Ray.BiliBiliTool.Web\Ray.BiliBiliTool.Web.csproj
```

评论执行服务在开发环境中由 Web 应用作为子进程托管；便携版优先使用包内 Python，不依赖系统 `PATH`。

## 验证

```powershell
dotnet test .\test\AppServiceTest\AppServiceTest.csproj -c Release

python -m unittest discover -s .\test\CommentExecutorTests -p "test_*.py" -v

powershell -ExecutionPolicy Bypass -File .\scripts\verify-portable-package.ps1 `
  -PackagePath .\output\BIBI-Portable-Windows-x64
```

## 目录说明

| 目录 | 用途 |
| --- | --- |
| `src/Ray.BiliBiliTool.Web` | Blazor 工作台、控制器、本地执行器与服务 |
| `src/Ray.BiliBiliTool.Application` | 评论策略、养号流程与运行状态 |
| `src/Ray.BiliBiliTool.DomainService` | 账号、视频、模板与运行记录领域能力 |
| `src/Ray.BiliBiliTool.Agent` | B 站接口封装和数据模型 |
| `test` | .NET 与 Python 回归测试 |
| `scripts` | 便携包发布与结构校验脚本 |

## 数据与隐私

账号会话、浏览器资料、数据库、日志、导出文件和便携包仅应保存在本机，并已通过 `.gitignore` 排除。API 密钥也只应保存在本地设置中。提交代码前仍需检查 `git status`，避免把 Cookie、密钥或业务数据带入版本库。

## 发布状态

仓库当前没有公开 GitHub Release；请从源码运行或自行构建便携包。

## 许可证

本项目使用 [GNU GPL v3](LICENSE)。使用、修改或分发时，还应遵守上游 [BiliBiliToolPro](https://github.com/RayWangQvQ/BiliBiliToolPro) 的许可证与署名要求。
