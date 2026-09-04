using System.Globalization;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Simulation.Axis;
using OpenVisionLab.Machine.Simulation.Commands;
using OpenVisionLab.Machine.Simulation.Engine;

namespace OpenVisionLab.MachineStudio.ViewModel;

internal sealed record AxisCommissioningProjection(
    AxisSnapshot? Snapshot,
    VirtualAxisDefinition? Definition,
    bool HasSelectedAxisStage,
    bool IsRunMode,
    bool IsApplyingProject,
    bool IsValidationBusy,
    bool RuntimeDefinitionDirty,
    bool IsRunning,
    SimulationControlOwner ControlOwner,
    bool AutomaticRunActive,
    bool SequenceRunActive);

/// <summary>
/// Owns the presentation state and command policy for manual axis commissioning.
/// The parent view model supplies the selected runtime snapshot and dispatches
/// commands into the simulation engine; this type owns no project or engine state.
/// </summary>
public sealed class AxisCommissioningViewModel : ViewModelBase
{
    private readonly Func<SimulationCommand, string, Task<SimulationCommandResult>> _dispatch;
    private readonly Action<Exception> _onCommandException;
    private AxisSnapshot? _currentAxis;
    private VirtualAxisDefinition? _currentAxisDefinition;
    private bool _hasSelectedAxisStage;
    private bool _isRunMode;
    private bool _isApplyingProject;
    private bool _isValidationBusy;
    private bool _runtimeDefinitionDirty;
    private bool _isRunning;
    private SimulationControlOwner _controlOwner = SimulationControlOwner.Definition;
    private bool _automaticRunActive;
    private bool _sequenceRunActive;
    private bool _axisJogInteractionActive;
    private string? _axisJogAxisId;
    private Task<SimulationCommandResult>? _axisJogStartTask;
    private string _axisTargetPositionText = string.Empty;
    private string? _axisTargetAxisId;
    private string _axisRelativeDistanceText = "10.000";
    private string _axisCommandVelocityText = "50.000";
    private ICommand? _moveAxisAbsoluteCommand;
    private ICommand? _moveAxisRelativeCommand;
    private ICommand? _moveAxisVelocityCommand;
    private ICommand? _beginAxisJogNegativeCommand;
    private ICommand? _beginAxisJogPositiveCommand;
    private ICommand? _endAxisJogCommand;
    private ICommand? _homeAxisCommand;
    private ICommand? _stopAxisMotionCommand;

    public AxisCommissioningViewModel(
        Func<SimulationCommand, string, Task<SimulationCommandResult>> dispatch,
        Action<Exception> onCommandException)
    {
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _onCommandException = onCommandException ?? throw new ArgumentNullException(nameof(onCommandException));
    }

    public bool HasCurrentAxis => _currentAxis is not null;
    public bool HasSelectedAxisStage => _hasSelectedAxisStage;
    public string CurrentAxisName => _currentAxis?.Name ?? OpenVisionLanguageService.T("Shell.NoAxis");
    public string CurrentAxisStateText => _currentAxis is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : LocalizeRuntimeState(_currentAxis.State.ToString());
    public string CurrentAxisPositionText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.Position:F3} {CurrentAxisUnit}";
    public string CurrentAxisVelocityText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.Velocity:F3} {CurrentAxisUnit}/s";
    public string CurrentAxisHomeText => _currentAxisDefinition is null
        ? "—"
        : $"{_currentAxisDefinition.HomePosition:F3} {CurrentAxisUnit}";
    public string CurrentAxisLimitsText => _currentAxisDefinition is null
        ? "—"
        : $"{_currentAxisDefinition.SoftLimitMin ?? 0:F3} … {_currentAxisDefinition.SoftLimitMax ?? 300:F3} {CurrentAxisUnit}";
    public string CurrentAxisFollowingErrorText => _currentAxis is null
        ? "—"
        : $"{_currentAxis.FollowingError:F3} / {_currentAxis.FollowingErrorLimit:F3} {CurrentAxisUnit}";
    public string CurrentAxisDriveTuningText => _currentAxis is null
        ? "—"
        : string.Format(
            CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Axis.DriveTuningFormat"),
            _currentAxis.MaximumVelocity,
            _currentAxis.Acceleration,
            _currentAxis.Deceleration,
            CurrentAxisUnit);
    public bool IsCurrentAxisDriveAlarmActive => _currentAxis?.DriveAlarmActive == true;
    public string CurrentAxisDriveAlarmText => _currentAxis is null
        ? "—"
        : OpenVisionLanguageService.T(
            IsCurrentAxisDriveAlarmActive ? "Axis.DriveAlarmActive" : "Axis.DriveAlarmReady");
    public string CurrentAxisUnitText => CurrentAxisUnit;
    public string CurrentAxisVelocityUnitText => $"{CurrentAxisUnit}/s";

    public string AxisTargetPositionText
    {
        get => _axisTargetPositionText;
        set
        {
            if (!SetProperty(ref _axisTargetPositionText, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisTargetPositionValid));
            OnPropertyChanged(nameof(HasAxisTargetPositionError));
            OnPropertyChanged(nameof(AxisTargetPositionValidationText));
            OnPropertyChanged(nameof(CanMoveAxisAbsolute));
            InvalidateCommands();
        }
    }

    public bool IsAxisTargetPositionValid => TryGetAxisTargetPosition(out _);
    public bool HasAxisTargetPositionError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisTargetPositionValid;
    public string AxisTargetPositionValidationText
    {
        get
        {
            if (!HasSelectedAxisStage || _currentAxis is null)
            {
                return string.Empty;
            }

            if (!TryParseAxisTargetPosition(out var target))
            {
                return OpenVisionLanguageService.T("Axis.TargetInvalid");
            }

            var minimum = _currentAxisDefinition?.SoftLimitMin ?? 0;
            var maximum = _currentAxisDefinition?.SoftLimitMax ?? 300;
            return target < minimum || target > maximum
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    OpenVisionLanguageService.T("Axis.TargetOutOfRange"),
                    minimum,
                    maximum,
                    CurrentAxisUnit)
                : string.Empty;
        }
    }

    public string AxisRelativeDistanceText
    {
        get => _axisRelativeDistanceText;
        set
        {
            if (!SetProperty(ref _axisRelativeDistanceText, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisRelativeDistanceValid));
            OnPropertyChanged(nameof(HasAxisRelativeDistanceError));
            OnPropertyChanged(nameof(AxisRelativeDistanceValidationText));
            OnPropertyChanged(nameof(CanMoveAxisRelative));
            InvalidateCommands();
        }
    }

    public bool IsAxisRelativeDistanceValid => TryGetAxisRelativeDistance(out _);
    public bool HasAxisRelativeDistanceError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisRelativeDistanceValid;
    public string AxisRelativeDistanceValidationText => HasAxisRelativeDistanceError
        ? OpenVisionLanguageService.T("Axis.RelativeInvalid")
        : string.Empty;

    public string AxisCommandVelocityText
    {
        get => _axisCommandVelocityText;
        set
        {
            if (!SetProperty(ref _axisCommandVelocityText, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(IsAxisCommandVelocityValid));
            OnPropertyChanged(nameof(HasAxisCommandVelocityError));
            OnPropertyChanged(nameof(AxisCommandVelocityValidationText));
            OnPropertyChanged(nameof(CanMoveAxisVelocity));
            InvalidateCommands();
        }
    }

    public bool IsAxisCommandVelocityValid => TryGetAxisCommandVelocity(out _);
    public bool HasAxisCommandVelocityError => HasSelectedAxisStage
        && _currentAxis is not null
        && !IsAxisCommandVelocityValid;
    public string AxisCommandVelocityValidationText
    {
        get
        {
            if (!HasAxisCommandVelocityError)
            {
                return string.Empty;
            }

            if (!TryParseAxisCommandVelocity(out var velocity) || velocity == 0)
            {
                return OpenVisionLanguageService.T("Axis.VelocityInvalid");
            }

            return string.Format(
                CultureInfo.CurrentCulture,
                OpenVisionLanguageService.T("Axis.VelocityOutOfRange"),
                _currentAxisDefinition?.MaxVelocity ?? 0,
                CurrentAxisVelocityUnitText);
        }
    }

    public bool IsCurrentAxisInterlocked => _currentAxis?.State == AxisState.Error;
    public string CurrentAxisInterlockText => _currentAxis is null
        ? "—"
        : OpenVisionLanguageService.T(
            IsCurrentAxisInterlocked ? "Axis.InterlockBlocked" : "Axis.InterlockReady");
    public string AxisCommissioningHintText => _currentAxis is null
        ? OpenVisionLanguageService.T("Axis.NoAxisHint")
        : IsCurrentAxisDriveAlarmActive
            ? OpenVisionLanguageService.T("Axis.ClearDriveAlarmHint")
            : IsCurrentAxisInterlocked
                ? OpenVisionLanguageService.T("Axis.ClearInterlockHint")
                : _controlOwner == SimulationControlOwner.Manual && _isRunning
                    ? OpenVisionLanguageService.T("Axis.ManualVelocityMoveHint")
                    : _isRunning || _automaticRunActive || _sequenceRunActive
                        ? OpenVisionLanguageService.T("Axis.ResetForManualHint")
                        : OpenVisionLanguageService.T("Axis.VelocityMoveStartManualHint");

    public bool CanJogAxis => _axisJogInteractionActive ||
        (CanUseManualAxis && _currentAxis?.State != AxisState.Moving);
    public bool CanMoveAxisAbsolute => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisTargetPosition(out _);
    public bool CanMoveAxisRelative => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisRelativeDistance(out _);
    public bool CanMoveAxisVelocity => CanUseManualAxis
        && !_axisJogInteractionActive
        && _currentAxis?.State != AxisState.Moving
        && TryGetAxisCommandVelocity(out _);

    public ICommand MoveAxisAbsoluteCommand => _moveAxisAbsoluteCommand ??=
        new AsyncRelayCommand(
            async _ =>
            {
                if (_currentAxis is not null && TryGetAxisTargetPosition(out var target))
                {
                    await _dispatch(
                        new MoveAbsoluteCommand(_currentAxis.Id, target),
                        "Axis.ActionMove");
                }
            },
            _ => CanMoveAxisAbsolute,
            _onCommandException,
            useCommandManagerRequery: false);

    public ICommand MoveAxisRelativeCommand => _moveAxisRelativeCommand ??=
        new AsyncRelayCommand(
            async _ =>
            {
                if (_currentAxis is not null && TryGetAxisRelativeDistance(out var distance))
                {
                    await _dispatch(
                        new MoveRelativeCommand(_currentAxis.Id, distance),
                        "Axis.ActionMoveRelative");
                }
            },
            _ => CanMoveAxisRelative,
            _onCommandException,
            useCommandManagerRequery: false);

    public ICommand MoveAxisVelocityCommand => _moveAxisVelocityCommand ??=
        new AsyncRelayCommand(
            async _ =>
            {
                if (_currentAxis is not null && TryGetAxisCommandVelocity(out var velocity))
                {
                    await _dispatch(
                        new MoveVelocityCommand(_currentAxis.Id, velocity),
                        "Axis.ActionMoveVelocity");
                }
            },
            _ => CanMoveAxisVelocity,
            _onCommandException,
            useCommandManagerRequery: false);

    public ICommand BeginAxisJogNegativeCommand => _beginAxisJogNegativeCommand ??= new RelayCommand(
        _ => BeginAxisJog(AxisJogDirection.Negative),
        _ => CanJogAxis,
        useCommandManagerRequery: false);

    public ICommand BeginAxisJogPositiveCommand => _beginAxisJogPositiveCommand ??= new RelayCommand(
        _ => BeginAxisJog(AxisJogDirection.Positive),
        _ => CanJogAxis,
        useCommandManagerRequery: false);

    public ICommand EndAxisJogCommand => _endAxisJogCommand ??= new AsyncRelayCommand(
        async _ => await EndAxisJogAsync(),
        _ => _axisJogInteractionActive,
        _onCommandException,
        useCommandManagerRequery: false);

    public ICommand HomeAxisCommand => _homeAxisCommand ??= new AsyncRelayCommand(
        async _ =>
        {
            if (_currentAxis is not null)
            {
                await _dispatch(
                    new OpenVisionLab.Machine.Simulation.Commands.HomeAxisCommand(_currentAxis.Id),
                    "Axis.ActionHome");
            }
        },
        _ => CanUseManualAxis
            && !_axisJogInteractionActive
            && _currentAxis?.State != AxisState.Moving,
        _onCommandException,
        useCommandManagerRequery: false);

    public ICommand StopAxisMotionCommand => _stopAxisMotionCommand ??= new AsyncRelayCommand(
        async _ => await StopAxisMotionAsync(),
        _ => CanUseManualAxis &&
            (_axisJogInteractionActive || _currentAxis?.State == AxisState.Moving),
        _onCommandException,
        useCommandManagerRequery: false);

    internal void ApplyProjection(
        AxisCommissioningProjection projection,
        bool invalidateCommands = true)
    {
        var axisChanged = !string.Equals(
            _currentAxis?.Id,
            projection.Snapshot?.Id,
            StringComparison.Ordinal);

        _currentAxis = projection.Snapshot;
        _currentAxisDefinition = projection.Definition;
        _hasSelectedAxisStage = projection.HasSelectedAxisStage;
        _isRunMode = projection.IsRunMode;
        _isApplyingProject = projection.IsApplyingProject;
        _isValidationBusy = projection.IsValidationBusy;
        _runtimeDefinitionDirty = projection.RuntimeDefinitionDirty;
        _isRunning = projection.IsRunning;
        _controlOwner = projection.ControlOwner;
        _automaticRunActive = projection.AutomaticRunActive;
        _sequenceRunActive = projection.SequenceRunActive;

        if (axisChanged && _currentAxis is not null)
        {
            _axisTargetAxisId = _currentAxis.Id;
            _axisTargetPositionText = _currentAxis.Position.ToString("F3", CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(AxisTargetPositionText));
        }

        NotifyProjectionChanged(invalidateCommands);
    }

    internal void InvalidateCommands()
    {
        RaiseCanExecuteChanged(_moveAxisAbsoluteCommand);
        RaiseCanExecuteChanged(_moveAxisRelativeCommand);
        RaiseCanExecuteChanged(_moveAxisVelocityCommand);
        RaiseCanExecuteChanged(_beginAxisJogNegativeCommand);
        RaiseCanExecuteChanged(_beginAxisJogPositiveCommand);
        RaiseCanExecuteChanged(_endAxisJogCommand);
        RaiseCanExecuteChanged(_homeAxisCommand);
        RaiseCanExecuteChanged(_stopAxisMotionCommand);
    }

    internal bool BeginAxisJog(AxisJogDirection direction)
    {
        if (!CanJogAxis || _axisJogInteractionActive || _currentAxis is null)
        {
            return false;
        }

        _axisJogInteractionActive = true;
        _axisJogAxisId = _currentAxis.Id;
        _axisJogStartTask = _dispatch(
            new JogAxisCommand(_axisJogAxisId, direction),
            direction == AxisJogDirection.Positive
                ? "Axis.ActionJogPositive"
                : "Axis.ActionJogNegative");
        NotifyProjectionChanged();
        return true;
    }

    internal Task EndAxisJogAsync()
    {
        if (!_axisJogInteractionActive || _axisJogAxisId is null || _axisJogStartTask is null)
        {
            return Task.CompletedTask;
        }

        _axisJogInteractionActive = false;
        var axisId = _axisJogAxisId;
        var startTask = _axisJogStartTask;
        _axisJogAxisId = null;
        _axisJogStartTask = null;
        NotifyProjectionChanged();
        return StopAxisJogAfterStartAsync(axisId, startTask);
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(CurrentAxisName));
        OnPropertyChanged(nameof(CurrentAxisStateText));
        OnPropertyChanged(nameof(CurrentAxisPositionText));
        OnPropertyChanged(nameof(CurrentAxisVelocityText));
        OnPropertyChanged(nameof(CurrentAxisHomeText));
        OnPropertyChanged(nameof(CurrentAxisLimitsText));
        OnPropertyChanged(nameof(CurrentAxisFollowingErrorText));
        OnPropertyChanged(nameof(CurrentAxisDriveTuningText));
        OnPropertyChanged(nameof(CurrentAxisDriveAlarmText));
        OnPropertyChanged(nameof(CurrentAxisUnitText));
        OnPropertyChanged(nameof(CurrentAxisVelocityUnitText));
        OnPropertyChanged(nameof(AxisTargetPositionValidationText));
        OnPropertyChanged(nameof(AxisRelativeDistanceValidationText));
        OnPropertyChanged(nameof(AxisCommandVelocityValidationText));
        OnPropertyChanged(nameof(CurrentAxisInterlockText));
        OnPropertyChanged(nameof(AxisCommissioningHintText));
    }

    private bool CanUseManualAxis => _isRunMode
        && !_isApplyingProject
        && !_isValidationBusy
        && !_runtimeDefinitionDirty
        && _isRunning
        && _controlOwner == SimulationControlOwner.Manual
        && _hasSelectedAxisStage
        && !IsCurrentAxisInterlocked
        && _currentAxis is not null;

    private string CurrentAxisUnit => string.IsNullOrWhiteSpace(_currentAxisDefinition?.Unit)
        ? "mm"
        : _currentAxisDefinition.Unit;

    private bool TryGetAxisTargetPosition(out double target)
    {
        target = default;
        if (_currentAxis is null || _currentAxisDefinition is null || !TryParseAxisTargetPosition(out target))
        {
            return false;
        }

        var minimum = _currentAxisDefinition.SoftLimitMin ?? 0;
        var maximum = _currentAxisDefinition.SoftLimitMax ?? 300;
        return target >= minimum && target <= maximum;
    }

    private bool TryParseAxisTargetPosition(out double target) =>
        (double.TryParse(_axisTargetPositionText, NumberStyles.Float, CultureInfo.CurrentCulture, out target)
         || double.TryParse(_axisTargetPositionText, NumberStyles.Float, CultureInfo.InvariantCulture, out target))
        && double.IsFinite(target);

    private bool TryGetAxisRelativeDistance(out double distance) =>
        (double.TryParse(_axisRelativeDistanceText, NumberStyles.Float, CultureInfo.CurrentCulture, out distance)
         || double.TryParse(_axisRelativeDistanceText, NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
        && double.IsFinite(distance)
        && distance != 0;

    private bool TryGetAxisCommandVelocity(out double velocity)
    {
        velocity = default;
        return _currentAxisDefinition is not null
            && TryParseAxisCommandVelocity(out velocity)
            && velocity != 0
            && Math.Abs(velocity) <= _currentAxisDefinition.MaxVelocity;
    }

    private bool TryParseAxisCommandVelocity(out double velocity) =>
        (double.TryParse(_axisCommandVelocityText, NumberStyles.Float, CultureInfo.CurrentCulture, out velocity)
         || double.TryParse(_axisCommandVelocityText, NumberStyles.Float, CultureInfo.InvariantCulture, out velocity))
        && double.IsFinite(velocity);

    private async Task StopAxisJogAfterStartAsync(
        string axisId,
        Task<SimulationCommandResult> startTask)
    {
        var startResult = await startTask;
        if (startResult.IsAccepted)
        {
            await _dispatch(
                new StopAxisCommand(axisId),
                "Axis.ActionStop");
        }
    }

    private Task StopAxisMotionAsync()
    {
        if (_axisJogInteractionActive)
        {
            return EndAxisJogAsync();
        }

        return _currentAxis is null
            ? Task.CompletedTask
            : _dispatch(
                new StopAxisCommand(_currentAxis.Id),
                "Axis.ActionStop");
    }

    private void NotifyProjectionChanged(bool invalidateCommands = true)
    {
        OnPropertyChanged(nameof(HasCurrentAxis));
        OnPropertyChanged(nameof(HasSelectedAxisStage));
        OnPropertyChanged(nameof(CurrentAxisName));
        OnPropertyChanged(nameof(CurrentAxisStateText));
        OnPropertyChanged(nameof(CurrentAxisPositionText));
        OnPropertyChanged(nameof(CurrentAxisVelocityText));
        OnPropertyChanged(nameof(CurrentAxisHomeText));
        OnPropertyChanged(nameof(CurrentAxisLimitsText));
        OnPropertyChanged(nameof(CurrentAxisFollowingErrorText));
        OnPropertyChanged(nameof(CurrentAxisDriveTuningText));
        OnPropertyChanged(nameof(IsCurrentAxisDriveAlarmActive));
        OnPropertyChanged(nameof(CurrentAxisDriveAlarmText));
        OnPropertyChanged(nameof(CurrentAxisUnitText));
        OnPropertyChanged(nameof(CurrentAxisVelocityUnitText));
        OnPropertyChanged(nameof(IsAxisTargetPositionValid));
        OnPropertyChanged(nameof(HasAxisTargetPositionError));
        OnPropertyChanged(nameof(AxisTargetPositionValidationText));
        OnPropertyChanged(nameof(IsAxisRelativeDistanceValid));
        OnPropertyChanged(nameof(HasAxisRelativeDistanceError));
        OnPropertyChanged(nameof(AxisRelativeDistanceValidationText));
        OnPropertyChanged(nameof(IsAxisCommandVelocityValid));
        OnPropertyChanged(nameof(HasAxisCommandVelocityError));
        OnPropertyChanged(nameof(AxisCommandVelocityValidationText));
        OnPropertyChanged(nameof(IsCurrentAxisInterlocked));
        OnPropertyChanged(nameof(CurrentAxisInterlockText));
        OnPropertyChanged(nameof(AxisCommissioningHintText));
        OnPropertyChanged(nameof(CanMoveAxisAbsolute));
        OnPropertyChanged(nameof(CanMoveAxisRelative));
        OnPropertyChanged(nameof(CanMoveAxisVelocity));
        OnPropertyChanged(nameof(CanJogAxis));
        if (invalidateCommands)
        {
            InvalidateCommands();
        }
    }

    private static string LocalizeRuntimeState(string state) =>
        OpenVisionLanguageService.T($"Equipment.State.{state}", state, state);

    private static void RaiseCanExecuteChanged(ICommand? command)
    {
        switch (command)
        {
            case AsyncRelayCommand asyncCommand:
                asyncCommand.RaiseCanExecuteChanged();
                break;
            case RelayCommand relayCommand:
                relayCommand.RaiseCanExecuteChanged();
                break;
        }
    }
}
