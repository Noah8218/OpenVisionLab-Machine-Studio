using System.Windows.Input;
using OpenVisionLab;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns Shell selection and first-use presentation state without depending on
/// a project, Runtime Engine, or visual tree.
/// </summary>
public sealed class ShellNavigationViewModel : ViewModelBase, IDisposable
{
    private readonly Func<bool> _canStartBlankLayout;
    private readonly Func<bool> _canOpenBundledSample;
    private readonly Func<Task> _openBundledSampleAsync;
    private readonly Action _onBlankLayoutStarted;
    private readonly Action<Exception> _onCommandException;
    private bool _isCompactLayout;
    private bool _isStartupChoiceVisible;
    private int _selectedDocumentTabIndex;
    private int _selectedLeftToolTabIndex;
    private OpenVisionLanguageOption _selectedLanguageOption;
    private bool _disposed;

    public ShellNavigationViewModel(
        bool isStartupChoiceVisible,
        Func<bool> canStartBlankLayout,
        Func<bool> canOpenBundledSample,
        Func<Task> openBundledSampleAsync,
        Action onBlankLayoutStarted,
        Action<Exception> onCommandException)
    {
        _canStartBlankLayout = canStartBlankLayout ?? throw new ArgumentNullException(nameof(canStartBlankLayout));
        _canOpenBundledSample = canOpenBundledSample ?? throw new ArgumentNullException(nameof(canOpenBundledSample));
        _openBundledSampleAsync = openBundledSampleAsync ?? throw new ArgumentNullException(nameof(openBundledSampleAsync));
        _onBlankLayoutStarted = onBlankLayoutStarted ?? throw new ArgumentNullException(nameof(onBlankLayoutStarted));
        _onCommandException = onCommandException ?? throw new ArgumentNullException(nameof(onCommandException));
        _isStartupChoiceVisible = isStartupChoiceVisible;
        OpenVisionLanguageService.Load();
        _selectedLanguageOption = LanguageOptions.First(option =>
            option.Language == OpenVisionLanguageService.CurrentLanguage);
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;

        StartBlankLayoutCommand = new RelayCommand(
            _ => StartBlankLayout(),
            _ => CanStartBlankLayout,
            useCommandManagerRequery: false);
        OpenBundledSampleCommand = new AsyncRelayCommand(
            async _ => await _openBundledSampleAsync(),
            _ => CanOpenBundledSample,
            _onCommandException,
            useCommandManagerRequery: false);
    }

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<OpenVisionLanguageOption> LanguageOptions =>
        OpenVisionLanguageService.LanguageOptions;

    public ICommand StartBlankLayoutCommand { get; }

    public ICommand OpenBundledSampleCommand { get; }

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        set => SetProperty(ref _isCompactLayout, value);
    }

    public int SelectedDocumentTabIndex
    {
        get => _selectedDocumentTabIndex;
        set => SetProperty(ref _selectedDocumentTabIndex, value);
    }

    public int SelectedLeftToolTabIndex
    {
        get => _selectedLeftToolTabIndex;
        set => SetProperty(ref _selectedLeftToolTabIndex, value);
    }

    public bool IsStartupChoiceVisible => _isStartupChoiceVisible;

    public OpenVisionLanguageOption SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_selectedLanguageOption, value))
            {
                return;
            }

            _selectedLanguageOption = value;
            OnPropertyChanged();
            OpenVisionLanguageService.SetLanguage(value.Language);
        }
    }

    internal bool CanStartBlankLayout => !_disposed
        && IsStartupChoiceVisible
        && _canStartBlankLayout();

    internal bool CanOpenBundledSample => !_disposed
        && IsStartupChoiceVisible
        && _canOpenBundledSample();

    public void HideStartupChoice()
    {
        if (_disposed || !SetProperty(ref _isStartupChoiceVisible, false, nameof(IsStartupChoiceVisible)))
        {
            return;
        }

        InvalidateCommands();
    }

    public void InvalidateCommands()
    {
        if (_disposed)
        {
            return;
        }

        ((RelayCommand)StartBlankLayoutCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)OpenBundledSampleCommand).RaiseCanExecuteChanged();
    }

    private void StartBlankLayout()
    {
        if (!CanStartBlankLayout)
        {
            return;
        }

        _isStartupChoiceVisible = false;
        OnPropertyChanged(nameof(IsStartupChoiceVisible));
        SelectedLeftToolTabIndex = 1;
        _onBlankLayoutStarted();
        InvalidateCommands();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var option = LanguageOptions.FirstOrDefault(item =>
            item.Language == OpenVisionLanguageService.CurrentLanguage);
        if (option is not null && !ReferenceEquals(_selectedLanguageOption, option))
        {
            _selectedLanguageOption = option;
            OnPropertyChanged(nameof(SelectedLanguageOption));
        }

        LanguageChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OpenVisionLanguageService.LanguageChanged -= OnLanguageChanged;
        LanguageChanged = null;
    }
}
