param (
    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]
    $Configuration = 'Release',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $OutputPath = 'release',

    [Parameter()]
    [string]
    $Version = '',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]
    $Runtime = 'win-x64',

    [Parameter()]
    [string]
    $PlatformToolset = ''
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
                throw "Refusing to modify a repository path that traverses a reparse point: $currentPath"
            }
        }

        $parentPath = [System.IO.Path]::GetDirectoryName($currentPath)
        if ([string]::IsNullOrWhiteSpace($parentPath) -or $parentPath.Equals($currentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Could not validate repository path: $Path"
        }

        $currentPath = $parentPath
    }
}

function Reset-RepositoryDirectory {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    $safePath = Get-RepositoryChildPath -Path $Path
    Assert-NoRepositoryReparsePoint -Path $safePath

    if (Test-Path -LiteralPath $safePath) {
        Remove-Item -Recurse -Force -LiteralPath $safePath
    }

    New-Item -ItemType Directory -Force -Path $safePath | Out-Null
    return $safePath
}

function Get-DefaultVersion {
    # Branch and pull-request builds also expose GITHUB_REF_NAME (for example
    # "main" or "123/merge"). Only a SemVer tag is a valid version override;
    # all other CI refs must use the version declared by the NPP project.
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and -Not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME)) {
        $tagVersion = ($env:GITHUB_REF_NAME.Trim() -replace '^[vV]', '')
        if ($tagVersion -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?(?:\+[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?$') {
            return $tagVersion
        }
    }

    $projectPath = Join-Path $repositoryRoot 'NPP\NPP.csproj'
    [xml] $project = Get-Content -Raw -LiteralPath $projectPath
    $projectVersion = @(
        $project.Project.PropertyGroup |
            ForEach-Object { @($_.Version, $_.VersionPrefix) } |
            Where-Object { -Not [string]::IsNullOrWhiteSpace($_) }
    ) | Select-Object -First 1

    if (-Not [string]::IsNullOrWhiteSpace($projectVersion)) {
        return $projectVersion
    }

    $updateCheckerPath = Join-Path $repositoryRoot 'NPP.Core\Controllers\UpdateChecker.cs'
    $updateChecker = Get-Content -Raw -Encoding UTF8 -LiteralPath $updateCheckerPath
    $match = [regex]::Match($updateChecker, 'AssemblyVersion\s*=\s*@"(?<version>[^"]+)"')
    if ($match.Success) {
        return $match.Groups['version'].Value
    }

    throw 'Version is required because no NPP project version or UpdateChecker AssemblyVersion was found.'
}

function Assert-AdministratorManifest {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Path
    )

    [xml] $manifest = Get-Content -Raw -LiteralPath $Path
    $requestedExecutionLevel = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($null -eq $requestedExecutionLevel -or
        $requestedExecutionLevel.GetAttribute('level') -ne 'requireAdministrator') {
        throw "NPP must request administrator privileges in its application manifest: $Path"
    }
}

function Assert-EmbeddedAdministratorManifest {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $ExecutablePath
    )

    $mtCommand = Get-Command 'mt.exe' -ErrorAction SilentlyContinue
    $mtPath = if ($null -ne $mtCommand) { $mtCommand.Source } else { $null }

    if ([string]::IsNullOrWhiteSpace($mtPath)) {
        $globalPackagesOutput = @(& dotnet nuget locals global-packages --list)
        if ($LASTEXITCODE -ne 0 -or $globalPackagesOutput.Count -eq 0) {
            throw 'Unable to locate the NuGet global-packages directory for manifest verification.'
        }

        $globalPackagesPath = ($globalPackagesOutput[0] -replace '^[^:]+:\s*', '').Trim()
        $sdkBuildToolsPath = Join-Path $globalPackagesPath 'microsoft.windows.sdk.buildtools'
        $mtPath = Get-ChildItem -LiteralPath $sdkBuildToolsPath -Recurse -Filter 'mt.exe' -File |
            Where-Object { $_.FullName -match '[\\/]x64[\\/]mt\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if ([string]::IsNullOrWhiteSpace($mtPath) -or -Not (Test-Path -LiteralPath $mtPath -PathType Leaf)) {
        throw 'Microsoft Manifest Tool (mt.exe) was not found; NPP.exe elevation cannot be verified.'
    }

    $extractedManifestPath = Join-Path ([System.IO.Path]::GetTempPath()) "NPP-manifest-$([guid]::NewGuid().ToString('N')).xml"
    try {
        & $mtPath '-nologo' "-inputresource:$ExecutablePath;#1" "-out:$extractedManifestPath"
        if ($LASTEXITCODE -ne 0) {
            throw "Extracting the NPP executable manifest failed with exit code $LASTEXITCODE."
        }

        [xml] $embeddedManifest = Get-Content -Raw -LiteralPath $extractedManifestPath
        $requestedExecutionLevel = $embeddedManifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
        if ($null -eq $requestedExecutionLevel -or
            $requestedExecutionLevel.GetAttribute('level') -ne 'requireAdministrator') {
            throw "Published NPP.exe does not request administrator privileges: $ExecutablePath"
        }
    }
    finally {
        if (Test-Path -LiteralPath $extractedManifestPath) {
            Remove-Item -Force -LiteralPath $extractedManifestPath
        }
    }
}

function Get-VersionInfo {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Value
    )

    $semanticVersion = ($Value.Trim() -replace '^[vV]', '')
    $match = [regex]::Match(
        $semanticVersion,
        '^(?<numeric>\d+\.\d+\.\d+)(?:-[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?(?:\+[0-9A-Za-z](?:[0-9A-Za-z.-]*[0-9A-Za-z])?)?$')
    if (-Not $match.Success) {
        throw "Version must be a SemVer 2 version with three numeric parts: $Value"
    }

    $numericVersion = "$($match.Groups['numeric'].Value).0"

    return [pscustomobject]@{
        Semantic = $semanticVersion
        Numeric  = $numericVersion
    }
}

function Get-MSBuildPath {
    $msbuildCommand = Get-Command 'msbuild.exe' -ErrorAction SilentlyContinue
    if ($null -ne $msbuildCommand) {
        return $msbuildCommand.Source
    }

    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $vswherePath = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath) {
        $discoveredPaths = @(& $vswherePath -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
        $discoveredPath = $discoveredPaths | Select-Object -First 1
        if (-Not [string]::IsNullOrWhiteSpace($discoveredPath) -and (Test-Path -LiteralPath $discoveredPath)) {
            return $discoveredPath
        }
    }

    throw 'MSBuild was not found. Install Visual Studio C++ Build Tools or run microsoft/setup-msbuild first.'
}

function Invoke-CheckedCommand {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]
        $ArgumentList,

        [Parameter(Mandatory = $true)]
        [string]
        $Description
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot

try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-DefaultVersion
    }

    $versionInfo = Get-VersionInfo -Value $Version
    $applicationManifestPath = Join-Path $repositoryRoot 'NPP\app.manifest'
    Assert-AdministratorManifest -Path $applicationManifestPath
    $stagingPath = Reset-RepositoryDirectory -Path $OutputPath
    $msbuildPath = Get-MSBuildPath

    Write-Host "Building NPP native components ($Configuration|x64)..."
    $nativeProjects = @(
        'Redirector\Redirector.vcxproj',
        'RouteHelper\RouteHelper.vcxproj'
    )

    foreach ($nativeProject in $nativeProjects) {
        $nativeBuildArguments = @(
            (Join-Path $repositoryRoot $nativeProject),
            '/nologo',
            '/m',
            '/verbosity:minimal',
            "/property:Configuration=$Configuration",
            '/property:Platform=x64'
        )

        if (-Not [string]::IsNullOrWhiteSpace($PlatformToolset)) {
            $nativeBuildArguments += "/property:PlatformToolset=$PlatformToolset"
        }

        Invoke-CheckedCommand `
            -FilePath $msbuildPath `
            -ArgumentList $nativeBuildArguments `
            -Description "Building $nativeProject"
    }

    $redirectorPath = Join-Path $repositoryRoot "Redirector\bin\$Configuration\Redirector.bin"
    $nfapiPath = Join-Path $repositoryRoot "Redirector\bin\$Configuration\nfapi.dll"
    $routeHelperPath = Join-Path $repositoryRoot "RouteHelper\bin\$Configuration\RouteHelper.bin"
    $nfdriverPath = Join-Path $repositoryRoot 'Storage\nfdriver.sys'
    $iconPath = Join-Path $repositoryRoot 'NPP\Assets\NPP.ico'

    foreach ($requiredFile in @($redirectorPath, $nfapiPath, $routeHelperPath, $nfdriverPath, $iconPath)) {
        if (-Not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Required NPP release file was not produced: $requiredFile"
        }
    }

    Write-Host "Publishing NPP $($versionInfo.Semantic) as a self-contained WinUI app..."
    $publishArguments = @(
        'publish',
        (Join-Path $repositoryRoot 'NPP\NPP.csproj'),
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--self-contained', 'true',
        '--output', $stagingPath,
        '-p:Platform=x64',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:EnableMsixTooling=true',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        "-p:Version=$($versionInfo.Semantic)",
        "-p:AssemblyVersion=$($versionInfo.Numeric)",
        "-p:FileVersion=$($versionInfo.Numeric)",
        "-p:InformationalVersion=$($versionInfo.Semantic)",
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )

    if ($Configuration -eq 'Release') {
        $publishArguments += @('-p:DebugSymbols=false', '-p:DebugType=None')
    }

    Invoke-CheckedCommand `
        -FilePath 'dotnet' `
        -ArgumentList $publishArguments `
        -Description 'Publishing NPP'

    $nativeBinPath = Join-Path $stagingPath 'bin'
    New-Item -ItemType Directory -Force -Path $nativeBinPath | Out-Null
    Copy-Item -Force -LiteralPath $redirectorPath -Destination (Join-Path $stagingPath 'Redirector.bin')
    Copy-Item -Force -LiteralPath $nfapiPath -Destination (Join-Path $stagingPath 'nfapi.dll')
    Copy-Item -Force -LiteralPath $routeHelperPath -Destination (Join-Path $stagingPath 'RouteHelper.bin')
    Copy-Item -Force -LiteralPath $nfdriverPath -Destination (Join-Path $nativeBinPath 'nfdriver.sys')
    Copy-Item -Force -LiteralPath $iconPath -Destination (Join-Path $stagingPath 'NPP.ico')

    $mainExecutable = Join-Path $stagingPath 'NPP.exe'
    foreach ($stagedFile in @(
        $mainExecutable,
        (Join-Path $stagingPath 'NPP.dll'),
        (Join-Path $stagingPath 'NPP.Core.dll'),
        (Join-Path $stagingPath 'NPP.pri'),
        (Join-Path $stagingPath 'Velopack.dll'),
        (Join-Path $stagingPath 'Assets\NPP.ico'),
        (Join-Path $stagingPath 'Assets\NPP.svg'),
        (Join-Path $stagingPath 'Redirector.bin'),
        (Join-Path $stagingPath 'nfapi.dll'),
        (Join-Path $stagingPath 'RouteHelper.bin'),
        (Join-Path $nativeBinPath 'nfdriver.sys'),
        (Join-Path $stagingPath 'NPP.ico')
    )) {
        if (-Not (Test-Path -LiteralPath $stagedFile -PathType Leaf)) {
            throw "NPP staging verification failed: $stagedFile"
        }
    }

    if (Test-Path -LiteralPath (Join-Path $stagingPath 'Netch.exe')) {
        throw 'NPP staging verification failed because it contains the legacy Netch.exe.'
    }

    Assert-EmbeddedAdministratorManifest -ExecutablePath $mainExecutable
    $publishedVersion = (Get-Item -LiteralPath $mainExecutable).VersionInfo.ProductVersion
    if (-Not $publishedVersion.StartsWith($versionInfo.Semantic, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "NPP staging version mismatch: expected $($versionInfo.Semantic), found $publishedVersion."
    }

    Write-Host "NPP staging is ready: $stagingPath"
    Write-Host "NPP.exe product version: $publishedVersion"
    Write-Host 'NPP.exe execution level: requireAdministrator'
}
finally {
    Pop-Location
}

exit 0
