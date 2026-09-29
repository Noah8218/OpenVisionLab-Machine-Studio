param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectPath = '',

    [Parameter(Mandatory = $false)]
    [string[]]$Resolutions = @('1280x760', '1920x1040'),

    [Parameter(Mandatory = $false)]
    [int[]]$DpiScales = @(100, 125),

    [Parameter(Mandatory = $false)]
    [int]$Runs = 2,

    [Parameter(Mandatory = $false)]
    [int]$RecheckRuns = 2,

    [Parameter(Mandatory = $false)]
    [int]$Samples = 12,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = '',

    [Parameter(Mandatory = $false)]
    [string]$BaselinePath = '',

    [Parameter(Mandatory = $false)]
    [switch]$CreateBaseline,

    [Parameter(Mandatory = $false)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild,

    [Parameter(Mandatory = $false)]
    [switch]$AutoRecheck,

    [Parameter(Mandatory = $false)]
    [ValidateSet('DotnetRun', 'DirectExe')]
    [string]$ExecutionMode = 'DotnetRun',

    [Parameter(Mandatory = $false)]
    [string]$ExecutablePath = '',

    [Parameter(Mandatory = $false)]
    [int]$WarmupRuns = 1,

    [Parameter(Mandatory = $false)]
    [ValidateSet('Normal', 'AboveNormal', 'High')]
    [string]$ProcessPriority = 'High',

    [Parameter(Mandatory = $false)]
    [long]$ProcessorAffinityMask = 0,

    [Parameter(Mandatory = $false)]
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\OpenVisionLab.MachineStudio\OpenVisionLab.MachineStudio.csproj'
$meanRegressionLimitMultiplier = 1.25
$p95RegressionLimitMultiplier = 1.4
$maxJitterRatio = 0.15
$preflightCpuSampleMilliseconds = 1000
$maxCompetingProcessCpuCoreRatio = 0.5

function Get-SmokeResolutionPlan
{
    param(
        [Parameter(Mandatory = $true)] [string[]]$RequestedResolutions,
        [Parameter(Mandatory = $true)] [int[]]$RequestedDpiScales
    )

    Add-Type -AssemblyName System.Windows.Forms
    $screens = @([System.Windows.Forms.Screen]::AllScreens)
    if ($screens.Count -eq 0)
    {
        throw 'Performance smoke requires an available Windows display.'
    }

    $monitor = $screens |
        Sort-Object { $_.Bounds.Left }, { $_.WorkingArea.Width * $_.WorkingArea.Height } |
        Select-Object -First 1
    $bounds = $monitor.Bounds
    $supportedCases = [System.Collections.Generic.List[object]]::new()
    $skippedCases = [System.Collections.Generic.List[object]]::new()

    foreach ($resolution in $RequestedResolutions)
    {
        if ($resolution -notmatch '^(?<width>[1-9]\d*)x(?<height>[1-9]\d*)$')
        {
            throw "Invalid smoke resolution '$resolution'. Expected WIDTHxHEIGHT."
        }

        $logicalWidth = [int]$Matches.width
        $logicalHeight = [int]$Matches.height
        foreach ($dpi in $RequestedDpiScales)
        {
            if ($dpi -lt 100 -or $dpi -gt 200)
            {
                throw "Unsupported DPI scale $dpi. Expected 100 through 200."
            }

            $requiredWidth = [int][Math]::Round($logicalWidth * $dpi / 100.0)
            $requiredHeight = [int][Math]::Round($logicalHeight * $dpi / 100.0)
            if ($requiredWidth -gt $bounds.Width -or $requiredHeight -gt $bounds.Height)
            {
                $skippedCases.Add([pscustomobject]@{
                        resolution = $resolution
                        dpi = $dpi
                        requiredPixelSize = "${requiredWidth}x${requiredHeight}"
                        availableMonitorBounds = "$($bounds.Width)x$($bounds.Height)"
                        reason = 'The selected test monitor cannot host the requested physical window size.'
                    })
                continue
            }

            $supportedCases.Add([pscustomobject]@{ resolution = $resolution; dpi = $dpi })
        }
    }

    if ($supportedCases.Count -eq 0)
    {
        throw 'The requested performance smoke matrix contains no cases that fit the selected monitor.'
    }

    return [pscustomobject]@{
        monitor = [pscustomobject]@{
            deviceName = $monitor.DeviceName
            isPrimary = $monitor.Primary
            bounds = "$($bounds.Left),$($bounds.Top),$($bounds.Width),$($bounds.Height)"
            workingArea = "$($monitor.WorkingArea.Left),$($monitor.WorkingArea.Top),$($monitor.WorkingArea.Width),$($monitor.WorkingArea.Height)"
        }
        supportedCases = @($supportedCases.ToArray())
        skippedCases = @($skippedCases.ToArray())
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectPath))
{
    $ProjectPath = Join-Path $repoRoot 'samples\AutomaticTransferCell\AutomaticTransferCell.ovmachine'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $dDriveOutput = Join-Path 'D:\OpenVisionLab-TestData' 'OpenVisionLab-Machine-Studio\artifacts\smoke-performance-local'
    if (Test-Path -LiteralPath (Split-Path $dDriveOutput -Parent))
    {
        $OutputDirectory = $dDriveOutput
    }
    else
    {
        $OutputDirectory = Join-Path $repoRoot 'artifacts\smoke-performance-local'
    }
}
if ([string]::IsNullOrWhiteSpace($BaselinePath))
{
    $BaselinePath = Join-Path $OutputDirectory 'baseline-smoke-performance.json'
}
if ([string]::IsNullOrWhiteSpace($ExecutablePath))
{
    $ExecutablePath = Join-Path $repoRoot "src\OpenVisionLab.MachineStudio\bin\$Configuration\net8.0-windows\OpenVisionLab.MachineStudio.exe"
}

$ProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$BaselinePath = [System.IO.Path]::GetFullPath($BaselinePath)
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)

if ($ExecutionMode -eq 'DirectExe' -and -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf))
{
    throw "Direct EXE path does not exist: $ExecutablePath. Build the project or pass -ExecutablePath."
}
if (-not (Test-Path -LiteralPath $ProjectPath))
{
    throw "Smoke project path does not exist: $ProjectPath"
}
if ($Runs -lt 1)
{
    throw "Runs must be at least 1."
}
if ($RecheckRuns -lt 1)
{
    throw "RecheckRuns must be at least 1."
}
if ($Samples -lt 1)
{
    throw "Samples must be at least 1."
}
if ($WarmupRuns -lt 0)
{
    throw "WarmupRuns must be 0 or greater."
}
if ($ProcessorAffinityMask -lt 0)
{
    throw "ProcessorAffinityMask must be zero or greater."
}
if ($CreateBaseline -and $Runs -lt 2)
{
    throw "Baseline creation requires at least two measured runs."
}
if ($CreateBaseline -and
    $ExecutionMode -eq 'DirectExe' -and
    $ProcessorAffinityMask -gt 0 -and
    (($ProcessorAffinityMask -band ($ProcessorAffinityMask - 1)) -eq 0))
{
    throw "DirectExe baseline creation cannot pin the multi-threaded WPF process to one logical CPU. Use ProcessorAffinityMask 0 or a validated multi-CPU mask."
}
if (-not $PreflightOnly)
{
    $script:SmokeResolutionPlan = Get-SmokeResolutionPlan -RequestedResolutions $Resolutions -RequestedDpiScales $DpiScales
    foreach ($skippedCase in $script:SmokeResolutionPlan.skippedCases)
    {
        Write-Warning "Skipped $($skippedCase.resolution) at $($skippedCase.dpi)% DPI: requires $($skippedCase.requiredPixelSize) pixels; selected monitor bounds are $($skippedCase.availableMonitorBounds)."
    }
    if ($CreateBaseline -and $script:SmokeResolutionPlan.skippedCases.Count -gt 0)
    {
        throw 'Baseline creation requires the full requested resolution/DPI matrix to fit the selected monitor. Choose a supported matrix explicitly.'
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Get-Mean
{
    param(
        [Parameter(Mandatory = $true)] [double[]]$Values
    )

    if ($Values.Count -eq 0)
    {
        return 0
    }

    return [Math]::Round(($Values | Measure-Object -Average).Average, 3)
}

function Get-Percentile
{
    param(
        [Parameter(Mandatory = $true)] [double[]]$Values,
        [Parameter(Mandatory = $true)] [double]$Percentile
    )

    if ($Values.Count -eq 0)
    {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    $safePercentile = [Math]::Min([Math]::Max($Percentile, 0), 1)
    $index = [int][Math]::Ceiling($safePercentile * $sorted.Count) - 1
    if ($index -lt 0)
    {
        $index = 0
    }

    if ($index -ge $sorted.Count)
    {
        $index = $sorted.Count - 1
    }

    return [Math]::Round($sorted[$index], 3)
}

function Get-MinValue
{
    param(
        [Parameter(Mandatory = $true)] [double[]]$Values
    )

    return ($Values | Measure-Object -Minimum).Minimum
}

function Get-MaxValue
{
    param(
        [Parameter(Mandatory = $true)] [double[]]$Values
    )

    return ($Values | Measure-Object -Maximum).Maximum
}

function Get-SmokeEnvironment
{
    return [ordered]@{
        machineName = $env:COMPUTERNAME
        osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription.Trim()
        osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
        processorCount = [Environment]::ProcessorCount
        dotnetRuntime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        executionMode = $ExecutionMode
        configuration = $Configuration
        processPriority = $ProcessPriority
        processorAffinityMask = $ProcessorAffinityMask
        testMonitorKey = "$($script:SmokeResolutionPlan.monitor.deviceName)|$($script:SmokeResolutionPlan.monitor.bounds)|$($script:SmokeResolutionPlan.monitor.workingArea)"
        requestedMatrixKey = "resolutions=$($Resolutions -join ',');dpi=$($DpiScales -join ',')"
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
}

function Get-PerformanceSmokePreflight
{
    param(
        [Parameter(Mandatory = $false)] [string]$ReportPath = ''
    )

    $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    $processLookup = @{}
    foreach ($process in $processes)
    {
        $processLookup[[int]$process.ProcessId] = $process
    }

    $ignoredProcessIds = @{}
    $ancestorProcessId = [int]$PID
    for ($depth = 0; $depth -lt 32 -and $ancestorProcessId -gt 0; $depth++)
    {
        if (-not $processLookup.ContainsKey($ancestorProcessId) -or $ignoredProcessIds.ContainsKey($ancestorProcessId))
        {
            break
        }

        $ignoredProcessIds[$ancestorProcessId] = $true
        $ancestorProcessId = [int]$processLookup[$ancestorProcessId].ParentProcessId
    }

    $competingProcessesById = @{}

    foreach ($process in $processes)
    {
        if ($ignoredProcessIds.ContainsKey([int]$process.ProcessId))
        {
            continue
        }

        $processName = [string]$process.Name
        $commandLine = [string]$process.CommandLine
        $nameStem = [System.IO.Path]::GetFileNameWithoutExtension($processName)
        $isKnownBuildOrTestProcess = $nameStem -match '^(MSBuild|testhost|vstest\.console)$'
        $isKnownStudioProcess = $nameStem -match '^OpenVisionLab\.(LabelingStudio|MachineStudio)$'
        $isDotnetProcess = $nameStem -eq 'dotnet'
        $isDotnetBuildOrTestProcess = $isDotnetProcess -and $commandLine -match '(?i)(?:^|\s)(?:build|test|run)(?:\s|$)'
        $isDotnetAutomationProcess = $isDotnetProcess -and $commandLine -match '(?i)(?:LabelingApplication\.Tests\.dll|OpenVisionLab\.(LabelingStudio|MachineStudio)\.Tests\.dll)'
        $isKnownAutomationCommand = $isDotnetBuildOrTestProcess -or $isDotnetAutomationProcess

        if ($isKnownBuildOrTestProcess -or $isKnownStudioProcess -or $isKnownAutomationCommand)
        {
            $competingProcessesById[[int]$process.ProcessId] = [pscustomobject]@{
                processId = [int]$process.ProcessId
                parentProcessId = [int]$process.ParentProcessId
                name = $processName
                commandLine = $commandLine
                creationDate = [string]$process.CreationDate
                classifications = @('known-process')
                sampledCpuCoreRatio = $null
            }
        }
    }

    $cpuStartSamples = @{}
    foreach ($runtimeProcess in @(Get-Process -ErrorAction SilentlyContinue))
    {
        $runtimeProcessId = [int]$runtimeProcess.Id
        if ($runtimeProcessId -le 0 -or $ignoredProcessIds.ContainsKey($runtimeProcessId))
        {
            continue
        }

        try
        {
            $cpuStartSamples[$runtimeProcessId] = [pscustomobject]@{
                startTimeUtcTicks = $runtimeProcess.StartTime.ToUniversalTime().Ticks
                totalProcessorMilliseconds = $runtimeProcess.TotalProcessorTime.TotalMilliseconds
            }
        }
        catch
        {
            continue
        }
    }

    $cpuSampleTimer = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds $preflightCpuSampleMilliseconds
    $cpuSampleTimer.Stop()
    $cpuSampleElapsedMilliseconds = [Math]::Max($cpuSampleTimer.Elapsed.TotalMilliseconds, 1)

    foreach ($runtimeProcess in @(Get-Process -ErrorAction SilentlyContinue))
    {
        $runtimeProcessId = [int]$runtimeProcess.Id
        if (-not $cpuStartSamples.ContainsKey($runtimeProcessId) -or -not $processLookup.ContainsKey($runtimeProcessId))
        {
            continue
        }

        try
        {
            $startSample = $cpuStartSamples[$runtimeProcessId]
            if ($runtimeProcess.StartTime.ToUniversalTime().Ticks -ne $startSample.startTimeUtcTicks)
            {
                continue
            }

            $cpuDeltaMilliseconds = $runtimeProcess.TotalProcessorTime.TotalMilliseconds - $startSample.totalProcessorMilliseconds
            $sampledCpuCoreRatio = [Math]::Round([Math]::Max($cpuDeltaMilliseconds, 0) / $cpuSampleElapsedMilliseconds, 3)
        }
        catch
        {
            continue
        }

        if ($sampledCpuCoreRatio -lt $maxCompetingProcessCpuCoreRatio)
        {
            continue
        }

        if ($competingProcessesById.ContainsKey($runtimeProcessId))
        {
            $competingProcess = $competingProcessesById[$runtimeProcessId]
            $competingProcess.classifications = @($competingProcess.classifications) + 'sustained-cpu'
            $competingProcess.sampledCpuCoreRatio = $sampledCpuCoreRatio
            continue
        }

        $process = $processLookup[$runtimeProcessId]
        $competingProcessesById[$runtimeProcessId] = [pscustomobject]@{
            processId = $runtimeProcessId
            parentProcessId = [int]$process.ParentProcessId
            name = [string]$process.Name
            commandLine = [string]$process.CommandLine
            creationDate = [string]$process.CreationDate
            classifications = @('sustained-cpu')
            sampledCpuCoreRatio = $sampledCpuCoreRatio
        }
    }

    $competingProcesses = @($competingProcessesById.Values | Sort-Object processId)

    $operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $lastBootUpTime = $null
    if ($operatingSystem.LastBootUpTime)
    {
        if ($operatingSystem.LastBootUpTime -is [DateTime])
        {
            $lastBootUpTime = ([DateTime]$operatingSystem.LastBootUpTime).ToUniversalTime().ToString('O')
        }
        else
        {
            $lastBootUpTime = ([Management.ManagementDateTimeConverter]::ToDateTime([string]$operatingSystem.LastBootUpTime)).ToUniversalTime().ToString('O')
        }
    }

    $status = if ($competingProcesses.Count -eq 0) { 'ready' } else { 'blocked' }
    $blockers = @($competingProcesses | ForEach-Object {
            $classification = @($_.classifications) -join ','
            $cpuRatio = if ($null -eq $_.sampledCpuCoreRatio) { '' } else { " CpuCoreRatio=$($_.sampledCpuCoreRatio)" }
            "PID=$($_.processId) Classifications=$classification$cpuRatio Name=$($_.name) CommandLine=$($_.commandLine)"
        })
    $report = [ordered]@{
        schema = '1.0'
        artifactType = 'performance-smoke-preflight'
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = $status
        executionMode = $ExecutionMode
        configuration = $Configuration
        projectPath = $ProjectPath
        executablePath = $ExecutablePath
        baselinePath = $BaselinePath
        lastBootUpTimeUtc = $lastBootUpTime
        cpuLoadPolicy = [ordered]@{
            sampleDurationMilliseconds = $preflightCpuSampleMilliseconds
            actualSampleDurationMilliseconds = [Math]::Round($cpuSampleElapsedMilliseconds, 3)
            maxProcessCpuCoreRatio = $maxCompetingProcessCpuCoreRatio
            ratioDefinition = 'process CPU milliseconds divided by sample wall-clock milliseconds; 1.0 equals one logical core'
        }
        ignoredProcessIds = @($ignoredProcessIds.Keys | Sort-Object)
        competingProcesses = @($competingProcesses)
        blockers = $blockers
    }
    $preflightPath = if ([string]::IsNullOrWhiteSpace($ReportPath))
    {
        Join-Path $OutputDirectory 'smoke-performance-preflight.json'
    }
    else
    {
        [System.IO.Path]::GetFullPath($ReportPath)
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preflightPath -Encoding UTF8

    return [pscustomobject]@{
        ReportPath = $preflightPath
        Report = $report
    }
}

function Get-Jitter
{
    param(
        [Parameter(Mandatory = $true)] [double[]]$Values
    )

    if ($Values.Count -eq 0)
    {
        return 0
    }

    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -ge 4)
    {
        $trimmed = $sorted[1..($sorted.Count - 2)]
    }
    else
    {
        $trimmed = $sorted
    }

    if ($trimmed.Count -eq 0)
    {
        return 0
    }

    $min = Get-MinValue $trimmed
    $max = Get-MaxValue $trimmed
    $mean = Get-Mean $Values
    if ($mean -le 0)
    {
        return 0
    }

    return [Math]::Round(($max - $min) / $mean, 3)
}

function Try-GetBaselineMetricSummary
{
    param(
        [Parameter(Mandatory = $true)] $BaselineCase,
        [Parameter(Mandatory = $true)] [string]$MetricPath
    )

    $runsSummary = $BaselineCase.runsSummary
    if ($null -eq $runsSummary)
    {
        return $null
    }

    $metricSummary = $runsSummary.$MetricPath
    if ($null -eq $metricSummary)
    {
        return $null
    }

    $mean = $metricSummary.mean
    $p95 = $metricSummary.p95
    $jitter = $metricSummary.jitter
    if ($null -eq $mean -or $null -eq $p95 -or $null -eq $jitter)
    {
        return $null
    }

    return [pscustomobject]@{
        Mean = [double]$mean
        P95 = [double]$p95
        Jitter = [double]$jitter
    }
}

function Test-Threshold
{
    param(
        [Parameter(Mandatory = $true)] [double]$Actual,
        [Parameter(Mandatory = $true)] [double]$Baseline,
        [Parameter(Mandatory = $true)] [double]$LimitMultiplier,
        [Parameter(Mandatory = $true)] [string]$MetricName,
        [Parameter(Mandatory = $true)] [string]$Resolution,
        [Parameter(Mandatory = $true)] [int]$Dpi
    )

    if ($Baseline -le 0)
    {
        return [pscustomobject]@{
            Status = 'SKIP'
            Metric = $MetricName
            Resolution = $Resolution
            Dpi = $Dpi
            Actual = $Actual
            Baseline = $Baseline
            Limit = 0
            Passed = $false
            Reason = 'Baseline missing or non-positive.'
        }
    }

    $limit = [math]::Round($Baseline * $LimitMultiplier, 3)
    $passed = $Actual -le $limit
    return [pscustomobject]@{
        Status = 'CHECK'
        Metric = $MetricName
        Resolution = $Resolution
        Dpi = $Dpi
        Actual = $Actual
        Baseline = $Baseline
        Limit = $limit
        Passed = $passed
        Reason = if ($passed) { 'PASS' } else { 'FAIL' }
    }
}

function Get-BaselineCandidateJitterFailures
{
    param(
        [Parameter(Mandatory = $true)] [object[]]$RunSummaries
    )

    $failures = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in $RunSummaries)
    {
        $caseJitters = [ordered]@{
            startupToIdleMs = [double]$entry.runsSummary.startupToIdleMs.jitter
            navigationMeanMs = [double]$entry.runsSummary.navigationMeanMs.jitter
            navigationP95Ms = [double]$entry.runsSummary.navigationP95Ms.jitter
            steadyMeanMs = [double]$entry.runsSummary.steadyInteractionMeanMs.jitter
            steadyP95Ms = [double]$entry.runsSummary.steadyInteractionP95Ms.jitter
        }

        foreach ($name in $caseJitters.Keys)
        {
            $actualJitter = $caseJitters[$name]
            if ($actualJitter -gt $maxJitterRatio)
            {
                $failures.Add([pscustomobject]@{
                    Resolution = $entry.resolution
                    Dpi = $entry.dpi
                    Metric = $name
                    Actual = $actualJitter
                    Limit = $maxJitterRatio
                    Reason = 'BASELINE_CANDIDATE_JITTER_INVALID'
                })
            }
        }
    }

    return @($failures)
}

function Invoke-SmokePerfRun
{
    param(
        [Parameter(Mandatory = $true)] [string]$Resolution,
        [Parameter(Mandatory = $true)] [int]$Dpi,
        [Parameter(Mandatory = $true)] [int]$RunCount,
        [Parameter(Mandatory = $false)] [string]$RunTag
    )
    $perfSampleCount = [int]$script:Samples
    $warmupRuns = [Math]::Max(0, [int]$script:WarmupRuns)
    $totalRuns = $RunCount + $warmupRuns

    $runSamples = [System.Collections.Generic.List[object]]::new()
    for ($runIndex = 1; $runIndex -le $totalRuns; $runIndex++)
    {
        $runId = "${Resolution}_dpi${Dpi}_run${runIndex}"
        if (-not [string]::IsNullOrWhiteSpace($RunTag))
        {
            $runId = "${RunTag}_${runId}"
        }

        $reportPath = Join-Path $OutputDirectory ("smoke-perf-${runId}.json")
        $runPreflightPath = Join-Path $OutputDirectory ("smoke-perf-${runId}-preflight.json")
        $runPreflight = Get-PerformanceSmokePreflight -ReportPath $runPreflightPath
        if ($runPreflight.Report.status -eq 'blocked')
        {
            [Console]::Error.WriteLine("Performance smoke run preflight blocked before $runId. See $($runPreflight.ReportPath)")
            exit 2
        }

        $dotnetArgs = @(
            '--smoke-project', $ProjectPath,
            '--smoke-size', $Resolution,
            '--smoke-dpi', "$Dpi",
            '--smoke-perf',
            '--smoke-perf-samples', "$perfSampleCount",
            '--smoke-perf-report', "$reportPath"
        )
        if ($runIndex -le $warmupRuns)
        {
            Write-Host "[$Resolution dpi=$Dpi] warmup run #$runIndex/$totalRuns"
        }
        else
        {
            Write-Host "[$Resolution dpi=$Dpi] run #$((($runIndex - $warmupRuns))) /$RunCount (of $totalRuns)"
        }

        $exitCode = 0
        if ($ExecutionMode -eq 'DirectExe')
        {
            $directArgumentList = ($dotnetArgs | ForEach-Object {
                    if ($_ -match '[\s"]')
                    {
                        '"' + $_.Replace('"', '\"') + '"'
                    }
                    else
                    {
                        $_
                    }
                }) -join ' '
            $directProcess = Start-Process -FilePath $ExecutablePath -ArgumentList $directArgumentList -PassThru
            try
            {
                $directProcess.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::$ProcessPriority
                if ($ProcessorAffinityMask -gt 0)
                {
                    $directProcess.ProcessorAffinity = [IntPtr]$ProcessorAffinityMask
                }
            }
            catch
            {
                try
                {
                    $directProcess.Kill()
                }
                catch
                {
                    # Preserve the original configuration error.
                }

                throw "Failed to apply performance process policy (priority=$ProcessPriority, affinityMask=$ProcessorAffinityMask): $($_.Exception.Message)"
            }

            $directProcess.WaitForExit()
            $exitCode = $directProcess.ExitCode
            $directProcess.Dispose()
        }
        else
        {
            $dotnetRunArgs = @(
                'run',
                '--project',
                $project,
                '-c',
                $Configuration
            )
            if ($script:UseNoBuild)
            {
                $dotnetRunArgs += '--no-build'
            }
            $dotnetRunArgs += '--'
            $dotnetRunArgs += $dotnetArgs
            $dotnetRunArgs = [string[]]($dotnetRunArgs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if (-not $dotnetRunArgs -or $dotnetRunArgs.Count -eq 0)
            {
                throw "Smoke performance run failed for $Resolution @${Dpi} (run $runIndex). ExitCode=InvalidDotnetArgs"
            }
            & dotnet @dotnetRunArgs
            $exitCode = $LASTEXITCODE
        }

        if ($exitCode -ne 0)
        {
            throw "Smoke performance run failed for $Resolution @${Dpi} (run $runIndex). ExitCode=$exitCode"
        }

        if (-not (Test-Path -LiteralPath $reportPath))
        {
            throw "Expected performance report path was not created: $reportPath"
        }

        $payload = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
        if ($runIndex -le $warmupRuns)
        {
            continue
        }

        $runSamples.Add([pscustomobject]@{
            preflightReportPath = $runPreflight.ReportPath
            startupToIdleMs = [double]$payload.startupToIdleMs
            navigationMeanMs = [double]$payload.navigationMeanMs
            navigationP95Ms = [double]$payload.navigationP95Ms
            steadyMeanMs = [double]$payload.steadyInteractionMeanMs
            steadyP95Ms = [double]$payload.steadyInteractionP95Ms
            requestedSize = [string]$payload.requestedSize
            requestedScalePercent = [int]$payload.requestedScalePercent
            navigationTimingsMs = @($payload.navigationTimingsMs)
            navigationSelectionTimingsMs = @($payload.navigationSelectionTimingsMs)
            navigationDispatcherTimingsMs = @($payload.navigationDispatcherTimingsMs)
            steadyInteractionTimingsMs = @($payload.steadyInteractionTimingsMs)
            steadyModeMutationTimingsMs = @($payload.steadyModeMutationTimingsMs)
            steadyDispatcherTimingsMs = @($payload.steadyDispatcherTimingsMs)
            steadyDesignModeMutationTimingsMs = @($payload.steadyDesignModeMutationTimingsMs)
            steadyDesignModeDispatcherTimingsMs = @($payload.steadyDesignModeDispatcherTimingsMs)
            steadyRunModeMutationTimingsMs = @($payload.steadyRunModeMutationTimingsMs)
            steadyRunModeDispatcherTimingsMs = @($payload.steadyRunModeDispatcherTimingsMs)
        })
    }

    if ($runSamples.Count -ne $RunCount)
    {
        throw "Smoke performance run count mismatch for $Resolution @${Dpi}. Expected $RunCount measured runs, got $($runSamples.Count)."
    }

    $startupValues = @($runSamples.startupToIdleMs)
    $navMeanValues = @($runSamples.navigationMeanMs)
    $navP95Values = @($runSamples.navigationP95Ms)
    $steadyMeanValues = @($runSamples.steadyMeanMs)
    $steadyP95Values = @($runSamples.steadyP95Ms)

    $worstStartup = Get-MaxValue $startupValues
    $worstNavMean = Get-MaxValue $navMeanValues
    $worstNavP95 = Get-MaxValue $navP95Values
    $worstSteadyMean = Get-MaxValue $steadyMeanValues
    $worstSteadyP95 = Get-MaxValue $steadyP95Values

    return [pscustomobject]@{
        executionMode = $ExecutionMode
        resolution = $Resolution
        dpi = $Dpi
        runs = @($runSamples)
        runsSummary = [ordered]@{
            startupToIdleMs = @{
                mean = Get-Mean $startupValues
                p95 = Get-Percentile $startupValues 0.95
                jitter = Get-Jitter $startupValues
            }
            navigationMeanMs = @{
                mean = Get-Mean $navMeanValues
                p95 = Get-Percentile $navMeanValues 0.95
                jitter = Get-Jitter $navMeanValues
            }
            navigationP95Ms = @{
                mean = Get-Mean $navP95Values
                p95 = Get-Percentile $navP95Values 0.95
                jitter = Get-Jitter $navP95Values
            }
            steadyInteractionMeanMs = @{
                mean = Get-Mean $steadyMeanValues
                p95 = Get-Percentile $steadyMeanValues 0.95
                jitter = Get-Jitter $steadyMeanValues
            }
            steadyInteractionP95Ms = @{
                mean = Get-Mean $steadyP95Values
                p95 = Get-Percentile $steadyP95Values 0.95
                jitter = Get-Jitter $steadyP95Values
            }
        }
        worst = [ordered]@{
            startupToIdleMs = $worstStartup
            navigationMeanMs = $worstNavMean
            navigationP95Ms = $worstNavP95
            steadyMeanMs = $worstSteadyMean
            steadyP95Ms = $worstSteadyP95
        }
    }
}

function Invoke-SmokePerfSuite
{
    param(
        [Parameter(Mandatory = $true)] [int]$RunCount,
        [Parameter(Mandatory = $false)] [string]$RunTag
    )

    $runSummaries = [System.Collections.Generic.List[object]]::new()

    foreach ($matrixCase in $script:SmokeResolutionPlan.supportedCases)
    {
        $case = Invoke-SmokePerfRun -Resolution $matrixCase.resolution -Dpi $matrixCase.dpi -RunCount $RunCount -RunTag $RunTag
        $null = $runSummaries.Add($case)
    }

    return ,$runSummaries
}

function Evaluate-SmokePerf
{
    param(
        [Parameter(Mandatory = $true)] [System.Collections.Generic.List[object]]$RunSummaries,
        [Parameter(Mandatory = $true)] $BaselinePayload,
        [Parameter(Mandatory = $true)] $RunEnvironment
    )

    $failures = [System.Collections.Generic.List[object]]::new()
    $isBaselineInvalid = $false

    $baselineEnvironment = $BaselinePayload.environment
    if ($null -eq $baselineEnvironment)
    {
        $isBaselineInvalid = $true
        $failures.Add([pscustomobject]@{
                Resolution = 'GLOBAL'
                Dpi = 0
                Metric = 'BASELINE_INCOMPLETE'
                Actual = 0
                Baseline = 0
                Limit = 0
                Reason = 'Baseline environment block missing.'
            })
    }
    else
    {
        $environmentMismatches = @(
            @{ Name = 'machineName'; Current = $RunEnvironment.machineName; Baseline = $baselineEnvironment.machineName; },
            @{ Name = 'osDescription'; Current = $RunEnvironment.osDescription; Baseline = $baselineEnvironment.osDescription; },
            @{ Name = 'processArchitecture'; Current = $RunEnvironment.processArchitecture; Baseline = $baselineEnvironment.processArchitecture; },
            @{ Name = 'processorCount'; Current = [int]$RunEnvironment.processorCount; Baseline = [int]$baselineEnvironment.processorCount; },
            @{ Name = 'executionMode'; Current = $RunEnvironment.executionMode; Baseline = $baselineEnvironment.executionMode; },
            @{ Name = 'configuration'; Current = $RunEnvironment.configuration; Baseline = $baselineEnvironment.configuration; },
            @{ Name = 'processPriority'; Current = $RunEnvironment.processPriority; Baseline = $baselineEnvironment.processPriority; },
            @{ Name = 'processorAffinityMask'; Current = [long]$RunEnvironment.processorAffinityMask; Baseline = [long]$baselineEnvironment.processorAffinityMask; },
            @{ Name = 'testMonitorKey'; Current = $RunEnvironment.testMonitorKey; Baseline = $baselineEnvironment.testMonitorKey; },
            @{ Name = 'requestedMatrixKey'; Current = $RunEnvironment.requestedMatrixKey; Baseline = $baselineEnvironment.requestedMatrixKey; }
        )

        foreach ($mismatch in $environmentMismatches)
        {
            if ($mismatch.Current -ne $mismatch.Baseline)
            {
                $isBaselineInvalid = $true
                $failures.Add([pscustomobject]@{
                        Resolution = 'GLOBAL'
                        Dpi = 0
                        Metric = 'ENVIRONMENT_MISMATCH'
                        Actual = "$($mismatch.Name)=$($mismatch.Current)"
                        Baseline = "$($mismatch.Name)=$($mismatch.Baseline)"
                        Limit = 0
                        Reason = 'BASELINE_ENVIRONMENT_MISMATCH'
                    })
            }
        }
    }

    foreach ($entry in $RunSummaries)
    {
        $key = "$($entry.resolution)@$($entry.dpi)"
        $baselineCase = $BaselinePayload.cases.$key
        if ($null -eq $baselineCase)
        {
            $isBaselineInvalid = $true
            Write-Warning "Baseline missing for case $key; skipping threshold check for this case."
            $failures.Add([pscustomobject]@{
                Resolution = $entry.resolution
                Dpi = $entry.dpi
                Metric = 'BASELINE_MISSING'
                Actual = 0
                Baseline = 0
                Limit = 0
                Reason = 'No baseline case for this resolution/dpi'
            })
            continue
        }

        $baselineStartupSummary = Try-GetBaselineMetricSummary -BaselineCase $baselineCase -MetricPath 'startupToIdleMs'
        $baselineNavigationMeanSummary = Try-GetBaselineMetricSummary -BaselineCase $baselineCase -MetricPath 'navigationMeanMs'
        $baselineNavigationP95Summary = Try-GetBaselineMetricSummary -BaselineCase $baselineCase -MetricPath 'navigationP95Ms'
        $baselineSteadyMeanSummary = Try-GetBaselineMetricSummary -BaselineCase $baselineCase -MetricPath 'steadyInteractionMeanMs'
        $baselineSteadyP95Summary = Try-GetBaselineMetricSummary -BaselineCase $baselineCase -MetricPath 'steadyInteractionP95Ms'
        if ($null -eq $baselineStartupSummary -or $null -eq $baselineNavigationMeanSummary -or $null -eq $baselineNavigationP95Summary -or $null -eq $baselineSteadyMeanSummary -or $null -eq $baselineSteadyP95Summary)
        {
            $isBaselineInvalid = $true
            $failures.Add([pscustomobject]@{
                Resolution = $entry.resolution
                Dpi = $entry.dpi
                Metric = 'BASELINE_INCOMPLETE'
                Actual = 0
                Baseline = 0
                Limit = 0
                Reason = 'Baseline case exists but required runsSummary statistics are missing.'
            })
            continue
        }

        $baselineStartupJitter = $baselineStartupSummary.Jitter
        $baselineNavMeanJitter = $baselineNavigationMeanSummary.Jitter
        $baselineNavP95Jitter = $baselineNavigationP95Summary.Jitter
        $baselineSteadyMeanJitter = $baselineSteadyMeanSummary.Jitter
        $baselineSteadyP95Jitter = $baselineSteadyP95Summary.Jitter
        $caseJitters = [ordered]@{
            startupToIdleMs = @([double]$baselineStartupJitter, [double]$entry.runsSummary.startupToIdleMs.jitter)
            navigationMeanMs = @([double]$baselineNavMeanJitter, [double]$entry.runsSummary.navigationMeanMs.jitter)
            navigationP95Ms = @([double]$baselineNavP95Jitter, [double]$entry.runsSummary.navigationP95Ms.jitter)
            steadyMeanMs = @([double]$baselineSteadyMeanJitter, [double]$entry.runsSummary.steadyInteractionMeanMs.jitter)
            steadyP95Ms = @([double]$baselineSteadyP95Jitter, [double]$entry.runsSummary.steadyInteractionP95Ms.jitter)
        }

        foreach ($name in $caseJitters.Keys)
        {
            $actualJitter = $caseJitters[$name][1]
            $baselineJitter = $caseJitters[$name][0]
            if ($baselineJitter -gt $maxJitterRatio)
            {
                $isBaselineInvalid = $true
                $failures.Add([pscustomobject]@{
                    Resolution = $entry.resolution
                    Dpi = $entry.dpi
                    Metric = $name
                    Actual = $actualJitter
                    Baseline = $baselineJitter
                    Limit = $maxJitterRatio
                    Reason = 'BASELINE_JITTER_INVALID'
                })
            }

            if ($actualJitter -gt $maxJitterRatio)
            {
                $failures.Add([pscustomobject]@{
                    Resolution = $entry.resolution
                    Dpi = $entry.dpi
                    Metric = $name
                    Actual = $actualJitter
                    Baseline = $baselineJitter
                    Limit = $maxJitterRatio
                    Reason = 'CURRENT_JITTER_INVALID'
                })
            }
        }

        $startupToIdleMeanBaseline = $baselineStartupSummary.Mean
        $startupToIdleP95Baseline = $baselineStartupSummary.P95
        $navigationMeanBaseline = $baselineNavigationMeanSummary.Mean
        $navigationP95Baseline = $baselineNavigationP95Summary.P95
        $steadyMeanBaseline = $baselineSteadyMeanSummary.Mean
        $steadyP95Baseline = $baselineSteadyP95Summary.P95

        $checks = @(
            (Test-Threshold -Actual $entry.runsSummary.startupToIdleMs.mean -Baseline $startupToIdleMeanBaseline -LimitMultiplier $meanRegressionLimitMultiplier -MetricName 'startupToIdleMeanMs' -Resolution $entry.resolution -Dpi $entry.dpi),
            (Test-Threshold -Actual $entry.runsSummary.startupToIdleMs.p95 -Baseline $startupToIdleP95Baseline -LimitMultiplier $p95RegressionLimitMultiplier -MetricName 'startupToIdleP95Ms' -Resolution $entry.resolution -Dpi $entry.dpi),
            (Test-Threshold -Actual $entry.runsSummary.navigationMeanMs.mean -Baseline $navigationMeanBaseline -LimitMultiplier $meanRegressionLimitMultiplier -MetricName 'navigationMeanMs' -Resolution $entry.resolution -Dpi $entry.dpi),
            (Test-Threshold -Actual $entry.runsSummary.navigationP95Ms.p95 -Baseline $navigationP95Baseline -LimitMultiplier $p95RegressionLimitMultiplier -MetricName 'navigationP95Ms' -Resolution $entry.resolution -Dpi $entry.dpi),
            (Test-Threshold -Actual $entry.runsSummary.steadyInteractionMeanMs.mean -Baseline $steadyMeanBaseline -LimitMultiplier $meanRegressionLimitMultiplier -MetricName 'steadyInteractionMeanMs' -Resolution $entry.resolution -Dpi $entry.dpi),
            (Test-Threshold -Actual $entry.runsSummary.steadyInteractionP95Ms.p95 -Baseline $steadyP95Baseline -LimitMultiplier $p95RegressionLimitMultiplier -MetricName 'steadyInteractionP95Ms' -Resolution $entry.resolution -Dpi $entry.dpi)
        )

        foreach ($check in $checks)
        {
            if ($check.Status -eq 'SKIP')
            {
                $isBaselineInvalid = $true
                $failures.Add([pscustomobject]@{
                    Resolution = $check.Resolution
                    Dpi = $check.Dpi
                    Metric = $check.Metric
                    Actual = $check.Actual
                    Baseline = $check.Baseline
                    Limit = $check.Limit
                    Reason = $check.Reason
                })
                continue
            }

            if (-not $check.Passed)
            {
                $failures.Add([pscustomobject]@{
                    Resolution = $check.Resolution
                    Dpi = $check.Dpi
                    Metric = $check.Metric
                    Actual = $check.Actual
                    Baseline = $check.Baseline
                    Limit = $check.Limit
                    Reason = $check.Reason
                })
            }
        }
    }

    return [pscustomobject]@{
        BaselineInvalid = $isBaselineInvalid
        Failures = @($failures)
    }
}

$preflight = Get-PerformanceSmokePreflight
if ($preflight.Report.status -eq 'blocked')
{
    [Console]::Error.WriteLine("Performance smoke preflight blocked by competing process(es). See $($preflight.ReportPath)")
    exit 2
}
if ($PreflightOnly)
{
    Write-Host "Performance smoke preflight passed. Report: $($preflight.ReportPath)"
    exit 0
}

$resolvedBaselineDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($BaselinePath))
if (-not [string]::IsNullOrWhiteSpace($resolvedBaselineDirectory))
{
    New-Item -ItemType Directory -Path $resolvedBaselineDirectory -Force | Out-Null
}

$runEnvironment = Get-SmokeEnvironment
$script:UseNoBuild = $false
if ($SkipBuild)
{
    Write-Host "Skipping build by request. Running with --no-build."
    $script:UseNoBuild = $true
}
else
{
    Write-Host "Preparing one-time build: $project -c $Configuration"
    & dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0)
    {
        throw "Project build failed before running smoke performance."
    }

    $script:UseNoBuild = $true
}
$runSummaries = Invoke-SmokePerfSuite -RunCount $Runs
$existingBaseline = if (Test-Path -LiteralPath $BaselinePath)
{
    Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
} else {
    $null
}

if ($CreateBaseline)
{
    $baselineCandidateJitterFailures = @(Get-BaselineCandidateJitterFailures -RunSummaries $runSummaries)
    if ($baselineCandidateJitterFailures.Count -gt 0)
    {
        $rejectionPath = Join-Path $OutputDirectory 'smoke-performance-baseline-rejection.json'
        [ordered]@{
            schema = '1.0'
            capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            status = 'rejected'
            maxJitterRatio = $maxJitterRatio
            failures = $baselineCandidateJitterFailures
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $rejectionPath -Encoding UTF8
        throw "Baseline candidate is unstable. No baseline was written. See: $rejectionPath"
    }

    $baselinePayload = [ordered]@{
        schema = '1.0'
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        generatedBy = 'smoke-performance.ps1'
        executionMode = $ExecutionMode
        meanRegressionLimitMultiplier = $meanRegressionLimitMultiplier
        p95RegressionLimitMultiplier = $p95RegressionLimitMultiplier
        maxJitterRatio = $maxJitterRatio
        environment = $runEnvironment
        runs = $Runs
        samples = $Samples
        recheckRuns = $RecheckRuns
        testMonitor = $script:SmokeResolutionPlan.monitor
        resolutions = $Resolutions
        dpiScales = $DpiScales
        executedResolutionDpiCases = @($script:SmokeResolutionPlan.supportedCases)
        skippedResolutionDpiCases = @($script:SmokeResolutionPlan.skippedCases)
        fullMatrixExecuted = ($script:SmokeResolutionPlan.skippedCases.Count -eq 0)
        cases = [ordered]@{}
    }

    foreach ($entry in $runSummaries)
    {
        $key = "$($entry.resolution)@$($entry.dpi)"
        $baselinePayload.cases[$key] = [ordered]@{
            capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            runs = @($entry.runs)
            runsSummary = $entry.runsSummary
            worst = $entry.worst
            baselineMetrics = [ordered]@{
                startupToIdleMeanMs = $entry.runsSummary.startupToIdleMs.mean
                startupToIdleP95Ms = $entry.runsSummary.startupToIdleMs.p95
                navigationMeanMs = $entry.runsSummary.navigationMeanMs.mean
                navigationP95Ms = $entry.runsSummary.navigationP95Ms.p95
                steadyInteractionMeanMs = $entry.runsSummary.steadyInteractionMeanMs.mean
                steadyInteractionP95Ms = $entry.runsSummary.steadyInteractionP95Ms.p95
            }
        }
    }

    $baselinePayload | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $BaselinePath -Encoding UTF8
    Write-Host "Baseline created/updated: $BaselinePath"
    exit 0
}
if ($null -eq $existingBaseline)
{
    throw "Baseline path does not exist: $BaselinePath. Re-run with -CreateBaseline."
}

$initialEvaluation = Evaluate-SmokePerf -RunSummaries $runSummaries -BaselinePayload $existingBaseline -RunEnvironment $runEnvironment
$initialFailures = $initialEvaluation.Failures
$finalFailures = $initialFailures
$finalBaselineInvalid = $initialEvaluation.BaselineInvalid

$summaryPayload = [ordered]@{
    schema = '1.0'
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    executionMode = $ExecutionMode
    environment = $runEnvironment
    runs = $Runs
    samples = $Samples
    recheckRuns = $RecheckRuns
    testMonitor = $script:SmokeResolutionPlan.monitor
    resolutions = $Resolutions
    dpiScales = $DpiScales
    executedResolutionDpiCases = @($script:SmokeResolutionPlan.supportedCases)
    skippedResolutionDpiCases = @($script:SmokeResolutionPlan.skippedCases)
    fullMatrixExecuted = ($script:SmokeResolutionPlan.skippedCases.Count -eq 0)
    summary = [ordered]@{
        caseCount = $runSummaries.Count
        baselineInvalid = $finalBaselineInvalid
        baselinePath = $BaselinePath
        failureCount = $initialFailures.Count
        recheckPerformed = $false
        recheckFailureCount = 0
        unstableInitialRunRecovered = $false
    }
    cases = @($runSummaries)
    initialFailures = @($initialFailures)
    recheckCases = @()
    recheckFailures = @()
    failures = @($finalFailures)
}

if (($initialFailures.Count -gt 0) -and $AutoRecheck)
{
    Write-Warning ("Initial run had {0} issue(s). Running {1}-run recheck." -f $initialFailures.Count, $RecheckRuns)
    $recheckSummaries = Invoke-SmokePerfSuite -RunCount $RecheckRuns -RunTag 'recheck'
    $recheckEvaluation = Evaluate-SmokePerf -RunSummaries $recheckSummaries -BaselinePayload $existingBaseline -RunEnvironment $runEnvironment
    $summaryPayload.summary.recheckPerformed = $true
    $summaryPayload.summary.recheckFailureCount = $recheckEvaluation.Failures.Count
    $summaryPayload.recheckCases = @($recheckSummaries)
    $summaryPayload.recheckFailures = @($recheckEvaluation.Failures)

    if ((-not $recheckEvaluation.BaselineInvalid) -and $recheckEvaluation.Failures.Count -eq 0)
    {
        Write-Host "Recheck passed: initial failure appears unstable. Treating as transient and continuing with PASS."
        $summaryPayload.summary.baselineInvalid = $false
        $summaryPayload.summary.failureCount = 0
        $summaryPayload.summary.unstableInitialRunRecovered = $true
        $summaryPayload.failures = @()
        $summaryPayload.initialFailures = @($initialFailures)
        $finalFailures = @()
        $finalBaselineInvalid = $false
    }
    else
    {
        $summaryPayload.summary.baselineInvalid = $recheckEvaluation.BaselineInvalid
        $summaryPayload.summary.failureCount = $recheckEvaluation.Failures.Count
        $summaryPayload.failures = @($recheckEvaluation.Failures)
        $finalFailures = @($recheckEvaluation.Failures)
        $finalBaselineInvalid = $recheckEvaluation.BaselineInvalid
    }
}

$summaryPayload.failures = @($finalFailures)
$summaryPath = Join-Path $OutputDirectory 'smoke-performance-summary.json'
$summaryPayload | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

if ($summaryPayload.summary.failureCount -gt 0)
{
    if ($summaryPayload.summary.baselineInvalid)
    {
        Write-Error "Baseline is invalid. Recheck environment and rebuild baseline before declaring pass. See summary: $summaryPath"
    }
    else
    {
        Write-Error "UI performance smoke check failed for $($summaryPayload.summary.failureCount) metric(s). See summary: $summaryPath"
    }

    foreach ($failure in $summaryPayload.failures)
    {
        Write-Error ("  [{0}@{1}] {2} actual={3} baseline={4} limit={5}" -f $failure.Resolution, $failure.Dpi, $failure.Metric, $failure.Actual, $failure.Baseline, $failure.Limit)
        if (-not [string]::IsNullOrWhiteSpace($failure.Reason))
        {
            Write-Error ("      reason: {0}" -f $failure.Reason)
        }
    }

    exit 2
}

Write-Host "UI performance smoke check passed. Summary: $summaryPath"
exit 0
