using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Faults;
using OpenVisionLab.Machine.Simulation.Scenarios;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.ViewModel.Simulation;

public sealed record SimulationScenarioTargetOption(string Id, string Name);
public sealed record SimulationScenarioFaultKindOption(SimulationFaultKind Kind, string Name);

/// <summary>
/// Stores Test Scenario configuration only. Runtime state belongs to the
/// simulation engine and is projected through MainViewModel snapshots.
/// </summary>
public sealed class SimulationWorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private const string AutomaticCycleAssertionDefaultId = "automatic-cycle-completed";
    private const string NoActiveFaultsAssertionDefaultId = "final-faults-cleared";
    private const string FinalEquipmentStateAssertionDefaultId = "final-equipment-state";
    private readonly RelayCommand loadScenarioProfileCommand;
    private readonly RelayCommand resetScenarioCommand;
    private readonly ObservableCollection<SimulationScenarioProfile> scenarioProfiles = [];
    private HashSet<string> availableFinalEquipmentTargetIds = new(StringComparer.Ordinal);
    private SimulationScenarioProfile? selectedScenarioProfile;
    private string scenarioProfilePath = string.Empty;
    private string? scenarioTargetId;
    private int scenarioSeed;
    private int scenarioDurationCycles = 200;
    private int batchRepetitionCount = 3;
    private bool isScheduledFaultEnabled;
    private SimulationFaultKind scheduledFaultKind = SimulationFaultKind.AxisMotionBlocked;
    private string? scheduledFaultTargetId;
    private bool scheduledFaultForcedValue;
    private int scheduledFaultInjectTick = 50;
    private int scheduledFaultHoldTicks = 3;
    private bool restartSequenceAfterFault = true;
    private string? recoverySequenceId;
    private bool requireAutomaticCycleCompleted;
    private int minimumCompletedCycles = 1;
    private bool requireNoActiveFaults;
    private bool requireFinalEquipmentState;
    private string? finalEquipmentTargetId;
    private string finalEquipmentExpectedState = string.Empty;
    private string automaticCycleAssertionId = AutomaticCycleAssertionDefaultId;
    private string noActiveFaultsAssertionId = NoActiveFaultsAssertionDefaultId;
    private string finalEquipmentStateAssertionId = FinalEquipmentStateAssertionDefaultId;
    private bool isDisposed;

    public SimulationWorkspaceViewModel()
    {
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
        loadScenarioProfileCommand = new RelayCommand(_ => LoadScenarioProfile(), _ => !isDisposed);
        resetScenarioCommand = new RelayCommand(_ => ResetScenario(), _ => !isDisposed);

        foreach (var profile in SimulationScenarioProfile.BuiltIns)
        {
            scenarioProfiles.Add(profile);
        }

        selectedScenarioProfile = scenarioProfiles.FirstOrDefault()
            ?? SimulationScenarioProfile.Normalize(null);
        if (!scenarioProfiles.Contains(selectedScenarioProfile))
        {
            scenarioProfiles.Add(selectedScenarioProfile);
        }

        scenarioSeed = selectedScenarioProfile.Seed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SimulationScenarioProfile> ScenarioProfiles => scenarioProfiles;

    public ICommand LoadScenarioProfileCommand => loadScenarioProfileCommand;

    public ICommand ResetScenarioCommand => resetScenarioCommand;

    public string ScenarioProfilePath
    {
        get => scenarioProfilePath;
        set
        {
            if (scenarioProfilePath == value)
            {
                return;
            }

            scenarioProfilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoadScenarioProfileTooltip));
        }
    }

    public SimulationScenarioProfile SelectedScenarioProfile
    {
        get => selectedScenarioProfile ?? scenarioProfiles[0];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(selectedScenarioProfile, value))
            {
                return;
            }

            selectedScenarioProfile = value;
            scenarioSeed = value.Seed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScenarioSeed));
            OnPropertyChanged(nameof(ScenarioSummary));
            OnPropertyChanged(nameof(WorkspaceScenarioDescription));
        }
    }

    public string? ScenarioTargetId
    {
        get => scenarioTargetId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(scenarioTargetId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            scenarioTargetId = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScenarioSummary));
            OnPropertyChanged(nameof(IsScheduledFaultConfigurationValid));
            OnPropertyChanged(nameof(ScheduledFaultSummary));
        }
    }

    public int ScenarioSeed
    {
        get => scenarioSeed;
        set
        {
            if (scenarioSeed == value)
            {
                return;
            }

            scenarioSeed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScenarioSummary));
        }
    }

    public int ScenarioDurationCycles
    {
        get => scenarioDurationCycles;
        set
        {
            var normalized = Math.Clamp(value, 1, 100_000);
            if (scenarioDurationCycles == normalized)
            {
                return;
            }

            scenarioDurationCycles = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScenarioSummary));
            OnPropertyChanged(nameof(IsScheduledFaultConfigurationValid));
            OnPropertyChanged(nameof(ScheduledFaultSummary));
        }
    }

    public int BatchRepetitionCount
    {
        get => batchRepetitionCount;
        set
        {
            var normalized = Math.Clamp(value, 1, 100);
            if (batchRepetitionCount == normalized)
            {
                return;
            }

            batchRepetitionCount = normalized;
            OnPropertyChanged();
        }
    }

    public bool RequireAutomaticCycleCompleted
    {
        get => requireAutomaticCycleCompleted;
        set
        {
            if (requireAutomaticCycleCompleted == value)
            {
                return;
            }

            requireAutomaticCycleCompleted = value;
            OnAssertionSettingChanged();
        }
    }

    public int MinimumCompletedCycles
    {
        get => minimumCompletedCycles;
        set
        {
            var normalized = Math.Clamp(value, 1, int.MaxValue);
            if (minimumCompletedCycles == normalized)
            {
                return;
            }

            minimumCompletedCycles = normalized;
            OnAssertionSettingChanged();
        }
    }

    public bool RequireNoActiveFaults
    {
        get => requireNoActiveFaults;
        set
        {
            if (requireNoActiveFaults == value)
            {
                return;
            }

            requireNoActiveFaults = value;
            OnAssertionSettingChanged();
        }
    }

    public bool RequireFinalEquipmentState
    {
        get => requireFinalEquipmentState;
        set
        {
            if (requireFinalEquipmentState == value)
            {
                return;
            }

            requireFinalEquipmentState = value;
            OnAssertionSettingChanged();
        }
    }

    public string? FinalEquipmentTargetId
    {
        get => finalEquipmentTargetId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(finalEquipmentTargetId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            finalEquipmentTargetId = normalized;
            OnAssertionSettingChanged();
        }
    }

    public string FinalEquipmentExpectedState
    {
        get => finalEquipmentExpectedState;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(finalEquipmentExpectedState, normalized, StringComparison.Ordinal))
            {
                return;
            }

            finalEquipmentExpectedState = normalized;
            OnAssertionSettingChanged();
        }
    }

    public bool HasConfiguredAssertions =>
        RequireAutomaticCycleCompleted || RequireNoActiveFaults || RequireFinalEquipmentState;

    public bool IsAssertionConfigurationValid => !RequireFinalEquipmentState
        || (FinalEquipmentTargetId is not null
            && availableFinalEquipmentTargetIds.Contains(FinalEquipmentTargetId)
            && !string.IsNullOrWhiteSpace(FinalEquipmentExpectedState));

    public string AssertionSummary => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Simulation.AssertionSummary"),
        (RequireAutomaticCycleCompleted ? 1 : 0)
        + (RequireNoActiveFaults ? 1 : 0)
        + (RequireFinalEquipmentState ? 1 : 0));

    public IReadOnlyList<SimulationScenarioFaultKindOption> AvailableScheduledFaultKinds =>
    [
        new(SimulationFaultKind.StuckDigitalInput,
            OpenVisionLanguageService.T("Fault.StuckDigitalInput")),
        new(SimulationFaultKind.CylinderTravelBlocked,
            OpenVisionLanguageService.T("Fault.CylinderTravelBlocked")),
        new(SimulationFaultKind.AxisMotionBlocked,
            OpenVisionLanguageService.T("Fault.AxisMotionBlocked"))
    ];

    public bool IsScheduledFaultEnabled
    {
        get => isScheduledFaultEnabled;
        set
        {
            if (isScheduledFaultEnabled == value)
            {
                return;
            }

            isScheduledFaultEnabled = value;
            OnScheduledFaultSettingChanged();
        }
    }

    public SimulationFaultKind ScheduledFaultKind
    {
        get => scheduledFaultKind;
        set
        {
            if (scheduledFaultKind == value)
            {
                return;
            }

            scheduledFaultKind = value;
            scheduledFaultTargetId = null;
            OnScheduledFaultSettingChanged();
            OnPropertyChanged(nameof(ScheduledFaultTargetId));
            OnPropertyChanged(nameof(RequiresScheduledFaultValue));
        }
    }

    public string? ScheduledFaultTargetId
    {
        get => scheduledFaultTargetId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(scheduledFaultTargetId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            scheduledFaultTargetId = normalized;
            OnScheduledFaultSettingChanged();
        }
    }

    public bool ScheduledFaultForcedValue
    {
        get => scheduledFaultForcedValue;
        set
        {
            if (scheduledFaultForcedValue == value)
            {
                return;
            }

            scheduledFaultForcedValue = value;
            OnScheduledFaultSettingChanged();
        }
    }

    public bool RequiresScheduledFaultValue =>
        ScheduledFaultKind == SimulationFaultKind.StuckDigitalInput;

    public int ScheduledFaultInjectTick
    {
        get => scheduledFaultInjectTick;
        set
        {
            var normalized = Math.Clamp(value, 0, 99_999);
            if (scheduledFaultInjectTick == normalized)
            {
                return;
            }

            scheduledFaultInjectTick = normalized;
            OnScheduledFaultSettingChanged();
        }
    }

    public int ScheduledFaultHoldTicks
    {
        get => scheduledFaultHoldTicks;
        set
        {
            var normalized = Math.Clamp(value, 1, 100_000);
            if (scheduledFaultHoldTicks == normalized)
            {
                return;
            }

            scheduledFaultHoldTicks = normalized;
            OnScheduledFaultSettingChanged();
        }
    }

    public bool RestartSequenceAfterFault
    {
        get => restartSequenceAfterFault;
        set
        {
            if (restartSequenceAfterFault == value)
            {
                return;
            }

            restartSequenceAfterFault = value;
            OnScheduledFaultSettingChanged();
        }
    }

    public string? RecoverySequenceId
    {
        get => recoverySequenceId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(recoverySequenceId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            recoverySequenceId = normalized;
            OnScheduledFaultSettingChanged();
        }
    }

    public bool IsScheduledFaultConfigurationValid => !IsScheduledFaultEnabled
        || (ScheduledFaultTargetId is not null
            && ScheduledFaultKind is SimulationFaultKind.StuckDigitalInput
                or SimulationFaultKind.CylinderTravelBlocked
                or SimulationFaultKind.AxisMotionBlocked
            && ScheduledFaultInjectTick >= 0
            && ScheduledFaultHoldTicks >= 1
            && (long)ScheduledFaultInjectTick + ScheduledFaultHoldTicks < ScenarioDurationCycles
            && (!RestartSequenceAfterFault || RecoverySequenceId is not null));

    public string ScheduledFaultSummary
    {
        get
        {
            if (!IsScheduledFaultEnabled)
            {
                return OpenVisionLanguageService.T("Simulation.FaultScheduleDisabled");
            }

            if (!IsScheduledFaultConfigurationValid)
            {
                return string.Empty;
            }

            var recovery = RestartSequenceAfterFault
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Simulation.RecoveryRestart"),
                    RecoverySequenceId)
                : OpenVisionLanguageService.T("Simulation.RecoveryClearOnly");
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.ScheduledFaultScheduleSummary"),
                FaultKindDisplayName(ScheduledFaultKind),
                ScheduledFaultTargetId,
                ScheduledFaultInjectTick,
                ScheduledFaultHoldTicks,
                recovery);
        }
    }

    public string ScenarioSummary => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        OpenVisionLanguageService.T("Simulation.ScenarioSummary"),
        ScenarioDisplayName,
        ScenarioModeDisplayName,
        ScenarioSeed,
        ScenarioDurationCycles);

    public string WorkspaceScenarioDescription => LocalizeScenarioDescription(SelectedScenarioProfile);

    public string ScenarioDisplayName => LocalizeScenarioName(SelectedScenarioProfile);

    public string ScenarioModeDisplayName => OpenVisionLanguageService.T(
        $"Simulation.Mode.{SelectedScenarioProfile.Mode}",
        SelectedScenarioProfile.Mode.ToString(),
        SelectedScenarioProfile.Mode.ToString());

    public string LoadScenarioProfileTooltip => string.IsNullOrWhiteSpace(ScenarioProfilePath)
        ? OpenVisionLanguageService.T("Simulation.LoadJsonHint")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Simulation.LoadJsonPath"),
            ScenarioProfilePath);

    public string ResetScenarioTooltip => OpenVisionLanguageService.T("Simulation.ResetScenarioHint");

    public DeterministicConditionScenarioProfile BuildEngineProfile(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var profile = SelectedScenarioProfile;
        var initialState = profile.Mode switch
        {
            SimulationScenarioMode.Fault => DeterministicConditionState.Degraded,
            SimulationScenarioMode.Recovery => DeterministicConditionState.Fault,
            SimulationScenarioMode.Congested => DeterministicConditionState.Degraded,
            _ => DeterministicConditionState.Normal
        };

        var minimumStateTicks = profile.Mode switch
        {
            SimulationScenarioMode.Fault => 8,
            SimulationScenarioMode.Recovery => 8,
            SimulationScenarioMode.Congested => 12,
            _ => 20
        };

        var jitterTicks = profile.Mode switch
        {
            SimulationScenarioMode.Normal => 0,
            SimulationScenarioMode.Fault => 3,
            SimulationScenarioMode.Recovery => 2,
            _ => 4
        };

        var faultRecovery = IsScheduledFaultEnabled && IsScheduledFaultConfigurationValid
            ? new DeterministicFaultRecoverySchedule(
                ScheduledFaultKind,
                ScheduledFaultTargetId!,
                ScheduledFaultInjectTick,
                ScheduledFaultHoldTicks,
                RequiresScheduledFaultValue ? ScheduledFaultForcedValue : null,
                RestartSequenceAfterFault ? RecoverySequenceId : null)
            : null;
        var scenarioId = faultRecovery is null
            ? profile.ProfileId
            : $"{profile.ProfileId}:{faultRecovery.FaultKind}:{faultRecovery.TargetId}:" +
              $"{faultRecovery.ForcedValue}:{faultRecovery.InjectTick}:{faultRecovery.HoldTicks}:" +
              $"{faultRecovery.RestartSequenceId ?? "clear"}";

        return new DeterministicConditionScenarioProfile(
            DeterministicConditionScenarioProfile.CurrentSchemaVersion,
            scenarioId,
            profile.Name,
            profile.Description,
            targetId,
            ScenarioSeed,
            ScenarioDurationCycles,
            minimumStateTicks,
            jitterTicks,
            initialState,
            FaultRecovery: faultRecovery,
            Assertions: DeterministicScenarioAssertion.FromProjectDefinitions(
                BuildProjectAssertions()));
    }

    public void EnsureScenarioTarget(IEnumerable<string> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        var availableIds = targetIds.ToArray();
        if (availableIds.Length == 0
            || (ScenarioTargetId is not null
                && availableIds.Contains(ScenarioTargetId, StringComparer.Ordinal)))
        {
            return;
        }

        ScenarioTargetId = availableIds[0];
    }

    public void EnsureScheduledFaultTarget(IEnumerable<string> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        var availableIds = targetIds.ToArray();
        if (availableIds.Length == 0)
        {
            ScheduledFaultTargetId = null;
            return;
        }

        if (ScheduledFaultTargetId is not null
            && availableIds.Contains(ScheduledFaultTargetId, StringComparer.Ordinal))
        {
            return;
        }

        ScheduledFaultTargetId = availableIds[0];
    }

    public void UpdateFinalEquipmentTargets(IEnumerable<string> targetIds)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        availableFinalEquipmentTargetIds = targetIds.ToHashSet(StringComparer.Ordinal);
        OnPropertyChanged(nameof(IsAssertionConfigurationValid));
    }

    public void EnsureRecoverySequence(IEnumerable<string> sequenceIds)
    {
        ArgumentNullException.ThrowIfNull(sequenceIds);
        var availableIds = sequenceIds.ToArray();
        if (availableIds.Length == 0
            || (RecoverySequenceId is not null
                && availableIds.Contains(RecoverySequenceId, StringComparer.Ordinal)))
        {
            return;
        }

        RecoverySequenceId = availableIds[0];
    }

    public void LoadProjectScenario(SimulationDefinition simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var assertions = simulation.TestScenarioAssertions ?? [];
        var automaticCycle = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.AutomaticCycleCompleted);
        var noActiveFaults = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.NoActiveFaults);
        var finalEquipmentState = assertions.FirstOrDefault(assertion =>
            assertion.Kind == TestScenarioAssertionKind.FinalEquipmentState);
        requireAutomaticCycleCompleted = automaticCycle is not null;
        minimumCompletedCycles = automaticCycle is null
            ? 1
            : (int)Math.Clamp(automaticCycle.MinimumCount, 1, int.MaxValue);
        requireNoActiveFaults = noActiveFaults is not null;
        requireFinalEquipmentState = finalEquipmentState is not null;
        finalEquipmentTargetId = string.IsNullOrWhiteSpace(finalEquipmentState?.TargetId)
            ? null
            : finalEquipmentState.TargetId.Trim();
        finalEquipmentExpectedState = finalEquipmentState?.ExpectedState?.Trim() ?? string.Empty;
        automaticCycleAssertionId = NormalizeAssertionId(
            automaticCycle?.AssertionId,
            AutomaticCycleAssertionDefaultId);
        noActiveFaultsAssertionId = NormalizeAssertionId(
            noActiveFaults?.AssertionId,
            NoActiveFaultsAssertionDefaultId);
        finalEquipmentStateAssertionId = NormalizeAssertionId(
            finalEquipmentState?.AssertionId,
            FinalEquipmentStateAssertionDefaultId);
        selectedScenarioProfile = scenarioProfiles.FirstOrDefault(profile =>
            string.Equals(profile.ProfileId, simulation.TestScenarioProfileId, StringComparison.OrdinalIgnoreCase))
            ?? scenarioProfiles[0];
        scenarioSeed = simulation.TestScenarioSeed ?? simulation.Seed;
        scenarioDurationCycles = Math.Clamp(simulation.TestScenarioDurationCycles, 1, 100_000);
        batchRepetitionCount = Math.Clamp(simulation.TestScenarioBatchRepetitions, 1, 100);
        scenarioTargetId = string.IsNullOrWhiteSpace(simulation.TestScenarioTargetId)
            ? null
            : simulation.TestScenarioTargetId.Trim();
        var legacyAxisFault = simulation.TestScenarioAxisFault;
        var fault = simulation.TestScenarioFault;
        isScheduledFaultEnabled = fault?.Enabled ?? legacyAxisFault?.Enabled == true;
        scheduledFaultKind = fault is null
            ? SimulationFaultKind.AxisMotionBlocked
            : ToSimulationFaultKind(fault.Kind);
        var targetId = fault?.TargetId ?? legacyAxisFault?.AxisId;
        scheduledFaultTargetId = string.IsNullOrWhiteSpace(targetId)
            ? null
            : targetId.Trim();
        scheduledFaultForcedValue = fault?.ForcedValue ?? false;
        scheduledFaultInjectTick = Math.Clamp(
            fault?.InjectTick ?? legacyAxisFault?.InjectTick ?? 50,
            0,
            99_999);
        scheduledFaultHoldTicks = Math.Clamp(
            fault?.HoldTicks ?? legacyAxisFault?.HoldTicks ?? 3,
            1,
            100_000);
        var restartSequenceId = fault?.RestartSequenceId ?? legacyAxisFault?.RestartSequenceId;
        restartSequenceAfterFault = !string.IsNullOrWhiteSpace(restartSequenceId);
        recoverySequenceId = restartSequenceAfterFault
            ? restartSequenceId!.Trim()
            : null;
        OnPropertyChanged(nameof(SelectedScenarioProfile));
        OnPropertyChanged(nameof(ScenarioSeed));
        OnPropertyChanged(nameof(ScenarioDurationCycles));
        OnPropertyChanged(nameof(ScenarioTargetId));
        OnPropertyChanged(nameof(BatchRepetitionCount));
        OnPropertyChanged(nameof(IsScheduledFaultEnabled));
        OnPropertyChanged(nameof(ScheduledFaultKind));
        OnPropertyChanged(nameof(ScheduledFaultTargetId));
        OnPropertyChanged(nameof(ScheduledFaultForcedValue));
        OnPropertyChanged(nameof(ScheduledFaultInjectTick));
        OnPropertyChanged(nameof(ScheduledFaultHoldTicks));
        OnPropertyChanged(nameof(RequiresScheduledFaultValue));
        OnPropertyChanged(nameof(RestartSequenceAfterFault));
        OnPropertyChanged(nameof(RecoverySequenceId));
        OnPropertyChanged(nameof(IsScheduledFaultConfigurationValid));
        OnPropertyChanged(nameof(ScheduledFaultSummary));
        OnPropertyChanged(nameof(RequireAutomaticCycleCompleted));
        OnPropertyChanged(nameof(MinimumCompletedCycles));
        OnPropertyChanged(nameof(RequireNoActiveFaults));
        OnPropertyChanged(nameof(RequireFinalEquipmentState));
        OnPropertyChanged(nameof(FinalEquipmentTargetId));
        OnPropertyChanged(nameof(FinalEquipmentExpectedState));
        OnPropertyChanged(nameof(HasConfiguredAssertions));
        OnPropertyChanged(nameof(IsAssertionConfigurationValid));
        OnPropertyChanged(nameof(AssertionSummary));
        OnPropertyChanged(nameof(ScenarioSummary));
        OnPropertyChanged(nameof(WorkspaceScenarioDescription));
        OnPropertyChanged(nameof(ScenarioDisplayName));
        OnPropertyChanged(nameof(ScenarioModeDisplayName));
    }

    public void SaveProjectScenario(SimulationDefinition simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        simulation.TestScenarioProfileId = SelectedScenarioProfile.ProfileId;
        simulation.TestScenarioSeed = ScenarioSeed;
        simulation.TestScenarioDurationCycles = ScenarioDurationCycles;
        simulation.TestScenarioTargetId = ScenarioTargetId;
        simulation.TestScenarioBatchRepetitions = BatchRepetitionCount;
        simulation.TestScenarioAxisFault = null;
        simulation.TestScenarioFault = new TestScenarioFaultDefinition
        {
            Enabled = IsScheduledFaultEnabled,
            Kind = ToProjectFaultKind(ScheduledFaultKind),
            TargetId = ScheduledFaultTargetId,
            ForcedValue = RequiresScheduledFaultValue ? ScheduledFaultForcedValue : null,
            InjectTick = ScheduledFaultInjectTick,
            HoldTicks = ScheduledFaultHoldTicks,
            RestartSequenceId = RestartSequenceAfterFault ? RecoverySequenceId : null
        };
        simulation.TestScenarioAssertions = BuildProjectAssertions();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
    }

    private void LoadScenarioProfile()
    {
        if (isDisposed || string.IsNullOrWhiteSpace(ScenarioProfilePath) || !File.Exists(ScenarioProfilePath))
        {
            return;
        }

        var profile = SimulationScenarioProfile.LoadFromJson(ScenarioProfilePath);
        if (profile is null)
        {
            return;
        }

        var normalized = SimulationScenarioProfile.Normalize(profile);
        var existingIndex = scenarioProfiles
            .Select((item, index) => (item, index))
            .Where(pair => string.Equals(
                pair.item.ProfileId,
                normalized.ProfileId,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < scenarioProfiles.Count)
        {
            scenarioProfiles[existingIndex] = normalized;
        }
        else
        {
            scenarioProfiles.Add(normalized);
        }

        SelectedScenarioProfile = normalized;
    }

    private void ResetScenario()
    {
        var normal = SimulationScenarioProfile.GetBuiltInById("normal");
        selectedScenarioProfile = normal;
        scenarioSeed = normal.Seed;
        scenarioDurationCycles = 200;
        isScheduledFaultEnabled = false;
        scheduledFaultKind = SimulationFaultKind.AxisMotionBlocked;
        scheduledFaultForcedValue = false;
        scheduledFaultInjectTick = 50;
        scheduledFaultHoldTicks = 3;
        restartSequenceAfterFault = true;
        requireAutomaticCycleCompleted = false;
        minimumCompletedCycles = 1;
        requireNoActiveFaults = false;
        requireFinalEquipmentState = false;
        finalEquipmentTargetId = null;
        finalEquipmentExpectedState = string.Empty;
        automaticCycleAssertionId = AutomaticCycleAssertionDefaultId;
        noActiveFaultsAssertionId = NoActiveFaultsAssertionDefaultId;
        finalEquipmentStateAssertionId = FinalEquipmentStateAssertionDefaultId;
        OnPropertyChanged(nameof(SelectedScenarioProfile));
        OnPropertyChanged(nameof(ScenarioSeed));
        OnPropertyChanged(nameof(ScenarioDurationCycles));
        OnPropertyChanged(nameof(IsScheduledFaultEnabled));
        OnPropertyChanged(nameof(ScheduledFaultKind));
        OnPropertyChanged(nameof(ScheduledFaultForcedValue));
        OnPropertyChanged(nameof(ScheduledFaultInjectTick));
        OnPropertyChanged(nameof(ScheduledFaultHoldTicks));
        OnPropertyChanged(nameof(RequiresScheduledFaultValue));
        OnPropertyChanged(nameof(RestartSequenceAfterFault));
        OnPropertyChanged(nameof(IsScheduledFaultConfigurationValid));
        OnPropertyChanged(nameof(ScheduledFaultSummary));
        OnPropertyChanged(nameof(RequireAutomaticCycleCompleted));
        OnPropertyChanged(nameof(MinimumCompletedCycles));
        OnPropertyChanged(nameof(RequireNoActiveFaults));
        OnPropertyChanged(nameof(RequireFinalEquipmentState));
        OnPropertyChanged(nameof(FinalEquipmentTargetId));
        OnPropertyChanged(nameof(FinalEquipmentExpectedState));
        OnPropertyChanged(nameof(HasConfiguredAssertions));
        OnPropertyChanged(nameof(IsAssertionConfigurationValid));
        OnPropertyChanged(nameof(AssertionSummary));
        OnPropertyChanged(nameof(ScenarioSummary));
        OnPropertyChanged(nameof(WorkspaceScenarioDescription));
        OnPropertyChanged(nameof(ScenarioDisplayName));
        OnPropertyChanged(nameof(ScenarioModeDisplayName));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ScenarioSummary));
        OnPropertyChanged(nameof(WorkspaceScenarioDescription));
        OnPropertyChanged(nameof(ScenarioDisplayName));
        OnPropertyChanged(nameof(ScenarioModeDisplayName));
        OnPropertyChanged(nameof(LoadScenarioProfileTooltip));
        OnPropertyChanged(nameof(ResetScenarioTooltip));
        OnPropertyChanged(nameof(ScenarioProfiles));
        OnPropertyChanged(nameof(AvailableScheduledFaultKinds));
        OnPropertyChanged(nameof(ScheduledFaultSummary));
        OnPropertyChanged(nameof(AssertionSummary));
    }

    private void OnScheduledFaultSettingChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(IsScheduledFaultConfigurationValid));
        OnPropertyChanged(nameof(ScheduledFaultSummary));
    }

    private void OnAssertionSettingChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(HasConfiguredAssertions));
        OnPropertyChanged(nameof(IsAssertionConfigurationValid));
        OnPropertyChanged(nameof(AssertionSummary));
    }

    private List<TestScenarioAssertionDefinition> BuildProjectAssertions()
    {
        var assertions = new List<TestScenarioAssertionDefinition>(3);
        if (RequireAutomaticCycleCompleted)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = automaticCycleAssertionId,
                Kind = TestScenarioAssertionKind.AutomaticCycleCompleted,
                MinimumCount = MinimumCompletedCycles
            });
        }
        if (RequireNoActiveFaults)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = noActiveFaultsAssertionId,
                Kind = TestScenarioAssertionKind.NoActiveFaults
            });
        }
        if (RequireFinalEquipmentState)
        {
            assertions.Add(new TestScenarioAssertionDefinition
            {
                AssertionId = finalEquipmentStateAssertionId,
                Kind = TestScenarioAssertionKind.FinalEquipmentState,
                TargetId = FinalEquipmentTargetId,
                ExpectedState = FinalEquipmentExpectedState.Trim()
            });
        }

        return assertions;
    }

    private static string NormalizeAssertionId(string? assertionId, string defaultId) =>
        string.IsNullOrWhiteSpace(assertionId) ? defaultId : assertionId.Trim();

    private static SimulationFaultKind ToSimulationFaultKind(TestScenarioFaultKind kind) => kind switch
    {
        TestScenarioFaultKind.StuckDigitalInput => SimulationFaultKind.StuckDigitalInput,
        TestScenarioFaultKind.CylinderTravelBlocked => SimulationFaultKind.CylinderTravelBlocked,
        _ => SimulationFaultKind.AxisMotionBlocked
    };

    private static TestScenarioFaultKind ToProjectFaultKind(SimulationFaultKind kind) => kind switch
    {
        SimulationFaultKind.StuckDigitalInput => TestScenarioFaultKind.StuckDigitalInput,
        SimulationFaultKind.CylinderTravelBlocked => TestScenarioFaultKind.CylinderTravelBlocked,
        _ => TestScenarioFaultKind.AxisMotionBlocked
    };

    private static string FaultKindDisplayName(SimulationFaultKind kind) => kind switch
    {
        SimulationFaultKind.StuckDigitalInput => OpenVisionLanguageService.T("Fault.StuckDigitalInput"),
        SimulationFaultKind.CylinderTravelBlocked => OpenVisionLanguageService.T("Fault.CylinderTravelBlocked"),
        SimulationFaultKind.AxisMotionBlocked => OpenVisionLanguageService.T("Fault.AxisMotionBlocked"),
        _ => kind.ToString()
    };

    private static string LocalizeScenarioName(SimulationScenarioProfile profile) =>
        OpenVisionLanguageService.T(
            $"Simulation.ScenarioName.{profile.ProfileId}",
            profile.Name,
            profile.Name);

    private static string LocalizeScenarioDescription(SimulationScenarioProfile profile) =>
        OpenVisionLanguageService.T(
            $"Simulation.ScenarioDescription.{profile.ProfileId}",
            profile.Description,
            profile.Description);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
