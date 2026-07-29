param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$BridgePath,
    [Parameter(Mandatory = $true)][string]$InjectorPath,
    [Parameter(Mandatory = $true)][string]$GenerationDirectory,
    [string]$ProfileDirectory = "",
    [ValidateRange(1, 20)][int]$MaxGenerations = 8
)

$ErrorActionPreference = "Stop"

function Convert-HexToBytes([string]$Hex) {
    if (($Hex.Length % 2) -ne 0) {
        throw "Hex value has odd length."
    }
    $bytes = New-Object byte[] ($Hex.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte(
            $Hex.Substring($index * 2, 2),
            16)
    }
    return $bytes
}

function Convert-BytesToHex([byte[]]$Bytes) {
    return (($Bytes | ForEach-Object {
        $_.ToString("x2")
    }) -join "")
}

function Write-U32(
    [byte[]]$Buffer,
    [int]$Offset,
    [uint32]$Value) {
    [Array]::Copy(
        [BitConverter]::GetBytes($Value),
        0,
        $Buffer,
        $Offset,
        4)
}

function Quote-ProcessArg([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Read-ResidentCore([int]$ProcessId) {
    $mapping = $null
    $view = $null
    try {
        $mapping = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
            "Local\MecchaCamouflage.ResidentCore.$ProcessId",
            [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $view = $mapping.CreateViewStream(
            0,
            104,
            [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        $bytes = New-Object byte[] 104
        $read = 0
        while ($read -lt $bytes.Length) {
            $count = $view.Read(
                $bytes,
                $read,
                $bytes.Length - $read)
            if ($count -eq 0) {
                throw "Resident core mapping ended early."
            }
            $read += $count
        }
        if ([BitConverter]::ToUInt32($bytes, 0) -ne 0x3152434D -or
            [BitConverter]::ToUInt32($bytes, 4) -ne 104 -or
            [BitConverter]::ToUInt32($bytes, 8) -ne 1 -or
            [BitConverter]::ToUInt32($bytes, 12) -ne $ProcessId) {
            throw "Resident core identity is invalid."
        }
        $instanceBytes = New-Object byte[] 16
        $tokenBytes = New-Object byte[] 32
        $hashBytes = New-Object byte[] 32
        [Array]::Copy($bytes, 24, $instanceBytes, 0, 16)
        [Array]::Copy($bytes, 40, $tokenBytes, 0, 32)
        [Array]::Copy($bytes, 72, $hashBytes, 0, 32)
        return [pscustomobject]@{
            Port = [int][BitConverter]::ToUInt32($bytes, 16)
            Protocol = [int][BitConverter]::ToUInt32($bytes, 20)
            InstanceId = Convert-BytesToHex $instanceBytes
            Token = Convert-BytesToHex $tokenBytes
            Hash = Convert-BytesToHex $hashBytes
        }
    }
    catch [System.IO.FileNotFoundException] {
        return $null
    }
    finally {
        if ($null -ne $view) {
            $view.Dispose()
        }
        if ($null -ne $mapping) {
            $mapping.Dispose()
        }
    }
}

function Invoke-BridgeCommand(
    [object]$Resident,
    [string]$Command) {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $client.Connect("127.0.0.1", $Resident.Port)
        $stream = $client.GetStream()
        $reader = [System.IO.StreamReader]::new(
            $stream,
            $encoding,
            $false,
            4096,
            $true)
        $writer = [System.IO.StreamWriter]::new(
            $stream,
            $encoding,
            4096,
            $true)
        try {
            $writer.AutoFlush = $true
            $hello = @{
                type = "hello"
                bootstrap_protocol = $Resident.Protocol
                instance_id = $Resident.InstanceId
                token = $Resident.Token
            } | ConvertTo-Json -Compress
            $writer.WriteLine($hello)
            $helloReply = $reader.ReadLine()
            if (-not $helloReply) {
                throw "Bridge returned no hello reply."
            }
            $helloObject = $helloReply | ConvertFrom-Json
            if (-not $helloObject.success) {
                throw "Bridge authentication failed."
            }
            $writer.WriteLine($Command)
            $replyText = $reader.ReadToEnd()
            if (-not $replyText) {
                throw "Bridge returned no command reply."
            }
            return ($replyText | ConvertFrom-Json)
        }
        finally {
            $writer.Dispose()
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}

function Wait-ResidentCoreAbsent(
    [int]$ProcessId,
    [int]$TimeoutMilliseconds = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds(
        $TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq (Read-ResidentCore $ProcessId)) {
            return
        }
        Start-Sleep -Milliseconds 25
    }
    throw "Old resident core mapping remained published after shutdown."
}

function New-StartBlock(
    [int]$ProcessId,
    [long]$CreationTime,
    [string]$TargetExe,
    [string]$Hash) {
    $instanceId = [Guid]::NewGuid().ToString("N")
    $tokenBytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($tokenBytes)
    }
    finally {
        $rng.Dispose()
    }
    $block = New-Object byte[] 128
    Write-U32 $block 0 0x3153434D
    Write-U32 $block 4 128
    Write-U32 $block 8 1
    Write-U32 $block 12 ([uint32]$ProcessId)
    [Array]::Copy(
        (Convert-HexToBytes $instanceId),
        0,
        $block,
        16,
        16)
    [Array]::Copy($tokenBytes, 0, $block, 32, 32)
    [Array]::Copy(
        (Convert-HexToBytes $Hash),
        0,
        $block,
        64,
        32)
    Write-U32 $block 96 0
    Write-U32 $block 100 0
    Write-U32 $block 104 0
    Write-U32 $block 108 1
    Write-U32 $block 112 0
    Write-U32 $block 116 0
    return [pscustomobject]@{
        Block = $block
        ProcessId = $ProcessId
        CreationTime = $CreationTime
        TargetExe = $TargetExe
        InstanceId = $instanceId
        Token = Convert-BytesToHex $tokenBytes
        Hash = $Hash
    }
}

function Invoke-Injector(
    [object]$Start,
    [string]$StagedBridgePath,
    [string]$StagedInjectorPath) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $StagedInjectorPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = @(
        (Quote-ProcessArg "--direct"),
        (Quote-ProcessArg $Start.ProcessId.ToString()),
        (Quote-ProcessArg $Start.CreationTime.ToString()),
        (Quote-ProcessArg $Start.TargetExe),
        (Quote-ProcessArg $StagedBridgePath)
    ) -join " "
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) {
        throw "Could not start the injector."
    }
    try {
        $process.StandardInput.BaseStream.Write(
            $Start.Block,
            0,
            $Start.Block.Length)
        $process.StandardInput.BaseStream.Flush()
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "Injector failed: exit=$($process.ExitCode) stdout=$stdout stderr=$stderr"
        }
        $line = $stdout -split "`r?`n" |
            Where-Object { $_.Trim().Length -gt 0 } |
            Select-Object -Last 1
        if (-not $line) {
            throw "Injector produced no result: $stderr"
        }
        return ($line | ConvertFrom-Json)
    }
    finally {
        $process.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $BridgePath -PathType Leaf)) {
    throw "BridgePath not found."
}
if (-not (Test-Path -LiteralPath $InjectorPath -PathType Leaf)) {
    throw "InjectorPath not found."
}
if ([string]::IsNullOrWhiteSpace($ProfileDirectory)) {
    $repositoryRoot = Split-Path -Parent (
        Split-Path -Parent $PSScriptRoot)
    $ProfileDirectory = Join-Path `
        $repositoryRoot `
        "resources\mesh-profiles"
}
if (-not (Test-Path -LiteralPath $ProfileDirectory -PathType Container)) {
    throw "ProfileDirectory not found."
}

$target = Get-Process -Id $TargetPid
$targetExe = $target.Path
$creationTime = $target.StartTime.ToUniversalTime().ToFileTimeUtc()
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $BridgePath).
    Hash.ToLowerInvariant()
New-Item -ItemType Directory -Force -Path $GenerationDirectory |
    Out-Null
$stagedProfileDirectory = Join-Path `
    $GenerationDirectory `
    "mesh-profiles"
New-Item -ItemType Directory -Force -Path $stagedProfileDirectory |
    Out-Null
Get-ChildItem -LiteralPath $ProfileDirectory -Filter "*.json" -File |
    ForEach-Object {
        Copy-Item `
            -LiteralPath $_.FullName `
            -Destination (Join-Path $stagedProfileDirectory $_.Name) `
            -Force
    }
$aliasName = "runtime-bridge-hot-$hash.dll"
$aliasPath = Join-Path $GenerationDirectory $aliasName
$existingGenerations = @(
    Get-ChildItem -LiteralPath $GenerationDirectory `
        -Filter "runtime-bridge-hot-*.dll" `
        -File
)
if (-not (Test-Path -LiteralPath $aliasPath -PathType Leaf) -and
    $existingGenerations.Count -ge $MaxGenerations) {
    throw "Hot-reload generation cap reached ($MaxGenerations). Restart the game before loading another native generation."
}
if (-not (Test-Path -LiteralPath $aliasPath -PathType Leaf)) {
    Copy-Item -LiteralPath $BridgePath -Destination $aliasPath
}
$aliasHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $aliasPath).
    Hash.ToLowerInvariant()
if ($aliasHash -ne $hash) {
    throw "Content-addressed bridge alias hash mismatch."
}

$mutexName = "Local\MecchaCamouflage.Inject.$TargetPid"
$mutex = [System.Threading.Mutex]::new($false, $mutexName)
$ownsMutex = $false
$oldHash = ""
$shutdownStage = "not_needed"
try {
    try {
        $ownsMutex = $mutex.WaitOne([TimeSpan]::FromSeconds(30))
    }
    catch [System.Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        throw "Timed out waiting for the bridge injection mutex."
    }

    $resident = Read-ResidentCore $TargetPid
    if ($null -ne $resident) {
        $oldHash = $resident.Hash
        $shutdown = Invoke-BridgeCommand $resident '{"type":"shutdown"}'
        $shutdownStage = [string]$shutdown.stage
        if (-not $shutdown.success -or
            -not $shutdown.metadata.active_paint_quiescent -or
            -not $shutdown.metadata.hook_callbacks_quiescent) {
            throw "Resident bridge did not prove active_paint_quiescent and hook_callbacks_quiescent."
        }
        Wait-ResidentCoreAbsent $TargetPid
    }

    $start = New-StartBlock `
        $TargetPid `
        $creationTime `
        $targetExe `
        $hash
    $injector = Invoke-Injector `
        $start `
        $aliasPath `
        $InjectorPath
    if (-not $injector.success -or
        $injector.state -ne "listening") {
        throw "New bridge generation did not reach listening state."
    }
    $newResident = [pscustomobject]@{
        Port = [int]$injector.port
        Protocol = 1
        InstanceId = $start.InstanceId
        Token = $start.Token
    }
    $ping = Invoke-BridgeCommand $newResident '{"type":"ping"}'
    if (-not $ping.success -or $ping.stage -ne "ping") {
        throw "New bridge generation failed its authenticated ping."
    }
    $published = Read-ResidentCore $TargetPid
    if ($null -eq $published -or $published.Hash -ne $hash) {
        throw "New resident core was not published with the staged content hash."
    }

    [pscustomobject]@{
        success = $true
        target_pid = $TargetPid
        old_bridge_hash = $oldHash
        new_bridge_hash = $hash
        staged_bridge = $aliasPath
        generation_count = @(
            Get-ChildItem -LiteralPath $GenerationDirectory `
                -Filter "runtime-bridge-hot-*.dll" `
                -File
        ).Count
        max_generations = $MaxGenerations
        shutdown_stage = $shutdownStage
        injector_state = $injector.state
        port = [int]$injector.port
        resident_core_published = $true
        credentials = "redacted"
    } | ConvertTo-Json -Depth 6
}
finally {
    if ($ownsMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
