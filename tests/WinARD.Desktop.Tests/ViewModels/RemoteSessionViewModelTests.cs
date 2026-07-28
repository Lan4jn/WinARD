using System.Runtime.InteropServices;
using System.Text.Json;
using WinARD.Application.Ports;
using WinARD.Desktop.Rendering;
using WinARD.Desktop.Services;
using WinARD.Desktop.Threading;
using WinARD.Desktop.ViewModels;
using WinARD.Infrastructure.Diagnostics;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707, CA2201

namespace WinARD.Desktop.Tests.ViewModels;

public sealed class RemoteSessionViewModelTests
{
    [Fact]
    public async Task Dispose_cancels_single_receive_loop_and_releases_resources_once()
    {
        var runtime = new BlockingRuntime();
        var lifetime = new TrackingLifetime();
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(viewModel.DisposeAsync().AsTask(), viewModel.DisposeAsync().AsTask());

        Assert.Equal([false], runtime.UpdateRequests);
        Assert.Equal(1, runtime.MaximumConcurrentReceives);
        Assert.True(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Dispose_releases_ownership_before_waiting_for_receive_loop()
    {
        var receiveReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new OwnershipBoundRuntime(receiveReleased.Task);
        var lifetime = new SignalingLifetime(receiveReleased);
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, lifetime.DisposeCount);
        Assert.True(runtime.ReceiveExited);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Network_failure_is_observed_sanitized_and_releases_ownership()
    {
        var runtime = new FailingRuntime();
        var lifetime = new TrackingLifetime();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("连接已中断。", viewModel.StatusMessage);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Receive_failure_diagnostic_preserves_nested_aggregate_base_exception()
    {
        var runtime = new FailingWithExceptionRuntime(
            new AggregateException(
                new AggregateException(
                    new IOException("sensitive endpoint"))));
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.IsType<IOException>(diagnostic.Exception);
        Assert.DoesNotContain(
            diagnostic.Fields ?? [],
            item => item.Name == "PresentationStage");
    }

    [Fact]
    public async Task Receive_protocol_failure_exports_only_safe_public_fingerprint_fields()
    {
        const string secret = "decoder leaked-secret-payload clipboard=private-clipboard host=private-host frame=17,34,51,68";
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.DecoderFailure,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            0xFA,
            -239,
            7);
        var protocolException = RfbProtocolException.Create(secret, failure);
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(protocolException),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Same(protocolException, diagnostic.Exception);
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        Assert.Equal(
            [
                ("ProtocolFailureKind", "DecoderFailure"),
                ("ProtocolReadStage", "FramebufferRectanglePayload"),
                ("ServerMessageType", "0xFA"),
                ("EncodingId", "-239"),
                ("RectangleIndex", "7"),
            ],
            fields.Select(field => (field.Name, field.Value)).ToArray());
        Assert.All(fields, field => Assert.Equal(DiagnosticFieldCategory.Public, field.Category));

        var stored = Assert.Single(
            safeDiagnosticSink.Snapshot(),
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Equal(nameof(RfbProtocolException), stored.Exception?.Type);
        Assert.Equal($"0x{protocolException.HResult:X8}", stored.Exception?.HResult);
        var storedJson = JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("leaked-secret-payload", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-clipboard", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("17,34,51,68", storedJson, StringComparison.Ordinal);
        Assert.Equal(new RemoteFramebufferSize(1, 1), viewModel.FramebufferSize);
        Assert.Equal("连接已中断。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Receive_protocol_failure_exports_only_present_optional_fields()
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(RfbProtocolException.Create(
                "decoder failure",
                new RfbProtocolFailureInfo(
                    RfbProtocolFailureKind.UnsupportedEncoding,
                    EncodingId: 16))),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        Assert.Equal(
            [("ProtocolFailureKind", "UnsupportedEncoding"), ("EncodingId", "16")],
            fields.Select(field => (field.Name, field.Value)).ToArray());
        Assert.All(fields, field => Assert.Equal(DiagnosticFieldCategory.Public, field.Category));
    }

    [Theory]
    [MemberData(nameof(NonProtocolReceiveFailures))]
    public async Task Non_protocol_receive_failures_do_not_export_protocol_fields(Exception exception)
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(exception),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_protocol_failure_uses_outermost_failure_before_eof_base_exception()
    {
        const string secret = "decoder leaked-secret-payload inner-chain-secret";
        var protocolException = new RfbProtocolException(
                secret,
                new EndOfStreamException("eof inner-chain-secret"))
            .WithContext(new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead))
            .WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.DecoderFailure,
                RfbProtocolReadStage.FramebufferRectanglePayload,
                0,
                7,
                3));
        Assert.IsType<EndOfStreamException>(protocolException.GetBaseException());
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(new IOException(
                "transport wrapper inner-chain-secret",
                protocolException)),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
        Assert.Same(protocolException, diagnostic.Exception);
        Assert.Equal(
            [
                ("ProtocolFailureKind", "TruncatedRead"),
                ("ProtocolReadStage", "FramebufferRectanglePayload"),
                ("ServerMessageType", "0x00"),
                ("EncodingId", "7"),
                ("RectangleIndex", "3"),
            ],
            diagnostic.Fields!.Select(field => (field.Name, field.Value)).ToArray());
        var storedJson = JsonSerializer.Serialize(Assert.Single(safeDiagnosticSink.Snapshot()));
        Assert.DoesNotContain("leaked-secret-payload", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("inner-chain-secret", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(EndOfStreamException), storedJson, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> NonProtocolReceiveFailures =>
    [
        [new IOException("network details")],
        [new AggregateException(new OperationCanceledException("receive canceled"))],
    ];

    [Fact]
    public async Task Receive_failure_with_ten_thousand_exception_nodes_completes_safely()
    {
        var diagnostic = await RecordReceiveFailureAsync(
            WrapExceptionChain(new IOException("leaf"), 10_000));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task Receive_failure_repeated_aggregate_reference_is_visited_once()
    {
        var sharedBranch = WrapExceptionChain(new IOException("shared leaf"), 200);
        var firstFailure = RfbProtocolException.Create(
            "first failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.MalformedClipboard));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(
            sharedBranch,
            sharedBranch,
            firstFailure));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("MalformedClipboard", field.Value);
    }

    [Fact]
    public async Task Receive_failure_aggregate_branches_use_original_depth_first_order()
    {
        var firstFailure = RfbProtocolException.Create(
            "first failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.UnsupportedEncoding));
        var secondFailure = RfbProtocolException.Create(
            "second failure",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(new AggregateException(
            new IOException("first wrapper", firstFailure),
            secondFailure));

        var field = Assert.Single(diagnostic.Fields!);
        Assert.Equal("ProtocolFailureKind", field.Name);
        Assert.Equal("UnsupportedEncoding", field.Value);
    }

    [Fact]
    public async Task Receive_failure_prefers_outer_protocol_failure()
    {
        var protocolException = RfbProtocolException.Create(
                "outer failure",
                new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead))
            .WithContext(new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.DecoderFailure,
                RfbProtocolReadStage.ClipboardPayload));
        var diagnostic = await RecordReceiveFailureAsync(protocolException);

        Assert.Equal(
            [("ProtocolFailureKind", "TruncatedRead"), ("ProtocolReadStage", "ClipboardPayload")],
            diagnostic.Fields!.Select(field => (field.Name, field.Value)).ToArray());
    }

    [Fact]
    public async Task Receive_failure_does_not_scan_protocol_failure_beyond_node_limit()
    {
        var protocolException = RfbProtocolException.Create(
            "too deep",
            new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure));
        var diagnostic = await RecordReceiveFailureAsync(
            WrapExceptionChain(protocolException, 1_000));

        Assert.Empty(diagnostic.Fields ?? []);
    }

    [Fact]
    public async Task ThrowingDiagnosticSinkCannotSuppressTerminalErrorOrOwnershipRelease()
    {
        var lifetime = new TrackingLifetime();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: new ThrowingSink());

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("连接已中断。", viewModel.StatusMessage);
        Assert.Equal("REMOTE_SESSION_INTERRUPTED", viewModel.Error?.Code);
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task InterruptedErrorFlowsThroughCardRetryToCloseAndOneNewConnection()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);
        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        var card = ConnectionErrorViewModel.FromError(viewModel.Error!);
        var sequence = new List<string>();
        var connectCalls = 0;
        var openCalls = 0;
        var retry = new RemoteSessionRetryAction(
            async () =>
            {
                sequence.Add("close");
                await viewModel.DisposeAsync();
            },
            _ =>
            {
                sequence.Add("connect");
                connectCalls++;
                openCalls++;
                return Task.CompletedTask;
            });
        var handler = new ConnectionErrorActionHandler(
            [new(ConnectionErrorActionKind.Retry, retry.ExecuteAsync)]);

        await handler.HandleAsync(
            card.Actions.Single(action => action.Kind == ConnectionErrorActionKind.Retry).Kind,
            CancellationToken.None);

        Assert.Equal(["close", "connect"], sequence);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, connectCalls);
        Assert.Equal(1, openCalls);
    }

    [Fact]
    public void ThrowingDiagnosticSinkCannotSuppressInputErrorPresentation()
    {
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink: new ThrowingSink());

        viewModel.ObserveInputFailure(new InvalidOperationException("raw"));

        Assert.Equal("REMOTE_INPUT_FAILED", viewModel.Error?.Code);
    }

    [Fact]
    public async Task Receive_failure_releases_ownership_when_status_dispatcher_fails()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        try
        {
            await viewModel.StartAsync(CancellationToken.None);
            await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, lifetime.DisposeCount);
        }
        finally
        {
            _ = await Assert.ThrowsAsync<AggregateException>(
                () => viewModel.DisposeAsync().AsTask());
        }

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_releases_presenter_through_ui_dispatcher()
    {
        var dispatcher = new TrackingDispatcher();
        var presenter = new DispatcherBoundPresenter(() => dispatcher.IsDispatching);
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            new TrackingLifetime(),
            presenter,
            dispatcher,
            clipboardBridge: null);

        await viewModel.DisposeAsync();

        Assert.True(presenter.WasDisposed);
    }

    [Fact]
    public async Task Dispose_releases_ownership_when_presenter_cleanup_fails()
    {
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new BlockingRuntime(),
            lifetime,
            new ThrowingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        _ = await Assert.ThrowsAsync<AggregateException>(() => viewModel.DisposeAsync().AsTask());

        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Dispose_aggregates_synchronous_ownership_failure_and_disposes_ownership_once()
    {
        var lifetime = new SynchronouslyThrowingLifetime();
        var presenter = new TrackingPresenter();
        var viewModel = new RemoteSessionViewModel(
            new FailingRuntime(),
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());

        Assert.Contains(
            failure.InnerExceptions,
            exception => exception is InvalidOperationException
                && exception.Message == "ownership cleanup failed");
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.DisposeCount);
    }

    [Fact]
    public async Task Incremental_update_is_requested_only_after_framebuffer_messages()
    {
        var runtime = new ScriptedRuntime(
            new RemoteClipboardMessage("clipboard"),
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                [0, 0, 0, 255],
                4,
                [new RemoteRectangle(0, 0, 1, 1)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.Equal([(false, 0), (true, 2)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Framebuffer_size_tracks_the_latest_frame()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 3),
                new byte[24],
                8,
                [new RemoteRectangle(0, 0, 2, 3)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new RemoteFramebufferSize(2, 3), viewModel.FramebufferSize);
    }

    [Fact]
    public async Task Metadata_resize_requests_full_new_size_then_resumes_incremental_updates()
    {
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                new byte[8],
                8,
                []),
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                new byte[8],
                8,
                [new RemoteRectangle(0, 0, 2, 1)]));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();

        Assert.Equal([(false, 0), (false, 1), (true, 2)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Cursor_only_update_is_published_and_requests_the_next_incremental_update()
    {
        var owner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4)));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(viewModel.RemoteCursor);
        Assert.Equal([1, 2, 3, 4], viewModel.RemoteCursor.Bgra32.ToArray());
        Assert.False(owner.IsDisposed);
        Assert.Equal([(false, 0), (true, 1)], runtime.UpdateRequests);
    }

    [Fact]
    public async Task Hidden_cursor_releases_the_previous_cursor_and_clears_ui_state()
    {
        var visibleOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var hiddenOwner = new TrackingMemoryOwner([]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, visibleOwner, 4)),
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 0, 0, hiddenOwner, 0)));
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(viewModel.RemoteCursor);
        Assert.True(visibleOwner.IsDisposed);
        Assert.True(hiddenOwner.IsDisposed);
    }

    [Fact]
    public async Task Dispose_releases_the_current_cursor_once()
    {
        var owner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 1, 1, owner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await runtime.MessagesConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(viewModel.DisposeAsync().AsTask(), viewModel.DisposeAsync().AsTask());

        Assert.True(owner.IsDisposed);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task Permanent_presenter_failure_cancels_receive_releases_transport_and_completes()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var lifetime = new TrackingLifetime();
        var presenter = new PermanentlyFailingPresenter();
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.Completion.IsCompletedSuccessfully);
        Assert.Equal("画面呈现失败，会话正在关闭。", viewModel.StatusMessage);
        Assert.Equal(1, presenter.PresentCount);
        Assert.Equal(2, runtime.ReceiveCount);
        Assert.True(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.True(frameOwner.IsDisposed);
        Assert.Equal(1, frameOwner.DisposeCount);
    }

    [Fact]
    public async Task Presentation_failure_diagnostic_preserves_stage_and_hresult()
    {
        var frameOwner = new TrackingMemoryOwner([17, 34, 51, 68]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var presenter = new FailingWithExceptionPresenter(
            new D3DPresentationException(
                D3DPresentationStage.Present1,
                new COMException(
                    "sensitive native details",
                    unchecked((int)0x887A0001))));
        using var redactor = new SecretRedactor();
        var safeDiagnosticSink = new InMemorySafeDiagnosticSink(redactor);
        var diagnosticSink = new RecordingDiagnosticSink(safeDiagnosticSink);
        await using var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            presenter,
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        var diagnostic = Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_PRESENTATION_FAILED");
        Assert.Equal("Remote session loop failed.", diagnostic.Message);
        var exception = Assert.IsType<D3DPresentationException>(diagnostic.Exception);
        Assert.Equal(unchecked((int)0x887A0001), exception.HResult);
        var fields = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticField>>(diagnostic.Fields);
        var field = Assert.Single(fields);
        Assert.Equal("PresentationStage", field.Name);
        Assert.Equal("Present1", field.Value);
        Assert.Equal(DiagnosticFieldCategory.Public, field.Category);
        Assert.DoesNotContain(
            "sensitive native details",
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fields,
            item => item.Value?.Contains("sensitive native details", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(
            fields,
            item => item.Value?.Contains("17, 34, 51, 68", StringComparison.Ordinal) is true);

        var storedEvent = Assert.Single(
            safeDiagnosticSink.Snapshot(),
            item => item.Code == "REMOTE_PRESENTATION_FAILED");
        var storedField = Assert.Single(storedEvent.Fields);
        Assert.Equal("PresentationStage", storedField.Name);
        Assert.Equal("Present1", storedField.Value);
        Assert.Equal(DiagnosticFieldCategory.Public, storedField.Category);
        Assert.Equal(nameof(D3DPresentationException), storedEvent.Exception?.Type);
        Assert.Equal("0x887A0001", storedEvent.Exception?.HResult);
        var storedJson = JsonSerializer.Serialize(storedEvent);
        Assert.DoesNotContain(
            "sensitive native details",
            storedJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain("[17,34,51,68]", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("17, 34, 51, 68", storedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ESIzRA==", storedJson, StringComparison.Ordinal);
        Assert.Equal(1, presenter.PresentCount);
        Assert.True(frameOwner.IsDisposed);
        Assert.Equal(1, frameOwner.DisposeCount);
    }

    [Fact]
    public async Task Presenter_failure_still_terminates_when_status_dispatcher_fails()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var runtime = new SingleFrameThenBlockingRuntime(frameOwner);
        var lifetime = new TrackingLifetime();
        var presenter = new PermanentlyFailingPresenter();
        var dispatcher = new PresentationThenFailingDispatcher();
        var viewModel = new RemoteSessionViewModel(
            runtime,
            lifetime,
            presenter,
            dispatcher,
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.Completion.IsCompletedSuccessfully);
        Assert.True(runtime.ReceiveCancelled);
        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(1, presenter.PresentCount);
        Assert.True(frameOwner.IsDisposed);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, lifetime.DisposeCount);
    }

    [Fact]
    public async Task Hidden_cursor_owner_is_released_once_when_ui_dispatch_fails()
    {
        var cursorOwner = new TrackingMemoryOwner([]);
        var lifetime = new TrackingLifetime();
        var viewModel = new RemoteSessionViewModel(
            new ScriptedRuntime(
                new RemoteCursorMessage(new RemoteCursorUpdate(0, 0, 0, 0, cursorOwner, 0))),
            lifetime,
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, lifetime.DisposeCount);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, cursorOwner.DisposeCount);
    }

    [Fact]
    public async Task Mixed_update_releases_cursor_when_framebuffer_size_dispatch_fails()
    {
        var frameOwner = new TrackingMemoryOwner(new byte[8]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(2, 1),
                frameOwner,
                8,
                8,
                [new RemoteRectangle(0, 0, 2, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new FailingDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, frameOwner.DisposeCount);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Mixed_update_releases_cursor_when_frame_packet_validation_fails()
    {
        var frameOwner = new TrackingMemoryOwner(new byte[4]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                frameOwner,
                4,
                1,
                [new RemoteRectangle(0, 0, 1, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, cursorOwner.DisposeCount);
        Assert.Equal(1, frameOwner.DisposeCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Mixed_update_keeps_cursor_owner_when_dispatch_action_transfers_before_task_cancels()
    {
        var frameOwner = new TrackingMemoryOwner([0, 0, 0, 255]);
        var cursorOwner = new TrackingMemoryOwner([1, 2, 3, 4]);
        var runtime = new ScriptedRuntime(
            new RemoteFramebufferMessage(
                new RemoteFramebufferSize(1, 1),
                frameOwner,
                4,
                4,
                [new RemoteRectangle(0, 0, 1, 1)],
                new RemoteCursorUpdate(0, 0, 1, 1, cursorOwner, 4)));
        var viewModel = new RemoteSessionViewModel(
            runtime,
            new TrackingLifetime(),
            new TrackingPresenter(),
            new ActionThenCanceledDispatcher(),
            clipboardBridge: null);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(viewModel.RemoteCursor);
        Assert.False(cursorOwner.IsDisposed);

        _ = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.DisposeAsync().AsTask());
        Assert.Equal(1, cursorOwner.DisposeCount);
    }

    private static async Task<SafeDiagnosticEventInput> RecordReceiveFailureAsync(
        Exception exception)
    {
        var diagnosticSink = new RecordingDiagnosticSink();
        await using var viewModel = new RemoteSessionViewModel(
            new FailingWithExceptionRuntime(exception),
            new TrackingLifetime(),
            new TrackingPresenter(),
            new InlineDispatcher(),
            clipboardBridge: null,
            diagnosticSink);

        await viewModel.StartAsync(CancellationToken.None);
        await viewModel.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        return Assert.Single(
            diagnosticSink.Events,
            item => item.Code == "REMOTE_SESSION_INTERRUPTED");
    }

    private static Exception WrapExceptionChain(Exception innermost, int wrapperCount)
    {
        var exception = innermost;
        for (var index = 0; index < wrapperCount; index++)
        {
            exception = new IOException($"wrapper-{index}", exception);
        }

        return exception;
    }

    private sealed class BlockingRuntime : IRemoteSessionRuntime
    {
        private int _receives;
        private int _activeReceives;
        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<bool> UpdateRequests { get; } = [];
        public int MaximumConcurrentReceives { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add(incremental);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _receives);
            var active = Interlocked.Increment(ref _activeReceives);
            MaximumConcurrentReceives = Math.Max(MaximumConcurrentReceives, active);
            ReceiveEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ReceiveCancelled = true;
                throw;
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeReceives);
            }
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingRuntime : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(new IOException("sensitive endpoint"));
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWithExceptionRuntime(Exception exception) : IRemoteSessionRuntime
    {
        public RemoteFramebufferSize FramebufferSize => new(1, 1);
        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromException<RemoteServerMessage>(exception);
        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class OwnershipBoundRuntime(Task receiveReleased) : IRemoteSessionRuntime
    {
        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReceiveExited { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveEntered.TrySetResult();
            await receiveReleased;
            ReceiveExited = true;
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedRuntime(params RemoteServerMessage[] messages) : IRemoteSessionRuntime
    {
        private readonly Queue<RemoteServerMessage> _messages = new(messages);
        private int _receiveCount;
        public TaskCompletionSource MessagesConsumed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(bool Incremental, int ReceiveCount)> UpdateRequests { get; } = [];
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken)
        {
            UpdateRequests.Add((incremental, _receiveCount));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_messages.TryDequeue(out var message))
            {
                _receiveCount++;
                if (_messages.Count == 0)
                {
                    MessagesConsumed.TrySetResult();
                }

                return message;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleFrameThenBlockingRuntime(System.Buffers.IMemoryOwner<byte> owner) : IRemoteSessionRuntime
    {
        public int ReceiveCount { get; private set; }
        public bool ReceiveCancelled { get; private set; }
        public RemoteFramebufferSize FramebufferSize => new(1, 1);

        public ValueTask RequestFramebufferUpdateAsync(bool incremental, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask<RemoteServerMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            if (ReceiveCount == 1)
            {
                return new RemoteFramebufferMessage(
                    new RemoteFramebufferSize(1, 1),
                    owner,
                    4,
                    4,
                    [new RemoteRectangle(0, 0, 1, 1)]);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }
            catch (OperationCanceledException)
            {
                ReceiveCancelled = true;
                throw;
            }
        }

        public ValueTask SendPointerAsync(byte buttons, int x, int y, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendKeyAsync(uint keysym, bool down, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendClipboardTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class TrackingMemoryOwner(byte[] bytes) : System.Buffers.IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes;

        public bool IsDisposed => _bytes is null;
        public int DisposeCount { get; private set; }
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(TrackingMemoryOwner));

        public void Dispose()
        {
            DisposeCount++;
            _bytes = null;
        }
    }

    private sealed class SignalingLifetime(TaskCompletionSource receiveReleased) : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            receiveReleased.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronouslyThrowingLifetime : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw new InvalidOperationException("ownership cleanup failed");
        }
    }

    private sealed class TrackingPresenter : IFramePresenter
    {
        public int DisposeCount { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingPresenter : IFramePresenter
    {
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("presenter cleanup failed"));
    }

    private sealed class PermanentlyFailingPresenter : IFramePresenter
    {
        public int PresentCount { get; private set; }
        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
            PresentCount++;
            throw new InvalidOperationException("sensitive GPU failure");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingWithExceptionPresenter(Exception exception) : IFramePresenter
    {
        public int PresentCount { get; private set; }

        public void Resize(int width, int height) { }

        public void Present(
            ReadOnlySpan<byte> bgra32,
            int stride,
            IReadOnlyList<RemoteRectangle> dirtyRectangles)
        {
            PresentCount++;
            throw exception;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DispatcherBoundPresenter(Func<bool> isDispatching) : IFramePresenter
    {
        public bool WasDisposed { get; private set; }
        public void Resize(int width, int height) { }
        public void Present(ReadOnlySpan<byte> bgra32, int stride, IReadOnlyList<RemoteRectangle> dirtyRectangles) { }

        public ValueTask DisposeAsync()
        {
            if (!isDispatching())
            {
                throw new InvalidOperationException("Presenter disposal must run through the UI dispatcher.");
            }

            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingDispatcher : IUiDispatcher
    {
        public bool IsDispatching { get; private set; }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsDispatching = true;
            try
            {
                action();
            }
            finally
            {
                IsDispatching = false;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ISafeDiagnosticSink
    {
        public void Write(SafeDiagnosticEventInput diagnosticEvent) =>
            throw new InvalidOperationException("sink failed");

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class RecordingDiagnosticSink(ISafeDiagnosticSink? inner = null)
        : ISafeDiagnosticSink
    {
        public List<SafeDiagnosticEventInput> Events { get; } = [];

        public void Write(SafeDiagnosticEventInput diagnosticEvent)
        {
            Events.Add(diagnosticEvent);
            inner?.Write(diagnosticEvent);
        }

        public IReadOnlyList<SafeDiagnosticEvent> Snapshot() => [];
    }

    private sealed class FailingDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("dispatcher unavailable"));
    }

    private sealed class PresentationThenFailingDispatcher : IUiDispatcher
    {
        private int _invocationCount;

        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                action();
                return Task.CompletedTask;
            }

            return Task.FromException(new InvalidOperationException("status dispatcher unavailable"));
        }
    }

    private sealed class ActionThenCanceledDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }
    }
}
