using System.Buffers.Binary;
using WinARD.ProtocolProbe;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using Xunit;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Tools;

public sealed class FramebufferCaptureWriterTests
{
    [Fact]
    public async Task WriteBgra_writes_top_down_framebuffer_bytes_without_header()
    {
        using var framebuffer = new FramebufferModel(1, 2, ProtocolLimits.Default);
        framebuffer.ApplyRaw(
            new FramebufferRect(0, 0, 1, 2),
            [1, 2, 3, 255, 4, 5, 6, 255]);
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bgra");
        var directory = Path.GetDirectoryName(path)!;
        try
        {
            await FramebufferCaptureWriter.WriteAsync(path, framebuffer, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(framebuffer.Width * framebuffer.Height * 4, bytes.Length);
            Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255], bytes);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(directory),
                candidate => Path.GetFileName(candidate).StartsWith($".{Path.GetFileName(path)}.", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_creates_missing_parent_directory()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var directory = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}", "artifacts");
        var path = Path.Combine(directory, "first-frame.bgra");
        try
        {
            await FramebufferCaptureWriter.WriteAsync(path, framebuffer, CancellationToken.None);

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(directory)))
            {
                Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteBmp_includes_dimensions_and_bottom_up_bgra_pixels()
    {
        using var framebuffer = new FramebufferModel(1, 2, ProtocolLimits.Default);
        framebuffer.ApplyRaw(
            new FramebufferRect(0, 0, 1, 2),
            [1, 2, 3, 255, 4, 5, 6, 255]);
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bmp");
        try
        {
            await FramebufferCaptureWriter.WriteBmpAsync(path, framebuffer, CancellationToken.None);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal((byte)'B', bytes[0]);
            Assert.Equal((byte)'M', bytes[1]);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18)));
            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22)));
            Assert.Equal((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)));
            Assert.Equal([4, 5, 6, 255, 1, 2, 3, 255], bytes[54..]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteBmp_does_not_overwrite_existing_file()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bmp");
        await File.WriteAllTextAsync(path, "keep");
        var directory = Path.GetDirectoryName(path)!;
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                FramebufferCaptureWriter.WriteBmpAsync(path, framebuffer, CancellationToken.None));

            Assert.Equal("keep", await File.ReadAllTextAsync(path));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(directory),
                candidate => Path.GetFileName(candidate).StartsWith($".{Path.GetFileName(path)}.", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_failure_deletes_temporary_file_without_publishing_destination()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var operations = new FakeCaptureFileOperations(new ThrowingWriteStream(new IOException("write failed")));
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bgra");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            FramebufferCaptureWriter.WriteAsync(path, framebuffer, operations, CancellationToken.None));

        Assert.Equal("write failed", exception.Message);
        Assert.NotNull(operations.CreatedPath);
        Assert.Equal(operations.CreatedPath, operations.DeletedPath);
        Assert.Null(operations.Move);
    }

    [Fact]
    public async Task Write_cancellation_deletes_temporary_file_and_preserves_original_exception()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeCaptureFileOperations(new CancellingWriteStream(cancellation));
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bgra");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FramebufferCaptureWriter.WriteAsync(path, framebuffer, operations, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.NotNull(operations.CreatedPath);
        Assert.Equal(operations.CreatedPath, operations.DeletedPath);
        Assert.Null(operations.Move);
    }

    [Fact]
    public async Task Temporary_cleanup_failure_is_attached_without_replacing_write_failure()
    {
        using var framebuffer = new FramebufferModel(1, 1, ProtocolLimits.Default);
        var operations = new FakeCaptureFileOperations(new ThrowingWriteStream(new IOException("write failed")))
        {
            DeleteException = new UnauthorizedAccessException("cleanup failed"),
        };
        var path = Path.Combine(Path.GetTempPath(), $"winard-{Guid.NewGuid():N}.bgra");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            FramebufferCaptureWriter.WriteAsync(path, framebuffer, operations, CancellationToken.None));

        Assert.Equal("write failed", exception.Message);
        var cleanupException = Assert.IsType<UnauthorizedAccessException>(
            exception.Data["CaptureTemporaryFileCleanupException"]);
        Assert.Equal("cleanup failed", cleanupException.Message);
    }

    private sealed class FakeCaptureFileOperations(Stream stream) : ICaptureFileOperations
    {
        public string? CreatedPath { get; private set; }
        public string? DeletedPath { get; private set; }
        public (string Source, string Destination)? Move { get; private set; }
        public Exception? DeleteException { get; init; }

        public void CreateDirectory(string path)
        {
        }

        public Stream CreateNew(string path)
        {
            CreatedPath = path;
            return stream;
        }

        public void MoveNew(string source, string destination)
        {
            Move = (source, destination);
        }

        public void Delete(string path)
        {
            DeletedPath = path;
            if (DeleteException is not null)
            {
                throw DeleteException;
            }
        }
    }

    private sealed class ThrowingWriteStream(Exception exception) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(exception);
    }

    private sealed class CancellingWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        }
    }
}
