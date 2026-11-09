[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryDirectory 'Person Movie Player.csproj'
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryDirectory '.artifacts\official-release'))
$buildDirectory = Join-Path $releaseDirectory 'build'
$publishDirectory = Join-Path $releaseDirectory 'publish'
$programsDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'
$installDirectory = [IO.Path]::GetFullPath((Join-Path $programsDirectory 'MyCinema'))
$executablePath = Join-Path $installDirectory 'MyCinema.App.exe'
$updateDirectory = Join-Path $programsDirectory ('MyCinema.update-' + [Guid]::NewGuid().ToString('N'))
$backupDirectory = Join-Path $programsDirectory ('MyCinema.previous-' + [Guid]::NewGuid().ToString('N'))

function Assert-ManagedDirectory([string] $Path, [string] $Parent, [string] $NamePrefix) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($fullPath) -ne $parentPath -or
        -not [IO.Path]::GetFileName($fullPath).StartsWith($NamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a directory outside the MyCinema release locations: $fullPath"
    }
    if ((Test-Path -LiteralPath $fullPath) -and
        ((Get-Item -LiteralPath $fullPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a linked directory: $fullPath"
    }
}

function Assert-AppClosed {
    $running = @(Get-Process -Name 'MyCinema.App' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -eq $executablePath
    })
    if ($running.Count -gt 0) {
        throw "Close MyCinema before updating it, then run this script again. Installed app: $executablePath"
    }
}

Assert-AppClosed
Assert-ManagedDirectory $publishDirectory $releaseDirectory 'publish'
Assert-ManagedDirectory $installDirectory $programsDirectory 'MyCinema'
Assert-ManagedDirectory $updateDirectory $programsDirectory 'MyCinema.update-'
Assert-ManagedDirectory $backupDirectory $programsDirectory 'MyCinema.previous-'

Write-Host 'Building and testing the official Release version...'
& dotnet test (Join-Path $repositoryDirectory 'Person Movie Player.slnx') --configuration Release --artifacts-path $buildDirectory --verbosity minimal `
    --logger 'trx;LogFileName=release-tests.trx' --results-directory (Join-Path $releaseDirectory 'test-results')
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed. The installed app was not changed.' }

if (Test-Path -LiteralPath $publishDirectory) {
    Assert-ManagedDirectory $publishDirectory $releaseDirectory 'publish'
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
Write-Host 'Publishing the Windows x64 app with its .NET and VLC runtimes...'
& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true --property:PublishProfile=WindowsRelease --artifacts-path $buildDirectory --output $publishDirectory --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed. The installed app was not changed.' }

foreach ($relativePath in @('MyCinema.App.exe', 'coreclr.dll', 'PresentationFramework.dll', 'e_sqlite3.dll', 'libvlc\win-x64\libvlc.dll', 'libvlc\win-x64\libvlccore.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
        throw "The published app is incomplete: missing $relativePath. The installed app was not changed."
    }
}

foreach ($relativePath in @('README.md', 'docs\RELEASE-VALIDATION.md', 'THIRD-PARTY-NOTICES.md', 'licenses\LGPL-2.1.txt', 'licenses\Apache-2.0.txt', 'licenses\CommunityToolkit-MIT.md',
    'libvlc\win-x64\plugins\codec\libavcodec_plugin.dll', 'libvlc\win-x64\plugins\demux\libmkv_plugin.dll',
    'libvlc\win-x64\plugins\video_output\libdirect3d11_plugin.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
        throw "The published app is incomplete: missing $relativePath. The installed app was not changed."
    }
}
& (Join-Path $PSScriptRoot 'Write-ReleaseManifest.ps1') -PublishDirectory $publishDirectory `
    -AssetsPath (Join-Path $buildDirectory 'obj\Person Movie Player\project.assets.json')

try {
    $null = New-Item -ItemType Directory -Path $programsDirectory -Force
    Copy-Item -LiteralPath $publishDirectory -Destination $updateDirectory -Recurse
    Assert-AppClosed
    if (Test-Path -LiteralPath $installDirectory) {
        Assert-ManagedDirectory $installDirectory $programsDirectory 'MyCinema'
        Assert-ManagedDirectory $backupDirectory $programsDirectory 'MyCinema.previous-'
        Move-Item -LiteralPath $installDirectory -Destination $backupDirectory
    }
    try {
        Assert-ManagedDirectory $updateDirectory $programsDirectory 'MyCinema.update-'
        Assert-ManagedDirectory $installDirectory $programsDirectory 'MyCinema'
        Move-Item -LiteralPath $updateDirectory -Destination $installDirectory
    }
    catch {
        if (Test-Path -LiteralPath $backupDirectory) {
            Assert-ManagedDirectory $backupDirectory $programsDirectory 'MyCinema.previous-'
            Assert-ManagedDirectory $installDirectory $programsDirectory 'MyCinema'
            Move-Item -LiteralPath $backupDirectory -Destination $installDirectory
        }
        throw
    }

    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($shortcutDirectory in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
        $null = New-Item -ItemType Directory -Path $shortcutDirectory -Force
        $shortcut = $shortcutShell.CreateShortcut((Join-Path $shortcutDirectory 'MyCinema.lnk'))
        $shortcut.TargetPath = $executablePath
        $shortcut.WorkingDirectory = $installDirectory
        $shortcut.IconLocation = "$executablePath,0"
        $shortcut.Description = 'MyCinema - your local movie and TV library'
        $shortcut.Save()
    }
}
finally {
    if (Test-Path -LiteralPath $updateDirectory) {
        Assert-ManagedDirectory $updateDirectory $programsDirectory 'MyCinema.update-'
        Remove-Item -LiteralPath $updateDirectory -Recurse -Force
    }
}

if (Test-Path -LiteralPath $backupDirectory) {
    Assert-ManagedDirectory $backupDirectory $programsDirectory 'MyCinema.previous-'
    Remove-Item -LiteralPath $backupDirectory -Recurse -Force
}
Write-Host "Installed: $executablePath"
Write-Host 'Use the MyCinema Desktop or Start menu shortcut. Future releases update this same location.'
