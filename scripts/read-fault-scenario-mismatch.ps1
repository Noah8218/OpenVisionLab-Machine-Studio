param(
    [Parameter(Mandatory = $true)]
    [string] $MismatchReportPath
)

$errorActionPreference = "Continue"

$script:MismatchCodeDescriptions = @{
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

if (-not (Test-Path -LiteralPath $MismatchReportPath))
{
    Write-Error "Mismatch report file does not exist: $MismatchReportPath"
    exit 2
}

$mismatchPayloadText = Get-Content -Raw -LiteralPath $MismatchReportPath
$mismatchPayload = $mismatchPayloadText | ConvertFrom-Json

if (-not $mismatchPayload)
{
    Write-Error "Mismatch report is empty or not parseable: $MismatchReportPath"
    exit 2
}

if ($mismatchPayload.SchemaVersion -ne 1)
{
    Write-Error "Unsupported mismatch schema version: $($mismatchPayload.SchemaVersion)"
    exit 2
}

if ($mismatchPayload.Status -ne "Mismatch")
{
    if ($mismatchPayload.MismatchCount -eq 0)
    {
        Write-Host "No mismatches found."
        exit 0
    }

    Write-Error "Unexpected mismatch report status: $($mismatchPayload.Status)"
    exit 2
}

if ($null -eq $mismatchPayload.MismatchCount -or $mismatchPayload.MismatchCount -lt 0)
{
    Write-Error "MismatchCount must be a non-negative integer."
    exit 2
}

$mismatches = @($mismatchPayload.Mismatches)
if (($mismatchPayload.MismatchCount -eq 0) -or ($mismatchPayload.MismatchCount -eq $mismatches.Count))
{
    # okay
}
else
{
    Write-Error "MismatchCount mismatch: payload declares $($mismatchPayload.MismatchCount) but found $($mismatches.Count) entries."
    exit 2
}

if ($mismatches.Count -eq 0)
{
    Write-Host "No mismatches found."
    exit 0
}

$codes = @()
if ($mismatchPayload.MismatchCodes -ne $null -and $mismatchPayload.MismatchCodes.Count -gt 0)
{
    $codes = @($mismatchPayload.MismatchCodes)
}
else
{
    $codes = @($mismatches | ForEach-Object { $_.Code })
}

$invalidCodes = @($codes | ForEach-Object { [string] $_ } | Where-Object { -not $script:MismatchCodeDescriptions.ContainsKey($_) } | Sort-Object -Unique)
if ($invalidCodes.Count -gt 0)
{
    Write-Error "Unknown mismatch code(s): $($invalidCodes -join ', ')"
    exit 2
}

Write-Host "Mismatch report parsed: $MismatchReportPath"
Write-Host ("Mismatch count: {0}" -f $codes.Count)

$codeSummary = $codes | Group-Object | Sort-Object Count -Descending
foreach ($entry in $codeSummary)
{
    Write-Host ("{0}: {1}" -f $entry.Name, $entry.Count)
}

exit 2
