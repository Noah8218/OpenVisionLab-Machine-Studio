using System.Collections.ObjectModel;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Axes;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Simulation.Axis;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class MultiAxisCommissioningRecipeEditorViewModel : ViewModelBase
{
    private readonly Action _definitionChanged;
    private MachineProjectDocument _project = new();
    private bool _hasValidationErrors;
    private string _validationMessage = string.Empty;
    private string _runtimeStatusText = string.Empty;

    public MultiAxisCommissioningRecipeEditorViewModel(Action definitionChanged)
    {
        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));
        CreateRecipeCommand = new RelayCommand(_ => CreateRecipe(), _ => CanCreateRecipe);
        DeleteRecipeCommand = new RelayCommand(_ => DeleteRecipe(), _ => IsConfigured);
        AddTargetCommand = new RelayCommand(_ => AddTarget(), _ => CanAddTarget);
        Validate();
    }

    public ObservableCollection<MultiAxisCommissioningTargetEditorViewModel> Targets { get; } = new();
    public IReadOnlyList<VirtualAxisDefinition> AvailableAxes => _project.Axes;
    public ICommand CreateRecipeCommand { get; }
    public ICommand DeleteRecipeCommand { get; }
    public ICommand AddTargetCommand { get; }
    public bool IsConfigured => _project.MultiAxisCommissioningRecipe is not null;
    public bool CanCreateRecipe => !IsConfigured && _project.Axes.Count >= 2;
    public bool CanAddTarget => IsConfigured && Targets.Count < _project.Axes.Count;
    public bool IsValid => IsConfigured && !HasValidationErrors;

    public string Name
    {
        get => _project.MultiAxisCommissioningRecipe?.Name ?? string.Empty;
        set
        {
            if (_project.MultiAxisCommissioningRecipe is not { } recipe || recipe.Name == value)
            {
                return;
            }

            recipe.Name = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public int ValidationRepetitions
    {
        get => _project.MultiAxisCommissioningRecipe?.ValidationRepetitions ?? 3;
        set
        {
            if (_project.MultiAxisCommissioningRecipe is not { } recipe
                || recipe.ValidationRepetitions == value)
            {
                return;
            }

            recipe.ValidationRepetitions = value;
            OnPropertyChanged();
            Changed();
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

    public string RuntimeStatusText
    {
        get => _runtimeStatusText;
        private set => SetProperty(ref _runtimeStatusText, value);
    }

    public void Load(MachineProjectDocument project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        RebuildTargets();
        RaiseConfigurationChanged();
        Validate();
        ApplyAxisSnapshots(Array.Empty<AxisSnapshot>());
    }

    public void ApplyAxisSnapshots(IReadOnlyList<AxisSnapshot> axes)
    {
        foreach (var target in Targets)
        {
            target.ApplySnapshot(axes.FirstOrDefault(axis =>
                string.Equals(axis.Id, target.AxisId, StringComparison.Ordinal)));
        }

        RuntimeStatusText = !IsConfigured
            ? OpenVisionLanguageService.T("Axis.RecipeNotConfigured")
            : HasValidationErrors
                ? OpenVisionLanguageService.T("Axis.RecipeInvalid")
                : Targets.Any(target => target.RuntimeState == AxisState.Moving)
                    ? OpenVisionLanguageService.T("Axis.RecipeMoving")
                    : Targets.All(target => target.IsAtTarget)
                        ? OpenVisionLanguageService.T("Axis.RecipeAtTarget")
                        : OpenVisionLanguageService.T("Axis.RecipeReady");
    }

    public void RefreshLocalization()
    {
        Validate();
        ApplyAxisSnapshots(Targets
            .Where(target => target.RuntimeSnapshot is not null)
            .Select(target => target.RuntimeSnapshot!)
            .ToArray());
    }

    private void CreateRecipe()
    {
        if (!CanCreateRecipe)
        {
            return;
        }

        _project.MultiAxisCommissioningRecipe = new MultiAxisCommissioningRecipeDefinition
        {
            Targets = _project.Axes.Take(2).Select(axis =>
                new MultiAxisCommissioningTargetDefinition
                {
                    AxisId = axis.Id,
                    TargetPosition = axis.HomePosition
                }).ToList()
        };
        RebuildTargets();
        RaiseConfigurationChanged();
        Changed();
    }

    private void DeleteRecipe()
    {
        _project.MultiAxisCommissioningRecipe = null;
        RebuildTargets();
        RaiseConfigurationChanged();
        Changed();
    }

    private void AddTarget()
    {
        var recipe = _project.MultiAxisCommissioningRecipe;
        var axis = _project.Axes.FirstOrDefault(candidate => Targets.All(target =>
            !string.Equals(target.AxisId, candidate.Id, StringComparison.Ordinal)));
        if (recipe is null || axis is null)
        {
            return;
        }

        recipe.Targets.Add(new MultiAxisCommissioningTargetDefinition
        {
            AxisId = axis.Id,
            TargetPosition = axis.HomePosition
        });
        RebuildTargets();
        RaiseConfigurationChanged();
        Changed();
    }

    private void MoveTarget(MultiAxisCommissioningTargetEditorViewModel target, int offset)
    {
        var recipe = _project.MultiAxisCommissioningRecipe;
        if (recipe is null)
        {
            return;
        }

        var index = Targets.IndexOf(target);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= Targets.Count)
        {
            return;
        }

        var definition = recipe.Targets[index];
        recipe.Targets.RemoveAt(index);
        recipe.Targets.Insert(destination, definition);
        RebuildTargets();
        Changed();
    }

    private void RemoveTarget(MultiAxisCommissioningTargetEditorViewModel target)
    {
        var recipe = _project.MultiAxisCommissioningRecipe;
        var index = Targets.IndexOf(target);
        if (recipe is null || index < 0)
        {
            return;
        }

        recipe.Targets.RemoveAt(index);
        RebuildTargets();
        RaiseConfigurationChanged();
        Changed();
    }

    private void RebuildTargets()
    {
        Targets.Clear();
        if (_project.MultiAxisCommissioningRecipe is not { } recipe)
        {
            return;
        }

        foreach (var target in recipe.Targets)
        {
            Targets.Add(new MultiAxisCommissioningTargetEditorViewModel(
                target,
                _project.Axes,
                Changed,
                item => MoveTarget(item, -1),
                item => MoveTarget(item, 1),
                RemoveTarget));
        }
    }

    private void Changed()
    {
        Validate();
        ApplyAxisSnapshots(Targets
            .Where(target => target.RuntimeSnapshot is not null)
            .Select(target => target.RuntimeSnapshot!)
            .ToArray());
        _definitionChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseConfigurationChanged()
    {
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(CanCreateRecipe));
        OnPropertyChanged(nameof(CanAddTarget));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ValidationRepetitions));
    }

    private void Validate()
    {
        var recipe = _project.MultiAxisCommissioningRecipe;
        string message;
        if (recipe is null)
        {
            message = OpenVisionLanguageService.T("Axis.RecipeNotConfiguredHint");
            HasValidationErrors = false;
        }
        else if (string.IsNullOrWhiteSpace(recipe.Name))
        {
            message = OpenVisionLanguageService.T("Axis.RecipeNameRequired");
            HasValidationErrors = true;
        }
        else if (recipe.Targets.Count < 2)
        {
            message = OpenVisionLanguageService.T("Axis.RecipeTwoAxesRequired");
            HasValidationErrors = true;
        }
        else if (recipe.ValidationRepetitions is < 2 or > 100)
        {
            message = OpenVisionLanguageService.T("Axis.RecipeRepetitionsInvalid");
            HasValidationErrors = true;
        }
        else if (recipe.Targets.Select(target => target.AxisId).Distinct(StringComparer.Ordinal).Count()
                 != recipe.Targets.Count)
        {
            message = OpenVisionLanguageService.T("Axis.RecipeDuplicateAxis");
            HasValidationErrors = true;
        }
        else if (recipe.Targets.Any(target => !IsTargetValid(target)))
        {
            message = OpenVisionLanguageService.T("Axis.RecipeTargetInvalid");
            HasValidationErrors = true;
        }
        else
        {
            message = OpenVisionLanguageService.T("Axis.RecipeValid");
            HasValidationErrors = false;
        }

        ValidationMessage = message;
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(CanAddTarget));
    }

    private bool IsTargetValid(MultiAxisCommissioningTargetDefinition target)
    {
        var axis = _project.Axes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, target.AxisId, StringComparison.Ordinal));
        return axis is not null
            && double.IsFinite(target.TargetPosition)
            && target.TargetPosition >= (axis.SoftLimitMin ?? 0)
            && target.TargetPosition <= (axis.SoftLimitMax ?? 300);
    }
}

public sealed class MultiAxisCommissioningTargetEditorViewModel : ViewModelBase
{
    private readonly MultiAxisCommissioningTargetDefinition _target;
    private readonly Action _changed;
    private AxisSnapshot? _runtimeSnapshot;

    public MultiAxisCommissioningTargetEditorViewModel(
        MultiAxisCommissioningTargetDefinition target,
        IReadOnlyList<VirtualAxisDefinition> availableAxes,
        Action changed,
        Action<MultiAxisCommissioningTargetEditorViewModel> moveUp,
        Action<MultiAxisCommissioningTargetEditorViewModel> moveDown,
        Action<MultiAxisCommissioningTargetEditorViewModel> remove)
    {
        _target = target;
        AvailableAxes = availableAxes;
        _changed = changed;
        MoveUpCommand = new RelayCommand(_ => moveUp(this));
        MoveDownCommand = new RelayCommand(_ => moveDown(this));
        RemoveCommand = new RelayCommand(_ => remove(this));
    }

    public IReadOnlyList<VirtualAxisDefinition> AvailableAxes { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }
    internal AxisSnapshot? RuntimeSnapshot => _runtimeSnapshot;
    public AxisState RuntimeState => _runtimeSnapshot?.State ?? AxisState.Idle;

    public string AxisId
    {
        get => _target.AxisId;
        set
        {
            if (_target.AxisId == value)
            {
                return;
            }

            _target.AxisId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AxisName));
            OnPropertyChanged(nameof(Unit));
            _changed();
        }
    }

    public string AxisName => AvailableAxes.FirstOrDefault(axis => axis.Id == AxisId)?.Name ?? AxisId;
    public string Unit => AvailableAxes.FirstOrDefault(axis => axis.Id == AxisId)?.Unit ?? string.Empty;

    public double TargetPosition
    {
        get => _target.TargetPosition;
        set
        {
            if (_target.TargetPosition.Equals(value))
            {
                return;
            }

            _target.TargetPosition = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetText));
            OnPropertyChanged(nameof(IsAtTarget));
            _changed();
        }
    }

    public string TargetText => $"{TargetPosition:F3} {Unit}";
    public string CurrentPositionText => _runtimeSnapshot is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : $"{_runtimeSnapshot.Position:F3} {Unit}";
    public string StateText => _runtimeSnapshot is null
        ? OpenVisionLanguageService.T("Shell.Unavailable")
        : OpenVisionLanguageService.T(
            $"Properties.Value.{_runtimeSnapshot.State}",
            _runtimeSnapshot.State.ToString(),
            _runtimeSnapshot.State.ToString());
    public bool IsAtTarget => _runtimeSnapshot is not null
        && Math.Abs(_runtimeSnapshot.Position - TargetPosition) <= 1e-9
        && _runtimeSnapshot.State != AxisState.Moving;

    internal void ApplySnapshot(AxisSnapshot? snapshot)
    {
        _runtimeSnapshot = snapshot;
        OnPropertyChanged(nameof(RuntimeState));
        OnPropertyChanged(nameof(CurrentPositionText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsAtTarget));
    }
}
