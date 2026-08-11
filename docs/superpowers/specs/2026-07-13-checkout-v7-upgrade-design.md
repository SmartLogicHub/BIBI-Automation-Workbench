# GitHub Actions checkout v7 升级设计

## 目标

将仓库中所有旧版 `actions/checkout` 引用统一升级到 `actions/checkout@v7`，消除 Node.js 20 弃用警告，并让工作流使用该 Action 原生支持的 Node.js 24 运行时。

## 变更范围

仅修改以下 7 个工作流中的版本引用：

- `.github/workflows/auto-deploy-tencent-scf.yml`
- `.github/workflows/codeql-analysis.yml`
- `.github/workflows/no-toxic-comments.yml`
- `.github/workflows/publish-image.yml`
- `.github/workflows/publish-release.yml`
- `.github/workflows/repo-sync.yml`
- `.github/workflows/tag.yml`

每个文件只把 `actions/checkout@v2` 或 `actions/checkout@v3` 替换为 `actions/checkout@v7`。现有输入参数、权限、触发条件和其余步骤保持不变。

## 不在本次范围内

- 不修改 `repo-sync/github-sync@v2`。
- 不创建或修改仓库密钥 `PAT`。
- 不调整工作流权限、触发条件或业务脚本。
- 不升级其他 GitHub Actions。

## 验证

1. 搜索全部工作流，确认 7 处引用均为 `actions/checkout@v7`，且不再残留 `@v2` 或 `@v3`。
2. 解析全部工作流 YAML，确认语法有效。
3. 检查 Git 差异，确认除 7 个版本引用外没有工作流行为变更。

## 风险与回退

这些工作流只使用 checkout 的常规用法，升级到 v7 的兼容风险较低。如后续运行发现兼容问题，可将对应引用回退到升级前的版本；本次不使用允许旧 Node.js 运行时的临时环境变量。
