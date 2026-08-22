[CmdletBinding()]
param(
    [string] $ArtifactDirectory = 'artifacts\release-candidate'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot 'OpenVisionLab.MachineStudio.sln'
$projectPath = Join-Path $repoRoot 'src\OpenVisionLab.MachineStudio\OpenVisionLab.MachineStudio.csproj'
$artifactRoot = if ([System.IO.Path]::IsPathRooted($ArtifactDirectory)) {
    [System.IO.Path]::GetFullPath($ArtifactDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ArtifactDirectory))
}

if (Test-Path -LiteralPath $artifactRoot) {
    throw "Release artifact directory already exists: $artifactRoot"
}

$sourceStatus = @(& git -C $repoRoot status --porcelain=v1)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read Git working-tree state.'
}
if ($sourceStatus.Count -ne 0) {
    throw 'Release candidate requires a clean Git working tree.'
}

& (Join-Path $PSScriptRoot 'verify-asset-provenance.ps1') -RequireDistributionApproved

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$sourceBranch = & git -C $repoRoot branch --show-current
$sourceBranch = if ($null -eq $sourceBranch) { '' } else { $sourceBranch.Trim() }
$releaseTag = if ($env:GITHUB_REF_TYPE -eq 'tag') { $env:GITHUB_REF_NAME } else { $null }
[xml] $buildProperties = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$productVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ($productVersion -notmatch '^\d+\.\d+\.\d+(?:-rc\.\d+)?$') {
    throw "Product version is not a supported release version: $productVersion"
}

$runtimeIdentifier = 'win-x64'
$packageName = "OpenVisionLab.MachineStudio-$productVersion-windows-x64-self-contained"
$scratchRoot = [System.IO.Path]::GetFullPath("$artifactRoot.scratch")
if (Test-Path -LiteralPath $scratchRoot) {
    throw "Release scratch directory already exists: $scratchRoot"
}
$publishRoot = Join-Path $scratchRoot 'publish'
$stagingRoot = Join-Path $scratchRoot 'staging'
$packageRoot = Join-Path $stagingRoot $packageName
$extractRoot = Join-Path $scratchRoot 'extracted'
$dotnetArtifactsRoot = Join-Path $scratchRoot 'dotnet-artifacts'
$archivePath = Join-Path $artifactRoot "$packageName.zip"
$vulnerablePath = Join-Path $artifactRoot 'nuget-vulnerable.json'
$deprecatedPath = Join-Path $artifactRoot 'nuget-deprecated.json'

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$tempRoot = Join-Path $scratchRoot 'temp'
New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
$env:TEMP = $tempRoot
$env:TMP = $tempRoot
$env:ArtifactsPath = $dotnetArtifactsRoot

& dotnet restore $solutionPath -p:ArtifactsPath=$dotnetArtifactsRoot
if ($LASTEXITCODE -ne 0) {
    throw "Restore failed with exit code $LASTEXITCODE."
}

& dotnet build $solutionPath -c Release --no-restore `
    -p:ArtifactsPath=$dotnetArtifactsRoot `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed with exit code $LASTEXITCODE."
}

& dotnet test $solutionPath -c Release --no-build --no-restore `
    -p:ArtifactsPath=$dotnetArtifactsRoot
if ($LASTEXITCODE -ne 0) {
    throw "Release tests failed with exit code $LASTEXITCODE."
}

& dotnet list $projectPath package --vulnerable --include-transitive --format json > $vulnerablePath
if ($LASTEXITCODE -ne 0) {
    throw "NuGet vulnerability audit failed with exit code $LASTEXITCODE."
}
& dotnet list $projectPath package --deprecated --include-transitive --format json > $deprecatedPath
if ($LASTEXITCODE -ne 0) {
    throw "NuGet deprecation audit failed with exit code $LASTEXITCODE."
}

$vulnerableJson = Get-Content -Raw -LiteralPath $vulnerablePath
$deprecatedJson = Get-Content -Raw -LiteralPath $deprecatedPath
if ($vulnerableJson -match '"vulnerabilities"') {
    throw 'NuGet vulnerability audit reported one or more findings.'
}
if ($deprecatedJson -match '"deprecationReasons"') {
    throw 'NuGet deprecation audit reported one or more findings.'
}

& dotnet restore $projectPath -r $runtimeIdentifier `
    -p:ArtifactsPath=$dotnetArtifactsRoot
if ($LASTEXITCODE -ne 0) {
    throw "$runtimeIdentifier restore failed with exit code $LASTEXITCODE."
}

& dotnet publish $projectPath `
    -c Release `
    --no-restore `
    -r $runtimeIdentifier `
    --self-contained true `
    -p:ArtifactsPath=$dotnetArtifactsRoot `
    -p:ContinuousIntegrationBuild=true `
    -p:PublishSingleFile=false `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "Release publish failed with exit code $LASTEXITCODE."
}

$runtimeConfigPath = Join-Path $publishRoot 'OpenVisionLab.MachineStudio.runtimeconfig.json'
$runtimeConfig = Get-Content -Raw -LiteralPath $runtimeConfigPath | ConvertFrom-Json
$includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
$netCoreVersion = [string] ($includedFrameworks |
    Where-Object name -eq 'Microsoft.NETCore.App').version
$windowsDesktopVersion = [string] ($includedFrameworks |
    Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
if ([string]::IsNullOrWhiteSpace($netCoreVersion) -or
    [string]::IsNullOrWhiteSpace($windowsDesktopVersion)) {
    throw 'Published runtimeconfig does not prove bundled .NET and Windows Desktop runtimes.'
}

$nugetPackagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
else {
    [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
}
$runtimeNoticeTarget = Join-Path $publishRoot 'THIRD-PARTY-NOTICES'
New-Item -ItemType Directory -Force -Path $runtimeNoticeTarget | Out-Null
$runtimeNotices = @(
    @{
        source = Join-Path $nugetPackagesRoot "microsoft.netcore.app.runtime.win-x64\$netCoreVersion\LICENSE.TXT"
        target = 'DOTNET-RUNTIME-LICENSE.txt'
    },
    @{
        source = Join-Path $nugetPackagesRoot "microsoft.netcore.app.runtime.win-x64\$netCoreVersion\THIRD-PARTY-NOTICES.TXT"
        target = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt'
    },
    @{
        source = Join-Path $nugetPackagesRoot "microsoft.windowsdesktop.app.runtime.win-x64\$windowsDesktopVersion\LICENSE"
        target = 'WINDOWSDESKTOP-RUNTIME-LICENSE.txt'
    }
)
foreach ($notice in $runtimeNotices) {
    if (-not (Test-Path -LiteralPath $notice.source -PathType Leaf)) {
        throw "Runtime notice is missing: $($notice.source)"
    }
    Copy-Item -LiteralPath $notice.source -Destination (Join-Path $runtimeNoticeTarget $notice.target)
}

@(
    "OpenVisionLab Machine Studio $productVersion",
    'Windows x64 self-contained release',
    '',
    'Extract the complete ZIP, then run OpenVisionLab.MachineStudio.exe.',
    'No separate .NET installation is required.',
    "Bundled runtimes: Microsoft.NETCore.App $netCoreVersion and",
    "Microsoft.WindowsDesktop.App $windowsDesktopVersion.",
    '',
    'This package is unsigned and is not an installer or production-control software.',
    'See LICENSE.txt, NOTICE.txt, THIRD-PARTY-NOTICES.md, and the',
    'THIRD-PARTY-NOTICES directory for licensing information.'
) | Set-Content -LiteralPath (Join-Path $publishRoot 'SELF-CONTAINED-README.txt') -Encoding utf8

$requiredFiles = @(
    'OpenVisionLab.MachineStudio.exe',
    'OpenVisionLab.MachineStudio.dll',
    'OpenVisionLab.MachineStudio.runtimeconfig.json',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'PresentationFramework.dll',
    'WindowsBase.dll',
    'System.Private.CoreLib.dll',
    'Samples/AutomaticTransferCell.ovmachine',
    'Samples/SemiconductorRecipes/01-FoupLoadPort.ovmachine',
    'Samples/SemiconductorRecipes/02-CassetteMapper.ovmachine',
    'Samples/SemiconductorRecipes/03-WaferPrealigner.ovmachine',
    'Samples/SemiconductorRecipes/04-WaferOcrInspection.ovmachine',
    'Samples/SemiconductorRecipes/05-LoadLockEntry.ovmachine',
    'Samples/SemiconductorRecipes/06-SpinCoatTrack.ovmachine',
    'Samples/SemiconductorRecipes/07-DevelopTrack.ovmachine',
    'Samples/SemiconductorRecipes/08-DryEtchTransfer.ovmachine',
    'Samples/SemiconductorRecipes/09-CmpTransfer.ovmachine',
    'Samples/SemiconductorRecipes/10-MetrologySorter.ovmachine',
    'LICENSE.txt',
    'NOTICE.txt',
    'THIRD-PARTY-NOTICES.md',
    'SELF-CONTAINED-README.txt',
    'ASSET-PROVENANCE.json',
    'THIRD-PARTY-NOTICES/WPF-UI-LICENSE.md',
    'THIRD-PARTY-NOTICES/WPF-UI-THIRD-PARTY-NOTICES.txt',
    'THIRD-PARTY-NOTICES/DOTNET-RUNTIME-LICENSE.txt',
    'THIRD-PARTY-NOTICES/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
    'THIRD-PARTY-NOTICES/WINDOWSDESKTOP-RUNTIME-LICENSE.txt'
)
$missingFiles = @($requiredFiles | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $publishRoot $_) -PathType Leaf)
})
if ($missingFiles.Count -ne 0) {
    throw "Published payload is incomplete: $($missingFiles -join ', ')"
}

$executable = Get-Item -LiteralPath (Join-Path $publishRoot 'OpenVisionLab.MachineStudio.exe')
if ($executable.VersionInfo.ProductVersion -ne "$productVersion+g$sourceCommit.clean") {
    throw "Published product version is not the exact clean source commit: $($executable.VersionInfo.ProductVersion)."
}

$publishPrefixLength = $publishRoot.TrimEnd('\').Length + 1
$payloadFiles = @(
    Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject] [ordered]@{
                path = $_.FullName.Substring($publishPrefixLength).Replace('\', '/')
                sizeBytes = $_.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            }
        })

if (@(& git -C $repoRoot status --porcelain=v1).Count -ne 0) {
    throw 'Build or publish changed the tracked working tree.'
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
Copy-Item -Path "$publishRoot\*" -Destination $packageRoot -Recurse

$manifest = [ordered]@{
    schemaVersion = '1.0'
    applicationName = 'OpenVisionLab Machine Studio'
    applicationVersion = $productVersion
    publicationState = if ($releaseTag) { 'github-release-candidate' } else { 'ci-release-candidate' }
    releaseTag = $releaseTag
    signed = $false
    installer = $false
    configuration = 'Release'
    targetFramework = 'net8.0-windows'
    runtimeIdentifier = $runtimeIdentifier
    publishKind = 'self-contained'
    runtimeBundled = $true
    bundledNetCoreVersion = $netCoreVersion
    bundledWindowsDesktopVersion = $windowsDesktopVersion
    runtimePrerequisite = 'None; Microsoft.NETCore.App and Microsoft.WindowsDesktop.App 8.x are bundled.'
    gitCommit = $sourceCommit
    gitBranch = $sourceBranch
    gitWorkingTree = 'clean'
    dotNetSdkVersion = (& dotnet --version).Trim()
    osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    assemblyFileVersion = $executable.VersionInfo.FileVersion
    assemblyProductVersion = $executable.VersionInfo.ProductVersion
    vulnerablePackageFindings = 0
    deprecatedPackageFindings = 0
    requiredFiles = $requiredFiles
    payloadFileCount = $payloadFiles.Count
    payloadTotalBytes = [long] (($payloadFiles | Measure-Object sizeBytes -Sum).Sum)
    payloadFiles = $payloadFiles
}
$manifestPath = Join-Path $packageRoot 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 -LiteralPath $manifestPath
$manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash

Compress-Archive -Path $packageRoot -DestinationPath $archivePath -CompressionLevel Optimal
$archiveSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash
Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot

$extractedPackageRoot = Join-Path $extractRoot $packageName
$extractedManifestPath = Join-Path $extractedPackageRoot 'release-manifest.json'
$extractedManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $extractedManifestPath).Hash
if ($extractedManifestSha256 -ne $manifestSha256) {
    throw 'Extracted release manifest hash does not match the packaged manifest.'
}

$extractedPrefixLength = $extractedPackageRoot.TrimEnd('\').Length + 1
$extractedFiles = @(
    Get-ChildItem -LiteralPath $extractedPackageRoot -Recurse -File |
        Where-Object { $_.FullName -ne $extractedManifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject] [ordered]@{
                path = $_.FullName.Substring($extractedPrefixLength).Replace('\', '/')
                sizeBytes = $_.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            }
        })

$payloadByPath = @{}
foreach ($file in $payloadFiles) {
    $payloadByPath[$file.path] = $file
}
$extractedByPath = @{}
foreach ($file in $extractedFiles) {
    $extractedByPath[$file.path] = $file
}
$archiveDifferences = @()
foreach ($path in @($payloadByPath.Keys + $extractedByPath.Keys | Sort-Object -Unique)) {
    $sourceFile = $payloadByPath[$path]
    $extractedFile = $extractedByPath[$path]
    if ($null -eq $sourceFile -or
        $null -eq $extractedFile -or
        [long] $sourceFile.sizeBytes -ne [long] $extractedFile.sizeBytes -or
        $sourceFile.sha256 -ne $extractedFile.sha256) {
        $archiveDifferences += $path
    }
}
if ($archiveDifferences.Count -ne 0) {
    throw "Extracted archive payload differs: $($archiveDifferences -join ', ')"
}

$verification = [ordered]@{
    schemaVersion = '1.0'
    passed = $true
    applicationVersion = $productVersion
    releaseTag = $releaseTag
    gitCommit = $sourceCommit
    gitWorkingTree = 'clean'
    runtimeIdentifier = $runtimeIdentifier
    publishKind = 'self-contained'
    runtimeBundled = $true
    bundledNetCoreVersion = $netCoreVersion
    bundledWindowsDesktopVersion = $windowsDesktopVersion
    buildExitCode = 0
    testExitCode = 0
    vulnerablePackageFindings = 0
    deprecatedPackageFindings = 0
    payloadFileCount = $payloadFiles.Count
    archivePayloadDifferenceCount = 0
    archiveName = [System.IO.Path]::GetFileName($archivePath)
    archiveSha256 = $archiveSha256
    manifestSha256 = $manifestSha256
    extractedManifestSha256 = $extractedManifestSha256
}
$verificationPath = Join-Path $artifactRoot 'release-verification.json'
$verification | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 -LiteralPath $verificationPath
@(
    "SHA256 ($([System.IO.Path]::GetFileName($archivePath))) = $archiveSha256",
    "SHA256 (release-manifest.json) = $manifestSha256"
) | Set-Content -Encoding ascii -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt')

Write-Host "Release candidate archive: $archivePath"
Write-Host "Release candidate SHA-256: $archiveSha256"
Write-Host "Manifest SHA-256: $manifestSha256"
Write-Host "Bundled runtimes: Microsoft.NETCore.App $netCoreVersion; Microsoft.WindowsDesktop.App $windowsDesktopVersion"
Write-Host "Payload files: $($payloadFiles.Count)"
Write-Host "Archive extraction differences: 0"
