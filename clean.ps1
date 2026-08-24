Push-Location (Split-Path $MyInvocation.MyCommand.Path -Parent)

function Delete {
    param (
        [string]
        $Path
    )

    if (Test-Path $Path) {
        rm -Recurse -Force $Path | Out-Null
    }
}

Delete '.vs'
Delete 'release'
Delete 'velopack'
Delete 'Netch\bin'
Delete 'Netch\obj'
Delete 'NPP\bin'
Delete 'NPP\obj'
Delete 'NPP.Core\bin'
Delete 'NPP.Core\obj'
Delete 'Tests\bin'
Delete 'Tests\obj'
Delete 'TestResults'
Delete 'Redirector\bin'
Delete 'Redirector\obj'
Delete 'RedirectorTester\bin'
Delete 'RedirectorTester\obj'
Delete 'RouteHelper\bin'
Delete 'RouteHelper\obj'

Delete 'Netch\*.csproj.user'
Delete 'NPP\*.csproj.user'
Delete 'NPP.Core\*.csproj.user'
Delete 'Redirector\*.vcxproj.user'
Delete 'RedirectorTester\*.csproj.user'
Delete 'RouteHelper\*.vcxproj.user'

.\other\clean.ps1

Pop-Location
exit $lastExitCode
