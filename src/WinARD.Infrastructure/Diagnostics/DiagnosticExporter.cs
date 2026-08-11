using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinARD.Infrastructure.Diagnostics;

public sealed record DiagnosticApplicationInfo(
    string Application,
    string ApplicationVersion,
    string OperatingSystem,
    string DotNetVersion,
    string WindowsAppSdkVersion);

public sealed record DiagnosticProfileSummary(
    string DisplayName,
    string Host,
    int Port,
    string Username,
    string ProtocolVersion,
    string SecurityType,
    IReadOnlyDictionary<string, long>? EncodingStatistics = null,
    string? ErrorCode = null,
    string? CorrelationId = null);

public sealed record DiagnosticQualitySummary(
    string Preset,
    long? TargetBytesPerSecond,
    string QualityLevel,
    string ContentState,
    string Color,
    int ScalePercent,
    string EncodingName,
    int? TargetFramesPerSecond,
    int ActualFramesPerSecond,
    long AverageBytesPerSecond,
    long PeakBytesPerSecond,
    int ResponseMilliseconds,
    string ZlibCapability,
    string Rgb565Capability,
    string ServerScalingCapability,
    string AppleColor1002Capability,
    string AppleGrayscale1001Capability,
    bool SafeOnlinePixelFormatSwitch,
    bool SafeOnlineScaleSwitch,
    string Reason,
    bool TargetSatisfied);

public sealed record DiagnosticExportContext(
    DiagnosticApplicationInfo Application,
    IReadOnlyList<DiagnosticProfileSummary> Profiles,
    IReadOnlyDictionary<string, long> PerformanceCounters,
    bool IncludeHosts,
    DiagnosticQualitySummary? Quality = null)
{
    public static DiagnosticExportContext Empty { get; } = new(
        new DiagnosticApplicationInfo(
            "WinARD",
            typeof(DiagnosticExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.OSVersion.VersionString,
            Environment.Version.ToString(),
            "unknown"),
        [],
        new Dictionary<string, long>(),
        IncludeHosts: false);
}

public sealed record DiagnosticExportLimits(
    int MaxEvents = 500,
    int MaxFieldLength = 4_096,
    long MaxArchiveBytes = 5 * 1024 * 1024,
    int MaxProfiles = 100,
    int MaxEncodingStatisticsPerProfile = 100,
    int MaxPerformanceCounters = 200,
    int MaxFieldsPerEvent = 100,
    int MaxStringUtf8Bytes = 16 * 1024,
    long MaxUncompressedBytes = 20 * 1024 * 1024,
    long MaxEntryUncompressedBytes = 16 * 1024 * 1024);

public sealed class DiagnosticExporter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] ForbiddenFieldNameTokens =
    [
        "coordinate",
        "pointerx",
        "pointery",
        "keysym",
        "pixel",
        "ciphertext",
        "sequence",
        "keycontent",
        "clipboardcontent",
    ];
    private static readonly HashSet<string> AllowedPerformanceCounterKeys = new(StringComparer.Ordinal)
    {
        "Session.RefreshMode", "Session.TargetFps", "Session.ActualFps",
        "Session.ReceiveBytesPerSecond", "Session.ResponseMilliseconds",
        "Session.PresentationMilliseconds", "Session.InputWriteMilliseconds",
        "Session.InputQueueDepth", "Session.PointerMovesCoalesced",
        "Session.ReceiveRateInsideSshTunnel", "Session.AutomaticTargetChanges",
    };
    private static readonly HashSet<string> AllowedEncodingStatisticKeys = new(StringComparer.Ordinal)
    {
        "Raw", "CopyRect", "Zlib", "ZRLE", "DesktopSize", "Cursor",
        "ARD.DisplayInfo", "ARD.SessionEncryption", "ARD.DisplayInfo2", "Encoding.Other",
    };
    private static readonly HashSet<string> AllowedQualityPresets = new(StringComparer.Ordinal)
    {
        "Automatic", "Original", "Balanced", "Smooth", "Custom",
    };
    private static readonly HashSet<string> AllowedQualityLevels = new(StringComparer.Ordinal)
    {
        "Q0", "Q1", "Q2", "Q3", "Q4",
    };
    private static readonly HashSet<string> AllowedQualityContentStates = new(StringComparer.Ordinal)
    {
        "Idle", "Interactive", "Motion", "Recovery",
    };
    private static readonly HashSet<string> AllowedQualityColors = new(StringComparer.Ordinal)
    {
        "Automatic", "Full32", "Color16", "Grayscale",
    };
    private static readonly HashSet<string> AllowedEncodingNames = new(StringComparer.Ordinal)
    {
        "Raw", "CopyRect", "Zlib", "ZRLE", "DesktopSize", "Cursor",
        "ARD.DisplayInfo", "ARD.SessionEncryption", "ARD.DisplayInfo2", "Other",
    };
    private static readonly HashSet<string> AllowedCapabilitySupportValues = new(StringComparer.Ordinal)
    {
        "Unknown", "Unsupported", "Advertised", "Observed",
    };
    private static readonly HashSet<string> AllowedQualityDecisionReasons = new(StringComparer.Ordinal)
    {
        "Initial", "MotionDetected", "SustainedOverTarget", "SevereOverTarget", "StableRecovery",
        "UserConstraint", "CapabilityLimited", "TargetUnsatisfied",
    };
    private static readonly HashSet<string> AllowedProfileProtocolVersions = new(StringComparer.Ordinal)
    {
        "RFB 3.x", "3.3", "3.7", "3.8", "003.003", "003.007", "003.008",
    };
    private static readonly HashSet<string> AllowedProfileSecurityTypes = new(StringComparer.Ordinal)
    {
        "ARD-30", "30", "AppleRemoteDesktop",
    };
    private static readonly HashSet<string> AllowedDiagnosticCategories = new(StringComparer.Ordinal)
    {
        "InvalidRefreshRateRange",
    };
    private static readonly HashSet<string> AllowedConnectionStages = new(StringComparer.Ordinal)
    {
        "Resolving", "Connecting", "Negotiating", "Authenticating", "Initializing", "Connected",
        "Reconnecting", "Disconnecting",
    };
    private static readonly HashSet<string> AllowedDiagnosticActions = new(StringComparer.Ordinal)
    {
        "Retry", "ReenterCredentials", "UnlockVault", "OpenHelp", "CopyCorrelationId",
        "ExportDiagnostics", "Cancel", "Disconnect", "ReplaceHostKey", "RemoteSessionClosed",
        "AutoFBUpdateFailed", "AutoFBUpdateSent", "Consumed", "UnknownConsumed",
    };
    private static readonly HashSet<string> AllowedInputKinds = new(StringComparer.Ordinal)
    {
        "Keyboard", "Pointer",
    };
    private static readonly HashSet<string> AllowedInputBoundaries = new(StringComparer.Ordinal)
    {
        "UiCaptured", "UiDropped", "ProtocolWriteStarted", "ProtocolWriteCompleted",
    };
    private static readonly HashSet<string> AllowedInputDropReasons = new(StringComparer.Ordinal)
    {
        "SessionClosing", "InvalidTransform",
    };
    private static readonly HashSet<string> AllowedProtocolFailureKinds = new(StringComparer.Ordinal)
    {
        "UnexpectedServerMessage", "UnsupportedEncoding", "TruncatedRead", "MalformedFramebufferUpdate",
        "MalformedClipboard", "DecoderFailure", "MalformedArdStateChange", "RemoteSessionClosed",
        "ArdEncryptionNegotiation", "ArdEncryptionPacket", "ArdEncryptionIntegrity", "MalformedHandshake",
    };
    private static readonly HashSet<string> AllowedRfbHandshakeStages = new(StringComparer.Ordinal)
    {
        "VersionBanner", "VersionParse", "SecurityType33", "SecurityTypeCount", "SecurityTypes",
    };
    private static readonly HashSet<string> AllowedPresentationStages = new(StringComparer.Ordinal)
    {
        "CreateDevice", "CreateTexture2D", "CreateSwapChainForComposition", "SetSwapChain", "GetBuffer",
        "UpdateSubresource", "CopySubresourceRegion", "Present1", "RecoveryDetachSwapChain",
        "RecoveryCreateSwapChain", "RecoverySetSwapChain", "RecoveryGetBuffer", "RecoveryPresent",
    };
    private static readonly HashSet<string> AllowedProtocolReadStages = new(StringComparer.Ordinal)
    {
        "ServerMessageType", "FramebufferHeader", "FramebufferRectangleHeader",
        "FramebufferRectanglePayload", "ClipboardHeader", "ClipboardPayload", "ArdStateChangeHeader",
        "ArdStateChangePayload",
    };
    private static readonly HashSet<string> AllowedArdEncryptionStages = new(StringComparer.Ordinal)
    {
        "OuterLength", "TruncatedCiphertext", "CbcDecrypt", "PlaintextTooShort", "PayloadLength",
        "Padding", "Integrity", "StateCommit",
    };
    private static readonly HashSet<string> AllowedExceptionTypes = new(StringComparer.Ordinal)
    {
        "Exception", "InvalidOperationException", "IOException", "TimeoutException",
        "OperationCanceledException", "TaskCanceledException", "UnauthorizedAccessException",
        "ArgumentException", "ArgumentOutOfRangeException", "FormatException", "SocketException",
        "TransportTimeoutException", "SessionAlreadyActiveException", "OpenSshTunnelException",
        "OpenSshTunnelCleanupTimeoutException", "OpenSshAuthenticationUnsupportedException",
        "SshHostKeyUnknownException", "SshHostKeyChangedException", "OpenSshSystemDirectoryException",
        "OpenSshPlatformNotSupportedException", "OpenSshExecutableNotFoundException",
        "OpenSshExecutableConfigurationException", "OpenSshAskPassException", "OpenSshKeyScanException",
        "OpenSshOutputLimitExceededException", "OpenSshKeyScanProcessException",
        "OpenSshProcessCleanupTimeoutException", "VaultConcurrencyException", "VaultFormatException",
        "VaultLockedException", "MigrationSourceDeleteUncertainException",
        "MigrationTargetWriteUncertainException", "ArdAuthenticationRejectedException",
        "ArdExtendedInitializationRequiredException", "ArdControlNotAllowedException",
        "ArdSessionCommandUnavailableException", "ArdSessionDeniedException", "ArdSessionMalformedException",
        "RfbConnectionRejectedException", "RfbProtocolException", "UnsupportedRfbVersionException",
        "UnsupportedSecurityTypeException", "UnsupportedSchemaVersionException",
        "DeviceEndpointConflictException", "ConnectionFailedException", "D3DPresentationException",
    };
    private readonly ISafeDiagnosticSink _sink;
    private readonly SecretRedactor _redactor;
    private readonly DiagnosticExportLimits _limits;
    private readonly object _gate = new();
    private bool _busy;
    private bool _disposed;

    public DiagnosticExporter(
        ISafeDiagnosticSink sink,
        SecretRedactor redactor,
        DiagnosticExportLimits? limits = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _limits = limits ?? new DiagnosticExportLimits();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxProfiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEncodingStatisticsPerProfile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxPerformanceCounters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxFieldsPerEvent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxStringUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxUncompressedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxEntryUncompressedBytes);
    }

    public async Task ExportAsync(
        string destinationPath,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(context);
        EnterExport(cancellationToken);

        string? temporaryPath = null;
        try
        {
            var fullDestination = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullDestination) ??
                throw new ArgumentException("The destination must have a parent directory.", nameof(destinationPath));
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
            await WriteArchiveAsync(temporaryPath, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(temporaryPath).Length;
            if (length > _limits.MaxArchiveBytes)
            {
                throw new InvalidOperationException("The diagnostic archive exceeded its configured size limit.");
            }

            if (File.Exists(fullDestination))
            {
                File.Replace(temporaryPath, fullDestination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullDestination);
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }

            ExitExport();
        }
    }

    private void EnterExport(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_busy)
            {
                throw new InvalidOperationException("A diagnostic export is already in progress.");
            }

            _busy = true;
        }
    }

    private void ExitExport()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    private async Task WriteArchiveAsync(
        string temporaryPath,
        DiagnosticExportContext context,
        CancellationToken cancellationToken)
    {
        var privacy = new ExportPrivacyCounters();
        var events = _sink.Snapshot()
            .TakeLast(_limits.MaxEvents)
            .Select(item => SafeEvent(item, context.IncludeHosts, privacy))
            .ToArray();
        var profiles = context.Profiles.Take(_limits.MaxProfiles).Select(profile => new
        {
            displayName = "Remote session",
            host = OmitProfileHost(privacy),
            profile.Port,
            username = (string?)null,
            protocolVersion = ExportProfileProtocolVersion(profile.ProtocolVersion),
            securityType = ExportProfileSecurityType(profile.SecurityType),
            encodingStatistics = profile.EncodingStatistics is null
                ? null
                : ExportEncodingStatistics(
                    profile.EncodingStatistics,
                    _limits.MaxEncodingStatisticsPerProfile),
            errorCode = profile.ErrorCode is null ? null : ExportEventCode(profile.ErrorCode),
            correlationId = profile.CorrelationId is null ? null : ExportCorrelationId(profile.CorrelationId),
        }).ToArray();
        var diagnostics = new
        {
            generatedUtc = DateTimeOffset.UtcNow,
            application = new
            {
                name = "WinARD",
                version = ExportVersion(context.Application.ApplicationVersion),
                operatingSystem = Safe(RuntimeInformation.OSDescription, privacy),
                dotNet = Safe(RuntimeInformation.FrameworkDescription, privacy),
                windowsAppSdk = ExportVersion(context.Application.WindowsAppSdkVersion),
            },
            profiles,
            performanceCounters = ExportPerformanceCounters(
                context.PerformanceCounters,
                _limits.MaxPerformanceCounters),
            quality = context.Quality is null ? null : ExportQuality(context.Quality),
            events,
        };
        var manifest = new
        {
            schemaVersion = 1,
            redactionVersion = SecretRedactor.Version,
            included = new[]
            {
                "Application, OS, .NET and Windows App SDK versions",
                "Correlation IDs and stable error codes",
                "Redacted structured events",
                "Non-secret connection profile summaries",
                "Protocol, security, encoding and performance statistics",
            },
            excluded = new[]
            {
                "Passwords, passphrases, vault master secrets, private keys and credential files",
                "Clipboard content",
                "SQLite databases and SQLite WAL/SHM files",
                "known_hosts and temporary host-key material",
                "Raw exception text before redaction",
            },
            limits = _limits,
            privacy = new
            {
                hostFieldsOmitted = privacy.HostFieldsOmitted,
                pathFieldsOmitted = privacy.PathFieldsOmitted,
                invalidHostsOmitted = privacy.InvalidHostsOmitted,
                truncatedValues = privacy.TruncatedValues,
            },
        };

        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        long totalUncompressedBytes = 0;
        totalUncompressedBytes += await WriteJsonEntryAsync(
            archive,
            "manifest.json",
            manifest,
            _limits.MaxEntryUncompressedBytes,
            _limits.MaxUncompressedBytes - totalUncompressedBytes,
            cancellationToken).ConfigureAwait(false);
        totalUncompressedBytes += await WriteJsonEntryAsync(
            archive,
            "diagnostics.json",
            diagnostics,
            _limits.MaxEntryUncompressedBytes,
            _limits.MaxUncompressedBytes - totalUncompressedBytes,
            cancellationToken).ConfigureAwait(false);
    }

    private object SafeEvent(
        SafeDiagnosticEvent item,
        bool includeHosts,
        ExportPrivacyCounters privacy) => new
        {
            item.Timestamp,
            code = ExportEventCode(item.Code),
            correlationId = ExportCorrelationId(item.CorrelationId),
            message = Safe("Diagnostic event.", privacy),
            fields = ExportFields(item.Fields, includeHosts, privacy),
            exception = item.Exception is null
                ? null
                : new
                {
                    type = ExportExceptionType(item.Exception.Type),
                    hResult = ExportHResult(item.Exception.HResult),
                },
        };

    private string? Safe(string? value, ExportPrivacyCounters privacy)
    {
        if (value is null)
        {
            return null;
        }

        var redacted = _redactor.Redact(value);
        var characterLimited = LimitUtf16(redacted, _limits.MaxFieldLength);
        var characterTruncated = characterLimited.Length != redacted.Length;
        var limited = LimitUtf8(characterLimited, _limits.MaxStringUtf8Bytes);
        if (characterTruncated || limited.Length != characterLimited.Length)
        {
            privacy.TruncatedValues++;
        }

        return limited;
    }

    private static string LimitUtf16(string value, int maxChars)
    {
        var consumedChars = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out _, out var charsConsumed);
            if (status != System.Buffers.OperationStatus.Done || consumedChars + charsConsumed > maxChars)
            {
                break;
            }

            consumedChars += charsConsumed;
            remaining = remaining[charsConsumed..];
        }

        return consumedChars == value.Length ? value : value[..consumedChars];
    }

    private static Dictionary<string, long> ExportPerformanceCounters(
        IReadOnlyDictionary<string, long> values,
        int maxCount)
    {
        var safe = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in values
                     .Where(pair => AllowedPerformanceCounterKeys.Contains(pair.Key))
                     .Take(maxCount))
        {
            safe[pair.Key] = pair.Value;
        }

        return safe;
    }

    private Dictionary<string, string> ExportFields(
        IReadOnlyList<SafeDiagnosticField> values,
        bool includeHosts,
        ExportPrivacyCounters privacy)
    {
        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values
                     .Select(field => MapToFinalExportEntry(field, includeHosts, privacy))
                     .Where(static entry => entry is not null)
                     .Take(_limits.MaxFieldsPerEvent))
        {
            var finalEntry = entry!.Value;
            safe[finalEntry.Key] = finalEntry.Value;
        }

        return safe;
    }

    private static Dictionary<string, long> ExportEncodingStatistics(
        IReadOnlyDictionary<string, long> values,
        int maxCount)
    {
        var safe = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in values.Where(pair => IsAllowedEncodingStatisticKey(pair.Key)).Take(maxCount))
        {
            safe[pair.Key] = pair.Value;
        }

        return safe;
    }

    private static object ExportQuality(DiagnosticQualitySummary quality) => new
    {
        preset = ExportAllowedValue(quality.Preset, AllowedQualityPresets),
        targetBps = NonNegativeOrNull(quality.TargetBytesPerSecond),
        qualityLevel = ExportAllowedValue(quality.QualityLevel, AllowedQualityLevels),
        contentState = ExportAllowedValue(quality.ContentState, AllowedQualityContentStates),
        color = ExportAllowedValue(quality.Color, AllowedQualityColors),
        scalePercent = quality.ScalePercent is 50 or 75 or 100 ? quality.ScalePercent : (int?)null,
        encodingName = ExportAllowedValue(quality.EncodingName, AllowedEncodingNames) ?? "Other",
        targetFps = PositiveOrNull(quality.TargetFramesPerSecond),
        actualFps = NonNegativeOrNull(quality.ActualFramesPerSecond),
        averageBps = NonNegativeOrNull(quality.AverageBytesPerSecond),
        peakBps = NonNegativeOrNull(quality.PeakBytesPerSecond),
        responseMs = NonNegativeOrNull(quality.ResponseMilliseconds),
        zlibCapability = ExportAllowedValue(quality.ZlibCapability, AllowedCapabilitySupportValues),
        rgb565Capability = ExportAllowedValue(quality.Rgb565Capability, AllowedCapabilitySupportValues),
        serverScalingCapability = ExportAllowedValue(
            quality.ServerScalingCapability,
            AllowedCapabilitySupportValues),
        appleColor1002Capability = ExportAllowedValue(
            quality.AppleColor1002Capability,
            AllowedCapabilitySupportValues),
        appleGrayscale1001Capability = ExportAllowedValue(
            quality.AppleGrayscale1001Capability,
            AllowedCapabilitySupportValues),
        safeOnlineColorSwitch = quality.SafeOnlinePixelFormatSwitch,
        safeOnlineScaleSwitch = quality.SafeOnlineScaleSwitch,
        reason = ExportAllowedValue(quality.Reason, AllowedQualityDecisionReasons),
        targetSatisfied = quality.TargetSatisfied,
    };

    private static long? NonNegativeOrNull(long? value) => value >= 0 ? value : null;

    private static int? NonNegativeOrNull(int value) => value >= 0 ? value : null;

    private static int? PositiveOrNull(int? value) => value > 0 ? value : null;

    private KeyValuePair<string, string>? MapToFinalExportEntry(
        SafeDiagnosticField field,
        bool includeHosts,
        ExportPrivacyCounters privacy)
    {
        if (field.Category == DiagnosticFieldCategory.Path)
        {
            privacy.PathFieldsOmitted++;
            return null;
        }

        if (field.Category is
            DiagnosticFieldCategory.ClipboardContent or
            DiagnosticFieldCategory.Password or
            DiagnosticFieldCategory.Secret or
            DiagnosticFieldCategory.PrivateKey or
            DiagnosticFieldCategory.Credential or
            DiagnosticFieldCategory.VaultMaster)
        {
            return null;
        }

        var key = field.Name switch
        {
            "stage" => "Stage",
            "action" => "Action",
            "Kind" or "Boundary" or "Count" or "Reason" or "Encrypted" or "Sampled" or
            "Category" or "ProtocolVersion" or "ClientInit" or "ServerFlags" or "MayControl" or
            "SessionSelectRequired" or "SessionSelectCompleted" or "RequestedMode" or "FinalState" or
            "Status" or "Flags" or "Action" or "ProtocolFailureKind" or "RfbHandshakeStage" or
            "ExpectedByteCount" or "ActualByteCount" or "PresentationStage" or "ProtocolReadStage" or
            "ServerMessageType" or "EncodingName" or "RectangleIndex" or "ArdEncryptionStage" or
            "ArdEncryptionDirection" or "securityType" or "endpoint" or "fingerprint" or
            "oldFingerprint" or "newFingerprint" => field.Name,
            "ArdCiphertextLength" => "ArdEncryptedPacketLength",
            _ => null,
        };
        if (key is null || ContainsForbiddenToken(key))
        {
            return null;
        }

        if (field.Category == DiagnosticFieldCategory.Host)
        {
            if (key != "endpoint")
            {
                privacy.InvalidHostsOmitted++;
                return null;
            }

            var host = ExportHost(field.Value, includeHosts, privacy);
            return host is null ? null : new KeyValuePair<string, string>(key, host);
        }

        if (key == "endpoint")
        {
            return null;
        }

        var value = ExportFieldValue(key, field.Value);
        return value is null ? null : new KeyValuePair<string, string>(key, value);
    }

    private static string? ExportFieldValue(string key, string value) => key switch
    {
        "Stage" => ExportAllowedValue(value, AllowedConnectionStages),
        "Action" => ExportAllowedValue(value, AllowedDiagnosticActions),
        "Kind" => ExportAllowedValue(value, AllowedInputKinds),
        "Boundary" => ExportAllowedValue(value, AllowedInputBoundaries),
        "Count" => ExportNonNegativeInt64(value),
        "Reason" => ExportAllowedValue(value, AllowedInputDropReasons),
        "Encrypted" or "Sampled" or "SessionSelectRequired" or "SessionSelectCompleted" =>
            ExportBoolean(value),
        "Category" => ExportAllowedValue(value, AllowedDiagnosticCategories),
        "ProtocolVersion" => ExportProtocolVersion(value),
        "ClientInit" => value is "0xC1" or "0x01" ? value : null,
        "ServerFlags" => value == "NotApplicable" ? value : ExportFixedHex(value, 8),
        "MayControl" => value == "NotApplicable" ? value : ExportBoolean(value),
        "RequestedMode" => value is "Shared" or "StandardShared" ? value : null,
        "FinalState" => value is "SharedControlNegotiated" or "Initialized" ? value : null,
        "Status" => ExportNonNegativeUInt16(value),
        "Flags" => ExportFixedHex(value, 4),
        "ProtocolFailureKind" => ExportAllowedValue(value, AllowedProtocolFailureKinds),
        "RfbHandshakeStage" => ExportAllowedValue(value, AllowedRfbHandshakeStages),
        "ExpectedByteCount" or "ActualByteCount" or "RectangleIndex" or "ArdEncryptedPacketLength" =>
            ExportNonNegativeInt32(value),
        "PresentationStage" => ExportAllowedValue(value, AllowedPresentationStages),
        "ProtocolReadStage" => ExportAllowedValue(value, AllowedProtocolReadStages),
        "ServerMessageType" => ExportFixedHex(value, 2),
        "EncodingName" => ExportAllowedValue(value, AllowedEncodingNames) ?? "Other",
        "ArdEncryptionStage" => ExportAllowedValue(value, AllowedArdEncryptionStages),
        "ArdEncryptionDirection" => value is "Send" or "Receive" ? value : null,
        "securityType" => value == "30" ? value : null,
        "fingerprint" or "oldFingerprint" or "newFingerprint" => ExportFingerprint(value),
        _ => null,
    };

    private static string? ExportAllowedValue(string value, HashSet<string> allowed) =>
        allowed.Contains(value) ? value : null;

    private static string? ExportBoolean(string value) =>
        value is "True" or "False" ? value : null;

    private static string? ExportNonNegativeInt64(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? ExportNonNegativeInt32(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? ExportNonNegativeUInt16(string value) =>
        ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? ExportFixedHex(string value, int digits)
    {
        if (value.Length != digits + 2 || !value.StartsWith("0x", StringComparison.Ordinal) ||
            !value.Skip(2).All(Uri.IsHexDigit))
        {
            return null;
        }

        return $"0x{value[2..].ToUpperInvariant()}";
    }

    private static string? ExportProtocolVersion(string value)
    {
        var components = value.Split('.');
        return components.Length is >= 2 and <= 4 &&
               components.All(component => component.Length is > 0 and <= 4 && component.All(char.IsAsciiDigit)) &&
               Version.TryParse(value, out _)
            ? value
            : null;
    }

    private static string? ExportFingerprint(string value)
    {
        const string prefix = "SHA256:";
        const int encodedLength = 43;
        if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + encodedLength)
        {
            return null;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '/'))
            {
                return null;
            }
        }

        return value;
    }

    private static bool IsAllowedEncodingStatisticKey(string key) =>
        AllowedEncodingStatisticKeys.Contains(key);

    private static string ExportEventCode(string code) =>
        code.Length is > 0 and <= 64 &&
        code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') &&
        !ContainsForbiddenToken(code)
            ? code
            : "DIAGNOSTIC_EVENT_OMITTED";

    private static string? ExportCorrelationId(string correlationId) =>
        Guid.TryParse(correlationId, out var parsed) ? parsed.ToString("N") : null;

    private static string? ExportExceptionType(string type) =>
        AllowedExceptionTypes.Contains(type) ? type : null;

    private static string? ExportHResult(string value) =>
        value.Length == 10 &&
        value.StartsWith("0x", StringComparison.Ordinal) &&
        value.Skip(2).All(Uri.IsHexDigit)
            ? value.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal)
            : null;

    private static string ExportVersion(string value) =>
        Version.TryParse(value, out var version) ? version.ToString() : "unknown";

    private static string ExportProfileProtocolVersion(string value) =>
        AllowedProfileProtocolVersions.Contains(value) ? value : "Unknown";

    private static string ExportProfileSecurityType(string value) =>
        AllowedProfileSecurityTypes.Contains(value) ? value : "Unknown";

    private static string? OmitProfileHost(ExportPrivacyCounters privacy)
    {
        privacy.HostFieldsOmitted++;
        return null;
    }

    private static bool ContainsForbiddenToken(string value)
    {
        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return ForbiddenFieldNameTokens.Any(normalized.Contains);
    }

    private string? ExportHost(
        string value,
        bool includeHosts,
        ExportPrivacyCounters privacy)
    {
        if (!includeHosts)
        {
            privacy.HostFieldsOmitted++;
            return null;
        }

        var safe = Safe(value, privacy) ?? string.Empty;
        if (safe.Length == 0 || !safe.EnumerateRunes().All(IsValidHostRune))
        {
            privacy.InvalidHostsOmitted++;
            return null;
        }

        return safe;
    }

    private static bool IsValidHostRune(Rune rune) => Rune.IsLetterOrDigit(rune) ||
        rune.Value is (int)'.' or (int)'-' or (int)'_' or (int)':' or (int)'[' or (int)']' or (int)'%';

    private static string LimitUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxBytes));
        var written = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (written + bytes > maxBytes)
            {
                break;
            }

            builder.Append(rune);
            written += bytes;
        }

        return builder.ToString();
    }

    private static async Task<long> WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        long maxEntryBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(entryName) || entryName.Contains("..", StringComparison.Ordinal) ||
            entryName.Contains('\\'))
        {
            throw new InvalidOperationException("Unsafe diagnostic archive entry name.");
        }

        var limit = Math.Min(maxEntryBytes, remainingTotalBytes);
        if (limit <= 0)
        {
            throw new InvalidOperationException("The diagnostic archive exceeded its uncompressed size limit.");
        }

        await using var buffer = new MemoryStream();
        await using (var bounded = new BoundedWriteStream(buffer, limit))
        {
            await JsonSerializer.SerializeAsync(bounded, value, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        await using var entryStream = entry.Open();
        buffer.Position = 0;
        await buffer.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        return buffer.Length;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private sealed class ExportPrivacyCounters
    {
        public int HostFieldsOmitted { get; set; }
        public int PathFieldsOmitted { get; set; }
        public int InvalidHostsOmitted { get; set; }
        public int TruncatedValues { get; set; }
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        private void EnsureCapacity(int count)
        {
            if (_written + count > maxBytes)
            {
                throw new InvalidOperationException(
                    "The diagnostic archive exceeded its uncompressed size limit.");
            }
        }
    }
}
