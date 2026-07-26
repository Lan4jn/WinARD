using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinARD.Application.Ports;
using WinARD.Domain.Connections;
using WinARD.Domain.Errors;
using WinARD.Domain.Security;

namespace WinARD.Desktop.ViewModels;

public enum CredentialSaveMode
{
    WindowsCredentialManager,
    EncryptedVault,
    AskEveryTime,
}

public sealed record ConnectionTestStageResult(
    ConnectionStage Stage,
    bool Succeeded,
    TimeSpan Duration,
    string Message);

public sealed record ConnectionProfileSaveResult(
    ConnectionProfile Profile,
    string? Warning = null);

public sealed record ConnectionProfileTestResult(
    ConnectionProfile Profile,
    IReadOnlyList<ConnectionTestStageResult> Stages);

public sealed class ConnectionEditorViewModel : ObservableObject
{
    private const string UnsupportedCredentialStatus =
        "凭据由不受支持的后端管理，可保存连接信息，但无法测试连接或修改密码。" +
        "如需继续，请显式选择受支持的凭据保存方式。";
    private const string UnsupportedCredentialChangeError =
        "凭据由不受支持的后端管理。请先选择受支持的凭据保存方式再修改密码。";
    private readonly ConnectionProfile? _original;
    private readonly Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfileSaveResult>> _save;
    private readonly Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfileTestResult>> _test;
    private string _displayName = string.Empty;
    private string _host = string.Empty;
    private int _port = 5900;
    private string _macUsername = string.Empty;
    private bool _useSsh;
    private string _sshHost = string.Empty;
    private int _sshPort = 22;
    private string _sshUsername = string.Empty;
    private string _privateKeyPath = string.Empty;
    private bool _hasSshAuthenticationSecret;
    private bool _hasUnsupportedCredentialReference;
    private bool _credentialModeChanged;
    private SshHostKeyPin? _hostKeyPin;
    private CredentialSaveMode _credentialSaveMode;
    private IReadOnlyList<ConnectionTestStageResult> _testResults = [];
    private string _statusMessage = string.Empty;
    private int _busy;

    public ConnectionEditorViewModel(
        ConnectionProfile? profile,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfile>> save,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<IReadOnlyList<ConnectionTestStageResult>>> test)
        : this(
            profile,
            async (candidate, mode, secret, cancellationToken) => new ConnectionProfileSaveResult(
                await save(candidate, mode, secret, cancellationToken).ConfigureAwait(false)),
            async (candidate, mode, secret, cancellationToken) => new ConnectionProfileTestResult(
                candidate,
                await test(candidate, mode, secret, cancellationToken).ConfigureAwait(false)))
    {
    }

    public ConnectionEditorViewModel(
        ConnectionProfile? profile,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfileSaveResult>> save,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<IReadOnlyList<ConnectionTestStageResult>>> test)
        : this(
            profile,
            save,
            async (candidate, mode, secret, cancellationToken) => new ConnectionProfileTestResult(
                candidate,
                await test(candidate, mode, secret, cancellationToken).ConfigureAwait(false)))
    {
    }

    public ConnectionEditorViewModel(
        ConnectionProfile? profile,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfileSaveResult>> save,
        Func<ConnectionProfile, CredentialSaveMode, ISecret?, CancellationToken, Task<ConnectionProfileTestResult>> test)
    {
        _original = profile;
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _test = test ?? throw new ArgumentNullException(nameof(test));
        SaveCommand = new AsyncRelayCommand<ISecret?>(
            secret => SaveAsync(secret, CancellationToken.None),
            _ => CanSave());
        TestConnectionCommand = new AsyncRelayCommand<ISecret?>(
            secret => TestConnectionAsync(secret, CancellationToken.None),
            _ => CanTestConnection());

        if (profile is null)
        {
            return;
        }

        _displayName = profile.DisplayName;
        _host = profile.Host;
        _port = profile.Port;
        _macUsername = profile.MacUsername;
        _useSsh = profile.TransportMode == TransportMode.Ssh;
        (_credentialSaveMode, _hasUnsupportedCredentialReference) = ModeFromProfile(profile);
        if (_hasUnsupportedCredentialReference)
        {
            _statusMessage = UnsupportedCredentialStatus;
        }

        if (profile.SshProfile is { } ssh)
        {
            _sshHost = ssh.Host;
            _sshPort = ssh.Port;
            _sshUsername = ssh.Username;
            _privateKeyPath = ssh.PrivateKeyPath ?? string.Empty;
            _hostKeyPin = ssh.HostKeyPin;
        }
    }

    public IAsyncRelayCommand<ISecret?> SaveCommand { get; }

    public IAsyncRelayCommand<ISecret?> TestConnectionCommand { get; }

    public string DisplayName
    {
        get => _displayName;
        set => SetValidated(ref _displayName, value ?? string.Empty);
    }

    public string Host
    {
        get => _host;
        set => SetValidated(ref _host, value ?? string.Empty);
    }

    public int Port
    {
        get => _port;
        set => SetValidated(ref _port, value);
    }

    public string MacUsername
    {
        get => _macUsername;
        set => SetValidated(ref _macUsername, value ?? string.Empty);
    }

    public bool UseSsh
    {
        get => _useSsh;
        set => SetValidated(ref _useSsh, value);
    }

    public string SshHost
    {
        get => _sshHost;
        set => SetValidated(ref _sshHost, value ?? string.Empty);
    }

    public int SshPort
    {
        get => _sshPort;
        set => SetValidated(ref _sshPort, value);
    }

    public string SshUsername
    {
        get => _sshUsername;
        set => SetValidated(ref _sshUsername, value ?? string.Empty);
    }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => SetValidated(ref _privateKeyPath, value ?? string.Empty);
    }

    public bool HasSshAuthenticationSecret
    {
        get => _hasSshAuthenticationSecret;
        set => SetValidated(ref _hasSshAuthenticationSecret, value);
    }

    public CredentialSaveMode CredentialSaveMode
    {
        get => _credentialSaveMode;
        set
        {
            if (_credentialSaveMode == value && !_hasUnsupportedCredentialReference)
            {
                return;
            }

            var clearedUnsupportedReference = _hasUnsupportedCredentialReference;
            _credentialModeChanged = true;
            if (_hasUnsupportedCredentialReference)
            {
                _hasUnsupportedCredentialReference = false;
                OnPropertyChanged(nameof(HasUnsupportedCredentialReference));
                if (string.Equals(StatusMessage, UnsupportedCredentialStatus, StringComparison.Ordinal))
                {
                    StatusMessage = string.Empty;
                }
            }

            SetValidated(ref _credentialSaveMode, value);
            if (clearedUnsupportedReference)
            {
                SaveCommand.NotifyCanExecuteChanged();
                TestConnectionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasUnsupportedCredentialReference => _hasUnsupportedCredentialReference;

    public bool CredentialModeChanged => _credentialModeChanged;

    public IReadOnlyList<CredentialSaveMode> CredentialSaveModes { get; } =
        Enum.GetValues<CredentialSaveMode>();

    public IReadOnlyList<ConnectionTestStageResult> TestResults
    {
        get => _testResults;
        private set => SetProperty(ref _testResults, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public ConnectionProfileSaveResult? LastSaveResult { get; private set; }

    public async Task<ConnectionProfile> SaveAsync(ISecret? secret, CancellationToken cancellationToken)
    {
        EnterBusy();
        try
        {
            LastSaveResult = null;
            if (_hasUnsupportedCredentialReference && !_credentialModeChanged && secret is not null)
            {
                throw new InvalidOperationException(UnsupportedCredentialChangeError);
            }

            var profile = BuildProfile();
            var result = await _save(profile, CredentialSaveMode, secret, cancellationToken).ConfigureAwait(false);
            LastSaveResult = result;
            StatusMessage = result.Warning ?? $"已保存“{result.Profile.DisplayName}”。";
            return result.Profile;
        }
        finally
        {
            secret?.Dispose();
            ExitBusy();
        }
    }

    public async Task TestConnectionAsync(ISecret? secret, CancellationToken cancellationToken)
    {
        EnterBusy();
        try
        {
            if (_hasUnsupportedCredentialReference && !_credentialModeChanged)
            {
                throw new InvalidOperationException(UnsupportedCredentialStatus);
            }

            TestResults = [];
            var result = await _test(BuildProfile(), CredentialSaveMode, secret, cancellationToken)
                .ConfigureAwait(false);
            _hostKeyPin = result.Profile.SshProfile?.HostKeyPin;
            var results = result.Stages;
            TestResults = results;
            StatusMessage = results.Count == 0
                ? "测试连接未返回结果。"
                : results[^1].Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "测试连接已取消。";
            throw;
        }
        finally
        {
            secret?.Dispose();
            ExitBusy();
        }
    }

    public ConnectionProfile BuildProfile()
    {
        if (!FieldsAreValid())
        {
            throw new InvalidOperationException("连接信息不完整或无效。");
        }

        var profile = ConnectionProfile.Create(
            _original?.Id ?? Guid.NewGuid(), DisplayName, Host, Port, MacUsername);
        if (!_credentialModeChanged && _original?.CredentialReference is { } macReference)
        {
            profile = profile.WithCredential(macReference);
        }

        if (!UseSsh)
        {
            return profile;
        }

        var originalSsh = _original?.SshProfile;
        var ssh = SshProfile.Create(
            SshHost,
            SshPort,
            SshUsername,
            string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath,
            Host,
            Port,
            credentialReference: null,
            originalSsh?.PinnedHostKeyAlgorithm,
            originalSsh?.PinnedHostKeySha256);
        if (!_credentialModeChanged)
        {
            ssh = ssh.WithAuthenticationCredentials(
                originalSsh?.PasswordCredentialReference,
                originalSsh?.PrivateKeyPassphraseCredentialReference);
        }
        if (_hostKeyPin is { } pin)
        {
            ssh = ssh.WithHostKeyPin(pin);
        }

        return profile.WithSsh(ssh);
    }

    private bool CanSave() => !IsBusy && FieldsAreValid();

    private bool CanTestConnection() =>
        !IsBusy && !_hasUnsupportedCredentialReference && FieldsAreValid();

    private bool FieldsAreValid()
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || !ValidHost(Host) ||
            Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(MacUsername))
        {
            return false;
        }

        if (!UseSsh)
        {
            return true;
        }

        var hasCurrentCredential = string.IsNullOrWhiteSpace(PrivateKeyPath)
            ? _original?.SshProfile?.PasswordCredentialReference is not null
            : _original?.SshProfile?.PrivateKeyPassphraseCredentialReference is not null;
        return ValidHost(SshHost) && SshPort is >= 1 and <= 65535 &&
            !string.IsNullOrWhiteSpace(SshUsername) &&
            (!string.IsNullOrWhiteSpace(PrivateKeyPath) || HasSshAuthenticationSecret ||
             (!_credentialModeChanged && hasCurrentCredential) || CredentialSaveMode == CredentialSaveMode.AskEveryTime);
    }

    private static bool ValidHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var host = value.Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private static (CredentialSaveMode Mode, bool Unsupported) ModeFromProfile(ConnectionProfile profile)
    {
        var references = new[]
        {
            profile.CredentialReference,
            profile.SshProfile?.PasswordCredentialReference,
            profile.SshProfile?.PrivateKeyPassphraseCredentialReference,
        }.Where(static reference => reference is not null).Cast<CredentialReference>().ToArray();
        var unsupported = references.Any(static reference => !IsSupportedStore(reference.Store));
        var store = profile.CredentialReference?.Store ??
            profile.SshProfile?.PasswordCredentialReference?.Store ??
            profile.SshProfile?.PrivateKeyPassphraseCredentialReference?.Store;
        var mode = store?.ToLowerInvariant() switch
        {
            null or "windows" => CredentialSaveMode.WindowsCredentialManager,
            "vault" => CredentialSaveMode.EncryptedVault,
            "ask" => CredentialSaveMode.AskEveryTime,
            _ => CredentialSaveMode.WindowsCredentialManager,
        };
        return (mode, unsupported);
    }

    private static bool IsSupportedStore(string store) =>
        string.Equals(store, "windows", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(store, "vault", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(store, "ask", StringComparison.OrdinalIgnoreCase);

    private void SetValidated<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        SaveCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    private void EnterBusy()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("另一个连接编辑操作正在进行。");
        }

        OnPropertyChanged(nameof(IsBusy));
        SaveCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    private void ExitBusy()
    {
        Volatile.Write(ref _busy, 0);
        OnPropertyChanged(nameof(IsBusy));
        SaveCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }
}
