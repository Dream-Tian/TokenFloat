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
$utf8Bom = [System.Text.UTF8Encoding]::new($true)
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

$project = [System.IO.File]::ReadAllText($projectPath, $utf8NoBom)
$project = [regex]::Replace($project, '<Version>[^<]+</Version>', "<Version>$Version</Version>", 1)
[System.IO.File]::WriteAllText($projectPath, $project, $utf8Bom)

$installer = [System.IO.File]::ReadAllText($installerPath, $utf8NoBom)
$installer = [regex]::Replace(
    $installer,
    '#define MyAppVersion "[^"]+"',
    "#define MyAppVersion `"$Version`"",
    1)
[System.IO.File]::WriteAllText($installerPath, $installer, $utf8Bom)

$manifest = [System.IO.File]::ReadAllText($manifestPath, $utf8NoBom)
$manifest = [regex]::Replace($manifest, '"version"\s*:\s*"[^"]+"', '"version": "' + $Version + '"', 1)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

Write-Host "TokenFloat version updated to $Version"
