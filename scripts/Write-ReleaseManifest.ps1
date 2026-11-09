[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PublishDirectory,
    [Parameter(Mandatory = $true)][string] $AssetsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$publishPath = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\')
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)

function Find-Package([string] $RelativePath) {
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder $RelativePath
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "Restored package not found: $RelativePath"
}

function Copy-PackageNotices([string] $PackagePath, [string] $RelativeDestination) {
    $destination = Join-Path $publishPath $RelativeDestination
    $null = New-Item -ItemType Directory -Path $destination -Force
    $nuspec = Get-ChildItem -LiteralPath $PackagePath -Filter '*.nuspec' | Select-Object -First 1
    if ($null -eq $nuspec) { throw "Missing NuGet metadata: $PackagePath" }
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
    foreach ($file in Get-ChildItem -LiteralPath $PackagePath -File) {
        if ($file.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]PARTY)') {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Encoding UTF8
    $metadata = $document.SelectSingleNode("//*[local-name()='metadata']")
    function Field([string] $Name) {
        $node = $metadata.SelectSingleNode("./*[local-name()='$Name']")
        if ($null -ne $node) { return $node.InnerText }
        return ''
    }
    $license = Field 'license'
    if ($license -notin @('MIT', 'Apache-2.0', 'LGPL-2.1-or-later')) {
        throw "Review the new package license before release: $(Field 'id') ($license)"
    }
    $repository = $metadata.SelectSingleNode("./*[local-name()='repository']")
    return [ordered]@{
        id = Field 'id'
        version = Field 'version'
        authors = Field 'authors'
        copyright = Field 'copyright'
        license = $license
        repository = $(if ($null -ne $repository) { $repository.GetAttribute('url') } else { '' })
        commit = $(if ($null -ne $repository) { $repository.GetAttribute('commit') } else { '' })
        metadata = $RelativeDestination.Replace('\', '/') + '/' + $nuspec.Name
    }
}

$packages = @(foreach ($entry in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($entry.Value.type -ne 'package') { continue }
    $packagePath = Find-Package $entry.Value.path
    Copy-PackageNotices $packagePath (Join-Path 'licenses\packages' ($entry.Name.Replace('/', '-')))
})
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'MyCinema.App.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$runtimes = @(foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $relativePath = $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
    $packagePath = Find-Package $relativePath
    $notice = Copy-PackageNotices $packagePath (Join-Path 'licenses\runtimes' ($framework.name + '-' + $framework.version))
    if (@(Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '^LICENSE' }).Count -eq 0) {
        throw "Missing runtime license: $relativePath"
    }
    $notice
})
$files = @(Get-ChildItem -LiteralPath $publishPath -File -Recurse | Where-Object {
    $_.FullName -ne (Join-Path $publishPath 'release-manifest.json')
} | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($publishPath.Length + 1).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    application = 'MyCinema'
    version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'MyCinema.App.exe')).ProductVersion
    createdUtc = [DateTime]::UtcNow.ToString('o')
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    packages = $packages
    runtimes = $runtimes
    files = $files
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishPath 'release-manifest.json') -Encoding UTF8
Write-Host "Release manifest: $($packages.Count) packages, $($runtimes.Count) runtimes, $($files.Count) files."
