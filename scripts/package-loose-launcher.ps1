param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [ValidateSet("SelfContained", "FrameworkDependent")]
    [string]$Kind = "SelfContained"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PackageDir)) {
    throw "Package directory not found: $PackageDir"
}

$startPs1 = @'
# Meccha Camouflage launcher — strips Mark of the Web (MOTW) then starts the app.
# GitHub/browser downloads attach Zone.Identifier; Smart App Control often blocks
# those while the same files built locally work. Unblock-File clears that mark.
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

Write-Host "Unblocking files in package (Mark of the Web)..."
Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        try { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue } catch {}
        # Belt-and-suspenders: delete Zone.Identifier ADS if still present
        $zone = $_.FullName + ":Zone.Identifier"
        if (Test-Path -LiteralPath $zone) {
            try { Remove-Item -LiteralPath $zone -Force -ErrorAction SilentlyContinue } catch {}
        }
    }

$dll = Join-Path $PSScriptRoot "meccha-camouflage.dll"
$exe = Join-Path $PSScriptRoot "meccha-camouflage.exe"

# Prefer Microsoft-signed host when possible (framework-dependent package, or
# self-contained that still has a managed entry DLL + installed desktop runtime).
$dotnet = $null
foreach ($candidate in @(
        (Join-Path ${env:ProgramFiles} "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe"),
        "dotnet"
    )) {
    if ($candidate -eq "dotnet") {
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($cmd) { $dotnet = $cmd.Source; break }
    }
    elseif (Test-Path -LiteralPath $candidate) {
        $dotnet = $candidate
        break
    }
}

function Test-DesktopRuntimePresent {
    param([string]$DotNetPath)
    try {
        $runtimes = & $DotNetPath --list-runtimes 2>$null
        return ($runtimes | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App 10\.' }).Count -gt 0
    } catch {
        return $false
    }
}

$launched = $false
if ($dotnet -and (Test-Path -LiteralPath $dll) -and (Test-DesktopRuntimePresent -DotNetPath $dotnet)) {
    Write-Host "Starting via Microsoft-signed host:"
    Write-Host "  $dotnet"
    Write-Host "  $dll"
    try {
        # Use exec so the process image is dotnet.exe (Authenticode signed).
        Start-Process -FilePath $dotnet -ArgumentList @("exec", "`"$dll`"") -WorkingDirectory $PSScriptRoot
        $launched = $true
    } catch {
        Write-Host "dotnet exec failed: $($_.Exception.Message)"
    }
}

if (-not $launched) {
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "Neither dotnet+dll launch nor meccha-camouflage.exe is available."
    }
    Write-Host "Starting apphost: $exe"
    Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot
}

Write-Host "If Windows still says the app was blocked: Windows Security -> App and browser control -> Smart App Control -> Off"
'@

$startBat = @'
@echo off
setlocal
cd /d "%~dp0"

REM Always go through PowerShell so we can Unblock-File (clear Mark of the Web).
where powershell >nul 2>&1
if errorlevel 1 (
  echo PowerShell is required to clear download marks and start the app.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0START.ps1"
if errorlevel 1 (
  echo.
  echo Launch failed. See messages above.
  pause
  exit /b 1
)
'@

$readMe = @"
Meccha Camouflage — unofficial package ($Kind)
================================================

IMPORTANT if you downloaded this from GitHub/browser:
  Always start with START.bat (or START.ps1).
  It removes Mark of the Web so Smart App Control is less likely to block files.

1. Extract the WHOLE folder somewhere local (Desktop is fine).
2. Start MECCHA CHAMELEON.
3. Double-click START.bat  (do not only copy meccha-camouflage.exe alone).

Launch order inside START.ps1:
  - Unblock every file in this folder
  - Prefer: Microsoft-signed  C:\Program Files\dotnet\dotnet.exe  exec meccha-camouflage.dll
  - Fallback: meccha-camouflage.exe

Requirements:
  - Windows 10/11 x64
$(if ($Kind -eq "FrameworkDependent") {
  "  - .NET 10 Desktop Runtime (WindowsDesktop.App 10.x)`n    https://dotnet.microsoft.com/download/dotnet/10.0"
} else {
  "  - Self-contained (runtime bundled). Still use START.bat after download."
})

Source: https://github.com/Luckisugar/meccha_auto_paint
Upstream: https://github.com/acentrist/MecchaCamouflage
License: GPL-3.0-or-later (unofficial modified build)
"@

Set-Content -LiteralPath (Join-Path $PackageDir "START.ps1") -Value $startPs1 -Encoding UTF8
Set-Content -LiteralPath (Join-Path $PackageDir "START.bat") -Value $startBat -Encoding ASCII
Set-Content -LiteralPath (Join-Path $PackageDir "README-START-HERE.txt") -Value $readMe -Encoding UTF8
Write-Host "Wrote START.ps1 / START.bat / README-START-HERE.txt to $PackageDir"
