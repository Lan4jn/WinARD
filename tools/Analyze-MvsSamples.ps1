<#
.SYNOPSIS
    Analyzes and compares Apple MVS (Encoding 1011) captured samples.
.DESCRIPTION
    Parses Setup quantization tables, Slice headers (Macroblock, Magic, QP), and bitstream payloads.
    Provides cross-sample comparison to assist in reverse-engineering entropy and macroblock syntax.
.PARAMETER SamplesDirectory
    Directory containing sample subdirectories. Default: artifacts/protocol-research/samples.
.PARAMETER SpecificSample
    Optional specific sample folder path to analyze in detail.
#>
[CmdletBinding()]
param(
    [string]$SamplesDirectory,
    [string]$SpecificSample
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $SamplesDirectory) {
    $SamplesDirectory = Join-Path $repoRoot 'artifacts\protocol-research\samples'
}

function Get-PayloadBytes {
    param([string]$Dir)
    $candidates = @('payload-prefix.bin', 'payload.bin')
    foreach ($cand in $candidates) {
        $path = Join-Path $Dir $cand
        if (Test-Path -LiteralPath $path) {
            $raw = [System.IO.File]::ReadAllBytes($path)
            if ($raw.Length -ge 4) {
                $decl = ($raw[0] -shl 24) -bor ($raw[1] -shl 16) -bor ($raw[2] -shl 8) -bor $raw[3]
                if ($decl -eq ($raw.Length - 4)) {
                    return ,($raw[4..($raw.Length - 1)])
                }
            }
            return ,$raw
        }
    }
    return $null
}

function Format-Table8x8 {
    param([byte[]]$TableBytes)
    $sb = New-Object System.Text.StringBuilder
    for ($r = 0; $r -lt 8; $r++) {
        $row = ($TableBytes[($r * 8)..($r * 8 + 7)] | ForEach-Object { $_.ToString().PadLeft(4) }) -join ' '
        [void]$sb.AppendLine("      $row")
    }
    return $sb.ToString().TrimEnd()
}

function Analyze-SingleSample {
    param([string]$SamplePath)

    $sampleName = Split-Path -Leaf $SamplePath
    Write-Host "`n============================================================" -ForegroundColor Cyan
    Write-Host " SAMPLE ANALYSIS: $sampleName" -ForegroundColor Yellow
    Write-Host " Path: $SamplePath" -ForegroundColor Gray
    Write-Host "============================================================" -ForegroundColor Cyan

    # 1. Setup analysis
    $setupDir = Join-Path $SamplePath 'setup'
    $setupBytes = Get-PayloadBytes -Dir $setupDir
    if ($setupBytes) {
        $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($setupBytes))
        Write-Host "`n[Setup Payload]" -ForegroundColor Green
        Write-Host "  Total Length : $($setupBytes.Length) bytes (Expected: 129)" -ForegroundColor White
        Write-Host "  SHA256       : $sha" -ForegroundColor White
        if ($setupBytes.Length -ge 129) {
            $mode = $setupBytes[0]
            Write-Host "  Mode Byte    : 0x$($mode.ToString('X2'))" -ForegroundColor White
            $lumaTable = $setupBytes[1..64]
            $chromaTable = $setupBytes[65..128]
            Write-Host "  Luma Quant Table (8x8):" -ForegroundColor Gray
            Write-Host (Format-Table8x8 -TableBytes $lumaTable) -ForegroundColor DarkGray
            Write-Host "  Chroma Quant Table (8x8):" -ForegroundColor Gray
            Write-Host (Format-Table8x8 -TableBytes $chromaTable) -ForegroundColor DarkGray
        }
    } else {
        Write-Host "`n[Setup Payload] Not present in this sample." -ForegroundColor DarkGray
    }

    # 2. Slice analysis
    $sliceDir = Join-Path $SamplePath 'slice'
    $sliceBytes = Get-PayloadBytes -Dir $sliceDir
    $sliceManifestPath = Join-Path $sliceDir 'manifest.json'
    if ($sliceBytes) {
        $sha = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($sliceBytes))
        Write-Host "`n[Slice Payload]" -ForegroundColor Green
        Write-Host "  Total Length : $($sliceBytes.Length) bytes" -ForegroundColor White
        Write-Host "  SHA256       : $sha" -ForegroundColor White

        if (Test-Path -LiteralPath $sliceManifestPath) {
            $manifest = Get-Content -LiteralPath $sliceManifestPath -Raw | ConvertFrom-Json
            Write-Host "  Dimensions   : $($manifest.rectangle.Width)x$($manifest.rectangle.Height) at ($($manifest.rectangle.X),$($manifest.rectangle.Y))" -ForegroundColor White
            Write-Host "  Completeness : $($manifest.completeness)" -ForegroundColor Green
            Write-Host "  Successor Msg: $($manifest.successorMessageType)" -ForegroundColor White
        }

        if ($sliceBytes.Length -ge 6) {
            $mbLimit = ($sliceBytes[0] -shl 8) -bor $sliceBytes[1]
            $magic = ($sliceBytes[2] -shl 8) -bor $sliceBytes[3]
            $qp = ($sliceBytes[4] -shl 8) -bor $sliceBytes[5]
            Write-Host "  Header Parsing:" -ForegroundColor Cyan
            Write-Host "    MB Local Limit: $mbLimit (0x$($mbLimit.ToString('X4')) -> $(if ($mbLimit -eq 15) { '16x16 macroblock' } else { 'custom' }))" -ForegroundColor White
            Write-Host "    Magic Word    : 0x$($magic.ToString('X4'))" -ForegroundColor White
            Write-Host "    QP / Factor   : $qp (0x$($qp.ToString('X4')))" -ForegroundColor White

            $entropyBytes = if ($sliceBytes.Length -gt 6) { $sliceBytes[6..($sliceBytes.Length - 1)] } else { @() }
            Write-Host "  Entropy Bitstream Length: $($entropyBytes.Length) bytes" -ForegroundColor Yellow
            if ($entropyBytes.Length -gt 0) {
                $hex = ($entropyBytes | ForEach-Object { $_.ToString('X2') }) -join ' '
                Write-Host "  Bitstream Hex : $hex" -ForegroundColor White
                
                # Bitwise string representation
                $bitStr = ($entropyBytes | ForEach-Object { [Convert]::ToString($_, 2).PadLeft(8, '0') }) -join ' '
                Write-Host "  Bitstream Bits: $bitStr" -ForegroundColor Gray
            }
        }
    } else {
        Write-Host "`n[Slice Payload] Not present in this sample." -ForegroundColor DarkGray
    }
}

if ($SpecificSample) {
    if (-not (Test-Path -LiteralPath $SpecificSample)) {
        throw "Specific sample path not found: $SpecificSample"
    }
    Analyze-SingleSample -SamplePath $SpecificSample
    exit 0
}

if (-not (Test-Path -LiteralPath $SamplesDirectory)) {
    Write-Host "No samples directory found at: $SamplesDirectory" -ForegroundColor Yellow
    exit 0
}

$sampleDirs = Get-ChildItem -LiteralPath $SamplesDirectory -Directory
if ($sampleDirs.Count -eq 0) {
    Write-Host "No sample subdirectories found in: $SamplesDirectory" -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($sampleDirs.Count) sample(s) in $SamplesDirectory. Running comparative summary..." -ForegroundColor Cyan

$summaryRows = @()
foreach ($dir in $sampleDirs) {
    $name = $dir.Name
    $sliceDir = Join-Path $dir.FullName 'slice'
    $setupDir = Join-Path $dir.FullName 'setup'
    $sliceManifestPath = Join-Path $sliceDir 'manifest.json'

    $setupBytes = Get-PayloadBytes -Dir $setupDir
    $setupLen = if ($setupBytes) { $setupBytes.Length } else { '-' }
    
    $sliceBytes = Get-PayloadBytes -Dir $sliceDir
    $sliceLen = if ($sliceBytes) { $sliceBytes.Length } else { '-' }
    $qp = '-'
    $magic = '-'
    $entropyLen = '-'
    $hexPrefix = '-'
    $rect = '-'

    if ($sliceBytes) {
        if (Test-Path -LiteralPath $sliceManifestPath) {
            $m = Get-Content -LiteralPath $sliceManifestPath -Raw | ConvertFrom-Json
            if ($m.rectangle) {
                $rect = "$($m.rectangle.Width)x$($m.rectangle.Height)"
            }
        }
        if ($sliceBytes.Length -ge 6) {
            $magic = "0x" + ((($sliceBytes[2] -shl 8) -bor $sliceBytes[3])).ToString('X4')
            $qp = (($sliceBytes[4] -shl 8) -bor $sliceBytes[5]).ToString()
            $entropyLen = ($sliceBytes.Length - 6).ToString()
            $hexPrefix = ($sliceBytes | Select-Object -First 10 | ForEach-Object { $_.ToString('X2') }) -join ' '
        }
    }

    $decodedRgb = '-'
    if ($sliceBytes -and $setupBytes -and ($sliceBytes.Length -ge 6) -and ($setupBytes.Length -ge 129)) {
        try {
            $dllPath = Join-Path $repoRoot 'src\WinARD.Remote.Protocol\bin\x64\Release\net8.0-windows10.0.19041.0\WinARD.Remote.Protocol.dll'
            if (-not (Test-Path -LiteralPath $dllPath)) {
                $dllPath = Join-Path $repoRoot 'src\WinARD.Remote.Protocol\bin\Release\net8.0-windows10.0.19041.0\WinARD.Remote.Protocol.dll'
            }
            if (Test-Path -LiteralPath $dllPath) {
                if (-not ('WinARD.Remote.Protocol.Encodings.Mvs.MvsMacroblockParser' -as [type])) {
                    Add-Type -Path $dllPath
                }
                $luma = $setupBytes[1..64]
                $chroma = $setupBytes[65..128]
                $bgra = New-Object byte[] 1024
                [WinARD.Remote.Protocol.Encodings.Mvs.MvsMacroblockParser]::DecodeMacroblock(
                    $sliceBytes, $luma, $chroma, $bgra)
                $b = $bgra[0]; $g = $bgra[1]; $r = $bgra[2]
                $decodedRgb = "R=$r G=$g B=$b"

                # 导出 16x16 32-bit BMP 预览
                $previewBmp = Join-Path $sliceDir 'rendered-preview.bmp'
                $fileSize = 54 + 1024
                $bmp = New-Object byte[] $fileSize
                $bmp[0] = 0x42; $bmp[1] = 0x4D
                [System.BitConverter]::GetBytes([uint32]$fileSize).CopyTo($bmp, 2)
                [System.BitConverter]::GetBytes([uint32]54).CopyTo($bmp, 10)
                [System.BitConverter]::GetBytes([uint32]40).CopyTo($bmp, 14)
                [System.BitConverter]::GetBytes([int32]16).CopyTo($bmp, 18)
                [System.BitConverter]::GetBytes([int32]-16).CopyTo($bmp, 22)
                [System.BitConverter]::GetBytes([uint16]1).CopyTo($bmp, 26)
                [System.BitConverter]::GetBytes([uint16]32).CopyTo($bmp, 28)
                [System.BitConverter]::GetBytes([uint32]0).CopyTo($bmp, 30)
                [System.BitConverter]::GetBytes([uint32]1024).CopyTo($bmp, 34)
                [System.Array]::Copy($bgra, 0, $bmp, 54, 1024)
                [System.IO.File]::WriteAllBytes($previewBmp, $bmp)
            }
        } catch {
            $decodedRgb = "Err"
        }
    }

    $summaryRows += [PSCustomObject]@{
        SampleName = $name
        SetupBytes = $setupLen
        SliceBytes = $sliceLen
        Rectangle  = $rect
        QP         = $qp
        EntropyLen = $entropyLen
        DecodedRGB = $decodedRgb
        SliceHex   = $hexPrefix
    }
}

Write-Host "`n--- CROSS-SAMPLE COMPARISON TABLE ---" -ForegroundColor Green
$summaryRows | Format-Table -AutoSize

Write-Host "`nTo inspect any sample in detail, run:" -ForegroundColor Cyan
Write-Host "  pwsh -File tools/Analyze-MvsSamples.ps1 -SpecificSample <sample-folder-path>" -ForegroundColor Gray
