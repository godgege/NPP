param (
    [Parameter()]
    [string] $SourcePath = 'release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
$source = if ([IO.Path]::IsPathRooted($SourcePath)) {
    [IO.Path]::GetFullPath($SourcePath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $SourcePath))
}
$target = Join-Path $repositoryRoot 'velopack\NPP-Portable'
$shortcutName = 'NPP-Portable.lnk'

if (-not $source.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The portable source must be a child of this repository.'
}
foreach ($required in @('NPP.exe', 'NPP.dll', 'NPP.Core.dll', 'clients\v2rayN\v2rayN.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) {
        throw "Portable build is incomplete: $required"
    }
}

# Keep one stable name. A running Sandbox client must be closed before its
# changed binaries are replaced; a file lock never causes a versioned folder.
$changes = @()
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    if ($relative -eq $shortcutName) { continue }
    $destination = Join-Path $target $relative
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        $changes += [pscustomobject]@{ Source = $file.FullName; Destination = $destination }
    }
}

# Preflight every changed file before copying any of them, so a locked DLL
# cannot leave a mixture of old and new XAML/assemblies in the shared folder.
foreach ($change in $changes) {
    if (Test-Path -LiteralPath $change.Destination -PathType Leaf) {
        try {
            $handle = [IO.File]::Open($change.Destination, [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $handle.Dispose()
        }
        catch {
            throw "Close NPP from its tray menu inside Sandbox, then run update-portable.ps1 again. File in use: $($change.Destination)"
        }
    }
}

foreach ($change in $changes) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.Destination)) | Out-Null
    Copy-Item -LiteralPath $change.Source -Destination $change.Destination -Force
}

$shortcutLocations = @(
    @{ Directory = $source; GuestDirectory = 'C:\Users\WDAGUtilityAccount\Desktop\NPP-Portable' },
    @{ Directory = $target; GuestDirectory = 'C:\Users\WDAGUtilityAccount\Desktop\NPP-Package\NPP-Portable' },
    @{ Directory = (Join-Path $repositoryRoot 'velopack'); GuestDirectory = 'C:\Users\WDAGUtilityAccount\Desktop\NPP-Package\NPP-Portable' }
)
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($location in $shortcutLocations) {
        [IO.Directory]::CreateDirectory($location.Directory) | Out-Null
        $shortcut = $shell.CreateShortcut((Join-Path $location.Directory $shortcutName))
        try {
            $shortcut.TargetPath = Join-Path $location.GuestDirectory 'NPP.exe'
            $shortcut.WorkingDirectory = $location.GuestDirectory
            $shortcut.IconLocation = $shortcut.TargetPath + ',0'
            $shortcut.Description = 'NPP'
            $shortcut.Save()
        }
        finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) }
    }
}
finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
Write-Host "Latest portable build: $target"
Write-Host "Updated files: $($changes.Count). Entry: $shortcutName"
