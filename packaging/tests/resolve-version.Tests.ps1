[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$resolver = Join-Path $repoRoot 'packaging\resolve-version.ps1'

$mapped = & $resolver -Tag 'v0.2.0'
if ($mapped -cne '0.2.0.0') {
    throw "v0.2.0 mapped to an unexpected package version: $mapped"
}

$manual = & $resolver -Version '12.34.56.78'
if ($manual -cne '12.34.56.78') {
    throw "Manual version changed unexpectedly: $manual"
}

foreach ($invalidTag in @(
    '0.2.0',
    'v0.2',
    'v0.2.0.0',
    'v0.2.0-beta',
    'v01.2.3',
    'v65536.0.0',
    'v1.2.x',
    'v1/2/3'
)) {
    $rejected = $false
    try { & $resolver -Tag $invalidTag *> $null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid release tag was accepted: $invalidTag" }
}

foreach ($invalidManual in @(
    '01.2.3.4',
    '1.02.3.4',
    '1.2.03.4',
    '1.2.3.04',
    '+1.2.3.4',
    ' 1.2.3.4'
)) {
    $rejected = $false
    try { & $resolver -Version $invalidManual *> $null } catch { $rejected = $true }
    if (-not $rejected) { throw "Non-canonical manual version was accepted: $invalidManual" }
}

Write-Host 'Release version mapping fixtures passed.'
