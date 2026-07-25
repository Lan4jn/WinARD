using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using WinARD.Domain.Connections;

namespace WinARD.Transport.Ssh;

public interface IOpenSshProcessLauncher
{
    ValueTask<IOpenSshProcess> LaunchAsync(
        OpenSshProcessStart start,
        CancellationToken cancellationToken);
}

public interface IOpenSshKeyScanLauncher
{
    ValueTask<OpenSshKeyScanResult> ScanAsync(
        OpenSshProcessStart start,
        CancellationToken cancellationToken);
}

public sealed record OpenSshKeyScanResult(
    string StandardOutput,
    string StandardError,
    int ExitCode);

public interface IOpenSshProcess : IAsyncDisposable
{
    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    void Kill(bool entireProcessTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IOpenSshKnownHostsFileFactory
{
    ValueTask<IOpenSshKnownHostsFile> CreateAsync(
        SshHostKeyPin pin,
        CancellationToken cancellationToken);
}

public interface IOpenSshKnownHostsFile : IAsyncDisposable
{
    string Path { get; }
}

public sealed class SystemOpenSshProcessLauncher : IOpenSshProcessLauncher
{
    public ValueTask<IOpenSshProcess> LaunchAsync(
        OpenSshProcessStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(start.FileName))
        {
            throw new OpenSshExecutableConfigurationException(
                "OpenSSH processes must use a verified absolute executable path.");
        }

        var executablePath = Path.GetFullPath(start.FileName);
        var executableKind = Path.GetFileName(executablePath) switch
        {
            var name when string.Equals(
                name,
                "ssh.exe",
                StringComparison.OrdinalIgnoreCase) => OpenSshExecutableKind.Ssh,
            var name when string.Equals(
                name,
                "ssh-keyscan.exe",
                StringComparison.OrdinalIgnoreCase) => OpenSshExecutableKind.KeyScan,
            _ => throw new OpenSshExecutableConfigurationException(
                "Only ssh.exe and ssh-keyscan.exe may be launched."),
        };
        if (!File.Exists(executablePath))
        {
            throw new OpenSshExecutableNotFoundException(
                executableKind,
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in start.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Unable to start OpenSSH process '{start.FileName}'.");
            }

            return ValueTask.FromResult<IOpenSshProcess>(
                new SystemOpenSshProcess(process));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

public sealed class SystemOpenSshKeyScanLauncher : IOpenSshKeyScanLauncher
{
    private readonly IOpenSshProcessLauncher _processLauncher;
    private readonly OpenSshKeyScanLimits _limits;
    private readonly TimeProvider _timeProvider;

    public SystemOpenSshKeyScanLauncher()
        : this(
            new SystemOpenSshProcessLauncher(),
            OpenSshKeyScanLimits.Default,
            TimeProvider.System)
    {
    }

    public SystemOpenSshKeyScanLauncher(IOpenSshProcessLauncher processLauncher)
        : this(
            processLauncher,
            OpenSshKeyScanLimits.Default,
            TimeProvider.System)
    {
    }

    public SystemOpenSshKeyScanLauncher(
        IOpenSshProcessLauncher processLauncher,
        OpenSshKeyScanLimits limits,
        TimeProvider timeProvider)
    {
        _processLauncher = processLauncher ??
            throw new ArgumentNullException(nameof(processLauncher));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<OpenSshKeyScanResult> ScanAsync(
        OpenSshProcessStart start,
        CancellationToken cancellationToken)
    {
        var process = await _processLauncher
            .LaunchAsync(start, cancellationToken)
            .ConfigureAwait(false);
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = CollectAsync(
            process.StandardOutput,
            OpenSshOutputKind.StandardOutput,
            _limits.MaximumStandardOutputBytes,
            operationCancellation);
        var stderr = CollectAsync(
            process.StandardError,
            OpenSshOutputKind.StandardError,
            _limits.MaximumStandardErrorBytes,
            operationCancellation);
        var waitForExit = process.WaitForExitAsync(operationCancellation.Token);
        try
        {
            await AwaitOperationAsync(stdout, stderr, waitForExit).ConfigureAwait(false);
            var standardOutput = Encoding.UTF8.GetString(await stdout.ConfigureAwait(false));
            var standardError = OpenSshDiagnostics.Sanitize(
                Encoding.UTF8.GetString(await stderr.ConfigureAwait(false)),
                _limits.MaximumStandardErrorBytes);
            var exitCode = process.ExitCode ?? -1;
            if (exitCode != 0)
            {
                throw new OpenSshKeyScanProcessException(exitCode, standardError);
            }

            await process.DisposeAsync().ConfigureAwait(false);
            return new OpenSshKeyScanResult(standardOutput, standardError, exitCode);
        }
        catch (Exception exception)
        {
            operationCancellation.Cancel();
            var primary = SelectPrimaryException(
                exception,
                stdout,
                stderr,
                waitForExit,
                cancellationToken);
            await RethrowAfterCleanupAsync(primary, process).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static async Task AwaitOperationAsync(
        Task<byte[]> stdout,
        Task<byte[]> stderr,
        Task waitForExit)
    {
        var active = new List<Task> { stdout, stderr, waitForExit };
        while (active.Count > 0)
        {
            var completed = await Task.WhenAny(active).ConfigureAwait(false);
            active.Remove(completed);
            await completed.ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> CollectAsync(
        Stream stream,
        OpenSshOutputKind outputKind,
        int maximumBytes,
        CancellationTokenSource operationCancellation)
    {
        var buffer = new byte[Math.Min(4096, maximumBytes)];
        using var retained = new MemoryStream(Math.Min(maximumBytes, 4096));
        try
        {
            while (true)
            {
                var read = await stream
                    .ReadAsync(buffer, operationCancellation.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return retained.ToArray();
                }

                if (retained.Length + read > maximumBytes)
                {
                    throw new OpenSshOutputLimitExceededException(
                        outputKind,
                        maximumBytes);
                }

                await retained
                    .WriteAsync(buffer.AsMemory(0, read), operationCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            operationCancellation.Cancel();
            throw;
        }
    }

    private static Exception SelectPrimaryException(
        Exception observed,
        Task stdout,
        Task stderr,
        Task waitForExit,
        CancellationToken callerToken)
    {
        foreach (var task in new[] { stdout, stderr, waitForExit })
        {
            var taskException = task.Exception?.Flatten().InnerExceptions.FirstOrDefault(
                exception => exception is not OperationCanceledException);
            if (taskException is not null)
            {
                return taskException;
            }
        }

        if (callerToken.IsCancellationRequested)
        {
            return new OperationCanceledException(callerToken);
        }

        return observed is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions[0]
            : observed;
    }

    private async Task RethrowAfterCleanupAsync(
        Exception primaryException,
        IOpenSshProcess process)
    {
        var exceptions = new List<Exception> { primaryException };
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
            {
                AddFlattened(exceptions, exception);
            }
        }

        using var cleanupCancellation = new CancellationTokenSource(
            _limits.CleanupTimeout,
            _timeProvider);
        try
        {
            await process
                .WaitForExitAsync(cleanupCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            exceptions.Add(
                new OpenSshProcessCleanupTimeoutException(_limits.CleanupTimeout));
        }
        catch (Exception exception)
        {
            AddFlattened(exceptions, exception);
        }

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddFlattened(exceptions, exception);
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        throw new AggregateException(exceptions);
    }

    private static void AddFlattened(
        List<Exception> exceptions,
        Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            exceptions.AddRange(aggregate.Flatten().InnerExceptions);
        }
        else
        {
            exceptions.Add(exception);
        }
    }
}

public sealed record OpenSshKeyScanLimits
{
    public static OpenSshKeyScanLimits Default { get; } =
        new(64 * 1024, 64 * 1024, TimeSpan.FromSeconds(2));

    public OpenSshKeyScanLimits(
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        TimeSpan cleanupTimeout)
    {
        MaximumStandardOutputBytes = Positive(
            maximumStandardOutputBytes,
            nameof(maximumStandardOutputBytes));
        MaximumStandardErrorBytes = Positive(
            maximumStandardErrorBytes,
            nameof(maximumStandardErrorBytes));
        if (cleanupTimeout <= TimeSpan.Zero ||
            cleanupTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                "Cleanup timeout must be finite and positive.");
        }

        CleanupTimeout = cleanupTimeout;
    }

    public int MaximumStandardOutputBytes { get; }

    public int MaximumStandardErrorBytes { get; }

    public TimeSpan CleanupTimeout { get; }

    private static int Positive(int value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                "Maximum output bytes must be positive.");
}

public enum OpenSshOutputKind
{
    StandardOutput,
    StandardError,
}

public sealed class OpenSshOutputLimitExceededException : Exception
{
    public OpenSshOutputLimitExceededException(
        OpenSshOutputKind outputKind,
        int maximumBytes)
        : base(
            $"OpenSSH {outputKind} exceeded the configured {maximumBytes} byte limit.")
    {
        OutputKind = outputKind;
        MaximumBytes = maximumBytes;
    }

    public OpenSshOutputKind OutputKind { get; }

    public int MaximumBytes { get; }
}

public sealed class OpenSshKeyScanProcessException : Exception
{
    public OpenSshKeyScanProcessException(int exitCode, string diagnosticSummary)
        : base($"ssh-keyscan failed with exit code {exitCode}.")
    {
        ExitCode = exitCode;
        DiagnosticSummary = diagnosticSummary ?? string.Empty;
    }

    public int ExitCode { get; }

    public string DiagnosticSummary { get; }
}

public sealed class OpenSshProcessCleanupTimeoutException : TimeoutException
{
    public OpenSshProcessCleanupTimeoutException(TimeSpan timeout)
        : base($"OpenSSH process cleanup exceeded {timeout}.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

public sealed class TemporaryOpenSshKnownHostsFileFactory
    : IOpenSshKnownHostsFileFactory
{
    public async ValueTask<IOpenSshKnownHostsFile> CreateAsync(
        SshHostKeyPin pin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        cancellationToken.ThrowIfCancellationRequested();

        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"winard-{Guid.NewGuid():N}.known_hosts");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var content = Encoding.UTF8.GetBytes(OpenSshKnownHosts.Format(pin));
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            return new TemporaryOpenSshKnownHostsFile(
                path,
                stream,
                File.Delete);
        }
        catch (Exception primaryException)
        {
            List<Exception>? cleanupFailures = null;
            if (stream is not null)
            {
                try
                {
                    await new TemporaryOpenSshKnownHostsFile(
                            path,
                            stream,
                            File.Delete)
                        .DisposeAsync()
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    TemporaryOpenSshKnownHostsFile.AddException(
                        ref cleanupFailures,
                        exception);
                }
            }
            else
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception)
                {
                    TemporaryOpenSshKnownHostsFile.AddException(
                        ref cleanupFailures,
                        exception);
                }
            }

            if (cleanupFailures is not null)
            {
                throw new AggregateException(
                    [primaryException, .. cleanupFailures]);
            }

            ExceptionDispatchInfo.Capture(primaryException).Throw();
            throw new InvalidOperationException("Unreachable.");
        }
    }

}

internal sealed class TemporaryOpenSshKnownHostsFile(
    string path,
    Stream stream,
    Action<string> deleteFile) : IOpenSshKnownHostsFile
{
    private readonly object _sync = new();
    private Task? _disposeTask;

    public string Path { get; } = path;

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_sync)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception>? failures = null;
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddException(ref failures, exception);
        }

        try
        {
            deleteFile(Path);
        }
        catch (Exception exception)
        {
            AddException(ref failures, exception);
        }

        ThrowIfAny(failures);
    }

    internal static void AddException(
        ref List<Exception>? failures,
        Exception exception)
    {
        failures ??= [];
        if (exception is AggregateException aggregate)
        {
            failures.AddRange(aggregate.Flatten().InnerExceptions);
        }
        else
        {
            failures.Add(exception);
        }
    }

    internal static void ThrowIfAny(List<Exception>? failures)
    {
        if (failures is null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }
}

internal sealed class SystemOpenSshProcess(Process process) : IOpenSshProcess
{
    private int _disposed;

    public Stream StandardInput => process.StandardInput.BaseStream;

    public Stream StandardOutput => process.StandardOutput.BaseStream;

    public Stream StandardError => process.StandardError.BaseStream;

    public bool HasExited => process.HasExited;

    public int? ExitCode => process.HasExited ? process.ExitCode : null;

    public void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            process.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
