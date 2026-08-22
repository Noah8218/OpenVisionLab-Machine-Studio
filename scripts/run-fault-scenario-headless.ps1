param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectPath,

    [Parameter(Mandatory = $true)]
    [string] $ScenarioPath,

    [string] $ReportPath,

    [string] $BaselineReportPath,

    [string] $MismatchReportPath,

    [switch] $CreateBaseline,

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release"
)

$errorActionPreference = "Stop"

$projectFile = Join-Path $PSScriptRoot "..\src\OpenVisionLab.MachineStudio\OpenVisionLab.MachineStudio.csproj"

if (-not (Test-Path -LiteralPath $ProjectPath))
{
    throw "Project path does not exist: $ProjectPath"
}

if (-not (Test-Path -LiteralPath $ScenarioPath))
{
    throw "Scenario path does not exist: $ScenarioPath"
}

if (-not [string]::IsNullOrWhiteSpace($BaselineReportPath) -and [string]::IsNullOrWhiteSpace($ReportPath))
{
    throw "Baseline comparison requires --fault-report output to be set."
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedScenarioPath = (Resolve-Path -LiteralPath $ScenarioPath).Path
$resolvedReportPath = [string]::Empty
$resolvedBaselineReportPath = [string]::Empty
$resolvedMismatchReportPath = [string]::Empty
$script:MismatchCodeDescriptions = [ordered]@{
    "MISMATCH_TOP_LEVEL_SUCCESS"      = "Top-level scenario success flag mismatch."
    "MISMATCH_REPLAY_RESULT_PRESENCE" = "Replay result was missing in one report."
    "MISMATCH_SCENARIO_ID"           = "Scenario id mismatch."
    "MISMATCH_SCENARIO_NAME"         = "Scenario name mismatch."
    "MISMATCH_FAILURE_REASON"         = "Failure reason mismatch."
    "MISMATCH_PLANNED_TICKS"         = "Planned tick count mismatch."
    "MISMATCH_EXECUTED_TICKS"        = "Executed tick count mismatch."
    "MISMATCH_PLANNED_ACTIONS"       = "Planned action count mismatch."
    "MISMATCH_FINAL_SNAPSHOT_HASH"   = "Final snapshot canonical hash mismatch."
    "MISMATCH_COMMAND_RESULTS_HASH"   = "Command-results canonical hash mismatch."
    "MISMATCH_SNAPSHOT_HISTORY_HASH"  = "Snapshot-history canonical hash mismatch."
    "MISMATCH_EVENT_HISTORY_HASH"     = "Event-history canonical hash mismatch."
    "MISMATCH_STRING_ARRAY_LENGTH"    = "String array length mismatch."
    "MISMATCH_STRING_ARRAY_ORDER"     = "String array order or value mismatch."
}

$argumentList = @(
    "--fault-project", $resolvedProjectPath,
    "--fault-scenario", $resolvedScenarioPath
)

if (-not [string]::IsNullOrWhiteSpace($ReportPath))
{
    $resolvedReportPath = [System.IO.Path]::GetFullPath($ReportPath)
    $resolvedReportDirectory = [System.IO.Path]::GetDirectoryName($resolvedReportPath)
    if (-not [string]::IsNullOrWhiteSpace($resolvedReportDirectory))
    {
        [System.IO.Directory]::CreateDirectory($resolvedReportDirectory) | Out-Null
    }

    $argumentList += "--fault-report"
    $argumentList += $resolvedReportPath
}

if (-not [string]::IsNullOrWhiteSpace($BaselineReportPath))
{
    $resolvedBaselineReportPath = [System.IO.Path]::GetFullPath($BaselineReportPath)
}

if (-not [string]::IsNullOrWhiteSpace($MismatchReportPath))
{
    $resolvedMismatchReportPath = [System.IO.Path]::GetFullPath($MismatchReportPath)
    $resolvedMismatchReportDirectory = [System.IO.Path]::GetDirectoryName($resolvedMismatchReportPath)
    if (-not [string]::IsNullOrWhiteSpace($resolvedMismatchReportDirectory))
    {
        [System.IO.Directory]::CreateDirectory($resolvedMismatchReportDirectory) | Out-Null
    }
}

if (-not [string]::IsNullOrWhiteSpace($BaselineReportPath) -and -not $CreateBaseline)
{
    if (-not (Test-Path -LiteralPath $resolvedBaselineReportPath))
    {
        throw "Baseline report path does not exist: $resolvedBaselineReportPath"
    }
}

function ConvertTo-StableObject
{
    param([object] $InputObject)

    if ($null -eq $InputObject)
    {
        return $null
    }

    if ($InputObject -is [string] -or $InputObject -is [bool] -or $InputObject -is [int] -or
        $InputObject -is [long] -or $InputObject -is [double] -or $InputObject -is [datetime] -or
        $InputObject -is [datetimeoffset] -or $InputObject -is [timespan])
    {
        return $InputObject
    }

    if ($InputObject -is [System.Collections.IDictionary])
    {
        $orderedDictionary = [ordered]@{}
        foreach ($entry in @($InputObject.GetEnumerator() |
            Sort-Object -Property {
                if ($_.PSObject.Properties["Name"]) { $_.Name } else { $_.Key }
            }))
        {
            $key = if ($entry.PSObject.Properties["Name"]) { $entry.Name } else { $entry.Key }
            $orderedDictionary[$key] = ConvertTo-StableObject -InputObject $entry.Value
        }

        return $orderedDictionary
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and $InputObject -isnot [string])
    {
        $items = @()
        foreach ($item in $InputObject)
        {
            $items += ConvertTo-StableObject -InputObject $item
        }

        return $items
    }

    if ($InputObject.PSObject -and $InputObject.PSObject.Properties)
    {
        $orderedObject = [ordered]@{}
        foreach ($property in $InputObject.PSObject.Properties |
            Where-Object { $_.MemberType -eq "NoteProperty" } |
            Sort-Object Name)
        {
            $orderedObject[$property.Name] = ConvertTo-StableObject -InputObject $property.Value
        }

        return $orderedObject
    }

    return $InputObject
}

function ConvertTo-CanonicalJson
{
    param([Parameter(Mandatory = $true)] [object] $InputObject)

    $stableObject = ConvertTo-StableObject -InputObject $InputObject
    return $stableObject | ConvertTo-Json -Depth 100 -Compress
}

function Get-Sha256Hash
{
    param([Parameter(Mandatory = $true)] [string] $Text)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        $hashBytes = $sha256.ComputeHash($bytes)
    }
    finally
    {
        $sha256.Dispose()
    }

    return [BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
}

function New-MismatchReport
{
    param(
        [Parameter(Mandatory = $true)] [string] $FieldPath,
        [Parameter(Mandatory = $true)] [string] $Code,
        [string] $Reason = "value",
        [object] $Actual,
        [object] $Baseline
    )

    return [pscustomobject]@{
        FieldPath = $FieldPath
        Code      = $Code
        Reason    = $Reason
        Actual    = $Actual
        Baseline  = $Baseline
    }
}

function Assert-StringArrayEqual
{
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [object] $Actual,
        [object] $Baseline
    )

    $actualList = @($Actual)
    $baselineList = @($Baseline)

    if ($actualList.Count -ne $baselineList.Count)
    {
        return New-MismatchReport -FieldPath $Path -Code "MISMATCH_STRING_ARRAY_LENGTH" -Reason "count" -Actual $actualList -Baseline $baselineList
    }

    for ($index = 0; $index -lt $actualList.Count; $index++)
    {
        if ($actualList[$index] -ne $baselineList[$index])
        {
            return New-MismatchReport -FieldPath $Path -Code "MISMATCH_STRING_ARRAY_ORDER" -Reason "sequence order or value" -Actual $actualList -Baseline $baselineList
        }
    }

    return $null
}

function Get-JsonObjectHash
{
    param([Parameter(Mandatory = $true)] [object] $InputObject)

    $canonical = ConvertTo-CanonicalJson -InputObject $InputObject
    return Get-Sha256Hash -Text $canonical
}

function Write-MismatchReport
{
    param(
        [Parameter(Mandatory = $true)] [string] $ReportPath,
        [Parameter(Mandatory = $true)] [object] $MismatchPayload
    )

    $payloadText = $MismatchPayload | ConvertTo-Json -Depth 100
    Set-Content -Path $ReportPath -Value $payloadText -Encoding UTF8
}

function Get-MismatchCode
{
    param(
        [Parameter(Mandatory = $true)] [string] $Code
    )

    return $script:MismatchCodeDescriptions[$Code]
}

function Get-MismatchCodes
{
    return $script:MismatchCodeDescriptions.Keys
}

function Assert-MismatchCode
{
    param([Parameter(Mandatory = $true)] [string] $Code)

    if (-not ($script:MismatchCodeDescriptions.Keys -contains $Code))
    {
        throw "Unknown mismatch code: $Code"
    }
}

function Add-Mismatch
{
    param(
        [Parameter(Mandatory = $true)] [object] $Collection,
        [Parameter(Mandatory = $true)] [string] $FieldPath,
        [Parameter(Mandatory = $true)] [string] $Code,
        [object] $Actual,
        [object] $Baseline,
        [string] $Reason = "value"
    )

    Assert-MismatchCode -Code $Code
    if ($null -eq $Collection)
    {
        throw "Mismatch collection is null."
    }
    if (-not ($Collection.PSObject.Methods.Name -contains "Add"))
    {
        throw "Mismatch collection is missing an Add method."
    }

    $null = $Collection.Add(
        (New-MismatchReport -FieldPath $FieldPath -Code $Code -Actual $Actual -Baseline $Baseline -Reason $Reason)
    )
}

function New-MismatchPayload
{
    param(
        [Parameter(Mandatory = $true)] [string] $BaselineReportPath,
        [Parameter(Mandatory = $true)] [string] $ReportPath,
        [Parameter(Mandatory = $true)] [int] $MismatchCount,
        [Parameter(Mandatory = $true)] [object[]] $Mismatches,
        [string] $MismatchReportPath
    )

    return [ordered]@{
        SchemaVersion      = 1
        ComparedAtUtc      = (Get-Date).ToUniversalTime().ToString("O")
        BaselineReportPath = $BaselineReportPath
        ReportPath         = $ReportPath
        MismatchReportPath = $MismatchReportPath
        MismatchCount      = $MismatchCount
        Status             = "Mismatch"
        MismatchCodes      = @($Mismatches | ForEach-Object { $_.Code } | Sort-Object -Unique)
        Mismatches         = $Mismatches
    }
}

Write-Host "Running: dotnet run --project $projectFile --configuration $Configuration -- $($argumentList -join ' ')"
& dotnet run --project $projectFile --configuration $Configuration -- @argumentList
if ($LASTEXITCODE -ne 0)
{
    Write-Error "Fault-scenario run failed. ExitCode=$LASTEXITCODE"
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($resolvedReportPath))
{
    exit 0
}

if (Test-Path -LiteralPath $resolvedReportPath)
{
    $reportText = Get-Content -Raw -LiteralPath $resolvedReportPath
    $report = $reportText | ConvertFrom-Json
    Write-Host "Fault run completed. PlannedTicks=$($report.replayResult.plannedTicks), ExecutedTicks=$($report.replayResult.executedTicks), CommandResults=$($report.replayResult.commandResults.Count), SnapshotHistory=$($report.replayResult.snapshotHistory.Count), EventHistory=$($report.replayResult.eventHistory.Count)"

    if ($CreateBaseline -and -not [string]::IsNullOrWhiteSpace($resolvedBaselineReportPath))
    {
        $resolvedBaselineReportDirectory = [System.IO.Path]::GetDirectoryName($resolvedBaselineReportPath)
        if (-not [string]::IsNullOrWhiteSpace($resolvedBaselineReportDirectory))
        {
            [System.IO.Directory]::CreateDirectory($resolvedBaselineReportDirectory) | Out-Null
        }
        Copy-Item -Path $resolvedReportPath -Destination $resolvedBaselineReportPath -Force
        Write-Host "Baseline report created/updated: $resolvedBaselineReportPath"
        exit 0
    }
}
else
{
    throw "Expected report file was not created: $resolvedReportPath"
}

if (-not [string]::IsNullOrWhiteSpace($BaselineReportPath))
{
    $baseline = Get-Content -Raw -LiteralPath $resolvedBaselineReportPath | ConvertFrom-Json
    $mismatches = [System.Collections.Generic.List[object]]::new()

    if ($report.IsSuccess -ne $baseline.IsSuccess)
    {
        Add-Mismatch `
            -Collection $mismatches `
            -FieldPath "IsSuccess" `
            -Code "MISMATCH_TOP_LEVEL_SUCCESS" `
            -Actual $report.IsSuccess `
            -Baseline $baseline.IsSuccess `
            -Reason "top-level"
    }

    $actualReplay = $report.replayResult
    $baselineReplay = $baseline.replayResult
    if ($null -eq $actualReplay -and $null -eq $baselineReplay)
    {
        # No replay block available in either report; nothing to compare below.
    }
    elseif ($null -eq $actualReplay -or $null -eq $baselineReplay)
    {
        Add-Mismatch `
            -Collection $mismatches `
            -FieldPath "ReplayResult" `
            -Code "MISMATCH_REPLAY_RESULT_PRESENCE" `
            -Actual ($actualReplay -ne $null) `
            -Baseline ($baselineReplay -ne $null) `
            -Reason "presence"
    }
    else
    {
        if ($actualReplay.scenarioId -ne $baselineReplay.scenarioId)
        {
            Add-Mismatch `
                -Collection $mismatches `
                -FieldPath "ReplayResult.ScenarioId" `
                -Code "MISMATCH_SCENARIO_ID" `
                -Actual $actualReplay.scenarioId `
                -Baseline $baselineReplay.scenarioId
        }
        if ($actualReplay.scenarioName -ne $baselineReplay.scenarioName)
        {
            Add-Mismatch `
                -Collection $mismatches `
                -FieldPath "ReplayResult.ScenarioName" `
                -Code "MISMATCH_SCENARIO_NAME" `
                -Actual $actualReplay.scenarioName `
                -Baseline $baselineReplay.scenarioName
        }
        if ($actualReplay.failureReason -ne $baselineReplay.failureReason)
        {
            Add-Mismatch `
                -Collection $mismatches `
                -FieldPath "ReplayResult.FailureReason" `
                -Code "MISMATCH_FAILURE_REASON" `
                -Actual $actualReplay.failureReason `
                -Baseline $baselineReplay.failureReason
        }

        $actualValidationErrors = @($actualReplay.validationErrors)
        $baselineValidationErrors = @($baselineReplay.validationErrors)
        $validationMismatch = Assert-StringArrayEqual -Path "ReplayResult.ValidationErrors" -Actual $actualValidationErrors -Baseline $baselineValidationErrors
        if ($validationMismatch)
        {
            Add-Mismatch `
                -Collection $mismatches `
                -FieldPath $validationMismatch.FieldPath `
                -Code $validationMismatch.Code `
                -Actual $validationMismatch.Actual `
                -Baseline $validationMismatch.Baseline `
                -Reason $validationMismatch.Reason
        }

        $reportChecks = @(
            @{ Label = "ReplayResult.PlannedTicks"; Actual = $actualReplay.plannedTicks; Baseline = $baselineReplay.plannedTicks; Code = "MISMATCH_PLANNED_TICKS" },
            @{ Label = "ReplayResult.ExecutedTicks"; Actual = $actualReplay.executedTicks; Baseline = $baselineReplay.executedTicks; Code = "MISMATCH_EXECUTED_TICKS" },
            @{ Label = "ReplayResult.PlannedActions"; Actual = $actualReplay.plannedActions; Baseline = $baselineReplay.plannedActions; Code = "MISMATCH_PLANNED_ACTIONS" },
            @{ Label = "ReplayResult.FinalSnapshot"; Actual = Get-JsonObjectHash -InputObject $actualReplay.finalSnapshot; Baseline = Get-JsonObjectHash -InputObject $baselineReplay.finalSnapshot; Code = "MISMATCH_FINAL_SNAPSHOT_HASH" },
            @{ Label = "ReplayResult.CommandResults"; Actual = Get-JsonObjectHash -InputObject ($actualReplay.commandResults | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty commandId }); Baseline = Get-JsonObjectHash -InputObject ($baselineReplay.commandResults | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty commandId }); Code = "MISMATCH_COMMAND_RESULTS_HASH" },
            @{ Label = "ReplayResult.SnapshotHistory"; Actual = Get-JsonObjectHash -InputObject $actualReplay.snapshotHistory; Baseline = Get-JsonObjectHash -InputObject $baselineReplay.snapshotHistory; Code = "MISMATCH_SNAPSHOT_HISTORY_HASH" },
            @{ Label = "ReplayResult.EventHistory"; Actual = Get-JsonObjectHash -InputObject ($actualReplay.eventHistory | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty commandId }); Baseline = Get-JsonObjectHash -InputObject ($baselineReplay.eventHistory | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty commandId }); Code = "MISMATCH_EVENT_HISTORY_HASH" }
        )

        foreach ($item in $reportChecks)
        {
            if ($item.Actual -ne $item.Baseline)
            {
                Add-Mismatch `
                    -Collection $mismatches `
                    -FieldPath $item.Label `
                    -Code $item.Code `
                    -Actual $item.Actual `
                    -Baseline $item.Baseline
            }
        }
    }

    $invalidMismatchCodes = @($mismatches | ForEach-Object { $_.Code } | Where-Object { -not ($script:MismatchCodeDescriptions.Keys -contains [string] $_) })
    if ($invalidMismatchCodes.Count -gt 0)
    {
        throw "Unknown mismatch code(s): $($invalidMismatchCodes | Sort-Object -Unique)"
    }

    if ($mismatches.Count -gt 0)
    {
        if ([string]::IsNullOrWhiteSpace($resolvedMismatchReportPath))
        {
            $resolvedMismatchReportValue = $null
        }
        else
        {
            $resolvedMismatchReportValue = $resolvedMismatchReportPath
        }
        $payload = New-MismatchPayload `
            -BaselineReportPath $resolvedBaselineReportPath `
            -ReportPath $resolvedReportPath `
            -MismatchReportPath $resolvedMismatchReportValue `
            -MismatchCount $mismatches.Count `
            -Mismatches @($mismatches)

        $payloadText = $payload | ConvertTo-Json -Depth 100
        Write-Host "Fault scenario baseline comparison failed."
        Write-Host $payloadText

        if (-not [string]::IsNullOrWhiteSpace($resolvedMismatchReportPath))
        {
            Write-MismatchReport -ReportPath $resolvedMismatchReportPath -MismatchPayload $payload
            Write-Host "Baseline mismatch report written: $resolvedMismatchReportPath"
        }

        foreach ($item in $mismatches)
        {
            $description = Get-MismatchCode -Code $item.Code
            Write-Host ("[{0}] {1} mismatch: {2} (actual={3}, baseline={4})" -f $item.Code, $item.FieldPath, $description, $item.Actual, $item.Baseline)
        }

        Write-Host ("Mismatch count: {0}" -f $mismatches.Count)
        exit 2
    }

    if (-not [string]::IsNullOrWhiteSpace($resolvedMismatchReportPath) -and (Test-Path -LiteralPath $resolvedMismatchReportPath))
    {
        Remove-Item -LiteralPath $resolvedMismatchReportPath -Force
        Write-Host "Cleared stale mismatch report: $resolvedMismatchReportPath"
    }

    Write-Host "Baseline comparison passed for deterministic replay payload."
}

exit 0
