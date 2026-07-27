[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [string]$Version = '0.1.0.0',
    [switch]$ValidateVersionOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Packaging.Common.ps1')

if (-not (Test-WinArdPackageVersion -Version $Version)) {
    throw "Version must contain four numeric parts between 0 and 65535: $Version"
}
if ($ValidateVersionOnly) {
    Write-Host "Package version is valid: $Version"
    return
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsDirectory = Join-Path $repoRoot 'artifacts'
$portableDirectory = Join-Path $artifactsDirectory 'portable-win-x64'
$msixStagingDirectory = Join-Path $artifactsDirectory 'msix-staging'
$msixOutput = Join-Path $artifactsDirectory 'WinARD.msix'
$portableZip = Join-Path $artifactsDirectory 'WinARD-portable-win-x64.zip'
$checksumsPath = Join-Path $artifactsDirectory 'SHA256SUMS.txt'
$desktopProject = Join-Path $repoRoot 'src\WinARD.Desktop\WinARD.Desktop.csproj'
$manifestPath = Join-Path $repoRoot 'src\WinARD.Desktop\Package.appxmanifest'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function New-PackageAsset {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [int]$Width,
        [Parameter(Mandatory)]
        [int]$Height
    )

    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($Width, $Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::FromArgb(255, 31, 78, 121))
        } finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
foreach ($path in @($portableDirectory, $msixStagingDirectory)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}
foreach ($path in @($msixOutput, $portableZip, $checksumsPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

$publishArguments = @(
    'publish', $desktopProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $portableDirectory,
    '-p:Platform=x64',
    '-p:WindowsPackageType=None',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:TreatWarningsAsErrors=true',
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version",
    "-p:FileVersion=$Version",
    "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false'
)
if ($SkipRestore) {
    $publishArguments += '--no-restore'
}

Write-Host "Publishing self-contained portable application to $portableDirectory"
Invoke-Checked -FilePath 'dotnet' -ArgumentList $publishArguments

$requiredPortableFiles = @(
    'WinARD.Desktop.exe',
    'WinARD.Desktop.dll',
    'WinARD.OpenSshAskPass.exe',
    'Microsoft.UI.dll',
    'e_sqlite3.dll'
)
foreach ($name in $requiredPortableFiles) {
    $path = Join-Path $portableDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Portable publish is incomplete: $name is missing."
    }
}

Write-Host "Creating portable archive $portableZip"
Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $portableZip -CompressionLevel Optimal

Write-Host 'Preparing unsigned MSIX staging directory'
New-Item -ItemType Directory -Path $msixStagingDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $portableDirectory '*') -Destination $msixStagingDirectory -Recurse
$stagingManifestPath = Join-Path $msixStagingDirectory 'AppxManifest.xml'
Copy-Item -LiteralPath $manifestPath -Destination $stagingManifestPath
[xml]$stagingManifest = Get-Content -LiteralPath $stagingManifestPath -Raw
$identity = $stagingManifest.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identity) {
    throw 'Package manifest has no Identity element.'
}
$identity.SetAttribute('Version', $Version)
$xmlSettings = [Xml.XmlWriterSettings]::new()
$xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
$xmlSettings.Indent = $true
$writer = [Xml.XmlWriter]::Create($stagingManifestPath, $xmlSettings)
try {
    $stagingManifest.Save($writer)
} finally {
    $writer.Dispose()
}
$assetsDirectory = Join-Path $msixStagingDirectory 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
foreach ($asset in @(
    @{ Name = 'StoreLogo.png'; Width = 50; Height = 50 },
    @{ Name = 'Square44x44Logo.png'; Width = 44; Height = 44 },
    @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150 },
    @{ Name = 'Wide310x150Logo.png'; Width = 310; Height = 150 },
    @{ Name = 'SplashScreen.png'; Width = 620; Height = 300 }
)) {
    New-PackageAsset -Path (Join-Path $assetsDirectory $asset.Name) `
        -Width $asset.Width `
        -Height $asset.Height
}

$makeAppx = Find-WindowsSdkTool -Name 'makeappx.exe'
Write-Host "Creating unsigned MSIX with $makeAppx"
Invoke-Checked -FilePath $makeAppx -ArgumentList @(
    'pack',
    '/d', $msixStagingDirectory,
    '/p', $msixOutput,
    '/o'
)

$hashLines = foreach ($path in @($msixOutput, $portableZip)) {
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $path)
}
[IO.File]::WriteAllLines($checksumsPath, $hashLines, [Text.UTF8Encoding]::new($false))

Write-Host 'Release artifacts created:'
Get-Item -LiteralPath $msixOutput, $portableZip, $checksumsPath |
    Select-Object FullName, Length |
    Format-Table -AutoSize
