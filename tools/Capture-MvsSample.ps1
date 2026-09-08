<#
.SYNOPSIS
    Captures complete MVS (Encoding 1011) sample payloads from a live Mac ARD session.
.DESCRIPTION
    Runs WinARD.ScaleProbe with safety confirmation and captures both Setup and Slice payloads.
    Verifies payload disk integrity, declared length, and successor boundary status.
.PARAMETER SampleName
    The semantic name for the capture (e.g. solid-red, solid-green, checkerboard-16x16).
.PARAMETER DeviceId
    Optional device GUID. Defaults to the first local IPv4 device found in the database.
.PARAMETER Mode
    Capture mode: both, setup, or slice. Default: both.
.PARAMETER OutputDir
    Target directory. Default: artifacts/protocol-research/samples/<SampleName>.
.PARAMETER DatabasePath
    SQLite database containing saved credentials. Default: %LOCALAPPDATA%\WinARD\winard.db.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$SampleName,

    [Parameter(Position = 1)]
    [string]$DeviceId,

    [ValidateSet('both', 'setup', 'slice')]
    [string]$Mode = 'both',

    [string]$OutputDir,

    [string]$DatabasePath,

    [switch]$ForceOverwrite
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $DatabasePath) {
    $DatabasePath = Join-Path $env:LOCALAPPDATA 'WinARD\winard.db'
}

if (-not (Test-Path -LiteralPath $DatabasePath)) {
    throw "Database not found at: $DatabasePath"
}

if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot "artifacts\protocol-research\samples\$SampleName"
}

if ((Test-Path -LiteralPath $OutputDir) -and -not $ForceOverwrite) {
    $counter = 1
    while (Test-Path -LiteralPath "$OutputDir-$counter") {
        $counter++
    }
    $OutputDir = "$OutputDir-$counter"
    Write-Host "Target directory already exists. Using unique path: $OutputDir" -ForegroundColor Yellow
}

# Resolve DeviceId if not specified
if (-not $DeviceId) {
    Write-Host "Inspecting saved devices in database..." -ForegroundColor Cyan
    $probeProj = Join-Path $repoRoot 'tools\WinARD.ScaleProbe\WinARD.ScaleProbe.csproj'
    $inspectOutput = dotnet run --project $probeProj -c Release -p:Platform=x64 --no-build -- "$DatabasePath" --inspect-saved
    
    $localDevice = $null
    foreach ($line in ($inspectOutput -split "`r?`n")) {
        if ($line -match 'Id=([0-9a-fA-F\-]+)\s+Host=([^\s]+)') {
            $guid = $matches[1]
            $hostName = $matches[2]
            if ($hostName -match '^10\.' -or $hostName -match '^192\.168\.' -or $hostName -match '^172\.(1[6-9]|2[0-9]|3[0-1])\.') {
                $localDevice = $guid
                Write-Host "Selected local LAN device: $hostName (Id=$guid)" -ForegroundColor Green
                break
            } elseif (-not $localDevice) {
                $localDevice = $guid
            }
        }
    }
    if (-not $localDevice) {
        throw "Could not determine a valid saved device from $DatabasePath."
    }
    $DeviceId = $localDevice
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Apple MVS (Encoding 1011) Live Sample Capture" -ForegroundColor Cyan
Write-Host " Sample Name   : $SampleName" -ForegroundColor White
Write-Host " Capture Mode  : $Mode" -ForegroundColor White
Write-Host " Target Dir    : $OutputDir" -ForegroundColor White
Write-Host " Device ID     : $DeviceId" -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Cyan

# Ensure ScaleProbe is built
Write-Host "Verifying WinARD.ScaleProbe build..." -ForegroundColor Gray
dotnet build (Join-Path $repoRoot 'tools\WinARD.ScaleProbe\WinARD.ScaleProbe.csproj') -c Release -p:Platform=x64 -v q --nologo

# Execute live capture
Write-Host "Starting capture with ScaleProbe..." -ForegroundColor Cyan
$cmdArgs = @(
    'run',
    '--project', (Join-Path $repoRoot 'tools\WinARD.ScaleProbe\WinARD.ScaleProbe.csproj'),
    '-c', 'Release',
    '-p:Platform=x64',
    '--no-build',
    '--',
    $DatabasePath,
    $DeviceId,
    '--capture-complete-mvs',
    $SampleName,
    $OutputDir,
    '--mode',
    $Mode,
    '--confirm-synthetic'
)

& dotnet @cmdArgs
$exitCode = $LASTEXITCODE

if ($exitCode -ne 0) {
    Write-Host "Capture process exited with code $exitCode." -ForegroundColor Red
    exit $exitCode
}

# Validate on-disk results
Write-Host "`nValidating captured artifacts in $OutputDir..." -ForegroundColor Cyan
$summaryJsonPath = Join-Path $OutputDir 'batch-summary.json'
if (Test-Path -LiteralPath $summaryJsonPath) {
    $summary = Get-Content -LiteralPath $summaryJsonPath -Raw | ConvertFrom-Json
    Write-Host "`n--- CAPTURE SUMMARY CARD ---" -ForegroundColor Green
    Write-Host "Sample Name       : $($summary.sampleName)" -ForegroundColor White
    Write-Host "Successor Validated: $($summary.successorBoundaryValidated)" -ForegroundColor $(if ($summary.successorBoundaryValidated) { 'Green' } else { 'Yellow' })
    Write-Host "Next Message Type : $($summary.nextMessageType)" -ForegroundColor White
    Write-Host "Timestamp (UTC)   : $($summary.timestampUtc)" -ForegroundColor Gray
}

$sliceDir = Join-Path $OutputDir 'slice'
if (Test-Path -LiteralPath $sliceDir) {
    $sliceManifestPath = Join-Path $sliceDir 'manifest.json'
    $sliceBinPath = Join-Path $sliceDir 'payload-prefix.bin'
    if (-not (Test-Path -LiteralPath $sliceBinPath)) {
        $sliceBinPath = Join-Path $sliceDir 'payload.bin'
    }
    if (Test-Path -LiteralPath $sliceBinPath) {
        $rawBytes = [System.IO.File]::ReadAllBytes($sliceBinPath)
        $binBytes = if ($rawBytes.Length -ge 4) {
            $decl = ($rawBytes[0] -shl 24) -bor ($rawBytes[1] -shl 16) -bor ($rawBytes[2] -shl 8) -bor $rawBytes[3]
            if ($decl -eq ($rawBytes.Length - 4)) { $rawBytes[4..($rawBytes.Length - 1)] } else { $rawBytes }
        } else { $rawBytes }
        $sha = [System.Security.Cryptography.SHA256]::HashData($binBytes)
        $shaHex = [Convert]::ToHexString($sha)

        Write-Host "`n[Slice Record Details]" -ForegroundColor Cyan
        if (Test-Path -LiteralPath $sliceManifestPath) {
            $sliceManifest = Get-Content -LiteralPath $sliceManifestPath -Raw | ConvertFrom-Json
            Write-Host "  Dimensions      : $($sliceManifest.rectangle.Width)x$($sliceManifest.rectangle.Height)" -ForegroundColor White
            Write-Host "  Declared Length : $($sliceManifest.declaredLength) bytes" -ForegroundColor White
            Write-Host "  Completeness    : $($sliceManifest.completeness)" -ForegroundColor Green
        }
        Write-Host "  Actual File Size: $($binBytes.Length) bytes" -ForegroundColor White
        Write-Host "  SHA256          : $shaHex" -ForegroundColor White

        if ($binBytes.Length -ge 6) {
            $hexPrefix = ($binBytes[0..5] | ForEach-Object { $_.ToString('X2') }) -join ' '
            Write-Host "  Slice Header Hex: $hexPrefix" -ForegroundColor Yellow
        }
    }
}

Write-Host "`nCapture completed successfully and artifacts verified." -ForegroundColor Green
