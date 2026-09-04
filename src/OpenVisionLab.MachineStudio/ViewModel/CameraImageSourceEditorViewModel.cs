using System.IO;
using System.Windows.Input;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Devices;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Infrastructure.Vision;
using OpenVisionLab.MachineStudio.View.Dialogs;

namespace OpenVisionLab.MachineStudio.ViewModel;

public sealed class CameraImageSourceEditorViewModel : ViewModelBase
{
    private readonly Action<string, string> _sourceApplied;
    private readonly Func<string, string?> _selectSourceFile;
    private MachineProjectDocument _project = new();
    private string? _projectPath;
    private string? _cameraId;
    private string _pathText = string.Empty;
    private int _width = 1;
    private int _height = 1;
    private string _pixelFormatText = string.Empty;
    private string _validationErrorKey = "Camera.SourceFileRequired";
    private string _normalizedPath = string.Empty;
    private bool _needsProjectSave;

    public CameraImageSourceEditorViewModel(Action<string, string> sourceApplied)
        : this(sourceApplied, CameraImageSourceFileDialogHost.SelectSourceFile)
    {
    }

    internal CameraImageSourceEditorViewModel(
        Action<string, string> sourceApplied,
        Func<string, string?> selectSourceFile)
    {
        _sourceApplied = sourceApplied ?? throw new ArgumentNullException(nameof(sourceApplied));
        _selectSourceFile = selectSourceFile ?? throw new ArgumentNullException(nameof(selectSourceFile));
        BrowseCommand = new RelayCommand(_ => Browse(), _ => CanBrowse);
        ApplyCommand = new RelayCommand(_ => Apply(), _ => CanApply);
        RevertCommand = new RelayCommand(_ => Synchronize(), _ => CanRevert);
    }

    public ICommand BrowseCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RevertCommand { get; }

    public string PathText
    {
        get => _pathText;
        set
        {
            if (SetProperty(ref _pathText, value ?? string.Empty))
            {
                RefreshValidation();
            }
        }
    }

    public int Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value))
            {
                RefreshValidation();
            }
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value))
            {
                RefreshValidation();
            }
        }
    }

    public string PixelFormatText
    {
        get => _pixelFormatText;
        set
        {
            if (SetProperty(ref _pixelFormatText, value ?? string.Empty))
            {
                RefreshValidation();
            }
        }
    }

    public bool IsDirty
    {
        get
        {
            if (Definition?.Camera?.SingleImageSource is not { } source)
            {
                return !string.IsNullOrEmpty(_pathText)
                    || _width != 1
                    || _height != 1
                    || !string.IsNullOrEmpty(_pixelFormatText);
            }

            return !string.Equals(source.SourceRelativePath, _pathText, StringComparison.Ordinal)
                || source.Width != _width
                || source.Height != _height
                || !string.Equals(source.PixelFormat, _pixelFormatText, StringComparison.Ordinal);
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_validationErrorKey);
    public bool CanBrowse => Definition is not null && !string.IsNullOrWhiteSpace(_projectPath);
    public bool CanApply => CanBrowse && IsDirty && !HasError;
    public bool CanRevert => Definition is not null && IsDirty;
    public string ValidationText => HasError
        ? OpenVisionLanguageService.T(_validationErrorKey)
        : IsDirty
            ? OpenVisionLanguageService.T("Camera.SourceDraftReady")
            : OpenVisionLanguageService.T(_needsProjectSave
                ? "Camera.SourceAppliedSave"
                : "Camera.SourceConfigured");

    private DeviceDefinition? Definition => _project.Devices.FirstOrDefault(device =>
        device.Kind == DeviceKind.Camera
        && string.Equals(device.Id, _cameraId, StringComparison.Ordinal));

    public void Load(MachineProjectDocument project, string? projectPath, string? cameraId)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _projectPath = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
        _cameraId = cameraId;
        _needsProjectSave = false;
        Synchronize();
    }

    public void SelectCamera(string? cameraId)
    {
        _cameraId = cameraId;
        Synchronize();
    }

    public void SetProjectPath(string? projectPath, bool isSaved)
    {
        _projectPath = string.IsNullOrWhiteSpace(projectPath) ? null : Path.GetFullPath(projectPath);
        if (isSaved)
        {
            _needsProjectSave = false;
        }
        RefreshValidation();
    }

    public void RefreshLocalization() => OnPropertyChanged(nameof(ValidationText));

    private void Browse()
    {
        if (!CanBrowse || string.IsNullOrWhiteSpace(_projectPath))
        {
            return;
        }

        var projectRoot = Path.GetDirectoryName(_projectPath)!;
        var selectedPath = _selectSourceFile(projectRoot);
        if (selectedPath is null)
        {
            return;
        }

        try
        {
            var relativePath = Path.GetRelativePath(projectRoot, Path.GetFullPath(selectedPath));
            PathText = new ProjectAssetPathResolver(projectRoot)
                .ResolveExistingFile(relativePath)
                .RelativePath;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            _sourceApplied(string.Empty, exception.Message);
        }
    }

    private void Apply()
    {
        if (!CanApply || Definition is not { Camera: { } camera } definition)
        {
            return;
        }

        camera.SingleImageSource = new VirtualSingleImageSourceDefinition
        {
            SourceRelativePath = _normalizedPath,
            Width = _width,
            Height = _height,
            PixelFormat = _pixelFormatText
        };
        _pathText = _normalizedPath;
        _needsProjectSave = true;
        RefreshValidation();
        _sourceApplied(definition.Id, _normalizedPath);
    }

    private void Synchronize()
    {
        var source = Definition?.Camera?.SingleImageSource;
        _pathText = source?.SourceRelativePath ?? string.Empty;
        _width = source?.Width ?? 1;
        _height = source?.Height ?? 1;
        _pixelFormatText = source?.PixelFormat ?? string.Empty;
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(PixelFormatText));
        RefreshValidation();
    }

    private void RefreshValidation()
    {
        if (TryValidate(out var errorKey, out var normalizedPath))
        {
            _validationErrorKey = string.Empty;
            _normalizedPath = normalizedPath;
        }
        else
        {
            _validationErrorKey = errorKey;
            _normalizedPath = string.Empty;
        }

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(ValidationText));
    }

    private bool TryValidate(out string errorKey, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (Definition is null)
        {
            errorKey = "Camera.NoCameraHint";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_projectPath))
        {
            errorKey = "Camera.SaveProjectBeforeSource";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_pathText))
        {
            errorKey = "Camera.SourceFileRequired";
            return false;
        }
        if (_width <= 0 || _height <= 0)
        {
            errorKey = "Camera.SourceDimensionsInvalid";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_pixelFormatText)
            || !string.Equals(_pixelFormatText, _pixelFormatText.Trim(), StringComparison.Ordinal))
        {
            errorKey = "Camera.SourcePixelFormatInvalid";
            return false;
        }

        try
        {
            var asset = new ProjectAssetPathResolver(Path.GetDirectoryName(_projectPath)!)
                .ResolveExistingFile(_pathText);
            if (new FileInfo(asset.FullPath).Length == 0)
            {
                errorKey = "Camera.SourceFileEmpty";
                return false;
            }
            normalizedPath = asset.RelativePath;
            errorKey = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            errorKey = "Camera.SourceFileInvalid";
            return false;
        }
    }
}
