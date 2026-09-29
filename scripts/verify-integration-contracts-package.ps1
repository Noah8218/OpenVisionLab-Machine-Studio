[CmdletBinding()]
param([string]$ReportPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$feed = Join-Path $PSScriptRoot '..\third_party\OpenVisionLabIntegrationContracts'
$sourceCommit = 'f4743f3307d20a963b2197f2019713320b9859b9'
$packages = @(
    @{ Id = 'OpenVisionLab.Integration.Contracts'; Version = '0.2.0-alpha.4'; Protocol = 'docs/PROTOCOL.md' },
    @{ Id = 'OpenVisionLab.Integration.Transport.Tcp'; Version = '0.1.0-alpha.4'; Protocol = 'docs/TCP_TRANSPORT.md' }
)
$results = foreach ($expected in $packages) {
    $package = Join-Path $feed "$($expected.Id).$($expected.Version).nupkg"
    $expectedHash = ([regex]::Match((Get-Content -LiteralPath "$package.sha256" -Raw), '(?i)\b[A-F0-9]{64}\b')).Value
    $actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    if ([string]::IsNullOrWhiteSpace($expectedHash) -or $actualHash -ne $expectedHash) {
        throw "Package SHA-256 mismatch: $package"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $package).Path)
    try {
        $required = @("$($expected.Id).nuspec", 'LICENSE', 'NOTICE', 'README.md', $expected.Protocol, "lib/net8.0/$($expected.Id).dll")
        if ($expected.Id -eq 'OpenVisionLab.Integration.Contracts') {
            $required += @('fixtures/v1/valid/handoff.json', 'fixtures/v1/valid/acknowledgement.json', 'fixtures/v1/valid/result.json')
        }
        foreach ($name in $required) {
            if ($null -eq $archive.GetEntry($name)) { throw "Package is missing $name in $package" }
        }
        $reader = [System.IO.StreamReader]::new($archive.GetEntry("$($expected.Id).nuspec").Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.DocumentElement.SelectSingleNode("*[local-name()='metadata']")
        if ([string]$metadata.id -ne $expected.Id -or [string]$metadata.version -ne $expected.Version -or
            [string]$metadata.repository.commit -ne $sourceCommit -or [string]$metadata.license.InnerText -ne 'MIT') {
            throw "Package metadata mismatch: $package"
        }
    }
    finally { $archive.Dispose() }
    "$($expected.Id)|pass=True|version=$($expected.Version)|sourceCommit=$sourceCommit|sha256=$actualHash|target=net8.0"
}
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ReportPath))) | Out-Null
    Set-Content -LiteralPath $ReportPath -Value $results -Encoding utf8
}
$results
