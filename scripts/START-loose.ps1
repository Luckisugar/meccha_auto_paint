# Meccha Camouflage launcher â€” strips Mark of the Web (MOTW) then starts the app.
# GitHub/browser downloads attach Zone.Identifier; Smart App Control often blocks
# those while the same files built locally work. Unblock-File clears that mark.
$ErrorActionPreference = "Stop"
Set-Location -LiteralPath $PSScriptRoot

Write-Host "Attempting to unblock files in package (Mark of the Web)..."
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

Write-Host "If Windows still says the app was blocked.... there's no other way..: Execute order sixty six. Windows Security -> App and browser control -> Smart App Control -> Off"
