[CmdletBinding()]
param(
    [string[]]$AssetsFiles,
    [string]$NuGetPackageRoot,
    [string]$NoticePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $AssetsFiles) {
    $AssetsFiles = @(
        Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter 'project.assets.json' -File |
            Where-Object { $_.FullName -match '[\\/]obj[\\/]' } |
            Select-Object -ExpandProperty FullName
    )
}
if (-not $NuGetPackageRoot) {
    $NuGetPackageRoot = if ($env:NUGET_PACKAGES) {
        $env:NUGET_PACKAGES
    } else {
        Join-Path $HOME '.nuget\packages'
    }
}
if (-not $NoticePath) {
    $NoticePath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
}

if ($AssetsFiles.Count -eq 0) {
    throw 'No obj/project.assets.json files were found. Run dotnet restore first.'
}

$resolvedPackageRoot = (Resolve-Path -LiteralPath $NuGetPackageRoot -ErrorAction Stop).ProviderPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if ((Get-Item -LiteralPath $resolvedPackageRoot).Attributes.HasFlag(
    [IO.FileAttributes]::ReparsePoint)) {
    throw "NuGet package root cannot be a reparse point: $resolvedPackageRoot"
}

$allowedLicenses = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'MS-PL'),
    [StringComparer]::OrdinalIgnoreCase)

function Test-LicenseExpression {
    param(
        [Parameter(Mandatory)]
        [string]$Expression
    )

    if ([string]::IsNullOrWhiteSpace($Expression) -or
        $Expression.Contains('LicenseRef-', [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $matches = [regex]::Matches($Expression, '\(|\)|AND|OR|WITH|[A-Za-z0-9][A-Za-z0-9.+-]*',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $withoutTokens = $Expression
    foreach ($match in ($matches | Sort-Object Index -Descending)) {
        $withoutTokens = $withoutTokens.Remove($match.Index, $match.Length)
    }
    if (-not [string]::IsNullOrWhiteSpace($withoutTokens)) {
        return $false
    }

    $expectLicense = $true
    $depth = 0
    foreach ($match in $matches) {
        $token = $match.Value
        switch -Regex ($token.ToUpperInvariant()) {
            '^\($' {
                if (-not $expectLicense) { return $false }
                $depth++
            }
            '^\)$' {
                if ($expectLicense -or $depth -le 0) { return $false }
                $depth--
            }
            '^(AND|OR)$' {
                if ($expectLicense) { return $false }
                $expectLicense = $true
            }
            '^WITH$' {
                # SPDX exceptions are deliberately not allowlisted.
                return $false
            }
            default {
                if (-not $expectLicense -or -not $allowedLicenses.Contains($token)) {
                    return $false
                }
                $expectLicense = $false
            }
        }
    }

    return -not $expectLicense -and $depth -eq 0
}

$packageKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assetsFile in ($AssetsFiles | Sort-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) {
        throw "Assets file does not exist: $assetsFile"
    }

    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $assets.libraries.GetEnumerator()) {
        if ($entry.Value.type -eq 'package') {
            [void]$packageKeys.Add($entry.Key)
        }
    }
}

$violations = [Collections.Generic.List[string]]::new()
$packages = foreach ($packageKey in ($packageKeys | Sort-Object)) {
    $separator = $packageKey.LastIndexOf('/')
    if ($separator -le 0 -or $separator -eq $packageKey.Length - 1) {
        throw "Invalid NuGet package key in assets file: $packageKey"
    }
    $id = $packageKey.Substring(0, $separator)
    $version = $packageKey.Substring($separator + 1)
    if ($id -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or
        $id.Contains('..', [StringComparison]::Ordinal) -or
        $version -notmatch '^[0-9][0-9A-Za-z.+-]*$' -or
        $version.Contains('..', [StringComparison]::Ordinal)) {
        throw "Unsafe NuGet package id or version in assets file: $packageKey"
    }

    $idDirectory = Join-Path $resolvedPackageRoot $id.ToLowerInvariant()
    $packageDirectory = Join-Path $idDirectory $version.ToLowerInvariant()
    foreach ($candidateDirectory in @($idDirectory, $packageDirectory)) {
        $resolvedCandidate = (Resolve-Path -LiteralPath $candidateDirectory -ErrorAction Stop).ProviderPath
        if (-not $resolvedCandidate.StartsWith(
            "$resolvedPackageRoot$([IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet package path escaped the configured cache root: $resolvedCandidate"
        }
        if ((Get-Item -LiteralPath $resolvedCandidate).Attributes.HasFlag(
            [IO.FileAttributes]::ReparsePoint)) {
            throw "NuGet package path cannot traverse a reparse point: $resolvedCandidate"
        }
    }

    $expectedNuspecName = "$id.nuspec"
    $nuspecMatches = @(
        Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name.Equals($expectedNuspecName, [StringComparison]::OrdinalIgnoreCase)
            }
    )
    $nuspecPath = if ($nuspecMatches.Count -eq 1) {
        $nuspecMatches[0].FullName
    } else {
        $null
    }
    $metadata = $null
    $license = 'UNRESOLVED'
    $allowed = $false
    if (-not $nuspecPath) {
        $violations.Add("Missing nuspec for $id $version under $packageDirectory.")
    } else {
        $resolvedNuspec = (Resolve-Path -LiteralPath $nuspecPath -ErrorAction Stop).ProviderPath
        if (-not $resolvedNuspec.StartsWith(
            "$resolvedPackageRoot$([IO.Path]::DirectorySeparatorChar)",
            [StringComparison]::OrdinalIgnoreCase) -or
            (Get-Item -LiteralPath $resolvedNuspec).Attributes.HasFlag(
                [IO.FileAttributes]::ReparsePoint)) {
            throw "Nuspec cannot escape the package cache or be a reparse point: $nuspecPath"
        }
        if ((Get-Item -LiteralPath $nuspecPath).Length -gt 1MB) {
            throw "Nuspec exceeds the 1 MiB safety limit: $nuspecPath"
        }
        $xmlSettings = [Xml.XmlReaderSettings]::new()
        $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlSettings.XmlResolver = $null
        $xmlReader = [Xml.XmlReader]::Create($nuspecPath, $xmlSettings)
        try {
            $nuspec = [Xml.XmlDocument]::new()
            $nuspec.XmlResolver = $null
            $nuspec.Load($xmlReader)
        } finally {
            $xmlReader.Dispose()
        }
        $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw "Package $id $version has no nuspec metadata node."
        }
        $metadataIds = @($metadata.SelectNodes("*[local-name()='id']"))
        $metadataVersions = @($metadata.SelectNodes("*[local-name()='version']"))
        if ($metadataIds.Count -ne 1 -or $metadataVersions.Count -ne 1 -or
            -not $metadataIds[0].InnerText.Trim().Equals($id, [StringComparison]::OrdinalIgnoreCase) -or
            -not $metadataVersions[0].InnerText.Trim().Equals($version, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Nuspec id/version does not match assets entry $packageKey."
        }
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        if ($null -eq $licenseNode) {
            $license = 'UNRESOLVED (missing SPDX expression)'
            $violations.Add("Package $id $version has no SPDX license expression.")
        } elseif ($licenseNode.GetAttribute('type') -ne 'expression') {
            $license = "UNRESOLVED ($($licenseNode.GetAttribute('type')): $($licenseNode.InnerText.Trim()))"
            $violations.Add("Package $id $version uses a file or unsupported license declaration.")
        } else {
            $license = $licenseNode.InnerText.Trim()
            $allowed = Test-LicenseExpression -Expression $license
            if (-not $allowed) {
                $violations.Add("Package $id $version has denied or unsupported license expression: $license")
            }
        }
    }

    $projectUrlNode = if ($null -eq $metadata) {
        $null
    } else {
        $metadata.SelectSingleNode("*[local-name()='projectUrl']")
    }
    [pscustomobject]@{
        Id = $id
        Version = $version
        License = $license
        Allowed = $allowed
        ProjectUrl = if ($null -eq $projectUrlNode -or
            [string]::IsNullOrWhiteSpace($projectUrlNode.InnerText)) {
            'Not provided'
        } else {
            $projectUrlNode.InnerText.Trim()
        }
    }
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Third-Party Notices')
$lines.Add('')
$lines.Add('This file is generated by `packaging/check-licenses.ps1` from restored NuGet metadata.')
$lines.Add('Only the repository allowlist is accepted; review this file before every release.')
$lines.Add('')
$lines.Add('| Package | Version | SPDX license | Project URL |')
$lines.Add('| --- | --- | --- | --- |')
foreach ($package in ($packages | Sort-Object Id, Version)) {
    $safeUrl = $package.ProjectUrl.Replace('|', '%7C')
    $lines.Add("| $($package.Id) | $($package.Version) | $($package.License) | $safeUrl |")
}

$noticeDirectory = Split-Path -Parent $NoticePath
if ($noticeDirectory) {
    New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
}
[IO.File]::WriteAllLines($NoticePath, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Rebuilt $NoticePath"
if ($violations.Count -gt 0) {
    throw "License audit failed:`n - $($violations -join "`n - ")"
}
Write-Host "License audit passed for $($packages.Count) package(s)."
