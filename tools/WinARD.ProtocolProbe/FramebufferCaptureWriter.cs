using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe;

public static class FramebufferCaptureWriter
{
    private const int BitmapHeaderLength = 54;

    public static async Task WriteBmpAsync(
        string path,
        Framebuffer framebuffer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(framebuffer);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
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
            if (created)
            {
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

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pixels);
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
