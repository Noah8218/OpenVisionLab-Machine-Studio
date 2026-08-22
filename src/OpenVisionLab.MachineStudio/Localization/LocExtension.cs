using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.MachineStudio.Models.Simulation;

namespace OpenVisionLab.MachineStudio.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(LocalizationBindingSource.Revision))
        {
            Source = LocalizationBindingSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = new LocalizedTextConverter(Key)
        }.ProvideValue(serviceProvider);
    }

    private sealed class LocalizedTextConverter : IValueConverter
    {
        private readonly string key;

        public LocalizedTextConverter(string key)
        {
            this.key = key;
        }

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            OpenVisionLanguageService.T(key);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}

internal sealed class LocalizationBindingSource : INotifyPropertyChanged
{
    public static LocalizationBindingSource Instance { get; } = new();

    private int revision;

    private LocalizationBindingSource()
    {
        OpenVisionLanguageService.Load();
        OpenVisionLanguageService.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Revision => revision;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
    }
}

public sealed class LocalizedScenarioNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SimulationScenarioProfile profile)
        {
            return value?.ToString() ?? string.Empty;
        }

        string builtInName = OpenVisionLanguageService.T(
            $"Simulation.ScenarioName.{profile.ProfileId}",
            profile.Name,
            profile.Name);
        return OpenVisionLanguageService.TUserText(
            "scenario",
            $"{profile.ProfileId}.name",
            builtInName);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedSequenceNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SequenceDefinition sequence)
        {
            return value?.ToString() ?? string.Empty;
        }

        return OpenVisionLanguageService.TUserText(
            "sequence",
            $"{sequence.Id}.name",
            sequence.Name);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedFaultKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not OpenVisionLab.MachineStudio.ViewModel.SimulationFaultKindOption option
            ? value?.ToString() ?? string.Empty
            : OpenVisionLanguageService.T(
                $"Fault.Kind.{option.Kind}",
                option.Name,
                option.Name);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedForcedValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not OpenVisionLab.MachineStudio.ViewModel.FaultForcedValueOption option
            ? value?.ToString() ?? string.Empty
            : OpenVisionLanguageService.T(
                option.Value ? "Fault.ForceOn" : "Fault.ForceOff",
                option.Name,
                option.Name);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
