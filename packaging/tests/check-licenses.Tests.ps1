[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'packaging\check-licenses.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function New-Fixture {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Id,
        [Parameter(Mandatory)]
        [string]$Version,
        [string]$License,
        [ValidateSet('expression', 'file')]
        [string]$LicenseType = 'expression'
    )

    $packageDirectory = Join-Path (Join-Path $Root 'packages') $Id.ToLowerInvariant()
    $packageDirectory = Join-Path $packageDirectory $Version.ToLowerInvariant()
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    $licenseXml = if ($null -eq $License) {
        ''
    } else {
        "<license type=`"$LicenseType`">$License</license>"
    }
    $nuspec = @"
<?xml version="1.0"?>
<package>
  <metadata>
    <id>$Id</id>
    <version>$Version</version>
    $licenseXml
    <projectUrl>https://example.invalid/$Id</projectUrl>
  </metadata>
</package>
"@
    [IO.File]::WriteAllText(
        (Join-Path $packageDirectory "$($Id.ToLowerInvariant()).nuspec"),
        $nuspec,
        [Text.UTF8Encoding]::new($false))

    $assetsPath = Join-Path $Root "$Id.assets.json"
    $assets = @{
        version = 3
        libraries = @{
            "$Id/$Version" = @{ type = 'package' }
        }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($assetsPath, $assets, [Text.UTF8Encoding]::new($false))
    return $assetsPath
}

function Invoke-Scanner {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string[]]$Assets
    )

    $notice = Join-Path $Root 'NOTICE.md'
    try {
        $output = & $scriptPath `
            -NuGetPackageRoot (Join-Path $Root 'packages') `
            -NoticePath $notice `
            -AssetsFiles $Assets 2>&1
        return [pscustomobject]@{
            ExitCode = 0
            Output = ($output -join [Environment]::NewLine)
            Notice = $notice
        }
    } catch {
        return [pscustomobject]@{
            ExitCode = 1
            Output = $_.Exception.Message
            Notice = $notice
        }
    }
}

function New-RawFixture {
    param(
        [string]$Root,
        [string]$PackageKey,
        [string]$NuspecDirectory,
        [string]$NuspecFileName,
        [string]$MetadataId,
        [string]$MetadataVersion
    )

    New-Item -ItemType Directory -Path $NuspecDirectory -Force | Out-Null
    $nuspec = @"
<?xml version="1.0"?>
<package><metadata>
  <id>$MetadataId</id><version>$MetadataVersion</version>
  <license type="expression">MIT</license>
  <projectUrl>https://example.invalid/unsafe</projectUrl>
</metadata></package>
"@
    [IO.File]::WriteAllText(
        (Join-Path $NuspecDirectory $NuspecFileName),
        $nuspec,
        [Text.UTF8Encoding]::new($false))
    $assetsPath = Join-Path $Root 'unsafe.assets.json'
    $assets = @{
        version = 3
        libraries = @{ $PackageKey = @{ type = 'package' } }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($assetsPath, $assets, [Text.UTF8Encoding]::new($false))
    return $assetsPath
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('WinARD-license-tests-{0:N}' -f [Guid]::NewGuid())
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $allowRoot = Join-Path $testRoot 'allow'
    New-Item -ItemType Directory -Path $allowRoot | Out-Null
    $allowAssets = @(
        (New-Fixture -Root $allowRoot -Id 'Allowed.Mit' -Version '1.0.0' -License 'MIT'),
        (New-Fixture -Root $allowRoot -Id 'Allowed.Composite' -Version '2.0.0' -License '(Apache-2.0 OR BSD-3-Clause)')
    )
    $allowed = Invoke-Scanner -Root $allowRoot -Assets $allowAssets
    Assert-True ($allowed.ExitCode -eq 0) "Allowed fixture failed: $($allowed.Output)"
    $firstNotice = [IO.File]::ReadAllText($allowed.Notice)
    $allowedAgain = Invoke-Scanner -Root $allowRoot -Assets @($allowAssets[1], $allowAssets[0])
    Assert-True ($allowedAgain.ExitCode -eq 0) "Second allowed fixture failed: $($allowedAgain.Output)"
    $secondNotice = [IO.File]::ReadAllText($allowedAgain.Notice)
    Assert-True ($firstNotice -ceq $secondNotice) 'Notice output is not deterministic.'
    Assert-True ($firstNotice -notmatch '(\r?\n){2}$') 'Notice output has a blank line at EOF.'

    foreach ($case in @(
        @{ Name = 'deny'; License = 'GPL-3.0-only'; Type = 'expression' },
        @{ Name = 'missing'; License = $null; Type = 'expression' },
        @{ Name = 'file'; License = 'LICENSE.txt'; Type = 'file' },
        @{ Name = 'unknown'; License = 'MIT OR LicenseRef-Custom'; Type = 'expression' }
    )) {
        $caseRoot = Join-Path $testRoot $case.Name
        New-Item -ItemType Directory -Path $caseRoot | Out-Null
        $caseAssets = New-Fixture -Root $caseRoot -Id "Case.$($case.Name)" -Version '1.0.0' `
            -License $case.License -LicenseType $case.Type
        $result = Invoke-Scanner -Root $caseRoot -Assets @($caseAssets)
        Assert-True ($result.ExitCode -ne 0) "Denied fixture '$($case.Name)' was accepted."
    }

    $traversalRoot = Join-Path $testRoot 'traversal'
    New-Item -ItemType Directory -Path (Join-Path $traversalRoot 'packages') -Force | Out-Null
    $traversalAssets = New-RawFixture `
        -Root $traversalRoot `
        -PackageKey '../escaped/1.0.0' `
        -NuspecDirectory (Join-Path $traversalRoot 'escaped\1.0.0') `
        -NuspecFileName 'attacker-controlled.nuspec' `
        -MetadataId 'Different.Package' `
        -MetadataVersion '9.9.9'
    $traversal = Invoke-Scanner -Root $traversalRoot -Assets @($traversalAssets)
    Assert-True ($traversal.ExitCode -ne 0) `
        'Path traversal and mismatched nuspec metadata were accepted.'

    foreach ($unsafeKey in @(
        'evil\child/1.0.0',
        'C:\absolute/1.0.0',
        'Safe.Package/1.0.0\..\escape',
        '/rooted/1.0.0'
    )) {
        $unsafeRoot = Join-Path $testRoot ('unsafe-key-{0:N}' -f [Guid]::NewGuid())
        New-Item -ItemType Directory -Path (Join-Path $unsafeRoot 'packages') -Force | Out-Null
        $unsafeAssets = Join-Path $unsafeRoot 'unsafe.assets.json'
        $unsafeJson = @{
            version = 3
            libraries = @{ $unsafeKey = @{ type = 'package' } }
        } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($unsafeAssets, $unsafeJson, [Text.UTF8Encoding]::new($false))
        $unsafe = Invoke-Scanner -Root $unsafeRoot -Assets @($unsafeAssets)
        Assert-True ($unsafe.ExitCode -ne 0) "Unsafe package key was accepted: $unsafeKey"
    }

    $mismatchRoot = Join-Path $testRoot 'metadata-mismatch'
    New-Item -ItemType Directory -Path (Join-Path $mismatchRoot 'packages') -Force | Out-Null
    $mismatchAssets = New-RawFixture `
        -Root $mismatchRoot `
        -PackageKey 'Expected.Package/1.0.0' `
        -NuspecDirectory (Join-Path $mismatchRoot 'packages\expected.package\1.0.0') `
        -NuspecFileName 'Expected.Package.nuspec' `
        -MetadataId 'Different.Package' `
        -MetadataVersion '9.9.9'
    $mismatch = Invoke-Scanner -Root $mismatchRoot -Assets @($mismatchAssets)
    Assert-True ($mismatch.ExitCode -ne 0) 'Mismatched nuspec id/version was accepted.'

    $decoyRoot = Join-Path $testRoot 'decoy-nuspec'
    New-Item -ItemType Directory -Path $decoyRoot | Out-Null
    $decoyAssets = New-Fixture -Root $decoyRoot -Id 'Expected.Package' -Version '1.0.0' -License 'MIT'
    $decoyDirectory = Join-Path $decoyRoot 'packages\expected.package\1.0.0'
    [IO.File]::WriteAllText(
        (Join-Path $decoyDirectory 'aaa-attacker.nuspec'),
        '<package><metadata><id>Attacker</id><version>9.9.9</version><license type="expression">GPL-3.0-only</license></metadata></package>',
        [Text.UTF8Encoding]::new($false))
    $decoy = Invoke-Scanner -Root $decoyRoot -Assets @($decoyAssets)
    Assert-True ($decoy.ExitCode -eq 0) "Exact expected nuspec was not selected: $($decoy.Output)"

    $junctionRoot = Join-Path $testRoot 'junction'
    $junctionPackages = Join-Path $junctionRoot 'packages'
    $junctionOutside = Join-Path $junctionRoot 'outside\1.0.0'
    New-Item -ItemType Directory -Path $junctionPackages, $junctionOutside -Force | Out-Null
    $junctionCreated = $false
    try {
        New-Item -ItemType Junction -Path (Join-Path $junctionPackages 'linked.package') `
            -Target (Split-Path -Parent $junctionOutside) -ErrorAction Stop | Out-Null
        $junctionCreated = $true
    } catch {
        Write-Host 'Junction fixture skipped because the environment could not create it.'
    }
    if ($junctionCreated) {
        [IO.File]::WriteAllText(
            (Join-Path $junctionOutside 'Linked.Package.nuspec'),
            '<package><metadata><id>Linked.Package</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>',
            [Text.UTF8Encoding]::new($false))
        $junctionAssets = Join-Path $junctionRoot 'junction.assets.json'
        $junctionJson = @{
            version = 3
            libraries = @{ 'Linked.Package/1.0.0' = @{ type = 'package' } }
        } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($junctionAssets, $junctionJson, [Text.UTF8Encoding]::new($false))
        $junction = Invoke-Scanner -Root $junctionRoot -Assets @($junctionAssets)
        Assert-True ($junction.ExitCode -ne 0) 'NuGet cache junction was followed.'
    }

    Write-Host 'License scanner fixture tests passed: license policy, path safety, metadata, reparse and deterministic output.'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
