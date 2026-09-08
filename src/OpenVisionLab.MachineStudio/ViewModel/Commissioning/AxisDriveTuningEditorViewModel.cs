using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Edits one project-owned axis definition. Runtime motion remains owned by the
/// simulation axis compiled from that definition.
/// </summary>
public sealed class AxisDriveTuningEditorViewModel : ViewModelBase
{
    private readonly VirtualAxisDefinition _axis;
    private readonly Action _definitionChanged;
    private bool _hasValidationErrors;
    private string _validationMessage = string.Empty;
    private ICommand? _resetDriveDefaultsCommand;

    public AxisDriveTuningEditorViewModel(
        VirtualAxisDefinition axis,
        Action definitionChanged)
    {
        _axis = axis ?? throw new ArgumentNullException(nameof(axis));
        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));
        Validate();
    }

    public string Id => _axis.Id;
    public string Name => _axis.Name;
    public string Unit => _axis.Unit;

    public double HomePosition
    {
        get => _axis.HomePosition;
        set
        {
            var previous = _axis.HomePosition;
            Update(
                () => _axis.HomePosition = value,
                () => _axis.HomePosition = previous,
                nameof(HomePosition));
        }
    }

    public double SoftLimitMin
    {
        get => _axis.SoftLimitMin ?? 0;
        set
        {
            var previous = _axis.SoftLimitMin;
            Update(
                () => _axis.SoftLimitMin = value,
                () => _axis.SoftLimitMin = previous,
                nameof(SoftLimitMin));
        }
    }

    public double SoftLimitMax
    {
        get => _axis.SoftLimitMax ?? 300;
        set
        {
            var previous = _axis.SoftLimitMax;
            Update(
                () => _axis.SoftLimitMax = value,
                () => _axis.SoftLimitMax = previous,
                nameof(SoftLimitMax));
        }
    }

    public double MaxVelocity
    {
        get => _axis.MaxVelocity;
        set => UpdateValue(
            _axis.MaxVelocity,
            value,
            candidate => _axis.MaxVelocity = candidate,
            nameof(MaxVelocity));
    }

    public double MaxAcceleration
    {
        get => _axis.MaxAcceleration;
        set
        {
            var previous = _axis.MaxAcceleration;
            Update(
                () => _axis.MaxAcceleration = value,
                () => _axis.MaxAcceleration = previous,
                nameof(MaxAcceleration));
            if (_axis.MaxDeceleration is null)
            {
                OnPropertyChanged(nameof(MaxDeceleration));
            }
        }
    }

    public double MaxDeceleration
    {
        get => _axis.MaxDeceleration ?? _axis.MaxAcceleration;
        set
        {
            var previous = _axis.MaxDeceleration;
            Update(
                () => _axis.MaxDeceleration = value,
                () => _axis.MaxDeceleration = previous,
                nameof(MaxDeceleration));
        }
    }

    public double FollowingErrorLimit
    {
        get => _axis.FollowingErrorLimit ?? VirtualAxisDefinition.DefaultFollowingErrorLimit;
        set
        {
            var previous = _axis.FollowingErrorLimit;
            Update(
                () => _axis.FollowingErrorLimit = value,
                () => _axis.FollowingErrorLimit = previous,
                nameof(FollowingErrorLimit));
        }
    }

    public bool HasValidationErrors
    {
        get => _hasValidationErrors;
        private set => SetProperty(ref _hasValidationErrors, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public ICommand ResetDriveDefaultsCommand => _resetDriveDefaultsCommand ??= new RelayCommand(_ =>
    {
        _axis.MaxVelocity = VirtualAxisDefinition.DefaultMaxVelocity;
        _axis.MaxAcceleration = VirtualAxisDefinition.DefaultMaxAcceleration;
        _axis.MaxDeceleration = null;
        _axis.FollowingErrorLimit = null;
        OnPropertyChanged(nameof(MaxVelocity));
        OnPropertyChanged(nameof(MaxAcceleration));
        OnPropertyChanged(nameof(MaxDeceleration));
        OnPropertyChanged(nameof(FollowingErrorLimit));
        Validate();
        _definitionChanged();
    });

    public void RefreshLocalization() => Validate();

    private void UpdateValue(
        double previous,
        double value,
        Action<double> update,
        string propertyName) =>
        Update(() => update(value), () => update(previous), propertyName);

    private void Update(Action update, Action revert, string propertyName)
    {
        update();
        if (!TryValidate(out var message))
        {
            revert();
            HasValidationErrors = true;
            ValidationMessage = message;
            OnPropertyChanged(propertyName);
            return;
        }

        OnPropertyChanged(propertyName);
        Validate();
        _definitionChanged();
    }

    private void Validate()
    {
        HasValidationErrors = !TryValidate(out var message);
        ValidationMessage = message;
    }

    private bool TryValidate(out string message)
    {
        if (!double.IsFinite(SoftLimitMin) ||
            !double.IsFinite(SoftLimitMax) ||
            SoftLimitMin > SoftLimitMax ||
            !double.IsFinite(HomePosition) ||
            HomePosition < SoftLimitMin ||
            HomePosition > SoftLimitMax)
        {
            message = OpenVisionLanguageService.T("Axis.TuningTravelInvalid");
            return false;
        }

        if (!double.IsFinite(MaxVelocity) || MaxVelocity <= 0 ||
            !double.IsFinite(MaxAcceleration) || MaxAcceleration <= 0 ||
            !double.IsFinite(MaxDeceleration) || MaxDeceleration <= 0 ||
            !double.IsFinite(FollowingErrorLimit) || FollowingErrorLimit <= 0)
        {
            message = OpenVisionLanguageService.T("Axis.TuningDriveInvalid");
            return false;
        }

        message = OpenVisionLanguageService.T("Axis.TuningValid");
        return true;
    }
}
