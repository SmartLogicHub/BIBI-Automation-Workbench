[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path $PackagePath).Path
$localizedLauncherName = "$([char]0x542F)$([char]0x52A8)BIBI.cmd"
$required = @(
    "BIBI.exe",
    $localizedLauncherName,
    "Start-BIBI.cmd",
    "hostfxr.dll",
    "coreclr.dll",
    "Runtime\python\python.exe",
    "Runtime\python\Lib\site-packages\playwright\__init__.py",
    "Executor\server.py",
    "wwwroot\BIBI.styles.css",
    "README.txt"
)
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_)) })
if ($missing.Count -gt 0) {
    throw "Portable package validation failed. Missing: $($missing -join '; ')"
}

$unexpected = @(
    "appsettings.Development.json",
    "BlazorDebugProxy",
    "config",
    "data",
    "Data",
    "Logs",
    "WorkflowEditor",
    "wwwroot\appsettings.Development.json",
    "wwwroot\Ray.BiliBiliTool.Web.styles.css"
) | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) }
if ($unexpected.Count -gt 0) {
    throw "Portable package validation failed. Unexpected development artifacts: $($unexpected -join '; ')"
}

$compiledArtifacts = @(
    Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @(".pyc", ".pdb") }
)
if ($compiledArtifacts.Count -gt 0) {
    $sample = @($compiledArtifacts | Select-Object -First 10 | ForEach-Object {
        $_.FullName.Substring($root.Length).TrimStart([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    })
    throw "Portable package validation failed. Remove compiled/debug artifacts: $($sample -join '; ')"
}

Write-Host "Portable package structure passed: $root"
