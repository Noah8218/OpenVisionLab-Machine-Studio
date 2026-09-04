using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVisionLab;
using OpenVisionLab.Machine.Core.Projects;
using OpenVisionLab.Machine.Core.Layouts;
using OpenVisionLab.MachineStudio.Model;
using OpenVisionLab.MachineStudio.View.Dialogs;
using OpenVisionLab.MachineStudio.View.Shell;
using OpenVisionLab.MachineStudio.ViewModel;
using OpenVisionLab.Wpf.MessageDialogs;
using System.Windows.Media;

namespace OpenVisionLab.MachineStudio;

internal sealed class SmokeProjectSafetyReport
{
    public string Schema { get; init; } = "1.0";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required IReadOnlyDictionary<string, bool> Checks { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public SmokeMonitorEvidence? Monitor { get; init; }
    public bool IsValid => Failures.Count == 0 && Checks.Values.All(value => value);

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));
    }
}

internal static class SmokeProjectSafetyVerifier
{
    private const uint MouseEventLeftDown = 0x0002;
    private const byte VirtualKeyReturn = 0x0D;
    private const byte VirtualKeyEscape = 0x1B;

    public static async Task<SmokeProjectSafetyReport> VerifyAsync(
        ShellWindow window,
        MainViewModel vm,
        string savePath,
        string? unsavedDialogScreenshotPath,
        string? projectOpenFailureDialogScreenshotPath,
        int dpiScalePercent,
        Func<DependencyObject, Func<TextBlock, bool>, TextBlock?> findText,
        Func<DependencyObject, Func<Button, bool>, Button?> findButton,
        Action<Window> activateWindow,
        Action<FrameworkElement> movePointerToCenter,
        Action<uint, uint, uint, uint, UIntPtr> mouseEvent,
        Action markSmokePointerHeld,
        Action<int, int> setCursorPosition,
        Action synchronizeMouse,
        Action releaseSmokePointer,
        Action<Window, int, int, int> applyDpi,
        Func<Window, SmokeMonitorEvidence> captureMonitorEvidence,
        Action<Window, string> captureWindow,
        Action<byte> sendKey)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            checks[name] = passed;
            if (!passed)
            {
                failures.Add(name);
            }
        }

        vm.IsDesignMode = true;
        var fullSavePath = Path.GetFullPath(savePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(fullSavePath)!);
        await vm.SaveProjectAsync(fullSavePath);
        var initialComponentCount = int.Parse(
            vm.LayoutComponentCountText,
            System.Globalization.CultureInfo.InvariantCulture);
        Check("initial-project-clean", !vm.HasUnsavedChanges);

        Check("first-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
        Check("first-edit-marks-dirty", vm.HasUnsavedChanges);
        Check("dirty-title-visible", vm.Title.EndsWith(" *", StringComparison.Ordinal));

        vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Cancel;
        Check("cancel-blocks-open-replacement",
            !await vm.OpenProjectReplacingCurrentAsync(fullSavePath));
        Check("cancelled-open-keeps-project-dirty", vm.HasUnsavedChanges);
        Check("cancel-blocks-new-project", !await vm.CreateNewProjectAsync());
        Check("cancel-keeps-project-dirty", vm.HasUnsavedChanges);

        vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Save;
        Check("save-allows-open-replacement", await vm.OpenProjectReplacingCurrentAsync(fullSavePath));
        Check("save-clears-dirty", !vm.HasUnsavedChanges);
        Check("save-clears-title-marker", !vm.Title.EndsWith(" *", StringComparison.Ordinal));
        Check("backup-created", File.Exists(fullSavePath + ".bak"));
        var savedComponentCount = int.Parse(
            vm.LayoutComponentCountText,
            System.Globalization.CultureInfo.InvariantCulture);
        Check("first-edit-persisted", savedComponentCount == initialComponentCount + 1);

        Check("second-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
        vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        Check("discard-allows-open-replacement", await vm.OpenProjectReplacingCurrentAsync(fullSavePath));
        Check("discarded-edit-not-persisted",
            int.Parse(
                vm.LayoutComponentCountText,
                System.Globalization.CultureInfo.InvariantCulture) == savedComponentCount);
        Check("reopen-restores-clean-state", !vm.HasUnsavedChanges);

        Check("new-project-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
        Check("discard-allows-new-project", await vm.CreateNewProjectAsync());
        Check("new-project-is-clean", !vm.HasUnsavedChanges);
        Check("new-project-has-no-path", vm.Title.EndsWith("Untitled", StringComparison.Ordinal));
        Check("saved-project-reopens-after-new", await vm.OpenProjectAsync(fullSavePath));

        Check("visual-dirty-edit-applied", vm.TryAddLayoutComponent(LayoutComponentKind.MachineFrame));
        Check("visual-dirty-state", vm.HasUnsavedChanges);

        var rejectedProjectPath = fullSavePath + ".unsupported-schema.ovmachine";
        var unsupportedSchema = "2." + new string('9', 120);
        var rejectedProjectJson =
            $"{{\"schema\":\"{unsupportedSchema}\",\"name\":\"unsupported\"}}";
        await File.WriteAllTextAsync(rejectedProjectPath, rejectedProjectJson);
        var rejectedProjectBytes = await File.ReadAllBytesAsync(rejectedProjectPath);
        var titleBeforeRejectedOpen = vm.Title;
        var currentPathBeforeRejectedOpen = vm.CurrentProjectPath;
        var projectStatusBeforeRejectedOpen = vm.ProjectStatusText;
        var projectModelBeforeRejectedOpen = vm.ProjectTree.Roots.Single().Model;
        var layoutBeforeRejectedOpen = vm.Layout.Definition;
        var selectedItemBeforeRejectedOpen = vm.Layout.SelectedItem;
        var layoutCountBeforeRejectedOpen = vm.LayoutComponentCountText;
        var snapshotBeforeRejectedOpen = vm.SceneSnapshots.Latest;
        var designModeBeforeRejectedOpen = vm.IsDesignMode;
        var runningBeforeRejectedOpen = vm.IsRunning;
        var unsavedPromptCount = 0;
        var openFailurePresentationCount = 0;
        WpfMessageDialogOptions? openFailureOptions = null;
        var defaultProjectOpenFailurePresenter = vm.ProjectOpenFailurePresenter;
        vm.UnsavedProjectPrompt = () =>
        {
            unsavedPromptCount++;
            return UnsavedProjectDecision.Cancel;
        };
        vm.ProjectOpenFailurePresenter = details =>
        {
            openFailurePresentationCount++;
            openFailureOptions = MainMessageDialogHost.CreateProjectOpenFailureDialogOptions(details);
        };

        Check(
            "open-failure-rejected",
            !await vm.OpenProjectReplacingCurrentAsync(rejectedProjectPath));
        Check("open-failure-presented-once", openFailurePresentationCount == 1);
        Check("open-failure-skips-unsaved-prompt", unsavedPromptCount == 0);
        Check("open-failure-title-preserved", vm.Title == titleBeforeRejectedOpen);
        Check("open-failure-current-path-preserved",
            vm.CurrentProjectPath == currentPathBeforeRejectedOpen);
        Check("open-failure-project-status-preserved",
            vm.ProjectStatusText == projectStatusBeforeRejectedOpen);
        Check("open-failure-project-model-preserved",
            ReferenceEquals(vm.ProjectTree.Roots.Single().Model, projectModelBeforeRejectedOpen));
        Check("open-failure-layout-preserved",
            ReferenceEquals(vm.Layout.Definition, layoutBeforeRejectedOpen)
            && ReferenceEquals(vm.Layout.SelectedItem, selectedItemBeforeRejectedOpen)
            && vm.LayoutComponentCountText == layoutCountBeforeRejectedOpen);
        Check("open-failure-dirty-preserved", vm.HasUnsavedChanges);
        Check("open-failure-runtime-preserved",
            ReferenceEquals(vm.SceneSnapshots.Latest, snapshotBeforeRejectedOpen)
            && vm.IsDesignMode == designModeBeforeRejectedOpen
            && vm.IsRunning == runningBeforeRejectedOpen);
        Check("open-failure-source-preserved",
            rejectedProjectBytes.SequenceEqual(await File.ReadAllBytesAsync(rejectedProjectPath)));
        Check("open-failure-status-visible",
            vm.StatusMessage == OpenVisionLanguageService.T("Project.OpenFailedStatus"));
        var localizedUnsupportedSchemaDetail = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            OpenVisionLanguageService.T("Project.OpenFailedUnsupportedSchemaDetail"),
            unsupportedSchema,
            MachineProjectDocument.CurrentSchema);
        Check("open-failure-dialog-options-valid",
            openFailureOptions is
            {
                Kind: WpfMessageDialogKind.Warning,
                DefaultResult: WpfMessageDialogResult.OK
            }
            && !string.IsNullOrWhiteSpace(openFailureOptions.Title)
            && openFailureOptions.Message.Contains(
                localizedUnsupportedSchemaDetail,
                StringComparison.Ordinal)
            && !openFailureOptions.Message.Contains(
                "Unsupported machine project schema",
                StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(projectOpenFailureDialogScreenshotPath)
            && openFailureOptions is not null)
        {
            var shellMonitor = captureMonitorEvidence(window);
            var dialog = new WpfMessageDialogWindow(openFailureOptions)
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            applyDpi(
                dialog,
                dpiScalePercent,
                (int)Math.Ceiling(dialog.ActualWidth),
                (int)Math.Ceiling(dialog.ActualHeight));
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var dialogMonitor = captureMonitorEvidence(dialog);
            var dialogDpi = VisualTreeHelper.GetDpi(dialog);
            var expectedDpi = 96d * dpiScalePercent / 100d;
            Check("open-failure-dialog-on-shell-monitor",
                dialogMonitor.DeviceName == shellMonitor.DeviceName);
            Check("open-failure-dialog-contained", dialogMonitor.WindowContainedByMonitor);
            Check("open-failure-dialog-dpi-applied",
                Math.Abs(dialogDpi.PixelsPerInchX - expectedDpi) <= 0.5
                && Math.Abs(dialogDpi.PixelsPerInchY - expectedDpi) <= 0.5);
            var titleText = findText(dialog, text =>
                string.Equals(text.Text, openFailureOptions.Title, StringComparison.Ordinal));
            var messageText = findText(dialog, text =>
                string.Equals(text.Text, openFailureOptions.Message, StringComparison.Ordinal));
            var okButton = findButton(dialog, button =>
                string.Equals(
                    button.Content?.ToString(),
                    OpenVisionLanguageService.T("MessageBox.OK", "확인", "OK"),
                    StringComparison.Ordinal));
            Check("open-failure-dialog-title-visible",
                titleText is { IsVisible: true, ActualHeight: > 0 });
            Check("open-failure-dialog-message-visible",
                messageText is { IsVisible: true, ActualHeight: > 0 }
                && dialog.ActualHeight < dialog.MaxHeight - 0.5);
            Check("open-failure-dialog-ok-visible-default",
                okButton is { IsVisible: true, IsEnabled: true, IsDefault: true });
            if (okButton is not null)
            {
                activateWindow(dialog);
                for (var attempt = 0; attempt < 10 && !dialog.IsActive; attempt++)
                {
                    await Task.Delay(25);
                    activateWindow(dialog);
                }
                okButton.Focus();
                movePointerToCenter(okButton);
                await Task.Delay(100);
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("open-failure-dialog-ok-hover", okButton.IsMouseOver);
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await Task.Delay(150);
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("open-failure-dialog-ok-pointer-down", okButton.IsPressed);
                captureWindow(dialog, projectOpenFailureDialogScreenshotPath);
                dialog.Close();
                var releasePoint = window.PointToScreen(new Point(
                    Math.Max(8, window.ActualWidth / 2),
                    Math.Max(8, window.ActualHeight - 8)));
                setCursorPosition(
                    (int)Math.Round(releasePoint.X),
                    (int)Math.Round(releasePoint.Y));
                synchronizeMouse();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                releaseSmokePointer();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("open-failure-dialog-ok-pointer-release", !okButton.IsPressed);
            }

            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            (bool InputSent, bool TargetFound, bool? DialogResult, WpfMessageDialogResult Result)
                ExerciseModalKey(byte virtualKey, bool focusDefaultButton)
            {
                var modalDialog = new WpfMessageDialogWindow(openFailureOptions)
                {
                    Owner = window,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                var inputSent = false;
                var targetFound = !focusDefaultButton;
                var timeout = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                timeout.Tick += (_, _) =>
                {
                    timeout.Stop();
                    if (modalDialog.IsVisible)
                    {
                        modalDialog.Close();
                    }
                };
                modalDialog.ContentRendered += (_, _) =>
                {
                    modalDialog.Dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        () =>
                        {
                            applyDpi(
                                modalDialog,
                                dpiScalePercent,
                                (int)Math.Ceiling(modalDialog.ActualWidth),
                                (int)Math.Ceiling(modalDialog.ActualHeight));
                            activateWindow(modalDialog);
                            if (focusDefaultButton)
                            {
                                var defaultButton = findButton(
                                    modalDialog,
                                    button => button.IsDefault && button.IsVisible && button.IsEnabled);
                                targetFound = defaultButton?.Focus() == true;
                                if (defaultButton is not null)
                                {
                                    Keyboard.Focus(defaultButton);
                                }
                            }

                            modalDialog.Dispatcher.BeginInvoke(
                                DispatcherPriority.ApplicationIdle,
                                () =>
                                {
                                    activateWindow(modalDialog);
                                    inputSent = true;
                                    sendKey(virtualKey);
                                });
                        });
                };
                timeout.Start();
                var modalResult = modalDialog.ShowDialog();
                timeout.Stop();
                return (inputSent, targetFound, modalResult, modalDialog.Result);
            }

            var enterResult = ExerciseModalKey(VirtualKeyReturn, focusDefaultButton: true);
            Check("open-failure-dialog-enter-acknowledges",
                enterResult.InputSent
                && enterResult.TargetFound
                && enterResult.DialogResult == true
                && enterResult.Result == WpfMessageDialogResult.OK);
            var escapeResult = ExerciseModalKey(VirtualKeyEscape, focusDefaultButton: false);
            Check("open-failure-dialog-escape-dismisses",
                escapeResult.InputSent
                && escapeResult.DialogResult == true
                && escapeResult.Result == WpfMessageDialogResult.Cancel);
        }

        var defaultPresenterDialogShown = false;
        var defaultPresenterDialogOwned = false;
        var defaultPresenterDialogLocalized = false;
        var defaultPresenterDialogContained = false;
        var defaultPresenterDeadline = DateTime.UtcNow.AddSeconds(3);
        var defaultPresenterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        defaultPresenterTimer.Tick += (_, _) =>
        {
            var actualDialog = Application.Current.Windows
                .OfType<WpfMessageDialogWindow>()
                .FirstOrDefault(candidate => candidate.IsVisible);
            if (actualDialog is null)
            {
                if (DateTime.UtcNow >= defaultPresenterDeadline)
                {
                    defaultPresenterTimer.Stop();
                    sendKey(VirtualKeyEscape);
                }
                return;
            }

            defaultPresenterTimer.Stop();
            applyDpi(
                actualDialog,
                dpiScalePercent,
                (int)Math.Ceiling(actualDialog.ActualWidth),
                (int)Math.Ceiling(actualDialog.ActualHeight));
            var actualDialogMonitor = captureMonitorEvidence(actualDialog);
            var actualTitle = findText(actualDialog, text =>
                string.Equals(text.Text, openFailureOptions?.Title, StringComparison.Ordinal));
            var actualMessage = findText(actualDialog, text =>
                text.Text.Contains(unsupportedSchema, StringComparison.Ordinal));
            var actualDefaultButton = findButton(actualDialog, button =>
                button.IsDefault && button.IsVisible && button.IsEnabled);
            defaultPresenterDialogShown = true;
            defaultPresenterDialogOwned = ReferenceEquals(actualDialog.Owner, window);
            defaultPresenterDialogLocalized = actualTitle is { IsVisible: true }
                                              && actualMessage is { IsVisible: true }
                                              && actualDefaultButton is not null;
            defaultPresenterDialogContained = actualDialogMonitor.WindowContainedByMonitor;
            actualDialog.Close();
        };
        vm.ProjectOpenFailurePresenter = defaultProjectOpenFailurePresenter;
        defaultPresenterTimer.Start();
        Check(
            "open-failure-default-presenter-rejected",
            !await vm.OpenProjectReplacingCurrentAsync(rejectedProjectPath));
        defaultPresenterTimer.Stop();
        Check("open-failure-default-presenter-shown", defaultPresenterDialogShown);
        Check("open-failure-default-presenter-owned", defaultPresenterDialogOwned);
        Check("open-failure-default-presenter-localized", defaultPresenterDialogLocalized);
        Check("open-failure-default-presenter-contained", defaultPresenterDialogContained);
        Check("open-failure-default-presenter-state-preserved",
            vm.Title == titleBeforeRejectedOpen
            && vm.CurrentProjectPath == currentPathBeforeRejectedOpen
            && ReferenceEquals(vm.ProjectTree.Roots.Single().Model, projectModelBeforeRejectedOpen)
            && vm.HasUnsavedChanges
            && ReferenceEquals(vm.SceneSnapshots.Latest, snapshotBeforeRejectedOpen));

        if (!string.IsNullOrWhiteSpace(unsavedDialogScreenshotPath))
        {
            var dialog = new WpfMessageDialogWindow(MainMessageDialogHost.CreateUnsavedProjectDialogOptions())
            {
                Owner = window,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            var dialogMonitor = captureMonitorEvidence(dialog);
            Check("dialog-contained-on-test-monitor", dialogMonitor.WindowContainedByMonitor);
            var saveButton = findButton(dialog, button =>
                string.Equals(
                    button.Content?.ToString(),
                    OpenVisionLanguageService.T("Project.Save", "저장", "Save"),
                    StringComparison.Ordinal));
            Check("dialog-save-button-visible", saveButton is { IsVisible: true });
            if (saveButton is not null)
            {
                activateWindow(dialog);
                saveButton.Focus();
                movePointerToCenter(saveButton);
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                mouseEvent(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                markSmokePointerHeld();
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("dialog-save-button-pointer-down", saveButton.IsPressed);
                captureWindow(dialog, unsavedDialogScreenshotPath);
                releaseSmokePointer();
            }

            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        }

        vm.UnsavedProjectPrompt = () => UnsavedProjectDecision.Discard;
        return new SmokeProjectSafetyReport
        {
            Checks = checks,
            Failures = failures
        };

    }
}
