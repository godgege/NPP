param (
    [Parameter()]
    [string]
    $Version = '',

    [Parameter()]
    [string]
    $PlatformToolset = ''
)

$ErrorActionPreference = 'Stop'

Push-Location (Split-Path $MyInvocation.MyCommand.Path -Parent)

try {
    $displayVersion = if ([string]::IsNullOrWhiteSpace($Version)) { 'the configured version' } else { $Version }
    Write-Host "Packing NPP $displayVersion with Velopack..."

    $packParameters = @{
        Configuration = 'Release'
        PackDir       = 'release'
        OutputDir     = 'velopack'
    }
    if (-Not [string]::IsNullOrWhiteSpace($Version)) {
        $packParameters.Version = $Version
    }
    if (-Not [string]::IsNullOrWhiteSpace($PlatformToolset)) {
        $packParameters.PlatformToolset = $PlatformToolset
    }

    & .\build-velopack.ps1 @packParameters
    if ($LASTEXITCODE -ne 0) {
        throw "NPP Velopack packaging failed with exit code $LASTEXITCODE."
    }

    Write-Host ''
    Write-Host 'Velopack output:'
    Get-ChildItem -Path 'velopack' -File | Sort-Object Name | ForEach-Object {
        Write-Host "  $($_.Name)"
    }
}
finally {
    Pop-Location
}

exit 0
