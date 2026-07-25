using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe;

public static class FramebufferCaptureWriter
{
    private const int BitmapHeaderLength = 54;

    public static Task WriteAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".BGRA" => WriteBgraAsync(path, framebuffer, cancellationToken),
            ".BMP" => WriteBmpAsync(path, framebuffer, cancellationToken),
            _ => throw new ArgumentException("Capture path must use the .bgra or .bmp extension.", nameof(path)),
        };
    }

    public static async Task WriteBmpAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(framebuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        CreateParentDirectory(fullPath);
        var pixels = framebuffer.GetPixelsBgra32();
        var header = CreateHeader(framebuffer.Width, framebuffer.Height, pixels.Length);
        var created = false;
        try
        {
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            await output.WriteAsync(header, cancellationToken);
            for (var y = framebuffer.Height - 1; y >= 0; y--)
            {
                await output.WriteAsync(pixels.AsMemory(y * framebuffer.Stride, framebuffer.Stride), cancellationToken);
            }
        }
        catch
        {
            DeletePartialCapture(fullPath, created);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private static async Task WriteBgraAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(framebuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        CreateParentDirectory(fullPath);
        var pixels = framebuffer.GetPixelsBgra32();
        var created = false;
        try
        {
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            await output.WriteAsync(pixels, cancellationToken);
        }
        catch
        {
            DeletePartialCapture(fullPath, created);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
        }
    }

    private static void CreateParentDirectory(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }
    }

    private static void DeletePartialCapture(string fullPath, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            File.Delete(fullPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
