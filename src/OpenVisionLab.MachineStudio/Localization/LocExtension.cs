using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Sequences;
using OpenVisionLab.Machine.Sequence.Authoring;
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

    public static object RevisionSource => LocalizationBindingSource.Instance;

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

public sealed class LocalizedSequenceActionConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SequenceStepAction action
            ? OpenVisionLanguageService.T($"Sequence.Action.{action}", action.ToString(), action.ToString())
            : value?.ToString() ?? string.Empty;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is "description")
        {
            if (values.Length == 0 || values[0] is not SequenceStepAction action) return string.Empty;
            if (action == SequenceStepAction.MoveAxis)
                return OpenVisionLanguageService.T(values.Length > 2 && values[2] is true ? "R24.StepDescription.MoveAxisWait" : "R24.StepDescription.MoveAxis");
            string role = action switch
            {
                SequenceStepAction.SetChannel => nameof(SequenceStepAction.SetSignal),
                SequenceStepAction.None => nameof(SequenceStepAction.Complete),
                SequenceStepAction.Wait => values.Length < 4 || string.IsNullOrWhiteSpace(values[3]?.ToString())
                    || string.Equals(values[3]?.ToString()?.Trim(), "axisDone", StringComparison.OrdinalIgnoreCase)
                        ? nameof(SequenceStepAction.WaitAxisDone) : nameof(SequenceStepAction.WaitSignal),
                _ => action.ToString()
            };
            return OpenVisionLanguageService.T($"R24.StepDescription.{role}", string.Empty, string.Empty);
        }
        return Convert(values.Length == 0 ? null : values[0], targetType, parameter, culture);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedSequenceTemplateNameConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SequenceStepTemplateDefinition template) return value?.ToString() ?? string.Empty;
        return parameter is "description"
            ? OpenVisionLanguageService.T($"R24.StepTemplateDescription.{template.Id}")
            : OpenVisionLanguageService.T($"Sequence.Template.{template.Id}", template.Name, template.Name);
    }

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.Length == 0 ? null : values[0], targetType, parameter, culture);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedFaultKindConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not OpenVisionLab.MachineStudio.ViewModel.SimulationFaultKindOption option
            ? value?.ToString() ?? string.Empty
            : OpenVisionLanguageService.T(
                $"Fault.Kind.{option.Kind}",
                option.Name,
                option.Name);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.Length == 0 ? null : values[0], targetType, parameter, culture);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LocalizedForcedValueConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not OpenVisionLab.MachineStudio.ViewModel.FaultForcedValueOption option
            ? value?.ToString() ?? string.Empty
            : OpenVisionLanguageService.T(
                option.Value ? "Fault.ForceOn" : "Fault.ForceOff",
                option.Name,
                option.Name);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Convert(values.Length == 0 ? null : values[0], targetType, parameter, culture);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
