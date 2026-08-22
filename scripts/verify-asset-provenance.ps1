param(
    [switch]$RequireDistributionApproved
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'ASSET-PROVENANCE.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$errors = [System.Collections.Generic.List[string]]::new()

if ($manifest.schema -ne '1.0') {
    $errors.Add("Unsupported manifest schema '$($manifest.schema)'.")
}

$entriesByPath = @{}
foreach ($entry in @($manifest.assets)) {
    $path = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($path) -or $entriesByPath.ContainsKey($path)) {
        $errors.Add("Asset path is empty or duplicated: '$path'.")
        continue
    }

    $entriesByPath[$path] = $entry
    $fullPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $path))
    if (-not $fullPath.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("Asset path escapes the repository: '$path'.")
        continue
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        $errors.Add("Declared asset is missing: '$path'.")
        continue
    }

    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$entry.sha256).ToLowerInvariant()) {
        $errors.Add("SHA-256 mismatch: '$path'.")
    }
    foreach ($field in 'mediaType','purpose','originSummary','firstRecordedCommit','vendorReferenceStatus','distributionStatus','reviewNote') {
        if ([string]::IsNullOrWhiteSpace([string]$entry.$field)) {
            $errors.Add("Asset '$path' is missing '$field'.")
        }
    }
    if ($RequireDistributionApproved -and $entry.distributionStatus -ne 'approved') {
        $errors.Add("Asset '$path' is not approved for distribution: $($entry.distributionStatus).")
    }
}

$extensions = @($manifest.extensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
foreach ($root in @($manifest.inventoryRoots)) {
    $inventoryRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot ([string]$root)))
    if (-not (Test-Path -LiteralPath $inventoryRoot -PathType Container)) {
        $errors.Add("Inventory root is missing: '$root'.")
        continue
    }

    foreach ($file in Get-ChildItem -LiteralPath $inventoryRoot -Recurse -File) {
        if ($extensions -notcontains $file.Extension.ToLowerInvariant()) {
            continue
        }
        $relativePath = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
        if (-not $entriesByPath.ContainsKey($relativePath)) {
            $errors.Add("Asset is not declared: '$relativePath'.")
        }
    }
}

if ($errors.Count -gt 0) {
    throw "Asset provenance check failed:`n - $($errors -join "`n - ")"
}

$mode = if ($RequireDistributionApproved) { 'release' } else { 'inventory' }
Write-Output "Asset provenance $mode check passed: $($entriesByPath.Count) assets."
