using WinARD.Desktop.ViewModels;
using WinARD.Desktop.Views;
using WinARD.Domain.Connections;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class RemoteSessionWindowInputIntegrationTests
{
    [Fact]
    public async Task Quality_selection_applies_before_save_and_skips_duplicate()
    {
        var current = QualityProfile.Automatic;
        var applied = new List<QualityProfile>();
        var saved = new List<QualityProfile>();

        current = await RemoteSessionWindow.ApplyQualityProfileSelectionAsync(
            QualityProfile.Smooth,
            current,
            applied.Add,
            (profile, _) => { saved.Add(profile); return Task.CompletedTask; },
            _ => { },
            () => false,
            CancellationToken.None);
        current = await RemoteSessionWindow.ApplyQualityProfileSelectionAsync(
            QualityProfile.Smooth,
            current,
            applied.Add,
            (profile, _) => { saved.Add(profile); return Task.CompletedTask; },
            _ => { },
            () => false,
            CancellationToken.None);

        Assert.Equal(QualityProfile.Smooth, current);
        Assert.Equal([QualityProfile.Smooth], applied);
        Assert.Equal([QualityProfile.Smooth], saved);
    }

    [Fact]
    public async Task Failed_quality_save_keeps_session_value_and_reports_independent_status()
    {
        QualityProfile? applied = null;
        string? status = null;

        var selected = await RemoteSessionWindow.ApplyQualityProfileSelectionAsync(
            QualityProfile.Balanced,
            QualityProfile.Automatic,
            value => applied = value,
            (_, _) => Task.FromException(new InvalidOperationException("save failed")),
            value => status = value,
            () => false,
            CancellationToken.None);

        Assert.Same(QualityProfile.Balanced, selected);
        Assert.Same(QualityProfile.Balanced, applied);
        Assert.Equal("画质设置未保存，本次会话仍已应用", status);
    }

    [Fact]
    public async Task Closing_cancels_quality_save_without_writing_status_after_close()
    {
        using var cancellation = new CancellationTokenSource();
        var statuses = new List<string>();

        var selected = await RemoteSessionWindow.ApplyQualityProfileSelectionAsync(
            QualityProfile.Smooth,
            QualityProfile.Automatic,
            _ => { },
            (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); },
            statuses.Add,
            () => cancellation.IsCancellationRequested,
            cancellation.Token);

        Assert.Same(QualityProfile.Smooth, selected);
        Assert.Empty(statuses);
    }

    [Fact]
    public void Quality_controls_have_unique_stable_automation_ids_and_no_sensitive_fields()
    {
        var xaml = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"));
        var ids = System.Text.RegularExpressions.Regex.Matches(
                xaml,
                "AutomationProperties.AutomationId=\"(RemoteQuality[^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.True(ids.Length >= 12);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("Host", string.Join('|', ids), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", string.Join('|', ids), StringComparison.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            Assert.Matches(
                $"AutomationProperties.AutomationId=\"{id}\"[^>]*AutomationProperties.Name=\"[^\"]+\"",
                xaml);
        }

        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));
        Assert.DoesNotContain(
            "StatusText.Text = $\"诊断已导出：{path}\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains("QualityPresentation.SanitizePerformanceText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_selection_generation_rejects_stale_completion()
    {
        var coordinator = new QualityProfileSelectionCoordinator();
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public async Task Quality_selection_coordinator_only_completes_latest_async_handler()
    {
        var coordinator = new QualityProfileSelectionCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new List<string>();

        var first = coordinator.RunLatestAsync(
            async _ =>
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            },
            () => completed.Add("first"));
        await firstEntered.Task;
        await coordinator.RunLatestAsync(_ => Task.CompletedTask, () => completed.Add("second"));
        releaseFirst.TrySetResult();
        await first;

        Assert.Equal(["second"], completed);
    }

    [Fact]
    public async Task Programmatic_refresh_selection_does_not_save()
    {
        var coordinator = new FrameRateSelectionCoordinator();
        var applied = new List<FrameRefreshPolicy>();
        var persisted = new List<FrameRefreshPolicy>();
        Task<bool>? selectionChanged = null;
        var option = new FrameRefreshOption(FrameRefreshPolicy.Fixed(60), "60", true, null);

        coordinator.SynchronizeSelection(() =>
        {
            // Setting SelectedItem synchronously raises SelectionChanged in WinUI.
            selectionChanged = coordinator.ApplySelectionAsync(
                option,
                policy => RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
                    policy,
                    applied.Add,
                    (value, _) =>
                    {
                        persisted.Add(value);
                        return Task.CompletedTask;
                    },
                    _ => { },
                    isClosing: () => false,
                    CancellationToken.None));
        });

        Assert.False(await Assert.IsType<Task<bool>>(selectionChanged));
        Assert.Empty(applied);
        Assert.Empty(persisted);
    }

    [Fact]
    public async Task User_refresh_selection_saves_typed_policy()
    {
        var coordinator = new FrameRateSelectionCoordinator();
        var appliedPolicies = new List<FrameRefreshPolicy>();
        var persistedPolicies = new List<FrameRefreshPolicy>();
        var selected = new FrameRefreshOption(FrameRefreshPolicy.Fixed(60), "60", true, null);

        var applied = await coordinator.ApplySelectionAsync(
            selected,
            policy => RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
                policy,
                appliedPolicies.Add,
                (value, _) =>
                {
                    persistedPolicies.Add(value);
                    return Task.CompletedTask;
                },
                _ => { },
                isClosing: () => false,
                CancellationToken.None));

        Assert.True(applied);
        Assert.Equal([selected.Policy], appliedPolicies);
        Assert.Equal([selected.Policy], persistedPolicies);
    }

    [Fact]
    public async Task Refresh_selection_coordinator_rejects_reentry_until_save_completes()
    {
        var coordinator = new FrameRateSelectionCoordinator();
        var allowSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var selections = new List<FrameRefreshPolicy>();
        var firstOption = new FrameRefreshOption(FrameRefreshPolicy.Fixed(60), "60", true, null);
        var secondOption = new FrameRefreshOption(FrameRefreshPolicy.Fixed(90), "90", true, null);

        var first = coordinator.ApplySelectionAsync(
            firstOption,
            async policy =>
            {
                selections.Add(policy);
                await allowSave.Task;
            });
        var second = await coordinator.ApplySelectionAsync(
            secondOption,
            policy =>
            {
                selections.Add(policy);
                return Task.CompletedTask;
            });

        Assert.False(second);
        Assert.Equal([firstOption.Policy], selections);
        allowSave.TrySetResult();
        Assert.True(await first);
    }

    [Theory]
    [InlineData(FrameRefreshMode.Automatic, 0, "自动刷新")]
    [InlineData(FrameRefreshMode.Fixed, 60, "固定 60 FPS")]
    [InlineData(FrameRefreshMode.Unlimited, 0, "无限刷新")]
    public void Refresh_option_automation_name_describes_mode(
        FrameRefreshMode mode,
        int framesPerSecond,
        string expected)
    {
        var policy = mode switch
        {
            FrameRefreshMode.Automatic => FrameRefreshPolicy.Automatic,
            FrameRefreshMode.Fixed => FrameRefreshPolicy.Fixed(framesPerSecond),
            FrameRefreshMode.Unlimited => FrameRefreshPolicy.Unlimited,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var option = new FrameRefreshOption(policy, expected, true, "当前上限 59");

        Assert.Equal(
            $"{expected}，当前上限 59",
            FrameRefreshOptionPresentation.AutomationName(option));
    }

    [Fact]
    public async Task Closing_selection_releases_coordinator_without_failure_prompt()
    {
        var coordinator = new FrameRateSelectionCoordinator();
        var status = new FrameRateSaveStatus();
        using var closing = new CancellationTokenSource();
        var saveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var isClosing = false;
        var option = new FrameRefreshOption(FrameRefreshPolicy.Fixed(60), "60", true, null);
        var operation = coordinator.ApplySelectionAsync(
            option,
            policy => RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
                policy,
                _ => { },
                async (_, token) =>
                {
                    saveStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                status.Show,
                isClosing: () => isClosing,
                closing.Token));

        await saveStarted.Task;
        isClosing = true;
        closing.Cancel();
        Assert.True(await operation);
        Assert.False(coordinator.IsSelectionActive);
        Assert.False(status.IsVisible);
    }

    [Fact]
    public void Window_cancels_dedicated_save_lifetime_at_closing_notification()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains(
            "_saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("_saveCancellation.Cancel();", source, StringComparison.Ordinal);
        Assert.Contains("_saveCancellation.Token);", source, StringComparison.Ordinal);
        Assert.Contains("_saveCancellation.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_failure_status_model_is_independent_and_clearable()
    {
        var status = new FrameRateSaveStatus();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => { },
            (_, _) => Task.FromException(new InvalidOperationException("save failed")),
            status.Show,
            isClosing: () => false,
            CancellationToken.None);

        Assert.True(status.IsVisible);
        Assert.Equal("刷新设置未保存，本次会话仍已应用", status.Message);
        status.Clear();
        Assert.False(status.IsVisible);
        Assert.Null(status.Message);
    }

    [Fact]
    public void Performance_text_presentation_ignores_identical_text()
    {
        var presentation = new PerformanceTextPresentationState();

        Assert.True(presentation.TryUpdate("自动 60 FPS · 实际 — · — · — · —"));
        Assert.False(presentation.TryUpdate("自动 60 FPS · 实际 — · — · — · —"));
        Assert.True(presentation.TryUpdate("自动 60 FPS · 实际 30 FPS · 1.0 MiB/s · ZRLE · 20 ms"));
    }

    [Fact]
    public async Task Refresh_policy_selection_applies_before_persistence_completes()
    {
        var policy = FrameRefreshPolicy.Fixed(90);
        FrameRefreshPolicy? applied = null;
        FrameRefreshPolicy? saved = null;
        var allowSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operation = RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            policy,
            value => applied = value,
            async (value, cancellationToken) =>
            {
                saved = value;
                await allowSave.Task.WaitAsync(cancellationToken);
            },
            _ => { },
            isClosing: () => false,
            CancellationToken.None);

        Assert.Equal(policy, applied);
        Assert.False(operation.IsCompleted);
        allowSave.TrySetResult();
        await operation;
        Assert.Equal(policy, saved);
    }

    [Fact]
    public async Task Successful_refresh_policy_persistence_does_not_show_failure_status()
    {
        var policy = FrameRefreshPolicy.Fixed(90);
        var saved = new List<FrameRefreshPolicy>();
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            policy,
            _ => { },
            (value, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                saved.Add(value);
                return Task.CompletedTask;
            },
            statuses.Add,
            isClosing: () => false,
            CancellationToken.None);

        Assert.Equal([policy], saved);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Failed_refresh_policy_persistence_reports_failure_and_keeps_session_policy_applied()
    {
        var policy = FrameRefreshPolicy.Fixed(90);
        FrameRefreshPolicy applied = FrameRefreshPolicy.Automatic;
        string? status = null;

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            policy,
            value => applied = value,
            (_, _) => Task.FromException(new InvalidOperationException("save failed")),
            value => status = value,
            isClosing: () => false,
            CancellationToken.None);

        Assert.Equal(policy, applied);
        Assert.Equal("刷新设置未保存，本次会话仍已应用", status);
    }

    [Fact]
    public async Task Refresh_policy_selection_is_rejected_silently_when_window_is_already_closing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var applied = false;
        var persisted = false;
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => applied = true,
            (_, _) =>
            {
                persisted = true;
                return Task.CompletedTask;
            },
            statuses.Add,
            isClosing: () => false,
            cancellation.Token);

        Assert.False(applied);
        Assert.False(persisted);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Refresh_policy_selection_keeps_immediate_application_and_is_silent_when_save_is_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        var applied = false;
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => applied = true,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
            statuses.Add,
            isClosing: () => false,
            cancellation.Token);

        Assert.True(applied);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Refresh_policy_selection_is_rejected_when_closing_precedes_lifetime_cancellation()
    {
        var applied = false;
        var persisted = false;
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => applied = true,
            (_, _) =>
            {
                persisted = true;
                return Task.CompletedTask;
            },
            statuses.Add,
            isClosing: () => true,
            CancellationToken.None);

        Assert.False(applied);
        Assert.False(persisted);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Refresh_policy_selection_is_silent_when_ownership_is_disposed_after_save_starts()
    {
        var closing = false;
        var applied = false;
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => applied = true,
            (_, _) =>
            {
                closing = true;
                return Task.FromException(new ObjectDisposedException("ownership"));
            },
            statuses.Add,
            isClosing: () => closing,
            CancellationToken.None);

        Assert.True(applied);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Refresh_policy_selection_does_not_show_generic_save_failure_after_close_starts()
    {
        var closing = false;
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => { },
            (_, _) =>
            {
                closing = true;
                return Task.FromException(new InvalidOperationException("save failed during close"));
            },
            statuses.Add,
            isClosing: () => closing,
            CancellationToken.None);

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task Refresh_policy_selection_reports_object_disposed_when_session_is_not_closing()
    {
        var statuses = new List<string>();

        await RemoteSessionWindow.ApplyFrameRefreshPolicySelectionAsync(
            FrameRefreshPolicy.Fixed(90),
            _ => { },
            (_, _) => Task.FromException(new ObjectDisposedException("repository")),
            statuses.Add,
            isClosing: () => false,
            CancellationToken.None);

        Assert.Equal(["刷新设置未保存，本次会话仍已应用"], statuses);
    }

    [Fact]
    public void Main_window_passes_latest_profile_and_refresh_policy_persistence_to_remote_window()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "MainWindow.xaml.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains("profile: ownership.Profile", source, StringComparison.Ordinal);
        Assert.Contains(
            "updateFrameRefreshPolicy:\n" +
            "                        _sessionController.UpdateConnectedFrameRefreshPolicyAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains("new RemoteSessionReconnectRequest(", source, StringComparison.Ordinal);
        Assert.Contains("retryRequested: reconnectRequest.InvokeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConnectProfileWithHandlingAsync(\n                            effectiveProfile,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_captures_button_handled_key_down_and_key_up()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "InputSurface.AddHandler(\n            UIElement.KeyDownEvent,\n" +
            "            _keyDownHandler,\n            handledEventsToo: true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.AddHandler(\n            UIElement.KeyUpEvent,\n" +
            "            _keyUpHandler,\n            handledEventsToo: true)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("InputSurface.KeyDown +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InputSurface.KeyUp +=", source, StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.RemoveHandler(UIElement.KeyDownEvent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "InputSurface.RemoveHandler(UIElement.KeyUpEvent",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_keyboard_entry_point_consumes_local_quality_ui_before_remote_dispatch()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Equal(4, source.Split("ConsumeLocalQualityKeyboardInput()", StringSplitOptions.None).Length - 1);
        Assert.Contains("_textInput.Reset();", source, StringComparison.Ordinal);
        Assert.Contains("InputSurface.CharacterReceived -= OnCharacterReceived", source, StringComparison.Ordinal);
        Assert.Contains("FrameScrollViewer.SizeChanged -= _frameScrollViewerSizeChangedHandler", source, StringComparison.Ordinal);
        Assert.Contains("RootGrid.SizeChanged -= _rootGridSizeChangedHandler", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_quality_overlay_releases_remote_input_before_visibility_and_local_key_up_is_consumed()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("token => ReleaseInputAsync(token, releasePointer: true)", source, StringComparison.Ordinal);
        Assert.Contains("await _qualityOverlayOpenCoordinator.ToggleAsync", source, StringComparison.Ordinal);
        Assert.Contains("private void OnKeyUp", source, StringComparison.Ordinal);
        Assert.Contains("if (ConsumeLocalQualityKeyboardInput())", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_routes_pointer_events_through_frame_surface()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");
        var xaml = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml"))
            .ReplaceLineEndings("\n");
        var events = new (string EventName, string HandlerName)[]
        {
            ("PointerPressed", "_pointerPressedHandler"),
            ("PointerMoved", "_pointerMovedHandler"),
            ("PointerReleased", "_pointerReleasedHandler"),
            ("PointerCanceled", "_pointerCanceledHandler"),
            ("PointerWheelChanged", "_pointerWheelChangedHandler"),
        };

        foreach (var (eventName, handlerName) in events)
        {
            Assert.Contains(
                $"FrameSurface.AddHandler(\n" +
                $"            UIElement.{eventName}Event,\n" +
                $"            {handlerName},\n" +
                "            handledEventsToo: true);",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                $"FrameSurface.RemoveHandler(UIElement.{eventName}Event, {handlerName});",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.{eventName} +=",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.AddHandler(\n            UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"InputSurface.RemoveHandler(UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"ViewportHost.AddHandler(\n            UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"ViewportHost.RemoveHandler(UIElement.{eventName}Event",
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "FrameSurface.CapturePointer(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FrameSurface.ReleasePointerCapture(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            source.Split(
                "args.GetCurrentPoint(FrameSurface).Properties",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "args.GetCurrentPoint(ViewportHost).Position",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InputSurface.CapturePointer(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InputSurface.ReleasePointerCapture(args.Pointer)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"FrameSurface\"\n                    Background=\"Transparent\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_records_safe_keyboard_and_pointer_input_boundaries()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains(
            "new RemoteInputDiagnosticTracker(diagnosticSink)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputDropReason.SessionClosing",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputDropReason.InvalidTransform",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RemoteInputBoundary.UiCaptured",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RecordKeyboardCaptured()", source, StringComparison.Ordinal);
        Assert.Contains("RecordPointerCaptured()", source, StringComparison.Ordinal);
        Assert.Contains("RecordKeyboardDropped(", source, StringComparison.Ordinal);
        Assert.Contains("RecordPointerDropped(", source, StringComparison.Ordinal);
        Assert.Contains("private int _closingStarted;", source, StringComparison.Ordinal);
        Assert.Contains("IsInputClosing()", source, StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.Exchange(ref _closingStarted, 1)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("keysym", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Coordinate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_window_coalesces_moves_and_uses_ordered_pointer_barriers()
    {
        var windowSource = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");
        var viewModelSource = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "ViewModels", "RemoteSessionViewModel.cs"))
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "private void OnPointerMoved(object sender, PointerRoutedEventArgs args) =>\n" +
            "        QueuePointerMove(args);",
            windowSource,
            StringComparison.Ordinal);
        var localMoveUpdate = windowSource.IndexOf(
            "UpdateLocalPointerState(point, pointerMask);",
            StringComparison.Ordinal);
        var queuedMove = windowSource.IndexOf(
            "ViewModel.QueuePointerMove(pointerMask, point);",
            StringComparison.Ordinal);
        Assert.True(localMoveUpdate >= 0 && localMoveUpdate < queuedMove);
        Assert.Contains(
            "ViewModel.SendPointerBarrierAsync(\n" +
            "                    [new PointerWrite(wheelMask, point), new PointerWrite(baseMask, point)]",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.SendPointerBarrierAsync(\n" +
            "                    [new PointerWrite(0, releasePoint)]",
            windowSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.SendPointerAsync(", windowSource, StringComparison.Ordinal);

        Assert.Contains(
            "_pointerWrites = new RemotePointerWriteCoalescer(",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public void QueuePointerMove(byte buttons, RemotePoint point)",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public ValueTask SendPointerBarrierAsync(",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_session.SendPointerAsync(\n" +
            "                    write.Buttons,\n" +
            "                    write.Point.X,\n" +
            "                    write.Point.Y,\n" +
            "                    cancellationToken)",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "HandlePointerWriteFailureAsync(Exception exception) =>\n" +
            "        HandleInputFailureAsync(exception);",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains("MarkInputUnavailable();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(
            "QueueInputFailureStatusBestEffort();",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var operation = _dispatcher.InvokeAsync(update, CancellationToken.None);",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ = ObserveBestEffortUiUpdateAsync(operation);",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);",
            viewModelSource,
            StringComparison.Ordinal);

        var disposeStart = viewModelSource.IndexOf(
            "private async Task DisposeCoreAsync()",
            StringComparison.Ordinal);
        var disposeEnd = viewModelSource.IndexOf(
            "private static async Task CaptureFailureAsync",
            disposeStart,
            StringComparison.Ordinal);
        var disposeBody = viewModelSource[disposeStart..disposeEnd];
        var pointerDispose = disposeBody.IndexOf(
            "_pointerWrites.DisposeAsync()",
            StringComparison.Ordinal);
        var pointerAbort = disposeBody.IndexOf(
            "_pointerWrites.AbortActiveWrites()",
            StringComparison.Ordinal);
        var ownershipDispose = disposeBody.IndexOf(
            "DisposeOwnershipOnceAsync()",
            StringComparison.Ordinal);
        Assert.True(pointerAbort >= 0);
        Assert.True(pointerDispose >= 0);
        Assert.True(ownershipDispose >= 0);
        Assert.True(pointerAbort < pointerDispose);
        Assert.True(pointerDispose < ownershipDispose);

        var monitorStart = viewModelSource.IndexOf(
            "private async Task MonitorLoopsAsync",
            StringComparison.Ordinal);
        var monitorEnd = viewModelSource.IndexOf(
            "private async Task PublishDisconnectedBestEffortAsync",
            monitorStart,
            StringComparison.Ordinal);
        var monitorBody = viewModelSource[monitorStart..monitorEnd];
        var monitorAbort = monitorBody.IndexOf(
            "_pointerWrites.AbortActiveWrites()",
            StringComparison.Ordinal);
        var monitorDispose = monitorBody.IndexOf(
            "_pointerWrites.DisposeAsync()",
            StringComparison.Ordinal);
        var monitorOwnership = monitorBody.IndexOf(
            "DisposeOwnershipOnceAsync()",
            StringComparison.Ordinal);
        Assert.True(monitorAbort >= 0);
        Assert.True(monitorDispose >= 0);
        Assert.True(monitorOwnership >= 0);
        Assert.True(monitorAbort < monitorDispose);
        Assert.True(monitorDispose < monitorOwnership);
    }

    [Fact]
    public void Background_release_marshals_cursor_ui_and_still_attempts_both_remote_releases()
    {
        var source = File.ReadAllText(RepositoryFile(
                "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"))
            .ReplaceLineEndings("\n");
        var releaseStart = source.IndexOf(
            "private async Task ReleaseInputAsync(",
            StringComparison.Ordinal);
        var releaseEnd = source.IndexOf(
            "private void OnViewModelPropertyChanged",
            releaseStart,
            StringComparison.Ordinal);
        var releaseBody = source[releaseStart..releaseEnd];

        Assert.Contains(
            "cursorUpdate = UpdateCursorPositionBestEffortAsync();",
            releaseBody,
            StringComparison.Ordinal);
        Assert.Contains("await cursorUpdate;", releaseBody, StringComparison.Ordinal);
        Assert.Contains(
            "await _dispatcher.InvokeAsync(\n                UpdateCursorPosition,\n                CancellationToken.None)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateLocalPointerState(", releaseBody, StringComparison.Ordinal);
        var keyboardRelease = releaseBody.IndexOf(
            "await ViewModel.ReleaseInputAsync(cancellationToken)",
            StringComparison.Ordinal);
        var pointerRelease = releaseBody.IndexOf(
            "await ViewModel.SendPointerBarrierAsync(",
            StringComparison.Ordinal);
        Assert.True(keyboardRelease >= 0);
        Assert.True(pointerRelease >= 0);
        Assert.True(keyboardRelease < pointerRelease);
        Assert.Contains("ExceptionDispatchInfo.Capture(failure).Throw();", releaseBody, StringComparison.Ordinal);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
