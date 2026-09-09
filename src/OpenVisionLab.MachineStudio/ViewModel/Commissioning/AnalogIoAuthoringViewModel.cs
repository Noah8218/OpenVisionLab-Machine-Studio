using System.Globalization;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Channels;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Edits the persisted scalar initial value of one authored analog channel.
/// Runtime analog state remains owned by the Core/IO/Simulation boundary.
/// </summary>
public sealed class AnalogIoAuthoringViewModel : ViewModelBase
{
    private readonly ChannelDefinition _channel;
    private readonly Action _definitionChanged;
    private string _initialValueText;
    private bool _hasValidationErrors;
    private string _validationMessage = string.Empty;

    public AnalogIoAuthoringViewModel(
        ChannelDefinition channel,
        Action definitionChanged)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        if (channel.Kind is not (ChannelKind.AnalogInput or ChannelKind.AnalogOutput))
        {
            throw new ArgumentException(
                "An analog input or output channel is required.",
                nameof(channel));
        }

        _definitionChanged = definitionChanged ?? throw new ArgumentNullException(nameof(definitionChanged));
        _initialValueText = FormatValue(channel.InitialValue);
        Validate();
    }

    public string Id => _channel.Id;
    public string Name => _channel.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    public bool IsInput => _channel.Kind == ChannelKind.AnalogInput;
    public string KindText => OpenVisionLanguageService.T(
        IsInput ? "Io.AnalogInput" : "Io.AnalogOutput",
        IsInput ? "AI" : "AO",
        IsInput ? "AI" : "AO");

    public string InitialValueText
    {
        get => _initialValueText;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_initialValueText, value, StringComparison.Ordinal))
            {
                return;
            }

            _initialValueText = value;
            OnPropertyChanged();
            if (!TryParse(value, out var parsedValue))
            {
                HasValidationErrors = true;
                ValidationMessage = OpenVisionLanguageService.T("Io.AnalogValueInvalid");
                return;
            }

            HasValidationErrors = false;
            ValidationMessage = OpenVisionLanguageService.T("Io.AnalogValueValid");
            if (_channel.InitialValue == parsedValue)
            {
                return;
            }

            _channel.InitialValue = parsedValue;
            OnPropertyChanged(nameof(InitialValue));
            _definitionChanged();
        }
    }

    public double InitialValue
    {
        get => _channel.InitialValue;
        set => InitialValueText = FormatValue(value);
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

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(KindText));
        Validate();
    }

    private void Validate()
    {
        if (!TryParse(_initialValueText, out _))
        {
            HasValidationErrors = true;
            ValidationMessage = OpenVisionLanguageService.T("Io.AnalogValueInvalid");
            return;
        }

        HasValidationErrors = false;
        ValidationMessage = OpenVisionLanguageService.T("Io.AnalogValueValid");
    }

    private static bool TryParse(string text, out double value)
    {
        bool parsed = double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.CurrentCulture,
                         out value)
                     || double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out value);
        return parsed && double.IsFinite(value);
    }

    private static string FormatValue(double value) =>
        value.ToString("R", CultureInfo.CurrentCulture);
}
