[CmdletBinding()]
param(
    [string]$ExpectedVersion = '0.1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$verifier = Join-Path $repoRoot 'packaging\verify-artifacts.ps1'
$realArtifacts = Join-Path $repoRoot 'artifacts'
$requiredPayloads = @(
    'WinARD.Desktop.exe',
    'WinARD.Desktop.dll',
    'WinARD.OpenSshAskPass.exe',
    'Microsoft.UI.dll',
    'e_sqlite3.dll'
)

function Add-ZipEntry {
    param(
        [IO.Compression.ZipArchive]$Archive,
        [string]$Name,
        [byte[]]$Bytes
    )
    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    try {
        if ($null -ne $Bytes -and $Bytes.Length -gt 0) {
            $stream.Write($Bytes, 0, $Bytes.Length)
        }
    } finally {
        $stream.Dispose()
    }
}

function New-PeBytes {
    param([int]$Length = 2048)
    $bytes = [byte[]]::new($Length)
    $bytes[0] = 0x4d
    $bytes[1] = 0x5a
    return $bytes
}

function Write-Checksums {
    param([string]$Root)
    $lines = foreach ($name in @('WinARD.msix', 'WinARD-portable-win-x64.zip')) {
        $path = Join-Path $Root $name
        $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
        '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), $name
    }
    [IO.File]::WriteAllLines(
        (Join-Path $Root 'SHA256SUMS.txt'),
        $lines,
        [Text.UTF8Encoding]::new($false))
}

function New-SyntheticArtifacts {
    param(
        [string]$Root,
        [switch]$TinyPayloads,
        [switch]$EmptyPayloads,
        [switch]$OmitManifest,
        [switch]$OmitContentTypes,
        [switch]$OmitBlockMap
    )
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    $payload = if ($EmptyPayloads) {
        [byte[]]::new(0)
    } elseif ($TinyPayloads) {
        [byte[]]@(0x41)
    } else {
        New-PeBytes
    }
    $portablePath = Join-Path $Root 'WinARD-portable-win-x64.zip'
    $portable = [IO.Compression.ZipFile]::Open(
        $portablePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $requiredPayloads) {
            Add-ZipEntry -Archive $portable -Name $name -Bytes $payload
        }
    } finally {
        $portable.Dispose()
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="WinARD" Publisher="CN=WinARD Development" Version="$ExpectedVersion" ProcessorArchitecture="x64" />
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Applications><Application Id="App" Executable="WinARD.Desktop.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
  <Capabilities><Capability Name="internetClient" /><Capability Name="privateNetworkClientServer" /></Capabilities>
</Package>
"@
    $msixPath = Join-Path $Root 'WinARD.msix'
    $msix = [IO.Compression.ZipFile]::Open(
        $msixPath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        if (-not $OmitManifest) {
            Add-ZipEntry -Archive $msix -Name 'AppxManifest.xml' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes($manifest))
        }
        if (-not $OmitContentTypes) {
            Add-ZipEntry -Archive $msix -Name '[Content_Types].xml' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('<Types />'))
        }
        if (-not $OmitBlockMap) {
            Add-ZipEntry -Archive $msix -Name 'AppxBlockMap.xml' `
                -Bytes ([Text.Encoding]::UTF8.GetBytes('<BlockMap />'))
        }
        foreach ($name in $requiredPayloads) {
            Add-ZipEntry -Archive $msix -Name $name -Bytes $payload
        }
    } finally {
        $msix.Dispose()
    }
    Write-Checksums -Root $Root
}

function Assert-Accepted {
    param([string]$Root)
    & $verifier -ArtifactsDirectory $Root -ExpectedVersion $ExpectedVersion *> $null
}

function Assert-Rejected {
    param([string]$Root, [string]$Name)
    $rejected = $false
    try {
        & $verifier -ArtifactsDirectory $Root -ExpectedVersion $ExpectedVersion *> $null
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Malicious artifact fixture was accepted: $Name"
    }
}

function New-CaseRoot {
    param([string]$Parent, [string]$Name)
    $root = Join-Path $Parent $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    foreach ($file in @('WinARD.msix', 'WinARD-portable-win-x64.zip', 'SHA256SUMS.txt')) {
        $source = Join-Path $realArtifacts $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Build real release artifacts before running verifier fixtures: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $root $file)
    }
    return $root
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('WinARD-artifact-tests-{0:N}' -f [Guid]::NewGuid())
try {
    $valid = New-CaseRoot -Parent $testRoot -Name 'valid'
    Assert-Accepted -Root $valid

    $fakeMz = Join-Path $testRoot 'fake-mz'
    New-SyntheticArtifacts -Root $fakeMz
    Assert-Rejected -Root $fakeMz -Name 'fake MSIX BlockMap and Content_Types metadata'

    $fakePortablePe = New-CaseRoot -Parent $testRoot -Name 'fake-portable-pe'
    $zip = [IO.Compression.ZipFile]::Open(
        (Join-Path $fakePortablePe 'WinARD-portable-win-x64.zip'),
        [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($name in @('WinARD.Desktop.exe', 'WinARD.Desktop.dll')) {
            $zip.GetEntry($name).Delete()
            Add-ZipEntry -Archive $zip -Name $name -Bytes (New-PeBytes)
        }
    } finally {
        $zip.Dispose()
    }
    Write-Checksums -Root $fakePortablePe
    Assert-Rejected -Root $fakePortablePe -Name 'MZ header followed by zero bytes'

    foreach ($missing in @('manifest', 'content-types', 'block-map')) {
        $root = Join-Path $testRoot $missing
        New-SyntheticArtifacts -Root $root `
            -OmitManifest:($missing -eq 'manifest') `
            -OmitContentTypes:($missing -eq 'content-types') `
            -OmitBlockMap:($missing -eq 'block-map')
        Assert-Rejected -Root $root -Name "missing $missing"
    }

    $tiny = Join-Path $testRoot 'tiny'
    New-SyntheticArtifacts -Root $tiny -TinyPayloads
    Assert-Rejected -Root $tiny -Name 'one-byte PE payloads'
    $empty = Join-Path $testRoot 'empty'
    New-SyntheticArtifacts -Root $empty -EmptyPayloads
    Assert-Rejected -Root $empty -Name 'empty PE payloads'

    $unsafePaths = @(
        '../escape',
        '/rooted',
        'C:/rooted',
        'folder//empty',
        'folder/./dot',
        'folder/../parent',
        'ambiguous\path'
    )
    foreach ($unsafePath in $unsafePaths) {
        $root = New-CaseRoot -Parent $testRoot -Name ('path-{0:N}' -f [Guid]::NewGuid())
        $zip = [IO.Compression.ZipFile]::Open(
            (Join-Path $root 'WinARD-portable-win-x64.zip'),
            [IO.Compression.ZipArchiveMode]::Update)
        try {
            Add-ZipEntry -Archive $zip -Name $unsafePath -Bytes ([byte[]]@(1))
        } finally {
            $zip.Dispose()
        }
        Write-Checksums -Root $root
        Assert-Rejected -Root $root -Name "unsafe path $unsafePath"
    }

    foreach ($duplicateNames in @(
        @('duplicate.txt', 'DUPLICATE.txt'),
        @('folder/file.txt', 'folder\file.txt'),
        @('WinARD.Desktop.exe', 'WinARD.Desktop.exe')
    )) {
        $root = New-CaseRoot -Parent $testRoot -Name ('duplicate-{0:N}' -f [Guid]::NewGuid())
        $zip = [IO.Compression.ZipFile]::Open(
            (Join-Path $root 'WinARD-portable-win-x64.zip'),
            [IO.Compression.ZipArchiveMode]::Update)
        try {
            foreach ($name in $duplicateNames) {
                Add-ZipEntry -Archive $zip -Name $name -Bytes ([byte[]]@(1))
            }
        } finally {
            $zip.Dispose()
        }
        Write-Checksums -Root $root
        Assert-Rejected -Root $root -Name "duplicate/ambiguous paths $($duplicateNames -join ', ')"
    }

    $duplicateManifest = New-CaseRoot -Parent $testRoot -Name 'duplicate-manifest'
    $msix = [IO.Compression.ZipFile]::Open(
        (Join-Path $duplicateManifest 'WinARD.msix'),
        [IO.Compression.ZipArchiveMode]::Update)
    try {
        Add-ZipEntry -Archive $msix -Name 'appxmanifest.xml' `
            -Bytes ([Text.Encoding]::UTF8.GetBytes('<Package />'))
    } finally {
        $msix.Dispose()
    }
    Write-Checksums -Root $duplicateManifest
    Assert-Rejected -Root $duplicateManifest -Name 'duplicate case-insensitive MSIX manifest'

    $checksumMutations = @{
        missing = @('0' * 64 + '  WinARD.msix')
        extra = @(
            '0' * 64 + '  WinARD.msix',
            '0' * 64 + '  WinARD-portable-win-x64.zip',
            '0' * 64 + '  extra.bin'
        )
        duplicate = @(
            '0' * 64 + '  WinARD.msix',
            '1' * 64 + '  WinARD.msix'
        )
        invalidHex = @(
            'z' * 64 + '  WinARD.msix',
            '0' * 64 + '  WinARD-portable-win-x64.zip'
        )
        wrongName = @(
            '0' * 64 + '  winard.msix',
            '0' * 64 + '  WinARD-portable-win-x64.zip'
        )
    }
    foreach ($mutation in $checksumMutations.GetEnumerator()) {
        $root = New-CaseRoot -Parent $testRoot -Name "checksum-$($mutation.Key)"
        [IO.File]::WriteAllLines(
            (Join-Path $root 'SHA256SUMS.txt'),
            [string[]]$mutation.Value,
            [Text.UTF8Encoding]::new($false))
        Assert-Rejected -Root $root -Name "checksum $($mutation.Key)"
    }

    foreach ($invalidVersion in @(
        '01.2.3.4',
        '1.02.3.4',
        '1.2.03.4',
        '1.2.3.04',
        '+1.2.3.4',
        ' 1.2.3.4'
    )) {
        $rejected = $false
        try {
            & $verifier -ArtifactsDirectory $valid -ExpectedVersion $invalidVersion *> $null
        } catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "Verifier accepted a non-canonical expected version: $invalidVersion"
        }
    }

    Write-Host 'Artifact verifier fixtures passed: valid, metadata, PE, path, duplicate, checksum and version cases.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
