param (
    [Parameter()]
    [string] $ArchivePath = ''
)

$ErrorActionPreference = 'Stop'
$resourceRoot = Join-Path $PSScriptRoot 'Storage'
$packagePath = Join-Path $resourceRoot 'v2rayN'
$metadataPath = Join-Path $resourceRoot 'v2rayN.package.json'
$metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
$markerPath = Join-Path $packagePath 'package.json'

if ([string]::IsNullOrWhiteSpace($ArchivePath) -and (Test-Path -LiteralPath $markerPath)) {
    $installed = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
    $missing = @($metadata.requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $packagePath $_) -PathType Leaf) })
    if ($installed.sha256 -eq $metadata.sha256 -and $missing.Count -eq 0) {
        Write-Host "v2rayN $($metadata.version) resources are ready: $packagePath"
        return
    }
}

$stagingPath = Join-Path $resourceRoot ('.v2rayN-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingPath | Out-Null
try {
    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        $ArchivePath = Join-Path $stagingPath 'v2rayN.zip'
        Write-Host "Downloading v2rayN $($metadata.version)..."
        Invoke-WebRequest -Uri $metadata.url -OutFile $ArchivePath
    }

    $actualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $metadata.sha256) {
        throw "v2rayN archive SHA256 mismatch. Expected $($metadata.sha256), found $actualHash."
    }

    Expand-Archive -LiteralPath $ArchivePath -DestinationPath (Join-Path $stagingPath 'extracted')
    $extractedPath = Join-Path (Join-Path $stagingPath 'extracted') $metadata.archiveRoot
    foreach ($requiredFile in $metadata.requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $extractedPath $requiredFile) -PathType Leaf)) {
            throw "v2rayN resource is missing: $requiredFile"
        }
    }

    # This directory contains distribution files only. Never replace a directory
    # into which somebody has launched the portable client and saved their data.
    foreach ($userDirectory in @('guiConfigs', 'guiBackups', 'guiLogs')) {
        if (Test-Path -LiteralPath (Join-Path $packagePath $userDirectory)) {
            throw "Move user data out of Storage\v2rayN before replacing resources: $userDirectory"
        }
    }

    $resourcePrefix = [IO.Path]::GetFullPath($resourceRoot).TrimEnd('\') + '\'
    $resolvedPackage = [IO.Path]::GetFullPath($packagePath)
    $resolvedExtracted = [IO.Path]::GetFullPath($extractedPath)
    if (-not $resolvedPackage.StartsWith($resourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedExtracted.StartsWith([IO.Path]::GetFullPath($stagingPath) + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Resource paths must remain inside Storage.'
    }
    if (Test-Path -LiteralPath $packagePath) {
        if (((Get-Item -Force -LiteralPath $packagePath).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The v2rayN resource directory cannot be a reparse point.'
        }
        Remove-Item -Recurse -Force -LiteralPath $resolvedPackage
    }
    Move-Item -LiteralPath $resolvedExtracted -Destination $resolvedPackage
    Copy-Item -LiteralPath $metadataPath -Destination $markerPath
    Copy-Item -LiteralPath (Join-Path $resourceRoot 'v2rayN.LICENSE') -Destination (Join-Path $packagePath 'LICENSE')
    Write-Host "v2rayN $($metadata.version) resources prepared: $packagePath"
}
finally {
    $resolvedStaging = [IO.Path]::GetFullPath($stagingPath)
    $stagingPrefix = [IO.Path]::GetFullPath($resourceRoot).TrimEnd('\') + '\.v2rayN-'
    if ($resolvedStaging.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -Recurse -Force -LiteralPath $resolvedStaging
    }
}
