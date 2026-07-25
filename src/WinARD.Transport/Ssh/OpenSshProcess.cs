using System.Diagnostics;
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

        var startInfo = new ProcessStartInfo
        {
            FileName = start.FileName,
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

    public SystemOpenSshKeyScanLauncher()
        : this(new SystemOpenSshProcessLauncher())
    {
    }

    public SystemOpenSshKeyScanLauncher(IOpenSshProcessLauncher processLauncher)
    {
        _processLauncher = processLauncher ??
            throw new ArgumentNullException(nameof(processLauncher));
    }

    public async ValueTask<OpenSshKeyScanResult> ScanAsync(
        OpenSshProcessStart start,
        CancellationToken cancellationToken)
    {
        var process = await _processLauncher
            .LaunchAsync(start, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var stdout = ReadToEndAsync(process.StandardOutput, cancellationToken);
            var stderr = ReadToEndAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new OpenSshKeyScanResult(
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false),
                process.ExitCode ?? -1);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadToEndAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
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
            return new TemporaryOpenSshKnownHostsFile(path, stream);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                File.Delete(path);
            }

            throw;
        }
    }

    private sealed class TemporaryOpenSshKnownHostsFile(
        string path,
        FileStream stream) : IOpenSshKnownHostsFile
    {
        private int _disposed;

        public string Path { get; } = path;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
            File.Delete(Path);
        }
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
