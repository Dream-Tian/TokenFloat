param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'TokenFloat\TokenFloat.csproj'
$installerPath = Join-Path $repositoryRoot 'installer\TokenFloat.iss'
$manifestPath = Join-Path $repositoryRoot 'installer\update-manifest.example.json'

$project = Get-Content -LiteralPath $projectPath -Raw
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', "<Version>$Version</Version>", 1)
Set-Content -LiteralPath $projectPath -Value $project -Encoding utf8

$installer = Get-Content -LiteralPath $installerPath -Raw
$installer = [regex]::Replace(
    $installer,
    '#define MyAppVersion "[^"]+"',
    "#define MyAppVersion `"$Version`"",
    1)
Set-Content -LiteralPath $installerPath -Value $installer -Encoding utf8

$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifest = [regex]::Replace($manifest, '"version"\s*:\s*"[^"]+"', '"version": "' + $Version + '"', 1)
Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding utf8

Write-Host "TokenFloat version updated to $Version"
