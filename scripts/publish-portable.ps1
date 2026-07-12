[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\output\BIBI-Portable-Windows-x64"),
    [string]$PythonHome = "G:\Python311",
    [string]$ArchivePath = "",
    [switch]$SkipArchive,
    [switch]$ForceArchive
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$webProject = Join-Path $projectRoot "src\Ray.BiliBiliTool.Web\Ray.BiliBiliTool.Web.csproj"
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$pythonExecutable = Join-Path $PythonHome "python.exe"
$sitePackagesSource = Join-Path $PythonHome "Lib\site-packages"
$playwrightPackage = Join-Path $sitePackagesSource "playwright"

function Remove-PackageItemIfExists {
    param([Parameter(Mandatory)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Find-RarExecutable {
    $pathCommand = Get-Command "rar.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        return $pathCommand.Source
    }

    $candidates = @(
        "C:\Program Files\WinRAR\Rar.exe",
        "C:\Program Files (x86)\WinRAR\Rar.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

function Write-PortableLaunchers {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $localizedLauncherName = "$([char]0x542F)$([char]0x52A8)BIBI.cmd"
    $launcher = @'
@echo off
setlocal
cd /d "%~dp0"
if not exist "BIBI.exe" (
  echo BIBI.exe was not found in this folder.
  echo Please keep the whole portable package together.
  pause
  exit /b 1
)
start "" "%~dp0BIBI.exe"
'@

    foreach ($fileName in @($localizedLauncherName, "Start-BIBI.cmd")) {
        Set-Content -LiteralPath (Join-Path $PackageRoot $fileName) -Value $launcher -Encoding ascii
    }
}

function Remove-DevelopmentArtifacts {
    param([Parameter(Mandatory)][string]$PackageRoot)

    $relativeItems = @(
        "appsettings.Development.json",
        "BlazorDebugProxy",
        "config",
        "data",
        "Data",
        "Logs",
        "WorkflowEditor",
        "wwwroot\appsettings.Development.json"
    )
    foreach ($relativeItem in $relativeItems) {
        Remove-PackageItemIfExists -Path (Join-Path $PackageRoot $relativeItem)
    }

    Get-ChildItem -LiteralPath $PackageRoot -Recurse -Directory -Filter "__pycache__" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    $compiledArtifacts = @(
        Get-ChildItem -LiteralPath $PackageRoot -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @(".pyc", ".pdb") }
    )
    foreach ($artifact in $compiledArtifacts) {
        Remove-Item -LiteralPath $artifact.FullName -Force
    }

    Remove-PackageItemIfExists -Path (Join-Path $PackageRoot "Runtime\python\Lib\test")
    Remove-PackageItemIfExists -Path (Join-Path $PackageRoot "Runtime\python\Lib\idlelib\idle_test")
    Remove-PackageItemIfExists -Path (Join-Path $PackageRoot "Runtime\python\Lib\site-packages\greenlet\tests")
    Remove-PackageItemIfExists -Path (Join-Path $PackageRoot "Runtime\python\Lib\site-packages\openpyxl\tests")
}

function New-PortableArchive {
    param(
        [Parameter(Mandatory)][string]$PackageRoot,
        [Parameter(Mandatory)][string]$TargetArchive,
        [Parameter(Mandatory)][bool]$Overwrite
    )

    $resolvedArchivePath = [System.IO.Path]::GetFullPath($TargetArchive)
    if (Test-Path -LiteralPath $resolvedArchivePath) {
        if (-not $Overwrite) {
            throw "Archive already exists: $resolvedArchivePath. Pass -ForceArchive or choose another -ArchivePath."
        }
        Remove-Item -LiteralPath $resolvedArchivePath -Force
    }

    $archiveDirectory = Split-Path -Parent $resolvedArchivePath
    if (-not [string]::IsNullOrWhiteSpace($archiveDirectory)) {
        New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    }

    $extension = [System.IO.Path]::GetExtension($resolvedArchivePath).ToLowerInvariant()
    if ($extension -eq ".rar") {
        $rar = Find-RarExecutable
        if ($rar) {
            Push-Location (Split-Path -Parent $PackageRoot)
            try {
                & $rar "a" "-r" "-idq" "-y" $resolvedArchivePath (Split-Path -Leaf $PackageRoot)
                if ($LASTEXITCODE -ne 0) {
                    throw "Creating RAR archive failed with exit code $LASTEXITCODE."
                }
            }
            finally {
                Pop-Location
            }
            return $resolvedArchivePath
        }

        $resolvedArchivePath = [System.IO.Path]::ChangeExtension($resolvedArchivePath, ".zip")
        Write-Warning "WinRAR/Rar.exe was not found. Creating ZIP archive instead: $resolvedArchivePath"
    }

    Compress-Archive -LiteralPath $PackageRoot -DestinationPath $resolvedArchivePath -Force:$Overwrite
    return $resolvedArchivePath
}

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "Output folder already exists: $resolvedOutput. Use a new path to avoid overwriting an existing package."
}
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw "Private Python runtime was not found: $pythonExecutable"
}
if (-not (Test-Path -LiteralPath $playwrightPackage)) {
    throw "Playwright is missing from the private Python runtime: $playwrightPackage"
}

Write-Host "Publishing self-contained Windows x64 application..."
dotnet publish $webProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    --output $resolvedOutput

if ($LASTEXITCODE -ne 0) {
    throw "Application publishing failed."
}

$runtimeDirectory = Join-Path $resolvedOutput "Runtime"
$privatePython = Join-Path $runtimeDirectory "python"
$sitePackagesDestination = Join-Path $privatePython "Lib\site-packages"
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

Write-Host "Copying minimal private Python runtime..."
& robocopy $PythonHome $privatePython /E /XD $sitePackagesSource /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "Copying the Python standard runtime failed with robocopy exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $sitePackagesDestination -Force | Out-Null
$requiredPythonPackages = @(
    "playwright", "playwright-1.60.0.dist-info",
    "greenlet", "greenlet-3.3.2.dist-info",
    "pyee", "pyee-13.0.1.dist-info",
    "typing_extensions.py", "typing_extensions-4.15.0.dist-info",
    "openpyxl", "openpyxl-3.1.5.dist-info",
    "et_xmlfile", "et_xmlfile-2.0.0.dist-info",
    "yaml", "_yaml", "pyyaml-6.0.3.dist-info"
)
foreach ($package in $requiredPythonPackages) {
    $source = Join-Path $sitePackagesSource $package
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required Python package item was not found: $source"
    }
    Copy-Item -LiteralPath $source -Destination $sitePackagesDestination -Recurse -Force
}

$privatePythonExecutable = Join-Path $privatePython "python.exe"
& $privatePythonExecutable -c "import playwright, openpyxl, yaml; print('private-python-ok')"
if ($LASTEXITCODE -ne 0) {
    throw "The minimal private Python runtime failed its import check."
}

Write-Host "Removing development artifacts and local runtime state..."
Remove-DevelopmentArtifacts -PackageRoot $resolvedOutput

Write-Host "Writing portable launchers..."
Write-PortableLaunchers -PackageRoot $resolvedOutput
$localizedLauncher = Join-Path $resolvedOutput "$([char]0x542F)$([char]0x52A8)BIBI.cmd"

$requiredFiles = @(
    (Join-Path $resolvedOutput "BIBI.exe"),
    $localizedLauncher,
    (Join-Path $resolvedOutput "Start-BIBI.cmd"),
    (Join-Path $resolvedOutput "hostfxr.dll"),
    (Join-Path $resolvedOutput "coreclr.dll"),
    (Join-Path $privatePython "python.exe"),
    (Join-Path $resolvedOutput "Executor\server.py")
)
$missingFiles = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missingFiles.Count -gt 0) {
    throw "Portable package is incomplete: $($missingFiles -join '; ')"
}

@'
BIBI portable Windows package

1. Copy this entire folder to the target Windows computer.
2. Double-click Start-BIBI.cmd or BIBI.exe.
3. The local workbench opens in the default browser.

This package includes .NET and the minimal Python runtime needed by BIBI.
It uses the Chrome or Edge browser already installed on Windows.
Do not move or delete Runtime, Executor, or data individually.
'@ | Set-Content -LiteralPath (Join-Path $resolvedOutput "README.txt") -Encoding ascii

Write-Host "Portable package created: $resolvedOutput"

if (-not $SkipArchive) {
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $ArchivePath = "$resolvedOutput.rar"
    }

    $archive = New-PortableArchive -PackageRoot $resolvedOutput -TargetArchive $ArchivePath -Overwrite ([bool]$ForceArchive)
    Write-Host "Portable archive created: $archive"
}
