param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$changelogPath = Join-Path $repositoryRoot 'CHANGELOG.md'
$resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
} else {
    Join-Path $repositoryRoot $OutputPath
}
$utf8NoBom = [System.Text.UTF8Encoding]::new($false, $true)

if (-not (Test-Path -LiteralPath $changelogPath -PathType Leaf)) {
    throw "CHANGELOG.md was not found at $changelogPath."
}

# Extract the requested version section and fail the release when it is missing.
$changelog = [System.IO.File]::ReadAllText($changelogPath, $utf8NoBom)
$escapedVersion = [regex]::Escape($Version)
$pattern = "(?ms)^##\s+\[$escapedVersion\]\s+-\s+\d{4}-\d{2}-\d{2}\s*\r?\n(?<Notes>.*?)(?=^##\s+|\z)"
$matches = [regex]::Matches($changelog, $pattern)

if ($matches.Count -ne 1) {
    throw "Expected exactly one CHANGELOG.md section for version $Version, found $($matches.Count)."
}

$releaseNotes = $matches[0].Groups['Notes'].Value.Trim()
if ([string]::IsNullOrWhiteSpace($releaseNotes)) {
    throw "The CHANGELOG.md section for version $Version is empty."
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    $releaseNotes + [Environment]::NewLine,
    $utf8NoBom)

Write-Host "Release notes for $Version written to $resolvedOutputPath"
