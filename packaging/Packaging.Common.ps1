Set-StrictMode -Version Latest

function Test-WinArdPackageVersion {
    param([string]$Version)

    if ($Version -notmatch '^(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})\.(0|[1-9]\d{0,4})$') {
        return $false
    }
    foreach ($part in $Version.Split('.')) {
        if ([int]$part -gt 65535) {
            return $false
        }
    }
    return $true
}

function Find-WindowsSdkTool {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidates = Get-ChildItem -LiteralPath $kitsBin -Recurse -Filter $Name -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending
    $candidate = $candidates | Select-Object -First 1
    if ($null -eq $candidate) {
        throw "$Name was not found. Install the Windows 10/11 SDK."
    }

    return $candidate.FullName
}
