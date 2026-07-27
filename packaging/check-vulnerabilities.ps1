[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'WinARD.sln'
$json = & dotnet list $solution package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    throw "dotnet list package exited with code $LASTEXITCODE."
}

$report = $json | ConvertFrom-Json
$vulnerabilities = @(
    foreach ($project in $report.projects) {
        $frameworks = if ($null -ne $project.PSObject.Properties['frameworks']) {
            @($project.frameworks)
        } else {
            @()
        }
        foreach ($framework in $frameworks) {
            $topLevel = if ($null -ne $framework.PSObject.Properties['topLevelPackages']) {
                @($framework.topLevelPackages)
            } else {
                @()
            }
            $transitive = if ($null -ne $framework.PSObject.Properties['transitivePackages']) {
                @($framework.transitivePackages)
            } else {
                @()
            }
            foreach ($package in $topLevel + $transitive) {
                $packageVulnerabilities = if ($null -ne $package.PSObject.Properties['vulnerabilities']) {
                    @($package.vulnerabilities)
                } else {
                    @()
                }
                foreach ($vulnerability in $packageVulnerabilities) {
                    [pscustomobject]@{
                        Project = $project.path
                        Package = $package.id
                        Version = $package.resolvedVersion
                        Severity = $vulnerability.severity
                        AdvisoryUrl = $vulnerability.advisoryUrl
                    }
                }
            }
        }
    }
)

if ($vulnerabilities.Count -gt 0) {
    $vulnerabilities | Format-Table -AutoSize | Out-String | Write-Error
    throw "NuGet vulnerability audit found $($vulnerabilities.Count) vulnerable package reference(s)."
}

Write-Host 'NuGet vulnerability audit passed: no known vulnerable direct or transitive packages.'
