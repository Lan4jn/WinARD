[CmdletBinding(DefaultParameterSetName = 'Manual')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Tag')]
    [string]$Tag,
    [Parameter(Mandatory, ParameterSetName = 'Manual')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Packaging.Common.ps1')

function Assert-VersionParts {
    param([string[]]$Parts, [string]$Source)
    foreach ($part in $Parts) {
        if ($part -notmatch '^\d{1,5}$' -or [int]$part -gt 65535) {
            throw "$Source version components must be numeric values between 0 and 65535."
        }
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Tag') {
    if ($Tag -notmatch '^v(?<major>0|[1-9]\d{0,4})\.(?<minor>0|[1-9]\d{0,4})\.(?<patch>0|[1-9]\d{0,4})$') {
        throw 'Release tags must use exactly vMAJOR.MINOR.PATCH; prerelease tags are not supported.'
    }
    $parts = @($Matches.major, $Matches.minor, $Matches.patch)
    Assert-VersionParts -Parts $parts -Source 'Tag'
    return "$($parts[0]).$($parts[1]).$($parts[2]).0"
}

if (-not (Test-WinArdPackageVersion -Version $Version)) {
    throw 'Manual package version must use exactly four numeric components.'
}
return $Version
