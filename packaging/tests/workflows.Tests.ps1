[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ci = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\ci.yml') -Raw
$package = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\package.yml') -Raw

function Assert-Match {
    param([string]$Text, [string]$Pattern, [string]$Message)
    if ($Text -notmatch $Pattern) { throw $Message }
}

foreach ($workflow in @($ci, $package)) {
    foreach ($match in [regex]::Matches($workflow, '(?m)^\s*-\s+uses:\s+([^\s#]+)')) {
        $uses = $match.Groups[1].Value
        if (-not $uses.StartsWith('./') -and $uses -notmatch '@[0-9a-fA-F]{40}$') {
            throw "Workflow action is not pinned to a full commit SHA: $uses"
        }
    }
}

Assert-Match $package 'workflow_dispatch:\s*\r?\n\s+inputs:\s*\r?\n\s+version:' `
    'Package workflow has no explicit dispatch version input.'
Assert-Match $package '&\s+\$signTool\s+verify\s+/pa\s+/all\s+/v\s+/tw' `
    'Package workflow does not verify the signed MSIX.'
Assert-Match $package 'AppxSignature\.p7x' `
    'Package workflow does not require the MSIX signature payload.'
Assert-Match $package 'CN=WinARD Development' `
    'Package workflow does not validate the certificate subject against the manifest publisher.'
Assert-Match $package 'HasPrivateKey' `
    'Package workflow does not require the signing certificate private key.'
Assert-Match $package 'NotBefore.*NotAfter|NotBefore[\s\S]+NotAfter' `
    'Package workflow does not validate the signing certificate validity period.'
Assert-Match $package 'if:\s+always\(\).*refs/tags/' `
    'Package workflow does not always delete the temporary signing certificate.'
Assert-Match $package 'WinARD-\$\{\{\s*github\.run_number\s*\}\}-\$\{\{\s*github\.run_attempt\s*\}\}' `
    'Artifact name is not derived from safe numeric run identifiers.'
Assert-Match $package 'vMAJOR\.MINOR\.PATCH|v\\d\+' `
    'Package workflow does not enforce a strict release-tag version shape.'
Assert-Match $package 'resolve-version\.ps1\s+-Tag' `
    'Package workflow does not route tags through the tested version resolver.'
Assert-Match $package 'portable\.ps1[^\r\n]+-Version' `
    'Resolved package version is not passed to portable.ps1.'
Assert-Match $package 'verify-artifacts\.ps1[^\r\n]+-ExpectedVersion' `
    'Resolved package version is not passed to artifact verification.'

Write-Host 'Workflow static security gates passed.'
