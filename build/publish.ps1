<#
.SYNOPSIS
  Сборка поставки YakUI (NFR-01, 16.1): self-contained win-x64 и win-arm64, ZIP (portable) и установщик EXE (Inno Setup).

.DESCRIPTION
  Для каждой архитектуры:
    1. dotnet publish YakUI (окно) и YakUI.Cli (консоль) в одну папку, self-contained;
    2. подпись exe/dll (ОВ-08) — только если задан сертификат, иначе предупреждение;
    3. ZIP с portable.flag — данные рядом с программой в .\data\;
    4. установщик EXE через Inno Setup (iscc), если он установлен; затем подпись установщика.
  В конце — SHA256SUMS.txt по всем артефактам.

  Подпись (необязательно): переменные окружения
    YAKUI_SIGN_PFX + YAKUI_SIGN_PASSWORD — файл сертификата .pfx; или
    YAKUI_SIGN_THUMBPRINT — отпечаток сертификата в хранилище текущего пользователя.
    YAKUI_SIGN_TIMESTAMP — сервер меток времени (по умолчанию http://timestamp.digicert.com).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\publish.ps1
  powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Runtimes win-x64 -SkipTests
#>
[CmdletBinding()]
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64'),
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $OutputDir,
    [switch] $SkipTests,
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts' }

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}
Write-Host "YakUI $Version → $OutputDir"

function Invoke-Checked([string] $exe, [string[]] $arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe завершился с кодом $LASTEXITCODE" }
}

# ---- Подпись (ОВ-08: 1.0 — без подписи; шаг готов к сертификату) ----
function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        return Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    return $null
}

$signArgs = $null
if ($env:YAKUI_SIGN_PFX -or $env:YAKUI_SIGN_THUMBPRINT) {
    $signTool = Find-SignTool
    if (-not $signTool) { throw 'Задан сертификат подписи, но signtool.exe не найден (Windows SDK).' }
    $timestamp = if ($env:YAKUI_SIGN_TIMESTAMP) { $env:YAKUI_SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }
    $signArgs = @('sign', '/fd', 'sha256', '/tr', $timestamp, '/td', 'sha256')
    if ($env:YAKUI_SIGN_PFX) { $signArgs += @('/f', $env:YAKUI_SIGN_PFX, '/p', $env:YAKUI_SIGN_PASSWORD) }
    else { $signArgs += @('/sha1', $env:YAKUI_SIGN_THUMBPRINT) }
}
else {
    Write-Warning 'Сертификат подписи не задан (ОВ-08): сборка без подписи, SmartScreen предупредит при первом запуске.'
}

function Invoke-Sign([string[]] $files) {
    if (-not $signArgs -or $files.Count -eq 0) { return }
    Invoke-Checked $signTool ($signArgs + $files)
}

# ---- Тесты ----
if (-not $SkipTests -and (Test-Path (Join-Path $root 'tests'))) {
    Invoke-Checked dotnet @('test', (Join-Path $root 'YakUI.sln'), '-c', $Configuration)
}

# ---- Inno Setup ----
$iscc = $null
if (-not $SkipInstaller) {
    $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }
    if (-not $iscc) { Write-Warning 'Inno Setup 6 (ISCC.exe) не найден: установщик EXE не собирается, только ZIP. Установка: winget install JRSoftware.InnoSetup' }
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$artifacts = @()

foreach ($rid in $Runtimes) {
    $arch = $rid -replace '^win-', ''
    $publishDir = Join-Path $OutputDir "publish\$rid\YakUI"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    foreach ($project in @('src\YakUI.App\YakUI.App.csproj', 'src\YakUI.Cli\YakUI.Cli.csproj')) {
        Invoke-Checked dotnet @('publish', (Join-Path $root $project), '-c', $Configuration, '-r', $rid,
            '--self-contained', 'true', '-p:PublishSingleFile=false', "-p:Version=$Version", '-o', $publishDir)
    }

    Invoke-Sign @(Get-ChildItem $publishDir -File | Where-Object { $_.Name -like 'YakUI*.exe' -or $_.Name -like 'YakUI*.dll' } | ForEach-Object FullName)

    # ZIP: переносимая версия — portable.flag рядом с exe (данные в .\data\); без флага — данные в профиле Windows.
    $zip = Join-Path $OutputDir "YakUI-$Version-$rid-portable.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    $flag = Join-Path $publishDir 'portable.flag'
    Set-Content -Path $flag -Value 'YakUI: данные хранятся в папке data рядом с программой (16.1). Удалите этот файл, чтобы хранить их в профиле Windows.' -Encoding utf8
    Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item -Force $flag
    $artifacts += $zip

    if ($iscc) {
        Invoke-Checked $iscc @("/DAppVersion=$Version", "/DSourceDir=$publishDir", "/DArch=$arch", "/DOutputDir=$OutputDir",
            (Join-Path $PSScriptRoot 'installer\YakUI.iss'))
        $setup = Join-Path $OutputDir "YakUI-$Version-$rid-setup.exe"
        Invoke-Sign @($setup)
        $artifacts += $setup
    }
}

$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
$artifacts | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) } |
    Set-Content -Path $sums -Encoding ascii
Write-Host 'Готово:'
$artifacts + $sums | ForEach-Object { Write-Host "  $_" }
