[CmdletBinding()]
param(
    [string]$ExpectedVersion = '0.1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$portable = Join-Path $repoRoot 'artifacts\portable-win-x64'
$exePath = Join-Path $portable 'WinARD.Desktop.exe'
$dllPath = Join-Path $portable 'WinARD.Desktop.dll'

$exe = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$dll = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
foreach ($actual in @(
    $exe.FileVersion,
    $exe.ProductVersion,
    $dll.FileVersion,
    $dll.ProductVersion,
    $assemblyVersion
)) {
    if ($actual -cne $ExpectedVersion) {
        throw "Binary version mismatch. Expected $ExpectedVersion, found $actual."
    }
}

Write-Host "Portable binary versions match $ExpectedVersion."
