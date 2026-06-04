<#
.SYNOPSIS
    Build DVTk Modality Emulator + đóng gói .msi installer.

.DESCRIPTION
    DVTk có sẵn project "Modality Emulator Setup.vdproj" — Visual Studio Deployment
    Project (vdproj) format. Project này KHÔNG được MSBuild support; phải build qua
    devenv VÀ phải cài extension "Microsoft Visual Studio Installer Projects 2022".

    Script này:
      1. Locate Visual Studio 2022 (qua vswhere).
      2. Verify extension "Microsoft Visual Studio Installer Projects" đã cài.
      3. Migrate .vcproj C++ (VS 2008) sang .vcxproj qua devenv /upgrade.
      4. Build cả solution qua devenv (code projects + vdproj setup) → ra .msi.
      5. Locate .msi output, in đường dẫn.

.PARAMETER DvtkRoot
    Đường dẫn DVTk repo root. Mặc định đoán theo workspace layout.

.PARAMETER Configuration
    Debug | Release. Mặc định Release.

.EXAMPLE
    pwsh .\setup-dvtk-modality-emulator.ps1
    pwsh .\setup-dvtk-modality-emulator.ps1 -DvtkRoot "D:\path\to\DVTk"
#>

[CmdletBinding()]
param(
    [string]$DvtkRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 1. Resolve DVTk root
# ---------------------------------------------------------------------------
if (-not $DvtkRoot) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    if (-not $scriptDir) {
        throw "Không xác định được script directory. Truyền -DvtkRoot rõ ràng."
    }
    $candidate = Join-Path $scriptDir '..\..\..\DVTk'
    $resolved = Resolve-Path $candidate -ErrorAction SilentlyContinue
    if (-not $resolved) {
        throw "Không tìm thấy DVTk root tại '$candidate'. Truyền -DvtkRoot rõ ràng."
    }
    $DvtkRoot = $resolved.Path
}

if (-not (Test-Path (Join-Path $DvtkRoot 'Modality_Emulator'))) {
    throw "DvtkRoot không hợp lệ: '$DvtkRoot' không có folder Modality_Emulator."
}

$meSln  = Join-Path $DvtkRoot 'Modality_Emulator\Modality Emulator and DVTk Library.sln'
$meDir  = Join-Path $DvtkRoot 'Modality_Emulator'

if (-not (Test-Path $meSln)) { throw "Không tìm thấy solution: $meSln" }

Write-Host "DVTk root         : $DvtkRoot"
Write-Host "Solution          : $meSln"
Write-Host "Configuration     : $Configuration"
Write-Host ""

# ---------------------------------------------------------------------------
# 2. Locate Visual Studio 2022
# ---------------------------------------------------------------------------
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "Không tìm thấy vswhere.exe — cài Visual Studio 2022 trước."
}

$vsInstall = & $vswhere -latest -products '*' `
    -requires Microsoft.Component.MSBuild `
    -property installationPath
if (-not $vsInstall) { throw "Không tìm thấy Visual Studio installation." }

$devenv = Join-Path $vsInstall 'Common7\IDE\devenv.com'
if (-not (Test-Path $devenv)) { throw "Không tìm thấy devenv: $devenv" }

Write-Host "Visual Studio     : $vsInstall"
Write-Host "devenv            : $devenv"
Write-Host ""

# Cảnh báo nếu thiếu workload C++ / .NET
$cppWorkload = & $vswhere -latest -products '*' `
    -requires Microsoft.VisualStudio.Workload.NativeDesktop `
    -property installationPath
if (-not $cppWorkload) {
    Write-Warning "Workload 'Desktop development with C++' chưa cài — .vcproj sẽ fail."
}

$dotnetWorkload = & $vswhere -latest -products '*' `
    -requires Microsoft.VisualStudio.Workload.ManagedDesktop `
    -property installationPath
if (-not $dotnetWorkload) {
    Write-Warning "Workload '.NET desktop development' chưa cài — WinForms project sẽ fail."
}

# ---------------------------------------------------------------------------
# 3. Verify "Visual Studio Installer Projects" extension đã cài
# ---------------------------------------------------------------------------
# Extension này add support cho .vdproj. Không có nó devenv sẽ skip Setup project.
# Marketplace ID: visualstudioclient.MicrosoftVisualStudio2022InstallerProjects
# Cài rồi → có folder: <VS>\Common7\IDE\Extensions\<guid>\ chứa DeploymentProjects.*

# Extension cài tại: <VS>\Common7\IDE\CommonExtensions\Microsoft\VSI\
# Marker file: extension.vsixmanifest + VsDeploy folder
$vsiDir = Join-Path $vsInstall 'Common7\IDE\CommonExtensions\Microsoft\VSI'
$vdProjSupport = $null
if (Test-Path (Join-Path $vsiDir 'extension.vsixmanifest')) {
    $vdProjSupport = $vsiDir
}

if (-not $vdProjSupport) {
    Write-Error @"
Extension 'Microsoft Visual Studio Installer Projects 2022' chưa cài.
Without it, .vdproj không build được → không ra .msi.

Cài 1 trong 2 cách:

  Cách 1 (GUI):
    Visual Studio 2022 → Extensions → Manage Extensions → Online
    → search 'Microsoft Visual Studio Installer Projects 2022'
    → Download → restart VS để extension cài tiếp.

  Cách 2 (CLI):
    Tải VSIX:
      https://marketplace.visualstudio.com/items?itemName=visualstudioclient.MicrosoftVisualStudio2022InstallerProjects
    Rồi:
      & "$vsInstall\Common7\IDE\VSIXInstaller.exe" `<đường-dẫn-vsix`>

Sau khi cài xong, chạy lại script này.
"@
    exit 1
}
Write-Host "VS Installer Projects extension : OK"
Write-Host ""

# ---------------------------------------------------------------------------
# 4. Migrate old project formats
# ---------------------------------------------------------------------------
Write-Host "[1/3] Migrating old project formats (devenv /upgrade) ..." -ForegroundColor Cyan
& $devenv $meSln /upgrade
if ($LASTEXITCODE -ne 0) {
    Write-Warning "devenv /upgrade exit code $LASTEXITCODE — thường chỉ warning, tiếp tục."
}
Write-Host ""

# ---------------------------------------------------------------------------
# 5. Build solution (code + vdproj setup) qua devenv
# ---------------------------------------------------------------------------
# devenv /Build "<Configuration>" sẽ build hết, kể cả .vdproj.
# Output ra console kèm `/Out` để log lưu file.

Write-Host "[2/3] Building solution ($Configuration) qua devenv ..." -ForegroundColor Cyan
$logFile = Join-Path $env:TEMP "dvtk-me-build-$Configuration.log"

& $devenv $meSln /Build $Configuration /Out $logFile

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "Build FAILED (exit $LASTEXITCODE). Log: $logFile"
    Write-Host ""
    Write-Host "Common fixes:"
    Write-Host "  - 'Desktop development with C++' workload chưa cài → VS Installer → Modify"
    Write-Host "  - '.NET Framework 4 targeting pack' missing → VS Installer → Individual"
    Write-Host "    components → tick '.NET Framework 4 targeting pack' + '.NET Framework 4.8 SDK'"
    Write-Host "  - Lỗi 'project type not supported' với .vdproj → cài lại Installer Projects extension"
    exit 1
}

# ---------------------------------------------------------------------------
# 6. Locate .msi output
# ---------------------------------------------------------------------------
Write-Host "[3/3] Locating .msi output ..." -ForegroundColor Cyan

# .vdproj output mặc định: <project-dir>\<Configuration>\<ProductName>.msi
# Với DVTk: Modality_Emulator\<Configuration>\Modality Emulator Setup.msi
$msi = $null

$candidates = @(
    Join-Path $meDir "$Configuration\Modality Emulator Setup.msi"
    Join-Path $meDir "Modality Emulator Setup\$Configuration\Modality Emulator Setup.msi"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $msi = $c; break }
}

# Fallback: search recursive
if (-not $msi) {
    $msi = Get-ChildItem -Path $meDir -Filter '*.msi' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match [regex]::Escape($Configuration) } |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $msi) {
    Write-Warning "Build báo OK nhưng không tìm thấy .msi trong $meDir."
    Write-Warning "Tự kiểm tra:  Get-ChildItem -Path '$meDir' -Filter '*.msi' -Recurse"
    exit 1
}

$msiInfo = Get-Item $msi
Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host " BUILD SUCCESS — .msi installer ready" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Installer  : $msi"
Write-Host "Size       : $([math]::Round($msiInfo.Length / 1MB, 2)) MB"
Write-Host "Modified   : $($msiInfo.LastWriteTime)"
Write-Host ""
Write-Host "Cài đặt:"
Write-Host "  Double-click trong Explorer, hoặc:"
Write-Host "    Start-Process msiexec.exe -ArgumentList '/i','`"$msi`"'"
Write-Host ""
Write-Host "Silent install:"
Write-Host "    Start-Process msiexec.exe -ArgumentList '/i','`"$msi`"','/qn' -Wait"
Write-Host ""
Write-Host "Uninstall sau này:"
Write-Host "    Settings → Apps → 'Modality Emulator' → Uninstall"
Write-Host "    hoặc: Start-Process msiexec.exe -ArgumentList '/x','`"$msi`"'"
Write-Host ""
Write-Host "Sau khi cài, app ở: %ProgramFiles(x86)%\DVTk\Modality Emulator\"
Write-Host ""
Write-Host "Cấu hình initial trong app:"
Write-Host "  Tools → Options"
Write-Host "    Local AE Title  : XQUANG01     (match cấu hình gateway)"
Write-Host "    Remote AE Title : LINKRAD_GW   (AE Title gateway)"
Write-Host "    Remote host     : 127.0.0.1"
Write-Host "    Remote port     : 10401"
