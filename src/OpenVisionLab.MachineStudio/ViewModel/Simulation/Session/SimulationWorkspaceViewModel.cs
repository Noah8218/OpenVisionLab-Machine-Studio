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
    private readonly RelayCommand loadScenarioProfileCommand;
    private readonly RelayCommand resetScenarioCommand;
    private readonly SimulationScenarioProjectMapper scenarioProjectMapper = new();
    private readonly ObservableCollection<SimulationScenarioProfile> scenarioProfiles = [];
    private HashSet<string> availableFinalEquipmentTargetIds = new(StringComparer.Ordinal);
    private SimulationScenarioProfile? selectedScenarioProfile;
    private string scenarioProfilePath = string.Empty;
    private string? scenarioProfileLoadError;
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
    private string automaticCycleAssertionId = SimulationScenarioProjectMapper.AutomaticCycleAssertionDefaultId;
    private string noActiveFaultsAssertionId = SimulationScenarioProjectMapper.NoActiveFaultsAssertionDefaultId;
    private string finalEquipmentStateAssertionId = SimulationScenarioProjectMapper.FinalEquipmentStateAssertionDefaultId;
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
            scenarioProfileLoadError = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScenarioProfileLoadError));
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

    public bool IsScheduledFaultConfigurationValid =>
        SimulationScenarioProjectMapper.IsScheduledFaultConfigurationValid(CreateProjectSnapshot());

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
        : ScenarioProfileLoadError is not null
            ? ScenarioProfileLoadError
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Simulation.LoadJsonPath"),
                ScenarioProfilePath);

    public string? ScenarioProfileLoadError => scenarioProfileLoadError;

    public string ResetScenarioTooltip => OpenVisionLanguageService.T("Simulation.ResetScenarioHint");

    public DeterministicConditionScenarioProfile BuildEngineProfile(string targetId) =>
        scenarioProjectMapper.BuildEngineProfile(CreateProjectSnapshot(), targetId);

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
        ApplyProjectSnapshot(scenarioProjectMapper.Load(simulation, scenarioProfiles));
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
        scenarioProjectMapper.Save(simulation, CreateProjectSnapshot());
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
        if (isDisposed)
        {
            return;
        }

        if (!SimulationScenarioProfile.TryLoadFromJson(
                ScenarioProfilePath,
                out SimulationScenarioProfile? profile,
                out string? error)
            || profile is null)
        {
            scenarioProfileLoadError = error ?? "The scenario profile is invalid.";
            OnPropertyChanged(nameof(ScenarioProfileLoadError));
            OnPropertyChanged(nameof(LoadScenarioProfileTooltip));
            return;
        }

        scenarioProfileLoadError = null;
        OnPropertyChanged(nameof(ScenarioProfileLoadError));
        OnPropertyChanged(nameof(LoadScenarioProfileTooltip));
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
        scenarioProfileLoadError = null;
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
        automaticCycleAssertionId = SimulationScenarioProjectMapper.AutomaticCycleAssertionDefaultId;
        noActiveFaultsAssertionId = SimulationScenarioProjectMapper.NoActiveFaultsAssertionDefaultId;
        finalEquipmentStateAssertionId = SimulationScenarioProjectMapper.FinalEquipmentStateAssertionDefaultId;
        OnPropertyChanged(nameof(SelectedScenarioProfile));
        OnPropertyChanged(nameof(ScenarioProfileLoadError));
        OnPropertyChanged(nameof(LoadScenarioProfileTooltip));
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

    private SimulationScenarioProjectSnapshot CreateProjectSnapshot() =>
        new(
            SelectedScenarioProfile,
            scenarioSeed,
            scenarioDurationCycles,
            batchRepetitionCount,
            scenarioTargetId,
            isScheduledFaultEnabled,
            scheduledFaultKind,
            scheduledFaultTargetId,
            scheduledFaultForcedValue,
            scheduledFaultInjectTick,
            scheduledFaultHoldTicks,
            restartSequenceAfterFault,
            recoverySequenceId,
            requireAutomaticCycleCompleted,
            minimumCompletedCycles,
            requireNoActiveFaults,
            requireFinalEquipmentState,
            finalEquipmentTargetId,
            finalEquipmentExpectedState,
            automaticCycleAssertionId,
            noActiveFaultsAssertionId,
            finalEquipmentStateAssertionId);

    private void ApplyProjectSnapshot(SimulationScenarioProjectSnapshot snapshot)
    {
        requireAutomaticCycleCompleted = snapshot.RequireAutomaticCycleCompleted;
        minimumCompletedCycles = snapshot.MinimumCompletedCycles;
        requireNoActiveFaults = snapshot.RequireNoActiveFaults;
        requireFinalEquipmentState = snapshot.RequireFinalEquipmentState;
        finalEquipmentTargetId = snapshot.FinalEquipmentTargetId;
        finalEquipmentExpectedState = snapshot.FinalEquipmentExpectedState;
        automaticCycleAssertionId = snapshot.AutomaticCycleAssertionId;
        noActiveFaultsAssertionId = snapshot.NoActiveFaultsAssertionId;
        finalEquipmentStateAssertionId = snapshot.FinalEquipmentStateAssertionId;
        selectedScenarioProfile = snapshot.SelectedScenarioProfile;
        scenarioSeed = snapshot.ScenarioSeed;
        scenarioDurationCycles = snapshot.ScenarioDurationCycles;
        batchRepetitionCount = snapshot.BatchRepetitionCount;
        scenarioTargetId = snapshot.ScenarioTargetId;
        isScheduledFaultEnabled = snapshot.IsScheduledFaultEnabled;
        scheduledFaultKind = snapshot.ScheduledFaultKind;
        scheduledFaultTargetId = snapshot.ScheduledFaultTargetId;
        scheduledFaultForcedValue = snapshot.ScheduledFaultForcedValue;
        scheduledFaultInjectTick = snapshot.ScheduledFaultInjectTick;
        scheduledFaultHoldTicks = snapshot.ScheduledFaultHoldTicks;
        restartSequenceAfterFault = snapshot.RestartSequenceAfterFault;
        recoverySequenceId = snapshot.RecoverySequenceId;
    }

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
