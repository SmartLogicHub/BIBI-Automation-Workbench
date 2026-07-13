# GitHub Actions checkout v7 Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace all seven `actions/checkout@v2` and `actions/checkout@v3` workflow references with `actions/checkout@v7` without changing any other workflow behavior.

**Architecture:** This is a mechanical dependency-reference upgrade contained entirely in existing GitHub Actions YAML files. A pre-change scan establishes the failing baseline, the seven one-line substitutions provide the implementation, and repository-wide scans plus YAML parsing and diff review verify correctness and scope.

**Tech Stack:** GitHub Actions YAML, PowerShell, Python 3 with PyYAML, Git

---

### Task 1: Upgrade all checkout references

**Files:**
- Modify: `.github/workflows/auto-deploy-tencent-scf.yml:31`
- Modify: `.github/workflows/codeql-analysis.yml:49`
- Modify: `.github/workflows/no-toxic-comments.yml:9`
- Modify: `.github/workflows/publish-image.yml:27`
- Modify: `.github/workflows/publish-release.yml:18`
- Modify: `.github/workflows/repo-sync.yml:17`
- Modify: `.github/workflows/tag.yml:17`

- [ ] **Step 1: Run the failing old-reference check**

Run:

```powershell
$old = Get-ChildItem -LiteralPath '.github\workflows' -File |
  Select-String -Pattern 'actions/checkout@v[23]'
$old
if ($old.Count -ne 0) { throw "Found $($old.Count) old checkout references" }
```

Expected: FAIL with `Found 7 old checkout references`, listing two v2 references and five v3 references.

- [ ] **Step 2: Apply the minimal implementation**

In each of the seven files, change only the existing checkout line to:

```yaml
uses: actions/checkout@v7
```

Preserve the existing list indentation (`- uses`) where present. Do not change inputs, permissions, triggers, other actions, or repository secrets.

- [ ] **Step 3: Re-run the old-reference check**

Run:

```powershell
$old = Get-ChildItem -LiteralPath '.github\workflows' -File |
  Select-String -Pattern 'actions/checkout@v[23]'
if ($old.Count -ne 0) { $old; throw "Found $($old.Count) old checkout references" }
Write-Output 'No checkout v2/v3 references remain.'
```

Expected: PASS with `No checkout v2/v3 references remain.`

- [ ] **Step 4: Verify the exact new-reference count**

Run:

```powershell
$current = Get-ChildItem -LiteralPath '.github\workflows' -File |
  Select-String -Pattern 'actions/checkout@v7'
$current
if ($current.Count -ne 7) { throw "Expected 7 checkout v7 references, found $($current.Count)" }
```

Expected: PASS and list exactly seven `actions/checkout@v7` references.

- [ ] **Step 5: Parse every workflow as YAML**

Run:

```powershell
@'
from pathlib import Path
import yaml

files = sorted(Path('.github/workflows').glob('*.y*ml'))
for path in files:
    with path.open('r', encoding='utf-8-sig') as stream:
        yaml.safe_load(stream)
print(f'Parsed {len(files)} workflow files successfully.')
'@ | python -
```

Expected: PASS with the total workflow count and no parser exception.

- [ ] **Step 6: Verify whitespace and review the complete diff**

Run:

```powershell
git diff --check
git diff -- .github/workflows
git status --short
```

Expected: `git diff --check` exits 0; the workflow diff contains exactly seven one-line version substitutions; status contains no unrelated changes.

- [ ] **Step 7: Commit the workflow upgrade**

Run:

```powershell
git add .github/workflows/auto-deploy-tencent-scf.yml `
  .github/workflows/codeql-analysis.yml `
  .github/workflows/no-toxic-comments.yml `
  .github/workflows/publish-image.yml `
  .github/workflows/publish-release.yml `
  .github/workflows/repo-sync.yml `
  .github/workflows/tag.yml
git commit -m "ci: upgrade checkout actions to v7"
```

Expected: one commit containing only the seven workflow files.
