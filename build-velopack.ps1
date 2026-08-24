param (
    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]
    $Configuration = 'Release',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $PackDir = 'release',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $OutputDir = 'velopack',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $PackId = 'NPP',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $PackTitle = 'NPP',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $MainExe = 'NPP.exe',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $Icon = 'NPP\Assets\NPP.ico',

    [Parameter()]
    [string]
    $Version = '',

    [Parameter()]
    [switch]
    $SkipBuild,

    [Parameter()]
    [string]
    $PlatformToolset = '',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $Runtime = 'win-x64',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $PackAuthors = 'NetchX',

    [Parameter()]
    [string]
    $PreviousReleaseRepositoryUrl = '',

    [Parameter()]
    [switch]
    $PreviousReleaseIsPrerelease,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $MyInvocation.MyCommand.Path -Parent))
$repositoryPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

function Get-RepositoryChildPath {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A repository-relative path cannot be empty.'
    }

    $candidate = if ([System.IO.Path]::IsPathRooted($Path)) {
        $Path
    }
    else {
        Join-Path $repositoryRoot $Path
    }

    $fullPath = [System.IO.Path]::GetFullPath($candidate)
    if (-Not $fullPath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must be a child of the repository: $fullPath"
    }

    return $fullPath
}

function Assert-NoRepositoryReparsePoint {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    $currentPath = $Path
    while (-Not $currentPath.Equals($repositoryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -Force -LiteralPath $currentPath
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to remove a repository path that traverses a reparse point: $currentPath"
            }
        }

        $parentPath = [System.IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parentPath) -or $parentPath.Equals($currentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Could not validate repository path: $Path"
        }

        $currentPath = $parentPath
    }
}

function Get-MsiPropertyValue {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Path,

        [Parameter(Mandatory = $true)]
        [string]
        $Name
    )

    $installer = $null
    $database = $null
    $view = $null
    $record = $null

    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember(
            'OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
        $query = "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'"
        $view = $database.GetType().InvokeMember(
            'OpenView', 'InvokeMethod', $null, $database, @($query))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) {
            return $null
        }

        return $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    }
    finally {
        foreach ($comObject in @($record, $view, $database, $installer)) {
            if ($null -ne $comObject -and [Runtime.InteropServices.Marshal]::IsComObject($comObject)) {
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject) | Out-Null
            }
        }
    }
}

function Get-DefaultVersion {
    # GitHub also sets GITHUB_REF_NAME for branches and pull requests. Treat
    # only a valid SemVer tag as a package-version override.
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and -Not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        $tagVersion = ($env:GITHUB_REF_NAME.Trim() -replace '^[vV]', '')
        if ($tagVersion -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?(?:\+[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?$') {
            return $tagVersion
        }
    }

    [xml] $project = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'NPP\NPP.csproj')
    $projectVersion = @(
        $project.Project.PropertyGroup |
            ForEach-Object { @($_.Version, $_.VersionPrefix) } |
            Where-Object { -Not [string]::IsNullOrWhiteSpace($_) }
    ) | Select-Object -First 1

    if (-Not [string]::IsNullOrWhiteSpace($projectVersion)) {
        return $projectVersion
    }

    $updateChecker = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repositoryRoot 'NPP.Core\Controllers\UpdateChecker.cs')
    $match = [regex]::Match($updateChecker, 'AssemblyVersion\s*=\s*@"(?<version>[^"]+)"')
    if ($match.Success) {
        return $match.Groups['version'].Value
    }

    throw 'Version is required because no NPP project version or UpdateChecker AssemblyVersion was found.'
}

function Get-SemanticVersion {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Value
    )

    $semanticVersion = ($Value.Trim() -replace '^[vV]', '')
    if ($semanticVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?(?:\+[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?$') {
        throw "Version must be a SemVer 2 version with three numeric parts: $Value"
    }

    return $semanticVersion
}

Push-Location $repositoryRoot

try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-DefaultVersion
    }

    $Version = Get-SemanticVersion -Value $Version
    $packPath = Get-RepositoryChildPath -Path $PackDir
    $outputPath = Get-RepositoryChildPath -Path $OutputDir
    $iconPath = Get-RepositoryChildPath -Path $Icon

    if (-Not $SkipBuild) {
        $buildParameters = @{
            Configuration = $Configuration
            OutputPath    = $packPath
            Version       = $Version
            Runtime       = $Runtime
        }
        if (-Not [string]::IsNullOrWhiteSpace($PlatformToolset)) {
            $buildParameters.PlatformToolset = $PlatformToolset
        }

        & (Join-Path $repositoryRoot 'build-npp.ps1') @buildParameters
        if ($LASTEXITCODE -ne 0) {
            throw "Building the NPP staging directory failed with exit code $LASTEXITCODE."
        }
    }

    $mainExecutable = Join-Path $packPath $MainExe
    if (-Not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
        throw "Main executable not found: $mainExecutable"
    }

    if (-Not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "Velopack icon not found: $iconPath"
    }

    Assert-NoRepositoryReparsePoint -Path $outputPath
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -Recurse -Force -LiteralPath $outputPath
    }
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "Restoring the Velopack tool failed with exit code $LASTEXITCODE."
    }

    if (-Not [string]::IsNullOrWhiteSpace($PreviousReleaseRepositoryUrl)) {
        $downloadArguments = @(
            'tool', 'run', 'vpk', '--', 'download', 'github',
            '--repoUrl', $PreviousReleaseRepositoryUrl,
            '--outputDir', $outputPath,
            '--channel', $Channel
        )
        if ($PreviousReleaseIsPrerelease) {
            $downloadArguments += @('--pre', 'true')
        }

        & dotnet @downloadArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Downloading the previous Velopack release failed with exit code $LASTEXITCODE."
        }
    }

    $velopackArguments = @(
        'tool', 'run', 'vpk', '--', 'pack',
        '--packId', $PackId,
        '--packVersion', $Version,
        '--packDir', $packPath,
        '--mainExe', $MainExe,
        '--packTitle', $PackTitle,
        '--packAuthors', $PackAuthors,
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--icon', $iconPath,
        '--outputDir', $outputPath,
        '--msi', 'true',
        '--instLocation', 'PerMachine'
    )
    & dotnet @velopackArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Velopack packaging failed with exit code $LASTEXITCODE."
    }

    $machineInstallers = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.msi' -File)
    if ($machineInstallers.Count -ne 1) {
        throw "Expected exactly one per-machine MSI installer, but found $($machineInstallers.Count)."
    }

    $allUsers = Get-MsiPropertyValue -Path $machineInstallers[0].FullName -Name 'ALLUSERS'
    if ($allUsers -ne '1') {
        throw "MSI verification failed: expected ALLUSERS=1, found '$allUsers'."
    }

    $perUserInstallers = @(Get-ChildItem -LiteralPath $outputPath -Filter '*-Setup.exe' -File)
    foreach ($perUserInstaller in $perUserInstallers) {
        Remove-Item -Force -LiteralPath $perUserInstaller.FullName
    }

    $assetManifestPath = Join-Path $outputPath "assets.$Channel.json"
    if (-Not (Test-Path -LiteralPath $assetManifestPath -PathType Leaf)) {
        throw "Velopack asset manifest not found: $assetManifestPath"
    }

    $publishedAssets = @(Get-Content -Raw -LiteralPath $assetManifestPath | ConvertFrom-Json)
    $administratorAssets = @($publishedAssets | Where-Object { $_.Type -ne 'Installer' })
    if (-Not ($administratorAssets | Where-Object { $_.Type -eq 'Msi' })) {
        throw 'Velopack asset manifest does not contain the administrator MSI.'
    }
    ConvertTo-Json -InputObject $administratorAssets -Compress |
        Set-Content -NoNewline -Encoding UTF8 -LiteralPath $assetManifestPath

    Write-Host "Velopack package is ready: $outputPath"
    Write-Host "Administrator installer (ALLUSERS=1): $($machineInstallers[0].FullName)"
}
finally {
    Pop-Location
}

exit 0
