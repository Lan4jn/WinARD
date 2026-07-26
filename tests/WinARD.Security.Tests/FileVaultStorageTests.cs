using WinARD.Security.Vault;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Security.Tests;

public sealed class FileVaultStorageTests
{
    [Fact]
    public async Task Bounded_reader_rejects_a_stream_that_grows_past_its_length_hint()
    {
        await using var stream = new GrowingPartialReadStream(
            VaultFileFormat.MaximumFileBytes + 1,
            reportedLength: 1,
            maximumReadSize: 997);

        await Assert.ThrowsAsync<VaultFormatException>(
            () => FileVaultStorage.ReadBoundedAsync(stream, CancellationToken.None).AsTask());

        Assert.Equal(VaultFileFormat.MaximumFileBytes + 1, stream.TotalBytesRead);
    }

    [Fact]
    public async Task Bounded_reader_observes_cancellation_during_partial_reads()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new GrowingPartialReadStream(
            VaultFileFormat.MaximumFileBytes,
            reportedLength: 0,
            maximumReadSize: 31,
            onRead: count =>
            {
                if (count == 3)
                {
                    cancellation.Cancel();
                }
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileVaultStorage.ReadBoundedAsync(stream, cancellation.Token).AsTask());
        Assert.InRange(stream.TotalBytesRead, 1, 93);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_rejects_an_oversized_real_file(bool sparse)
    {
        var path = TemporaryVaultPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                if (sparse)
                {
                    stream.SetLength(VaultFileFormat.MaximumFileBytes + 1L);
                }
                else
                {
                    var block = new byte[64 * 1024];
                    var remaining = VaultFileFormat.MaximumFileBytes + 1;
                    while (remaining != 0)
                    {
                        var count = Math.Min(block.Length, remaining);
                        await stream.WriteAsync(block.AsMemory(0, count));
                        remaining -= count;
                    }
                }
            }

            await Assert.ThrowsAsync<VaultFormatException>(
                () => new FileVaultStorage(path).ReadAsync(CancellationToken.None).AsTask());
        }
        finally
        {
            DeleteTemporaryDirectory(path);
        }
    }

    [Fact]
    public async Task Compare_exchange_rejects_oversized_current_file_without_replacing_it()
    {
        var path = TemporaryVaultPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(VaultFileFormat.MaximumFileBytes + 1L);
            }

            await Assert.ThrowsAsync<VaultFormatException>(
                () => new FileVaultStorage(path)
                    .CompareExchangeAsync(
                        new byte[] { 1, 2, 3 },
                        null,
                        CancellationToken.None)
                    .AsTask());

            Assert.Equal(VaultFileFormat.MaximumFileBytes + 1L, new FileInfo(path).Length);
        }
        finally
        {
            DeleteTemporaryDirectory(path);
        }
    }

    private static string TemporaryVaultPath() => Path.Combine(
        Path.GetTempPath(),
        $"winard-file-vault-{Guid.NewGuid():N}",
        "credentials.vault");

    private static void DeleteTemporaryDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class GrowingPartialReadStream(
        int availableBytes,
        long reportedLength,
        int maximumReadSize,
        Action<int>? onRead = null) : Stream
    {
        private int _readCount;

        public int TotalBytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => reportedLength;

        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (TotalBytesRead == availableBytes)
            {
                return 0;
            }

            var count = Math.Min(
                Math.Min(buffer.Length, maximumReadSize),
                availableBytes - TotalBytesRead);
            buffer.Span[..count].Clear();
            TotalBytesRead += count;
            Position += count;
            onRead?.Invoke(++_readCount);
            return count;
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
