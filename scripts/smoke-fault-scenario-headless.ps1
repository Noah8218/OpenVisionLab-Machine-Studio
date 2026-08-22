param(
    [string] $ProjectPath = ".\samples\AutomaticTransferCell\AutomaticTransferCell.ovmachine",
    [string] $ScenarioPath = ".\samples\AutomaticTransferCell\fault-scenario-headless-smoke.json",
    [string] $ArtifactsDirectory = $env:OPENVISIONLAB_FAULT_SMOKE_ARTIFACTS,
    [switch] $CreateBaselineOnly,
    [switch] $InjectMismatchBeforeCompare,
    [string] $InjectedMismatchScenarioId = "INJECTED-SMOKE-MISMATCH"
)

$ErrorActionPreference = "Stop"

$runScript = Join-Path $PSScriptRoot "run-fault-scenario-headless.ps1"
$parserScript = Join-Path $PSScriptRoot "read-fault-scenario-mismatch.ps1"

if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory))
{
    $ArtifactsDirectory = "D:\OpenVisionLab-TestData\OpenVisionLab-Machine-Studio\artifacts\fault-smoke"
}
$resolvedArtifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)

try
{
    $resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
}
catch
{
    throw "ProjectPath does not exist: $ProjectPath"
}

try
{
    $resolvedScenarioPath = (Resolve-Path -LiteralPath $ScenarioPath).Path
}
catch
{
    throw "ScenarioPath does not exist: $ScenarioPath"
}

New-Item -ItemType Directory -Force -Path $resolvedArtifactsDirectory | Out-Null

$baselineReportPath = Join-Path $resolvedArtifactsDirectory "baseline-fault-scenario-report.json"
$tamperedBaselineReportPath = Join-Path $resolvedArtifactsDirectory "baseline-fault-scenario-report-mismatch.json"
$reportPath = Join-Path $resolvedArtifactsDirectory "fault-scenario-report.json"
$mismatchReportPath = Join-Path $resolvedArtifactsDirectory "fault-scenario-mismatch.json"
$compareBaselinePath = $baselineReportPath

if (Test-Path -LiteralPath $mismatchReportPath)
{
    Remove-Item -LiteralPath $mismatchReportPath -Force
}

Write-Host "Creating deterministic baseline..."
& $runScript `
    -ProjectPath $resolvedProjectPath `
    -ScenarioPath $resolvedScenarioPath `
    -ReportPath $reportPath `
    -BaselineReportPath $baselineReportPath `
    -CreateBaseline
$baselineExit = $LASTEXITCODE
Write-Host "Baseline exit: $baselineExit"
if ($baselineExit -ne 0)
{
    exit $baselineExit
}

if ($CreateBaselineOnly)
{
    exit 0
}

if ($InjectMismatchBeforeCompare)
{
    if (-not (Test-Path -LiteralPath $baselineReportPath))
    {
        throw "Baseline report not found for mismatch injection: $baselineReportPath"
    }

    Copy-Item -Path $baselineReportPath -Destination $tamperedBaselineReportPath -Force
    $tamperedJson = Get-Content -Raw -LiteralPath $tamperedBaselineReportPath | ConvertFrom-Json
    if ($tamperedJson -and $tamperedJson.replayResult -ne $null)
    {
        $tamperedJson.replayResult.scenarioId = $InjectedMismatchScenarioId
    }
    else
    {
        if ($null -eq $tamperedJson)
        {
            $tamperedJson = [ordered]@{}
        }

        $tamperedReplayResult = [ordered]@{}
        if ($tamperedJson -is [System.Collections.IDictionary])
        {
            $currentReplayResult = $tamperedJson["replayResult"]
            if ($currentReplayResult)
            {
                foreach ($property in $currentReplayResult.PSObject.Properties)
                {
                    $tamperedReplayResult[$property.Name] = $property.Value
                }
            }
        }
        elseif ($tamperedJson.replayResult)
        {
            foreach ($property in $tamperedJson.replayResult.PSObject.Properties)
            {
                $tamperedReplayResult[$property.Name] = $property.Value
            }
        }

        $tamperedReplayResult["scenarioId"] = $InjectedMismatchScenarioId
        if ($tamperedJson -is [System.Collections.IDictionary])
        {
            $tamperedJson["replayResult"] = $tamperedReplayResult
        }
        else
        {
            $tamperedJson | Add-Member -NotePropertyName replayResult -NotePropertyValue $tamperedReplayResult -Force
        }
    }

    $tamperedPayload = ConvertTo-Json -InputObject $tamperedJson -Depth 100
    Set-Content -Path $tamperedBaselineReportPath -Value $tamperedPayload -Encoding UTF8
    $compareBaselinePath = $tamperedBaselineReportPath
    Write-Host "Injected mismatch into baseline for compare: $compareBaselinePath"
}

Write-Host "Running deterministic replay comparison..."
& $runScript `
    -ProjectPath $resolvedProjectPath `
    -ScenarioPath $resolvedScenarioPath `
    -ReportPath $reportPath `
    -BaselineReportPath $compareBaselinePath `
    -MismatchReportPath $mismatchReportPath

$compareExit = $LASTEXITCODE
Write-Host "Compare exit: $compareExit"

if ($compareExit -eq 2 -and (Test-Path -LiteralPath $mismatchReportPath))
{
    & $parserScript -MismatchReportPath $mismatchReportPath
    Write-Host "Parser exit: $LASTEXITCODE"
    exit $LASTEXITCODE
}
if ($compareExit -eq 2)
{
    Write-Error "Mismatch report was expected but not produced."
    exit 2
}

if ($compareExit -ne 2 -and (Test-Path -LiteralPath $mismatchReportPath))
{
    Remove-Item -LiteralPath $mismatchReportPath -Force
}

exit $compareExit
