param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = "$PSScriptRoot\..\artifacts\smoke-current-build",

    [Parameter(Mandatory = $false)]
    [ValidateNotNullOrEmpty()]
    [int[]]$DpiScales = @(100, 125, 150),

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\OpenVisionLab.MachineStudio\OpenVisionLab.MachineStudio.csproj'
$sampleProject = Join-Path $repoRoot 'samples\VisionInspectionCell\VisionInspectionCell.ovmachine'
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not $SkipBuild) {
    dotnet build $project -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'Current-source Debug build failed.' }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Add-Type -AssemblyName System.Windows.Forms
$screens = @([System.Windows.Forms.Screen]::AllScreens)
$testMonitor = $screens |
    Sort-Object { $_.Bounds.Left }, { $_.Bounds.Top }, { $_.WorkingArea.Width * $_.WorkingArea.Height } |
    Select-Object -First 1
$monitorBounds = $testMonitor.Bounds
$workArea = $testMonitor.WorkingArea

$sizeCases = @(
    [pscustomobject]@{ Name = 'compact'; Size = '1280x760' },
    [pscustomobject]@{ Name = 'wide'; Size = '1920x1040' },
    [pscustomobject]@{ Name = 'minimum'; Size = '1280x720' },
    [pscustomobject]@{ Name = 'reference'; Size = '1920x1080' }
)
$results = [System.Collections.Generic.List[object]]::new()
$skippedCases = [System.Collections.Generic.List[object]]::new()

function Capture-Smoke {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Size,

        [Parameter(Mandatory = $true)]
        [int]$DpiScale
    )

    if ($DpiScale -lt 100 -or $DpiScale -gt 200) {
        throw "Unsupported DPI scale $DpiScale. Expected 100 through 200."
    }

    $baseName = 'machine_studio_{0}_{1}_dpi{2}' -f $Name, $Size, $DpiScale
    $screenshotPath = Join-Path $outputRoot ($baseName + '.png')
    $reportPath = Join-Path $outputRoot ($baseName + '.layout.json')

    dotnet run --project $project -c Debug --no-build -- --smoke-project $sampleProject --smoke-select x --smoke-size $Size --smoke-dpi $DpiScale --smoke-layout-report $reportPath --smoke-screenshot $screenshotPath
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke layout validation failed for $Size at $DpiScale percent."
    }

    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if (-not $report.isValid) {
        throw "Layout report is invalid for $Size at $DpiScale percent."
    }

    $results.Add([pscustomobject]@{
        name = $Name
        requestedSize = $Size
        requestedScalePercent = $DpiScale
        observedDpiX = $report.observedDpiX
        observedDpiY = $report.observedDpiY
        pixelWidth = $report.pixelWidth
        pixelHeight = $report.pixelHeight
        centerWidth = $report.regions.centerWidth
        textClipIssueCount = @($report.textClipIssues).Count
        visibleHorizontalScrollBarCount = @($report.visibleHorizontalScrollBars).Count
        screenshot = $screenshotPath
        report = $reportPath
    })

    Write-Host "Validated $Size at $DpiScale percent: $screenshotPath"
}

foreach ($dpiScale in $DpiScales) {
    foreach ($sizeCase in $sizeCases) {
        $sizeParts = $sizeCase.Size.Split('x')
        $targetPixelWidth = [int][Math]::Round([int]$sizeParts[0] * $dpiScale / 100.0)
        $targetPixelHeight = [int][Math]::Round([int]$sizeParts[1] * $dpiScale / 100.0)
        if ($targetPixelWidth -gt $monitorBounds.Width -or $targetPixelHeight -gt $monitorBounds.Height) {
            $skippedBaseName = 'machine_studio_{0}_{1}_dpi{2}' -f $sizeCase.Name, $sizeCase.Size, $dpiScale
            $staleScreenshotPath = Join-Path $outputRoot ($skippedBaseName + '.png')
            $staleReportPath = Join-Path $outputRoot ($skippedBaseName + '.layout.json')
            Remove-Item -LiteralPath $staleScreenshotPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $staleReportPath -Force -ErrorAction SilentlyContinue
            $skippedCases.Add([pscustomobject]@{
                name = $sizeCase.Name
                requestedSize = $sizeCase.Size
                requestedScalePercent = $dpiScale
                requiredPixelSize = ('{0}x{1}' -f $targetPixelWidth, $targetPixelHeight)
                availableMonitorBounds = ('{0}x{1}' -f $monitorBounds.Width, $monitorBounds.Height)
                reason = 'The current monitor cannot host the requested physical window size.'
            })
            Write-Warning "Skipped $($sizeCase.Size) at $dpiScale percent: requires $targetPixelWidth x $targetPixelHeight pixels; current monitor bounds are $($monitorBounds.Width) x $($monitorBounds.Height)."
            continue
        }

        Capture-Smoke -Name $sizeCase.Name -Size $sizeCase.Size -DpiScale $dpiScale
    }
}

$legacyCompact = Join-Path $outputRoot 'machine_studio_compact.png'
$legacyWide = Join-Path $outputRoot 'machine_studio_wide.png'
$dpi100Compact = Join-Path $outputRoot 'machine_studio_compact_1280x760_dpi100.png'
$dpi100Wide = Join-Path $outputRoot 'machine_studio_wide_1920x1040_dpi100.png'
if (Test-Path -LiteralPath $dpi100Compact) {
    Copy-Item -LiteralPath $dpi100Compact -Destination $legacyCompact -Force
}
if (Test-Path -LiteralPath $dpi100Wide) {
    Copy-Item -LiteralPath $dpi100Wide -Destination $legacyWide -Force
}

$summaryPath = Join-Path $outputRoot 'layout-validation-summary.json'
[pscustomobject]@{
    schema = '1.0'
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    caseCount = $results.Count
    skippedCaseCount = $skippedCases.Count
    allExecutedCasesValid = $true
    fullMatrixExecuted = ($skippedCases.Count -eq 0)
    currentMonitorBounds = ('{0}x{1}' -f $monitorBounds.Width, $monitorBounds.Height)
    currentMonitorWorkArea = ('{0}x{1}' -f $workArea.Width, $workArea.Height)
    testMonitor = [pscustomobject]@{
        deviceName = $testMonitor.DeviceName
        isPrimary = $testMonitor.Primary
        bounds = ('{0},{1},{2},{3}' -f $testMonitor.Bounds.Left, $testMonitor.Bounds.Top, $testMonitor.Bounds.Width, $testMonitor.Bounds.Height)
        workingArea = ('{0},{1},{2},{3}' -f $workArea.Left, $workArea.Top, $workArea.Width, $workArea.Height)
    }
    cases = $results
    skippedCases = $skippedCases
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8

Write-Host "Smoke layout matrix complete: $($results.Count)/$($results.Count)."
if ($skippedCases.Count -gt 0) {
    Write-Warning "$($skippedCases.Count) oversized case(s) require a larger monitor or VM."
}
Write-Host "Summary: $summaryPath"
