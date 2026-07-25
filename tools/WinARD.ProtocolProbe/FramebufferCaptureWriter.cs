using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe;

public static class FramebufferCaptureWriter
{
    private const int BitmapHeaderLength = 54;
    private static readonly ICaptureFileOperations FileOperations = new CaptureFileOperations();

    public static Task WriteAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken) =>
        WriteAsync(path, framebuffer, FileOperations, cancellationToken);

    internal static Task WriteAsync(
        string path,
        Framebuffer framebuffer,
        ICaptureFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileOperations);
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".BGRA" => WriteBgraAsync(path, framebuffer, fileOperations, cancellationToken),
            ".BMP" => WriteBmpAsync(path, framebuffer, fileOperations, cancellationToken),
            _ => throw new ArgumentException("Capture path must use the .bgra or .bmp extension.", nameof(path)),
        };
    }

    public static Task WriteBmpAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken) =>
        WriteBmpAsync(path, framebuffer, FileOperations, cancellationToken);

    private static async Task WriteBmpAsync(
        string path,
        Framebuffer framebuffer,
        ICaptureFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(framebuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = framebuffer.GetPixelsBgra32();
        var header = CreateHeader(framebuffer.Width, framebuffer.Height, pixels.Length);
        try
        {
            await WriteAtomicallyAsync(
                path,
                async output =>
                {
                    await output.WriteAsync(header, cancellationToken);
                    for (var y = framebuffer.Height - 1; y >= 0; y--)
                    {
                        await output.WriteAsync(
                            pixels.AsMemory(y * framebuffer.Stride, framebuffer.Stride),
                            cancellationToken);
                    }
                },
                fileOperations,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private static async Task WriteBgraAsync(
        string path,
        Framebuffer framebuffer,
        ICaptureFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = framebuffer.GetPixelsBgra32();
        try
        {
            await WriteAtomicallyAsync(
                path,
                output => output.WriteAsync(pixels, cancellationToken).AsTask(),
                fileOperations,
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        Func<Stream, Task> write,
        ICaptureFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            fileOperations.CreateDirectory(directory);
        }

        var temporaryPath = CreateTemporaryPath(fullPath);
        var temporaryCreated = false;
        try
        {
            await using (var output = fileOperations.CreateNew(temporaryPath))
            {
                temporaryCreated = true;
                await write(output);
                await output.FlushAsync(cancellationToken);
            }

            fileOperations.MoveNew(temporaryPath, fullPath);
        }
        catch (Exception exception)
        {
            if (!temporaryCreated)
            {
                throw;
            }

            try
            {
                fileOperations.Delete(temporaryPath);
            }
            catch (Exception cleanupException)
                when (cleanupException is IOException or UnauthorizedAccessException)
            {
                exception.Data["CaptureTemporaryFileCleanupException"] = cleanupException;
            }

            throw;
        }
    }

    private static string CreateTemporaryPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var fileName = Path.GetFileName(fullPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static byte[] CreateHeader(int width, int height, int pixelByteLength)
    {
        var fileSize = checked(BitmapHeaderLength + pixelByteLength);
        var header = new byte[BitmapHeaderLength];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), BitmapHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34), pixelByteLength);
        return header;
    }
}

internal interface ICaptureFileOperations
{
    void CreateDirectory(string path);
    Stream CreateNew(string path);
    void MoveNew(string source, string destination);
    void Delete(string path);
}

internal sealed class CaptureFileOperations : ICaptureFileOperations
{
    public void CreateDirectory(string path) => _ = Directory.CreateDirectory(path);

    public Stream CreateNew(string path) =>
        new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void MoveNew(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    public void Delete(string path) => File.Delete(path);
}
