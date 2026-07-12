# BIBI Automation Workbench

本地运行的 B 站运营工作台，集中管理账号登录、评论引流、养号流程、运行记录和本地数据维护。

它不是把多个脚本平铺在页面上，而是将账号、策略、执行和可验证结果组织成一个完整闭环：

1. 在账号管理中创建独立浏览器档案并登录。
2. 在评论引流中配置视频来源、模板库、参与账号和安全限制。
3. 在养号流程中按需编排随机观看、点赞、分享、投币、关注和等待动作。
4. 在运行记录中查看每一步的时间、账号、目标内容、结果和平台确认信息。

## 主要能力

### 账号管理

- 支持多账号独立登录与状态检查。
- 每个账号使用独立浏览器资料，登录会话和自动化配置互不混用。
- 提供重新登录、体检、启停、删除和每日额度展示。
- 删除账号会清理该账号对应的本地自动化资料，不保留孤立数据。

### 评论引流

- 从关键词或指定视频发现候选内容，形成发现池。
- 支持多个评论模板库；发布时从已选词库中随机选择内容。
- 支持 AI 辅助生成与模板库回退，AI 不可用时不会发布固定万能评论。
- 多账号可轮换参与，并可分别设置切换等待、同账号冷却、单次上限和下一轮发现周期。
- 发布结果会写入可查看、可筛选、可删除的台账，并保留可验证的业务证据。

### 养号流程

- 提供流程列表、创建、复制、删除、启停和最近运行结果。
- 可视化画布支持拖入节点、连接节点、平移、缩放、适配视图、一键整理、迷你地图、撤销和重做。
- 支持真实可执行的随机观看、随机点赞、随机分享、随机投币、随机关注、直播互动、每日任务、等待和结果记录。
- 动作参数使用业务含义表达：执行概率、次数区间、等待区间、每日上限和失败处理。
- 运行后记录动作目标、执行时间、账号、结果和失败原因，方便确认流程是否真正发生。

### 运行记录与数据维护

- 评论和养号运行均有独立的可追溯记录。
- 支持查看、筛选、导出和删除已产生的数据。
- 设置页集中管理 AI 模型、运行状态、备份、日志清理和数据导出。
- 页面不会展示路径、端口、Cookie、服务类名等工程实现信息。

## Windows 便携版

适用于不希望在目标电脑安装开发环境的场景。

1. 在发布目录中保留整个 `BIBI-Portable-Windows-x64` 文件夹。
2. 将整个文件夹复制到另一台 Windows x64 电脑。
3. 双击 `BIBI.exe`。
4. 程序自动启动本地工作台并在默认浏览器中打开。

便携包包含 .NET 运行时和精简 Python 运行时，目标电脑无需安装 .NET 8 或 Python。浏览器自动化优先使用系统已有的 Chrome；Windows 自带 Edge 也可使用。

不要单独移动或删除 `Runtime`、`Executor` 或 `data` 目录。

### 创建便携包

在打包机器上执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-portable.ps1
```

发布脚本会生成 `output\BIBI-Portable-Windows-x64`。它只携带 BIBI 运行必需的 Python 依赖，不打包开发机的浏览器缓存、训练库或其他无关软件。

## 本地开发

开发环境需要：

- Windows x64
- .NET SDK 8
- Python 3.11，并安装执行器需要的 `playwright`、`openpyxl`、`PyYAML` 等依赖
- Chrome 或 Edge

启动 Web 项目：

```powershell
dotnet run --project .\src\Ray.BiliBiliTool.Web\Ray.BiliBiliTool.Web.csproj
```

开发运行时，评论执行服务由 Web 应用作为子进程托管。发布运行时，服务优先使用包内 Python，不依赖系统 `PATH`。

## 验证

核心 .NET 测试：

```powershell
dotnet test .\test\AppServiceTest\AppServiceTest.csproj -c Release
```

评论执行器测试：

```powershell
python -m unittest discover -s .\test\CommentExecutorTests -p "test_*.py" -v
```

便携包结构验证：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-portable-package.ps1 `
  -PackagePath .\output\BIBI-Portable-Windows-x64
```

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `src/Ray.BiliBiliTool.Web` | Blazor Web 工作台、页面、服务、控制器和本地执行器 |
| `src/Ray.BiliBiliTool.Application` | 评论策略、养号流程和运行状态应用服务 |
| `src/Ray.BiliBiliTool.DomainService` | 账号、视频、评论候选、模板和运行记录领域能力 |
| `src/Ray.BiliBiliTool.Agent` | B 站接口封装和数据模型 |
| `test` | .NET 与 Python 回归测试 |
| `scripts` | 便携包发布与结构校验脚本 |

## 数据与隐私

- 账号会话、浏览器资料、数据库、日志、导出文件和便携包均只保存在本机，已通过 `.gitignore` 排除，不应提交到仓库。
- API 密钥只在本地设置页保存；提交代码前应再次检查配置和日志。
- 请仅对自己拥有或获授权的账号使用本项目，遵守平台规则、当地法律及相关服务条款。

## 状态

当前重构重点：统一账号生命周期、评论引流闭环、可验证的养号流程、Dify 风格流程编辑器、运行记录和 Windows 免环境便携发布。
