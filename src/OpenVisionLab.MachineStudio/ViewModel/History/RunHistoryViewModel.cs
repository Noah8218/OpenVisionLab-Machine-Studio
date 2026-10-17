using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.History;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Persistence.History;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>Owns recipe-scoped history selection, archive retries, deletion confirmation and the rerun draft.</summary>
public sealed class RunHistoryViewModel : ViewModelBase, IDisposable
{
    private readonly MachineRunRecordFileStore _store;
    private readonly Func<MachineProjectDocument> _project;
    private readonly Func<bool> _canApply;
    private readonly Action<RunCaptureRecord> _openCameraSettings;
    private readonly Predicate<RunCaptureRecord> _canOpenCameraSettings;
    private readonly Func<MachineProjectDocument, Task<bool>> _applyDraft;
    private readonly Func<MachineRunRecord, IReadOnlyList<RunInspectionRecord>> _inspections;
    private readonly Func<Action, Task> _dispatch;
    private readonly Action<string> _reportFailure;
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly Dictionary<string, MachineRunRecord> _pending = new(StringComparer.Ordinal);
    private Task _recordingTask = Task.CompletedTask;
    private RunHistoryEntry? _selected;
    private RunHistoryEntry? _comparedRun;
    private RunHistoryEntry? _comparisonCaptureOwner;
    private IReadOnlyList<RunCaptureDisplay> _comparedCaptureThumbnails = [];
    private RunCaptureDisplay? _comparedCaptureThumbnail;
    private bool _refreshingComparison;
    private RunCaptureRecord? _selectedCapture;
    private MachineProjectDocument? _draft;
    private string? _deleteId;
    private string _status = string.Empty;
    private bool _busy;
    private bool _disposed;
    private string _searchText = string.Empty;
    private bool _refreshingSearch;
    private IReadOnlyList<RunCaptureDisplay> _captureThumbnails = [];

    public RunHistoryViewModel(MachineRunRecordFileStore store, Func<MachineProjectDocument> project, Func<bool> canApply,
        Func<MachineProjectDocument, Task<bool>> applyDraft, Func<Action, Task> dispatch,
        Func<MachineRunRecord, IReadOnlyList<RunInspectionRecord>>? inspections = null, Action<string>? reportFailure = null,
        Action<RunCaptureRecord>? openCameraSettings = null, Predicate<RunCaptureRecord>? canOpenCameraSettings = null)
    {
        VisibleRuns = new ListCollectionView(Runs) { Filter = item => MatchesSearch((RunHistoryEntry)item) };
        VisibleRuns.CollectionChanged += OnVisibleRunsChanged;
        _store = store;
        _project = project;
        _canApply = canApply;
        _openCameraSettings = openCameraSettings ?? (_ => { });
        _canOpenCameraSettings = canOpenCameraSettings ?? (_ => false);
        _applyDraft = applyDraft;
        _dispatch = dispatch;
        _inspections = inspections ?? (_ => []);
        _reportFailure = reportFailure ?? (_ => { });
        ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty, _ => !IsBusy && SearchText.Length > 0);
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy, ReportError);
        RetrySaveCommand = new AsyncRelayCommand(_ => RetrySaveAsync(), _ => !IsBusy && _pending.Count > 0, ReportError);
        PrepareRerunCommand = new RelayCommand(_ => PrepareRerun(), _ => Selected is not null && !IsBusy && _canApply());
        ApplyDraftCommand = new AsyncRelayCommand(_ => ApplyDraftAsync(), _ => HasDraft && !IsBusy && _canApply(), ReportError);
        CancelDraftCommand = new RelayCommand(_ => { _draft = null; ChangedSelection(); }, _ => HasDraft && !IsBusy);
        DeleteCommand = new RelayCommand(_ => { _draft = null; _deleteId = Selected?.Record.Id; ChangedSelection(); },
            _ => Selected is not null && !IsBusy && !_pending.ContainsKey(Selected.Record.Id));
        ConfirmDeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => IsDeletePending && !IsBusy && !_pending.ContainsKey(_deleteId!), ReportError);
        CancelDeleteCommand = new RelayCommand(_ => { _deleteId = null; ChangedSelection(); }, _ => IsDeletePending && !IsBusy);
        OpenCameraSettingsCommand = new RelayCommand(parameter =>
        {
            if (CanOpenCameraSettings(parameter)) _openCameraSettings((RunCaptureRecord)parameter!);
        }, CanOpenCameraSettings);
        Runs.CollectionChanged += OnRunsChanged;
    }

    public ObservableCollection<RunHistoryEntry> Runs { get; } = [];
    public ICollectionView VisibleRuns { get; }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) { RefreshSearch(); ClearSearchCommand.RaiseCanExecuteChanged(); } }
    }
    public bool HasNoSearchMatches => !IsEmpty && VisibleRuns.IsEmpty;
    public RunHistoryEntry? Selected
    {
        get => _selected;
        set
        {
            if (_refreshingSearch && value is null) return;
            if (!SetProperty(ref _selected, value)) return;
            _captureThumbnails = value?.Record.Captures.Select(capture => new RunCaptureDisplay(capture, CreateImage(capture))).ToArray() ?? [];
            _draft = null;
            _deleteId = null;
            SelectedCapture = value?.Record.Captures.FirstOrDefault();
            ChangedSelection();
        }
    }
    public IReadOnlyList<RunHistoryEntry> ComparisonRuns => Selected?.Record.RecipeId == _project().Id
        ? Runs.Where(run => run.Record.RecipeId == Selected.Record.RecipeId && run.Record.Id != Selected.Record.Id).ToArray() : [];
    public RunHistoryEntry? ComparedRun
    {
        get => _comparedRun;
        set
        {
            if (_disposed || _refreshingComparison && value is null || value is not null && !ComparisonRuns.Contains(value)) return;
            if (SetProperty(ref _comparedRun, value)) RefreshComparison();
        }
    }
    public bool HasComparison => !_disposed && Selected is not null && ComparedRun is not null
        && Runs.Contains(Selected) && ComparisonRuns.Contains(ComparedRun);
    public string ComparisonHint => HasComparison ? T("RunHistory.ComparisonHint")
        : ComparisonRuns.Count == 0 ? T("RunHistory.ComparisonNeedsTwo") : T("RunHistory.SelectComparison");
    public IReadOnlyList<RunComparisonRow> ComparisonRows
    {
        get
        {
            if (!HasComparison) return [];
            var selected = Selected!.Record;
            var compared = ComparedRun!.Record;
            RunComparisonRow Text(string key, string? left, string? right) => new(T(key), left ?? T("RunHistory.Missing"), right ?? T("RunHistory.Missing"),
                left is null || right is null ? T("RunHistory.Missing") : T(left == right ? "RunHistory.Same" : "RunHistory.Different"));
            RunComparisonRow Settings(string key, string? left, string? right) => new(T(key), T(left is null ? "RunHistory.Missing" : "RunHistory.Recorded"),
                T(right is null ? "RunHistory.Missing" : "RunHistory.Recorded"), left is null || right is null ? T("RunHistory.Missing") : T(left == right ? "RunHistory.Same" : "RunHistory.Different"));
            return
            [
                Text("RunHistory.RunOutcome", Selected.Outcome, ComparedRun.Outcome),
                Text("RunHistory.Scope", selected.Scope, compared.Scope),
                CompareNumber("RunHistory.Duration", selected.EndedSimulationSeconds - selected.StartedSimulationSeconds, compared.EndedSimulationSeconds - compared.StartedSimulationSeconds),
                CompareNumber("RunHistory.Target", selected.TargetFeedCount, compared.TargetFeedCount),
                CompareNumber("RunHistory.ActualFeed", selected.ActualFeedCount, compared.ActualFeedCount),
                CompareNumber("RunHistory.Product", selected.Products.Count, compared.Products.Count),
                CompareNumber("RunHistory.Images", selected.Captures.Count, compared.Captures.Count),
                CompareNumber("RunHistory.Inspection", selected.Inspections.Count, compared.Inspections.Count),
                CompareNumber("RunHistory.RecordedAlarms", selected.Faults.Count, compared.Faults.Count),
                Text("RunHistory.AlarmDetails", selected.Faults.Count == 0 ? T("RunHistory.NoRecordedAlarms") : string.Join(Environment.NewLine, selected.Faults),
                    compared.Faults.Count == 0 ? T("RunHistory.NoRecordedAlarms") : string.Join(Environment.NewLine, compared.Faults)),
                Settings("RunHistory.AppliedRecipe", selected.AppliedRecipeJson, compared.AppliedRecipeJson),
                Settings("RunHistory.RuntimeSettings", selected.RuntimeSettingsJson, compared.RuntimeSettingsJson)
            ];
        }
    }

    public RunCaptureRecord? SelectedCapture
    {
        get => _selectedCapture;
        set { if (SetProperty(ref _selectedCapture, value)) { OnPropertyChanged(nameof(CaptureImage)); OnPropertyChanged(nameof(CaptureDescription)); OnPropertyChanged(nameof(CaptureImageStatusText)); OnPropertyChanged(nameof(SelectedCaptureThumbnail)); InvalidateCommands(); } }
    }
    public IReadOnlyList<RunCaptureDisplay> CaptureThumbnails => _captureThumbnails;
    public RunCaptureDisplay? SelectedCaptureThumbnail
    {
        get => CaptureThumbnails.FirstOrDefault(capture => ReferenceEquals(capture.Record, SelectedCapture));
        set { if (value is not null) SelectedCapture = value.Record; }
    }
    public IReadOnlyList<RunCaptureDisplay> ComparedCaptureThumbnails => _comparedCaptureThumbnails;
    public RunCaptureDisplay? ComparedCaptureThumbnail
    {
        get => _comparedCaptureThumbnail;
        set
        {
            if (_disposed || _refreshingComparison && value is null || value is not null && !ComparedCaptureThumbnails.Contains(value)) return;
            if (!SetProperty(ref _comparedCaptureThumbnail, value)) return;
            OnPropertyChanged(nameof(ComparedImage)); OnPropertyChanged(nameof(ComparedCaptureDescription)); OnPropertyChanged(nameof(ComparedImageStatusText));
        }
    }
    public ImageSource? ComparedImage => ComparedCaptureThumbnail?.Image;
    public string ComparedCaptureDescription => DescribeCapture(ComparedCaptureThumbnail?.Record);
    public string ComparedImageStatusText => ComparedCaptureThumbnail is null ? T("RunHistory.NoCaptures")
        : ComparedCaptureThumbnail.Record.Pixels is null ? T("RunHistory.ImageMissing") : ComparedImage is null ? T("RunHistory.ImageUnreadable") : string.Empty;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) InvalidateCommands(); } }
    public bool HasDraft => _draft is not null;
    public bool IsDeletePending => _deleteId is not null;
    public bool HasPendingSaves => _pending.Count > 0;
    public bool IsEmpty => Runs.Count == 0;
    public string LatestRunStatusText => Runs.FirstOrDefault() is { } run
        ? string.Format(CultureInfo.CurrentCulture, T("R24.Bottom.Run"), run.RunId, run.Outcome)
        : T("R24.Bottom.NoRun");
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string SummaryText => Selected?.Summary ?? T("RunHistory.SelectRun");
    public bool HasProducts => Products.Count > 0;
    public bool HasInspections => Inspections.Count > 0;
    public string CaptureImageStatusText => SelectedCapture is null ? T("RunHistory.NoCaptures")
        : SelectedCapture.Pixels is null ? T("RunHistory.ImageMissing")
        : CaptureImage is null ? T("RunHistory.ImageUnreadable") : string.Empty;
    public string AppliedSettingsText
    {
        get
        {
            if (Selected is null) return T("RunHistory.Missing");
            var applied = new ProjectDocumentStore().Load(Selected.Record.AppliedRecipeJson);
            var flow = applied.MaterialFlow;
            var units = string.Join(Environment.NewLine, applied.Stations.SelectMany(station => station.Units)
                .Select(unit => $"{unit.Name} · {unit.SlotName} · {T("RunHistory.LegacyRepeats")}: {unit.RepeatCount}"));
            return $"{applied.Name}\n{T("RunHistory.Product")}: {flow.WorkpieceName}\n{T("RunHistory.Target")}: {flow.TotalFeedCount?.ToString(CultureInfo.CurrentCulture) ?? T("RunHistory.LegacySupply")}\nOK: {flow.OkRoute} · NG: {flow.NgRoute}\n{units}\n\n{T("RunHistory.RuntimeSettings")}\n{Selected.Record.RuntimeSettingsJson ?? T("RunHistory.Missing")}";
        }
    }
    public string AppliedRecipeJson => Selected?.Record.AppliedRecipeJson ?? T("RunHistory.Missing");
    public string CurrentCameraSettingsTargetText
    {
        get
        {
            if (Selected is null || !Runs.Contains(Selected) || SelectedCapture is null) return T("RunHistory.SelectCaptureForSettings");
            if (Selected.Record.RecipeId != _project().Id) return T("RunHistory.DifferentRecipe");
            if (string.IsNullOrEmpty(SelectedCapture.CameraId)) return T("RunHistory.CameraNotRecorded");
            var camera = _project().Devices.FirstOrDefault(device => device.Kind == OpenVisionLab.Machine.Core.Devices.DeviceKind.Camera && device.Id == SelectedCapture.CameraId);
            return camera is null ? string.Format(CultureInfo.CurrentCulture, T("RunHistory.CurrentCameraMissing"), SelectedCapture.CameraId)
                : string.Format(CultureInfo.CurrentCulture, T("RunHistory.CurrentCameraTarget"), SelectedCapture.CameraId, camera.Name);
        }
    }
    public string CaptureDescription => DescribeCapture(SelectedCapture);
    public ImageSource? CaptureImage => SelectedCaptureThumbnail?.Image;
    public IReadOnlyList<RunProductDisplay> Products => Selected?.Record.Products.Select(product => new RunProductDisplay(product)).ToArray() ?? [];
    public IReadOnlyList<RunCaptureRecord> Captures => Selected?.Record.Captures ?? [];
    public IReadOnlyList<RunInspectionRecord> Inspections => Selected?.Record.Inspections ?? [];

    public RelayCommand ClearSearchCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RetrySaveCommand { get; }
    public RelayCommand PrepareRerunCommand { get; }
    public AsyncRelayCommand ApplyDraftCommand { get; }
    public RelayCommand CancelDraftCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ConfirmDeleteCommand { get; }
    public RelayCommand CancelDeleteCommand { get; }
    public RelayCommand OpenCameraSettingsCommand { get; }

    internal void StartRecording(ChannelReader<MachineRunRecord> reader) => _recordingTask = ConsumeAsync(reader);
    internal Task RecordingCompletion => _recordingTask;

    internal void LoadProject()
    {
        SearchText = string.Empty;
        if (Runs.Any(run => run.Record.RecipeId != _project().Id)) { Runs.Clear(); Selected = null; OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(LatestRunStatusText)); }
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        var recipeId = _project().Id;
        try
        {
            await _storeGate.WaitAsync();
            RunRecordReadResult result;
            try { result = await _store.ReadAsync(recipeId); }
            finally { _storeGate.Release(); }
            if (_disposed || recipeId != _project().Id) return;
            var selectedId = Selected?.Record.Id;
            var comparedId = ComparedRun?.Record.Id;
            Runs.Clear();
            foreach (var record in result.Records) Runs.Add(new(record));
            foreach (var record in _pending.Values.Where(record => record.RecipeId == recipeId && !result.Records.Any(saved => saved.Id == record.Id))) Runs.Insert(0, new(record));
            Selected = Runs.FirstOrDefault(run => run.Record.Id == selectedId) ?? Runs.FirstOrDefault();
            ComparedRun = ComparisonRuns.FirstOrDefault(run => run.Record.Id == comparedId);
            StatusText = result.Errors.Count > 0 ? T("RunHistory.ReadFailed") + " " + string.Join(Environment.NewLine, result.Errors)
                : HasPendingSaves ? T("RunHistory.SaveFailed") : string.Empty;
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(LatestRunStatusText));
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsBusy = false; if (!_disposed && recipeId != _project().Id) _ = RefreshAsync(); }
    }

    internal async Task RetrySaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { foreach (var record in _pending.Values.ToArray()) await ArchiveAsync(record, enrich: false); }
        finally { IsBusy = false; InvalidateCommands(); }
    }

    private async Task ConsumeAsync(ChannelReader<MachineRunRecord> reader)
    {
        await foreach (var record in reader.ReadAllAsync().ConfigureAwait(false)) await ArchiveAsync(record, enrich: true).ConfigureAwait(false);
    }

    private async Task ArchiveAsync(MachineRunRecord record, bool enrich)
    {
        var prepared = record;
        try
        {
            if (enrich)
            {
                await _dispatch(() => prepared = record with { Inspections = _inspections(record) }).ConfigureAwait(false);
                prepared = await _store.PreserveSourceImagesAsync(prepared).ConfigureAwait(false);
            }
            await _storeGate.WaitAsync().ConfigureAwait(false);
            try { await _store.SaveAsync(prepared).ConfigureAwait(false); }
            finally { _storeGate.Release(); }
            await _dispatch(() =>
            {
                _pending.Remove(record.Id);
                if (!_disposed && _project().Id == record.RecipeId)
                {
                    if (!Runs.Any(run => run.Record.Id == record.Id)) Runs.Insert(0, new(prepared));
                    if (Selected is null) Selected = Runs.FirstOrDefault();
                    StatusText = T("RunHistory.Saved");
                    OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(LatestRunStatusText));
                }
                InvalidateCommands();
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatch(() =>
            {
                _pending[record.Id] = prepared;
                if (!_disposed && _project().Id == record.RecipeId && !Runs.Any(run => run.Record.Id == record.Id)) Runs.Insert(0, new(prepared));
                StatusText = T("RunHistory.SaveFailed") + " " + record.RecipeName + " · " + exception.Message;
                _reportFailure(StatusText);
                OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(LatestRunStatusText));
                InvalidateCommands();
            }).ConfigureAwait(false);
        }
    }

    private void PrepareRerun()
    {
        if (Selected is null || !_canApply()) return;
        _draft = new ProjectDocumentStore().Load(Selected.Record.AppliedRecipeJson);
        _deleteId = null;
        ChangedSelection();
    }

    internal async Task ApplyDraftAsync()
    {
        if (_draft is null || !_canApply() || IsBusy || _draft.Id != _project().Id) return;
        IsBusy = true;
        try
        {
            if (await _applyDraft(_draft)) { _draft = null; StatusText = T("RunHistory.DraftApplied"); ChangedSelection(); }
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync()
    {
        if (_deleteId is null || Selected?.Record.Id != _deleteId || IsBusy || _pending.ContainsKey(_deleteId)) return;
        var selected = Selected;
        IsBusy = true;
        try
        {
            await _storeGate.WaitAsync();
            try { _store.Delete(selected.Record.RecipeId, selected.Record.Id); }
            finally { _storeGate.Release(); }
            Runs.Remove(selected);
            Selected = Runs.FirstOrDefault();
            _deleteId = null;
            StatusText = T("RunHistory.Deleted");
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(LatestRunStatusText));
            ChangedSelection();
        }
        catch (Exception exception) { ReportError(exception); }
        finally { IsBusy = false; }
    }

    private bool CanOpenCameraSettings(object? parameter) => !_disposed && !IsBusy && _canApply()
        && Selected is not null && Runs.Contains(Selected) && Selected.Record.RecipeId == _project().Id
        && parameter is RunCaptureRecord capture && ReferenceEquals(capture, SelectedCapture)
        && Selected.Record.Captures.Any(item => ReferenceEquals(item, capture)) && _canOpenCameraSettings(capture);

    public void InvalidateCommands()
    {
        OpenCameraSettingsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentCameraSettingsTargetText));
        ClearSearchCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged(); RetrySaveCommand.RaiseCanExecuteChanged(); PrepareRerunCommand.RaiseCanExecuteChanged();
        ApplyDraftCommand.RaiseCanExecuteChanged(); DeleteCommand.RaiseCanExecuteChanged(); ConfirmDeleteCommand.RaiseCanExecuteChanged();
        CancelDraftCommand.RaiseCanExecuteChanged(); CancelDeleteCommand.RaiseCanExecuteChanged();
    }

    public void RefreshLocalization()
    {
        foreach (var entry in Runs) entry.RefreshLocalization();
        RefreshSearch();
        ChangedSelection();
        OnPropertyChanged(nameof(CaptureDescription));
        OnPropertyChanged(nameof(CaptureImageStatusText));
        OnPropertyChanged(nameof(LatestRunStatusText));
    }

    private void ChangedSelection()
    {
        RefreshComparison();
        foreach (var name in new[] { nameof(SummaryText), nameof(AppliedSettingsText), nameof(AppliedRecipeJson), nameof(Products), nameof(Captures), nameof(CaptureThumbnails), nameof(SelectedCaptureThumbnail), nameof(Inspections), nameof(HasProducts), nameof(HasInspections), nameof(HasDraft), nameof(IsDeletePending), nameof(CaptureImageStatusText) }) OnPropertyChanged(name);
        InvalidateCommands();
    }

    private static RunComparisonRow CompareNumber(string key, double? selected, double? compared) => new(T(key),
        selected?.ToString("0.###", CultureInfo.CurrentCulture) ?? T("RunHistory.Missing"), compared?.ToString("0.###", CultureInfo.CurrentCulture) ?? T("RunHistory.Missing"),
        selected.HasValue && compared.HasValue ? (compared.Value - selected.Value).ToString("+0.###;-0.###;0", CultureInfo.CurrentCulture) : T("RunHistory.Missing"));

    private void OnRunsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshComparison();
        InvalidateCommands();
    }

    private void RefreshComparison()
    {
        if (_disposed) return;
        _refreshingComparison = true;
        try
        {
            if (_comparedRun is not null && !ComparisonRuns.Contains(_comparedRun)) _comparedRun = null;
            if (!ReferenceEquals(_comparisonCaptureOwner, _comparedRun))
            {
                _comparisonCaptureOwner = _comparedRun;
                _comparedCaptureThumbnails = _comparedRun?.Record.Captures.Select(capture => new RunCaptureDisplay(capture, CreateImage(capture))).ToArray() ?? [];
                _comparedCaptureThumbnail = _comparedCaptureThumbnails.FirstOrDefault();
            }
            foreach (var name in new[] { nameof(ComparisonRuns), nameof(ComparedRun), nameof(HasComparison), nameof(ComparisonRows), nameof(ComparisonHint),
                nameof(ComparedCaptureThumbnails), nameof(ComparedCaptureThumbnail), nameof(ComparedImage), nameof(ComparedCaptureDescription), nameof(ComparedImageStatusText) }) OnPropertyChanged(name);
        }
        finally { _refreshingComparison = false; }
    }

    private bool MatchesSearch(RunHistoryEntry entry)
    {
        var query = SearchText.Trim();
        var record = entry.Record;
        return query.Length == 0 || new[] { record.Id, record.RecipeName, entry.Outcome, record.Scope }
            .Concat(record.Faults).Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private void RefreshSearch()
    {
        // Filtering hides rows; it must not discard the selected record or its pending draft.
        _refreshingSearch = true;
        try { VisibleRuns.Refresh(); }
        finally { _refreshingSearch = false; }
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasNoSearchMatches));
    }

    private void OnVisibleRunsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasNoSearchMatches));

    private void ReportError(Exception exception)
    {
        StatusText = T("RunHistory.OperationFailed") + " " + exception.Message;
        _reportFailure(StatusText);
    }
    private static string T(string key) => OpenVisionLanguageService.T(key);

    private static string DescribeCapture(RunCaptureRecord? capture) => capture is null ? T("RunHistory.Missing")
        : $"{capture.CameraId} · {capture.AcquisitionId}\n{capture.ProductId ?? T("RunHistory.Missing")} · {capture.PixelFormat ?? T("RunHistory.Missing")} · {capture.Width?.ToString(CultureInfo.CurrentCulture) ?? T("RunHistory.Missing")}×{capture.Height?.ToString(CultureInfo.CurrentCulture) ?? T("RunHistory.Missing")}\n{capture.ContentSha256 ?? T("RunHistory.Missing")}\n{capture.ImageError}";

    private static ImageSource? CreateImage(RunCaptureRecord? capture)
    {
        if (capture?.Pixels is null) return null;
        try
        {
            BitmapSource image;
            if (capture.PreviewGray8 is { } preview && capture.Width is { } width && capture.Height is { } height && preview.Length == checked(width * height))
                image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, preview, width);
            else
            {
                using var stream = new MemoryStream(capture.Pixels, writable: false);
                image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            }
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or FileFormatException or NotSupportedException or InvalidOperationException or OverflowException) { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        Runs.CollectionChanged -= OnRunsChanged;
        VisibleRuns.CollectionChanged -= OnVisibleRunsChanged;
        _comparedRun = null;
        _comparisonCaptureOwner = null;
        _comparedCaptureThumbnails = [];
        _comparedCaptureThumbnail = null;
        _captureThumbnails = [];
    }
}

public sealed class RunHistoryEntry(MachineRunRecord record) : ViewModelBase
{
    public MachineRunRecord Record { get; } = record;
    public string Title => Record.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    public string RunId => Record.Id;
    public string SelectionText => $"{Title} · {Outcome}";
    public string Outcome => OpenVisionLanguageService.T("RunHistory." + Record.Outcome);
    public string Summary => $"{Outcome} · {(string.IsNullOrWhiteSpace(Record.Scope) ? OpenVisionLanguageService.T("RunHistory.Missing") : Record.Scope)}\n{Record.RecipeName} · {Record.Id}\n{OpenVisionLanguageService.T("RunHistory.Target")}: {Record.TargetFeedCount?.ToString(CultureInfo.CurrentCulture) ?? OpenVisionLanguageService.T("RunHistory.Missing")} · {OpenVisionLanguageService.T("RunHistory.ActualFeed")}: {Record.ActualFeedCount?.ToString(CultureInfo.CurrentCulture) ?? OpenVisionLanguageService.T("RunHistory.Missing")} · {OpenVisionLanguageService.T("RunHistory.Product")} {Record.Products.Count} · {OpenVisionLanguageService.T("RunHistory.Images")} {Record.Captures.Count}\n{Record.StartedAtUtc.LocalDateTime:g} → {Record.EndedAtUtc.LocalDateTime:g}\n{Record.BuildIdentity ?? OpenVisionLanguageService.T("RunHistory.Missing")}";
    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Outcome)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(SelectionText));
    }
}

public sealed record RunProductDisplay(RunProductRecord Record)
{
    public string Name => string.IsNullOrEmpty(Record.Name) ? OpenVisionLanguageService.T("RunHistory.Missing") : Record.Name;
    public string State => OpenVisionLanguageService.T("RunHistory.State." + Record.State);
}

public sealed record RunCaptureDisplay(RunCaptureRecord Record, ImageSource? Image);

public sealed record RunComparisonRow(string Label, string SelectedValue, string ComparedValue, string Difference);
