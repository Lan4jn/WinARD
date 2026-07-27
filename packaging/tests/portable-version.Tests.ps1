[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$portable = Join-Path $repoRoot 'packaging\portable.ps1'

& $portable -Version '0.2.0.0' -ValidateVersionOnly

foreach ($invalid in @(
    '0.2.0',
    'v0.2.0',
    '1.2.3.4.5',
    '65536.0.0.0',
    '-1.0.0.0',
    '1.2.3-beta.0',
    '01.2.3.4',
    '+1.2.3.4',
    ' 1.2.3.4'
)) {
    $rejected = $false
    try {
        & $portable -Version $invalid -ValidateVersionOnly *> $null
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Invalid package version was accepted: $invalid"
    }
}

Write-Host 'Portable package version validation fixtures passed.'
