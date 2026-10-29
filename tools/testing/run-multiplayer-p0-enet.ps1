param(
    [string]$Sts2GameRoot,
    [string]$RitsuWorkshopRoot,
    [string]$NoSolverPeerBuildDir,
    [ValidateRange(1024, 16384)]
    [int]$PeerMemoryReservationMiB = 1536,
    [ValidateSet(2, 4)]
    [int]$PlayerCount = 2,
    [ValidateRange(1, 65535)]
    [int]$Port = 33771,
    [switch]$VerifyControllerFullAuto,
    [switch]$VerifyControllerAutoNextTurn,
    [switch]$VerifyControllerRngDrift,
    [switch]$VerifyPaelsEyeExtraTurn,
    [switch]$VerifyRemoteExtraTurn,
    [switch]$VerifyPaelsEyeBothOwners,
    [ValidateRange(0, 16)]
    [int]$SearchMaxDegreeOfParallelismForTest = 0
)

$ErrorActionPreference = 'Stop'
if ($VerifyControllerAutoNextTurn -and !$VerifyControllerFullAuto) {
    throw 'VerifyControllerAutoNextTurn requires VerifyControllerFullAuto.'
}
if ($VerifyControllerRngDrift -and ($PlayerCount -ne 2 -or $VerifyControllerFullAuto)) {
    throw 'VerifyControllerRngDrift requires two players and manual controller execution.'
}
if ($VerifyPaelsEyeExtraTurn -and ($PlayerCount -ne 2 -or $VerifyControllerFullAuto -or $VerifyControllerRngDrift)) {
    throw 'VerifyPaelsEyeExtraTurn requires two players and scripted execution.'
}
if ($VerifyPaelsEyeBothOwners -and !$VerifyPaelsEyeExtraTurn) {
    throw 'VerifyPaelsEyeBothOwners requires VerifyPaelsEyeExtraTurn.'
}
if ($VerifyRemoteExtraTurn -and (!$VerifyPaelsEyeExtraTurn -or $VerifyPaelsEyeBothOwners)) {
    throw 'VerifyRemoteExtraTurn requires VerifyPaelsEyeExtraTurn and a single owner.'
}
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$session = Join-Path $repositoryRoot ('.local/multiplayer-p0/enet-' + $PlayerCount + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $session -Force | Out-Null
$processes = @()

foreach ($seat in 0..($PlayerCount - 1)) {
    $peer = Join-Path $session "peer-$seat"
    New-Item -ItemType Directory -Path $peer -Force | Out-Null
    $input = Join-Path $session "input-$seat.json"
    @{
        mode = if ($seat -eq 0) { 'host' } else { 'client' }
        playerCount = $PlayerCount
        seat = $seat
        port = $Port
        coordinationDirectory = $session
        expectedGameVersion = '0.111.0'
        verifyRootProjection = $true
        verifyActionDifferential = !$VerifyControllerFullAuto
        verifyRoundDifferential = !$VerifyControllerFullAuto
        verifyControllerFullAuto = [bool]$VerifyControllerFullAuto
        verifyControllerAutoNextTurn = [bool]$VerifyControllerAutoNextTurn
        verifyEnetControllerRng = [bool]$VerifyControllerRngDrift
        verifyEnetPaelsEyeExtraTurn = [bool]$VerifyPaelsEyeExtraTurn
        verifyEnetRemoteExtraTurn = [bool]$VerifyRemoteExtraTurn
        verifyEnetPaelsEyeBothOwners = [bool]$VerifyPaelsEyeBothOwners
    } | ConvertTo-Json | Set-Content -LiteralPath $input -Encoding utf8
    $instance = 'mp-p0-' + [Guid]::NewGuid().ToString('N')
    $arguments = @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-unattended-test.ps1'),
        '-ScenarioId', 'MULTIPLAYER-P0', '-MultiplayerProbePath', $input,
        '-EvidenceDirectory', $peer, '-HeadlessInstance', $instance,
        '-HeadlessExecutionMode', 'parallel', '-HeadlessMemoryReservationMiB', [string]$PeerMemoryReservationMiB,
        '-HeadlessCpuReservation', '1',
        '-TimeoutSeconds', '120', '-ExitOnComplete', '-CleanupInstanceOnExit'
    )
    if ($Sts2GameRoot) { $arguments += @('-Sts2GameRoot', $Sts2GameRoot) }
    if ($RitsuWorkshopRoot) { $arguments += @('-RitsuWorkshopRoot', $RitsuWorkshopRoot) }
    if ($seat -eq 1 -and $NoSolverPeerBuildDir) {
        $arguments += @('-CombatSolverBuildDir', $NoSolverPeerBuildDir,
            '-ReplacementTestModId', 'NoSolverPeerProbe')
    }
    if ($SearchMaxDegreeOfParallelismForTest -gt 0) {
        $arguments += @('-SearchMaxDegreeOfParallelismForTest', [string]$SearchMaxDegreeOfParallelismForTest)
    }
    if ($VerifyControllerRngDrift) {
        $arguments += @('-EncounterId', 'CULTISTS_NORMAL', '-DeploymentInterActionDelaySecondsForTest', '3')
    }
    if ($VerifyPaelsEyeExtraTurn) {
        $arguments += @('-EncounterId', 'FUZZY_WURM_CRAWLER_WEAK')
    }
    # Start-Process joins ArgumentList with spaces; quote paths before starting pwsh.
    $quotedArguments = $arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }
    $processes += Start-Process -FilePath 'pwsh' -ArgumentList $quotedArguments `
        -WorkingDirectory $repositoryRoot -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $peer 'stdout.txt') `
        -RedirectStandardError (Join-Path $peer 'stderr.txt') -PassThru
}

$processes | Wait-Process
foreach ($seat in 0..($PlayerCount - 1)) {
    $processes[$seat].Refresh()
    $resultPath = Join-Path $session "peer-$seat/result.json"
    if ($processes[$seat].ExitCode -ne 0 -or ![IO.File]::Exists($resultPath)) {
        throw "ENet peer $seat failed; inspect $session/peer-$seat."
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($result.status -ne 'Passed') {
        throw "ENet peer $seat reported $($result.status); inspect $session/peer-$seat."
    }
}
Write-Output "MULTIPLAYER_P0_ENET_OK evidence=$session peers=$PlayerCount"
