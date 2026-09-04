using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Sequences;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns selected-camera state and the recipe-selection policy used by the
/// shell and camera-related workflows.
/// </summary>
internal sealed class CameraSelectionWorkflow
{
    private const string SelectedCameraIdPropertyName = "SelectedCameraId";
    private const string SelectedVirtualCameraPropertyName = "SelectedVirtualCamera";
    private const string CurrentCameraRecipesPropertyName = "CurrentCameraRecipes";
    private const string SelectedCameraRecipePropertyName = "SelectedCameraRecipe";

    private readonly Func<MachineProjectDocument> _projectAccessor;
    private readonly Action<string?> _selectCameraInEditor;
    private readonly Action _refreshVisionEvidenceContext;
    private readonly Action _applyCurrentSnapshot;
    private readonly Action<string> _notifyPropertyChanged;
    private readonly Action _notifyCameraCommissioningChanged;
    private string? _selectedCameraId;
    private string? _selectedCameraRecipe;

    internal CameraSelectionWorkflow(
        Func<MachineProjectDocument> projectAccessor,
        Action<string?> selectCameraInEditor,
        Action refreshVisionEvidenceContext,
        Action applyCurrentSnapshot,
        Action<string> notifyPropertyChanged,
        Action notifyCameraCommissioningChanged)
    {
        ArgumentNullException.ThrowIfNull(projectAccessor);
        ArgumentNullException.ThrowIfNull(selectCameraInEditor);
        ArgumentNullException.ThrowIfNull(refreshVisionEvidenceContext);
        ArgumentNullException.ThrowIfNull(applyCurrentSnapshot);
        ArgumentNullException.ThrowIfNull(notifyPropertyChanged);
        ArgumentNullException.ThrowIfNull(notifyCameraCommissioningChanged);

        _projectAccessor = projectAccessor;
        _selectCameraInEditor = selectCameraInEditor;
        _refreshVisionEvidenceContext = refreshVisionEvidenceContext;
        _applyCurrentSnapshot = applyCurrentSnapshot;
        _notifyPropertyChanged = notifyPropertyChanged;
        _notifyCameraCommissioningChanged = notifyCameraCommissioningChanged;
    }

    internal DeviceDefinition? SelectedVirtualCamera =>
        _projectAccessor().Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera
            && string.Equals(device.Id, _selectedCameraId, StringComparison.Ordinal));

    internal string? SelectedCameraId => _selectedCameraId;

    internal IReadOnlyList<string> CurrentCameraRecipes =>
        GetCameraRecipes(_projectAccessor(), _selectedCameraId);

    internal string? SelectedCameraRecipe => _selectedCameraRecipe;

    internal DeviceDefinition? GetSelectedDefinition(string? fallbackCameraId) =>
        _projectAccessor().Devices.FirstOrDefault(device =>
            device.Kind == DeviceKind.Camera
            && string.Equals(
                device.Id,
                _selectedCameraId ?? fallbackCameraId,
                StringComparison.Ordinal));

    internal void EnsureSelectionFor(MachineProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, _selectedCameraId, StringComparison.Ordinal)))
        {
            _selectedCameraId = project.Devices.FirstOrDefault(device =>
                device.Kind == DeviceKind.Camera)?.Id;
        }

        EnsureSelectedCameraRecipe(project);
    }

    internal void SelectVirtualCamera(string? cameraId)
    {
        var project = _projectAccessor();
        if (cameraId is null && project.Devices.Any(device => device.Kind == DeviceKind.Camera))
        {
            return;
        }

        if (cameraId is not null && !project.Devices.Any(device =>
                device.Kind == DeviceKind.Camera
                && string.Equals(device.Id, cameraId, StringComparison.Ordinal)))
        {
            return;
        }

        if (string.Equals(_selectedCameraId, cameraId, StringComparison.Ordinal))
        {
            return;
        }

        _selectedCameraId = cameraId;
        EnsureSelectedCameraRecipe(project);
        _selectCameraInEditor(cameraId);
        _notifyPropertyChanged(SelectedCameraIdPropertyName);
        _notifyPropertyChanged(SelectedVirtualCameraPropertyName);
        _notifyPropertyChanged(CurrentCameraRecipesPropertyName);
        _refreshVisionEvidenceContext();
        _applyCurrentSnapshot();
    }

    internal void SelectCameraRecipe(string? recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe) && CurrentCameraRecipes.Count > 0)
        {
            return;
        }

        if (string.Equals(_selectedCameraRecipe, recipe, StringComparison.Ordinal))
        {
            return;
        }

        _selectedCameraRecipe = recipe;
        _notifyPropertyChanged(SelectedCameraRecipePropertyName);
        _refreshVisionEvidenceContext();
        _notifyCameraCommissioningChanged();
    }

    private void EnsureSelectedCameraRecipe(MachineProjectDocument project)
    {
        var recipes = GetCameraRecipes(project, _selectedCameraId);
        var next = recipes.Contains(_selectedCameraRecipe, StringComparer.Ordinal)
            ? _selectedCameraRecipe
            : recipes.FirstOrDefault();
        if (string.Equals(_selectedCameraRecipe, next, StringComparison.Ordinal))
        {
            return;
        }

        _selectedCameraRecipe = next;
        _notifyPropertyChanged(SelectedCameraRecipePropertyName);
    }

    private static IReadOnlyList<string> GetCameraRecipes(
        MachineProjectDocument project,
        string? cameraId) =>
        string.IsNullOrWhiteSpace(cameraId)
            ? []
            : project.Sequences
                .SelectMany(sequence => sequence.Steps)
                .Where(step => step.Action == SequenceStepAction.TriggerCamera
                    && string.Equals(step.TargetId, cameraId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(step.Parameter))
                .Select(step => step.Parameter.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(recipe => recipe, StringComparer.Ordinal)
                .ToArray();
}
