[CmdletBinding()]
param(
    [string]$ArtifactsDirectory,
    [string]$ExpectedVersion = '0.1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Packaging.Common.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactsDirectory) {
    $ArtifactsDirectory = Join-Path $repoRoot 'artifacts'
}

if (-not (Test-WinArdPackageVersion -Version $ExpectedVersion)) {
    throw "ExpectedVersion must contain four numeric parts between 0 and 65535: $ExpectedVersion"
}

$msixPath = Join-Path $ArtifactsDirectory 'WinARD.msix'
$portableZipPath = Join-Path $ArtifactsDirectory 'WinARD-portable-win-x64.zip'
$checksumsPath = Join-Path $ArtifactsDirectory 'SHA256SUMS.txt'
$requiredArtifacts = @($msixPath, $portableZipPath, $checksumsPath)
foreach ($artifact in $requiredArtifacts) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Missing release artifact: $artifact"
    }
    if ((Get-Item -LiteralPath $artifact).Length -le 0) {
        throw "Release artifact is empty: $artifact"
    }
}

function Read-Checksums {
    param([string]$Path)

    if ((Get-Item -LiteralPath $Path).Length -gt 1024) {
        throw 'SHA256SUMS.txt exceeds the 1 KiB safety limit.'
    }
    $expectedNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    [void]$expectedNames.Add('WinARD.msix')
    [void]$expectedNames.Add('WinARD-portable-win-x64.zip')
    $checksums = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    $nonEmptyLines = @(Get-Content -LiteralPath $Path | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($nonEmptyLines.Count -ne 2) {
        throw 'SHA256SUMS.txt must contain exactly two non-empty lines.'
    }
    foreach ($line in $nonEmptyLines) {
        if ($line -notmatch '^(?<hash>[0-9a-fA-F]{64})  (?<name>WinARD\.msix|WinARD-portable-win-x64\.zip)$') {
            throw "Invalid SHA-256 manifest line: $line"
        }
        if (-not $expectedNames.Contains($Matches.name) -or
            $checksums.ContainsKey($Matches.name)) {
            throw "Duplicate or unexpected SHA-256 manifest filename: $($Matches.name)"
        }
        $checksums.Add($Matches.name, $Matches.hash.ToLowerInvariant())
    }
    if ($checksums.Count -ne 2) {
        throw 'SHA256SUMS.txt does not cover exactly the required artifacts.'
    }
    return $checksums
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Reflection.Metadata

function Open-Zip {
    param([string]$Path)
    try {
        return [IO.Compression.ZipFile]::OpenRead($Path)
    } catch {
        throw "Release artifact is not a readable ZIP container: $Path"
    }
}

function Get-ValidatedEntries {
    param(
        [IO.Compression.ZipArchive]$Archive,
        [string]$ArtifactName
    )

    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    if ($Archive.Entries.Count -gt 4096) {
        throw "$ArtifactName contains too many ZIP entries."
    }
    [int64]$totalLength = 0
    foreach ($entry in $Archive.Entries) {
        $raw = $entry.FullName
        if ([string]::IsNullOrWhiteSpace($raw) -or
            $raw.Contains('\') -or
            $raw.Contains('//') -or
            $raw.StartsWith('/') -or
            $raw -match '^[A-Za-z]:') {
            throw "$ArtifactName contains a non-canonical ZIP entry path: $raw"
        }
        $isDirectory = $raw.EndsWith('/')
        $canonical = if ($isDirectory) { $raw.TrimEnd('/') } else { $raw }
        if ([string]::IsNullOrWhiteSpace($canonical) -or $canonical.Contains(':')) {
            throw "$ArtifactName contains a non-canonical ZIP entry path: $raw"
        }
        $segments = $canonical.Split('/')
        if ($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -eq '.' -or $_ -eq '..'
        }) {
            throw "$ArtifactName contains an unsafe ZIP entry path: $raw"
        }
        if ($entries.ContainsKey($canonical)) {
            throw "$ArtifactName contains a duplicate or ambiguous ZIP entry: $raw"
        }
        $totalLength += $entry.Length
        if ($entry.Length -gt 512MB -or $totalLength -gt 1536MB) {
            throw "$ArtifactName exceeds the uncompressed ZIP safety limit."
        }
        $entries.Add($canonical, $entry)
    }
    return $entries
}

function Assert-Entry {
    param(
        [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]$Entries,
        [string]$Name,
        [int64]$MinimumLength = 1,
        [int64]$MaximumLength = [int64]::MaxValue,
        [switch]$PortableExecutable
    )

    if (-not $Entries.ContainsKey($Name)) {
        throw "Archive is missing required entry: $Name"
    }
    $entry = $Entries[$Name]
    if ($entry.Length -lt $MinimumLength -or $entry.Length -gt $MaximumLength) {
        throw "Archive entry length is outside the allowed range: $Name"
    }
    if ($PortableExecutable) {
        $stream = $entry.Open()
        try {
            $header = [byte[]]::new(2)
            if ($stream.Read($header, 0, 2) -ne 2 -or
                $header[0] -ne 0x4d -or
                $header[1] -ne 0x5a) {
                throw "Archive entry is not a PE file with an MZ header: $Name"
            }
        } finally {
            $stream.Dispose()
        }
    }
    return $entry
}

function Copy-ZipEntryToFile {
    param(
        [IO.Compression.ZipArchiveEntry]$Entry,
        [string]$Destination
    )
    $source = $Entry.Open()
    $target = [IO.File]::Create($Destination)
    try {
        $source.CopyTo($target)
    } finally {
        $target.Dispose()
        $source.Dispose()
    }
}

function Assert-X64Pe {
    param(
        [string]$Path,
        [switch]$RequireManagedMetadata
    )

    $stream = [IO.File]::OpenRead($Path)
    try {
        try {
            $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                $headers = $reader.PEHeaders
                if ($null -eq $headers.PEHeader -or
                    $headers.CoffHeader.Machine -ne
                        [Reflection.PortableExecutable.Machine]::Amd64 -or
                    $headers.SectionHeaders.Length -le 0 -or
                    $headers.PEHeader.Magic -notin @(
                        [Reflection.PortableExecutable.PEMagic]::PE32,
                        [Reflection.PortableExecutable.PEMagic]::PE32Plus
                    )) {
                    throw "File is not a structurally valid x64 PE image: $Path"
                }
                if ($RequireManagedMetadata -and
                    (-not $reader.HasMetadata -or $null -eq $headers.CorHeader)) {
                    throw "Managed assembly has no CLR metadata: $Path"
                }
            } finally {
                $reader.Dispose()
            }
        } catch [BadImageFormatException] {
            throw "File is not a valid PE image: $Path"
        }
    } finally {
        $stream.Dispose()
    }

    if ($RequireManagedMetadata) {
        try {
            [void][Reflection.AssemblyName]::GetAssemblyName($Path)
        } catch [BadImageFormatException] {
            throw "Managed assembly metadata is unreadable: $Path"
        }
    }
}

function Assert-BinaryVersion {
    param(
        [string]$Directory,
        [string]$Version
    )

    foreach ($name in @('WinARD.Desktop.exe', 'WinARD.Desktop.dll')) {
        $path = Join-Path $Directory $name
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        if ($info.FileVersion -cne $Version -or $info.ProductVersion -cne $Version) {
            throw "Binary file/product version does not match $Version`: $name"
        }
    }
    $assemblyPath = Join-Path $Directory 'WinARD.Desktop.dll'
    if ([Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString() -cne $Version) {
        throw "WinARD.Desktop.dll assembly version does not match $Version."
    }
}

$requiredPayloads = @(
    'WinARD.Desktop.exe',
    'WinARD.Desktop.dll',
    'WinARD.OpenSshAskPass.exe',
    'Microsoft.UI.dll',
    'e_sqlite3.dll'
)

$portable = Open-Zip -Path $portableZipPath
try {
    $portableEntries = Get-ValidatedEntries -Archive $portable -ArtifactName 'Portable ZIP'
    foreach ($requiredEntry in $requiredPayloads) {
        [void](Assert-Entry -Entries $portableEntries -Name $requiredEntry `
            -MinimumLength 1024 -PortableExecutable)
    }
    foreach ($forbiddenEntry in @(
        'WinARD-portable-win-x64.zip',
        'WinARD.msix',
        'SHA256SUMS.txt'
    )) {
        if ($portableEntries.ContainsKey($forbiddenEntry)) {
            throw "Portable archive recursively contains a release artifact: $forbiddenEntry"
        }
    }
} finally {
    $portable.Dispose()
}

$msix = Open-Zip -Path $msixPath
try {
    $msixEntries = Get-ValidatedEntries -Archive $msix -ArtifactName 'MSIX'
    $manifestEntry = Assert-Entry -Entries $msixEntries -Name 'AppxManifest.xml' -MaximumLength 1MB
    [void](Assert-Entry -Entries $msixEntries -Name 'AppxBlockMap.xml' -MaximumLength 16MB)
    [void](Assert-Entry -Entries $msixEntries -Name '[Content_Types].xml' -MaximumLength 1MB)
    foreach ($requiredEntry in $requiredPayloads) {
        [void](Assert-Entry -Entries $msixEntries -Name $requiredEntry `
            -MinimumLength 1024 -PortableExecutable)
    }

    $xmlSettings = [Xml.XmlReaderSettings]::new()
    $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $xmlSettings.XmlResolver = $null
    $xmlReader = [Xml.XmlReader]::Create($manifestEntry.Open(), $xmlSettings)
    try {
        $manifest = [Xml.XmlDocument]::new()
        $manifest.XmlResolver = $null
        $manifest.Load($xmlReader)
    } finally {
        $xmlReader.Dispose()
    }
    $namespace = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespace.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identity = $manifest.SelectSingleNode('/f:Package/f:Identity', $namespace)
    $family = $manifest.SelectSingleNode('/f:Package/f:Dependencies/f:TargetDeviceFamily', $namespace)
    $application = $manifest.SelectSingleNode('/f:Package/f:Applications/f:Application', $namespace)
    if ($null -eq $identity -or $null -eq $family -or $null -eq $application) {
        throw 'MSIX manifest is missing required identity, dependency, or application nodes.'
    }
    $capabilities = @(
        $manifest.SelectNodes('/f:Package/f:Capabilities/f:Capability', $namespace) |
            ForEach-Object { $_.GetAttribute('Name') }
    )
    $expectedIdentity = @{
        Name = 'WinARD'
        Publisher = 'CN=WinARD Development'
        Version = $ExpectedVersion
        ProcessorArchitecture = 'x64'
    }
    foreach ($key in $expectedIdentity.Keys) {
        if ($identity.GetAttribute($key) -cne $expectedIdentity[$key]) {
            throw "MSIX Identity $key is invalid."
        }
    }
    if ($family.GetAttribute('Name') -cne 'Windows.Desktop' -or
        $family.GetAttribute('MinVersion') -cne '10.0.19041.0' -or
        $family.GetAttribute('MaxVersionTested') -cne '10.0.26100.0') {
        throw 'MSIX Windows.Desktop dependency is invalid.'
    }
    foreach ($capability in @('internetClient', 'privateNetworkClientServer')) {
        if ($capabilities -cnotcontains $capability) {
            throw "MSIX is missing required capability: $capability"
        }
    }
    if ($application.GetAttribute('Executable') -cne 'WinARD.Desktop.exe' -or
        $application.GetAttribute('EntryPoint') -cne 'Windows.FullTrustApplication') {
        throw 'MSIX application executable or entry point is invalid.'
    }
} finally {
    $msix.Dispose()
}

$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'WinARD-artifact-verification-{0:N}' -f [Guid]::NewGuid())
$portableVerificationDirectory = Join-Path $verificationRoot 'portable'
$msixVerificationDirectory = Join-Path $verificationRoot 'msix'
New-Item -ItemType Directory -Path $portableVerificationDirectory, $msixVerificationDirectory -Force |
    Out-Null
try {
    $portable = Open-Zip -Path $portableZipPath
    try {
        $portableEntries = Get-ValidatedEntries -Archive $portable -ArtifactName 'Portable ZIP'
        foreach ($name in $requiredPayloads) {
            Copy-ZipEntryToFile -Entry $portableEntries[$name] `
                -Destination (Join-Path $portableVerificationDirectory $name)
        }
    } finally {
        $portable.Dispose()
    }

    $makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
    $makeAppxOutput = & $makeAppx unpack /p $msixPath /d $msixVerificationDirectory /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe rejected the MSIX:`n$($makeAppxOutput -join [Environment]::NewLine)"
    }

    foreach ($name in $requiredPayloads) {
        $portablePath = Join-Path $portableVerificationDirectory $name
        $msixPayloadPath = Join-Path $msixVerificationDirectory $name
        if (-not (Test-Path -LiteralPath $msixPayloadPath -PathType Leaf)) {
            throw "Validated MSIX unpack is missing required payload: $name"
        }
        Assert-X64Pe -Path $portablePath -RequireManagedMetadata:($name -eq 'WinARD.Desktop.dll')
        Assert-X64Pe -Path $msixPayloadPath -RequireManagedMetadata:($name -eq 'WinARD.Desktop.dll')
        $portableHash = (Get-FileHash -LiteralPath $portablePath -Algorithm SHA256).Hash
        $msixHash = (Get-FileHash -LiteralPath $msixPayloadPath -Algorithm SHA256).Hash
        if ($portableHash -cne $msixHash) {
            throw "Portable and MSIX payloads differ: $name"
        }
    }
    Assert-BinaryVersion -Directory $portableVerificationDirectory -Version $ExpectedVersion
    Assert-BinaryVersion -Directory $msixVerificationDirectory -Version $ExpectedVersion
} finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar)
    $resolvedVerificationRoot = [IO.Path]::GetFullPath($verificationRoot)
    if ($resolvedVerificationRoot.StartsWith(
        "$resolvedTempRoot$([IO.Path]::DirectorySeparatorChar)WinARD-artifact-verification-",
        [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedVerificationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$checksums = Read-Checksums -Path $checksumsPath
foreach ($artifact in @($msixPath, $portableZipPath)) {
    $actualHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $fileName = Split-Path -Leaf $artifact
    if ($checksums[$fileName] -cne $actualHash) {
        throw "SHA-256 manifest does not match $fileName."
    }
}

Write-Host 'Artifact verification passed.'
Get-Item -LiteralPath $msixPath, $portableZipPath |
    Select-Object Name, Length, @{ Name = 'SHA256'; Expression = {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    } } |
    Format-Table -AutoSize
