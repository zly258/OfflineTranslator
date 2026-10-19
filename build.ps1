<#
.SYNOPSIS
    OfflineTranslator build script: publishes a lightweight, self-contained single-file executable.
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$SkipTest = $false
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Root)) {
    if ($PSCommandPath) { $Root = Split-Path $PSCommandPath -Parent }
    else { $Root = Split-Path $MyInvocation.MyCommand.Path -Parent }
}

$Wpf  = Join-Path $Root 'src\OfflineTranslator.Wpf\OfflineTranslator.Wpf.csproj'
$Dist = Join-Path $Root 'dist'

function Resolve-Dotnet {
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        return (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }
    $candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $candidate) { return $candidate }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Could not find dotnet SDK. Please install .NET 10 SDK.'
}
$Dotnet = Resolve-Dotnet

function Invoke-Dotnet([string[]]$dotnetArgs) {
    & $Dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet failed with exit code $LASTEXITCODE : $($dotnetArgs -join ' ')" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host "=== Building OfflineTranslator [Runtime: $Runtime] ===" -ForegroundColor Cyan
Write-Host "dotnet : $Dotnet"
Write-Host "root   : $Root"

# 1. Publish WPF
Write-Host ""
Write-Host "[1/4] Publishing Release (win-x64, self-contained, single-file)..." -ForegroundColor Cyan
Invoke-Dotnet @(
    'publish', $Wpf, '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=false',
    '-p:IncludeAllContentForSelfExtract=false',
    '--nologo',
    '-o', $Dist
)

# 2. Clean pdb and lib files in dist
Write-Host ""
Write-Host "[2/4] Cleaning debug and unnecessary symbols (*.pdb, *.lib)..." -ForegroundColor Cyan
Get-ChildItem -Path $Dist -Include '*.pdb', '*.lib' -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "  - Removing: $($_.Name)"
        Remove-Item -Force $_.FullName
    }

# 旧的 Python 后端和内嵌权重 DLL 不再随包发布
foreach ($legacyName in @('ct2_server.py', 'Sdcb.GgufWeights.Abstractions.dll', 'Sdcb.GgufWeights.Hy-MT2-1.8B-2Bit.dll', 'Sdcb.GgufWeights.Hy-MT2-1.8B-2Bit.Part2.dll', 'Sdcb.GgufWeights.Hy-MT2-1.8B-2Bit.Part3.dll')) {
    $legacy = Join-Path $Dist $legacyName
    if (Test-Path $legacy) { Remove-Item -Force $legacy }
}

# 清理 runtimes 下非当前目标系统 (win-x64) 的跨平台冗余目录 (linux-*, osx-*, win-arm64)
$runtimesDir = Join-Path $Dist 'runtimes'
if (Test-Path $runtimesDir) {
    Get-ChildItem -Path $runtimesDir -Directory | ForEach-Object {
        if ($_.Name -ne 'win-x64') {
            Write-Host "  - Removing non-Windows runtime directory: $($_.Name)"
            Remove-Item -Recurse -Force $_.FullName
        }
    }
}

# 3. 拷贝 models 目录下的所有 GGUF（包含 Hy-MT2、Qwen 及用户扩展模型）
Write-Host ""
Write-Host "[3/4] Copying models directory..." -ForegroundColor Cyan
$srcModels = Join-Path $Root 'models'
$distModels = Join-Path $Dist 'models'
if (-not (Test-Path $srcModels)) {
    throw "Missing model directory: models. Put model folders there."
}
if (Test-Path $distModels) {
    Get-ChildItem $distModels -Directory -Filter 'opus-mt-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
}
New-Item -ItemType Directory -Path $distModels -Force | Out-Null
Copy-Item -Path (Join-Path $srcModels '*') -Destination $distModels -Recurse -Force
$gguf = Get-ChildItem -Path $distModels -Filter '*.gguf' -Recurse -File -ErrorAction SilentlyContinue
if (-not $gguf) {
    throw 'models directory does not contain any .gguf file'
}
$ggufMb = [math]::Round(($gguf | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  Models: $($gguf.Count) gguf files, $ggufMb MB" -ForegroundColor Green

$exe = Join-Path $Dist 'OfflineTranslator.exe'
if (-not (Test-Path $exe)) {
    throw 'Publish completed but dist\OfflineTranslator.exe was not found'
}

$exeSizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "  Executable: dist\OfflineTranslator.exe ($exeSizeMb MB)" -ForegroundColor Green

$readmeSrcItem = Get-ChildItem -Path (Join-Path $Root 'assets') -Filter '*.txt' -File | Select-Object -First 1
if (-not $readmeSrcItem) {
    throw 'Missing release notes: assets\*.txt'
}
$readmeDest = Join-Path $Dist $readmeSrcItem.Name
$propsPath = Join-Path $Root 'Directory.Build.props'
[xml]$propsXml = Get-Content -Path $propsPath -Raw -Encoding UTF8
$appVersion = $propsXml.SelectSingleNode('//Version').InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw 'Version is missing in Directory.Build.props'
}
$readmeText = [System.IO.File]::ReadAllText($readmeSrcItem.FullName)
$readmeText = [regex]::Replace($readmeText, '(?m)^(.*\uFF1A)\d+\.\d+\.\d+(\s*)$', ('${1}' + $appVersion + '${2}'))
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText($readmeDest, $readmeText, $utf8Bom)
Write-Host "  Notes: $($readmeSrcItem.Name) (v$appVersion)" -ForegroundColor Green

# 4. Self check
if (-not $SkipTest) {
    Write-Host ""
    Write-Host "[4/4] Running automated self check (dist\OfflineTranslator.exe --autotest)..." -ForegroundColor Cyan
    & $exe --autotest
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Self check failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "Self-check completed successfully!" -ForegroundColor Green
    $selfCheckLog = Join-Path $Dist 'selfcheck-result.txt'
    if (Test-Path $selfCheckLog) { Remove-Item -Force $selfCheckLog }
}

# 术语库：只读消费三端共享规范文件（%ProgramData%\WISDRI\Glossary\glossary.csv，
# 可被 %AppData%\WISDRI\Glossary\glossary.csv 覆盖）；都不存在时内存使用内置出厂默认
# （assets\glossary-defaults.csv），程序不写盘、不管理词条。构建产物不附带 glossary.csv。

Write-Host ""
Write-Host "===================================================" -ForegroundColor Green
Write-Host "       OfflineTranslator Build Successful!         " -ForegroundColor Green
Write-Host " Directory: $Dist" -ForegroundColor Green
Write-Host " Executable: $exe" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Green
