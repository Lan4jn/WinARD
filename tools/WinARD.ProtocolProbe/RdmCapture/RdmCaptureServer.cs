using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinARD.Remote.Protocol.Ard;
using WinARD.Remote.Protocol.Framebuffer;

namespace WinARD.ProtocolProbe.RdmCapture;

public sealed class RdmCaptureServer
{
    private const int SyntheticWidth = 1920;
    private const int SyntheticHeight = 1080;
    private const string SyntheticDisplayName = "WinARD Synthetic Probe";
    private readonly IPAddress _listenAddress = IPAddress.Loopback;
    private readonly TimeSpan _inactivityTimeout;

    public RdmCaptureServer()
        : this(DefaultInactivityTimeout)
    {
    }

    internal RdmCaptureServer(TimeSpan inactivityTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(inactivityTimeout, TimeSpan.Zero);
        _inactivityTimeout = inactivityTimeout;
    }

    internal static TimeSpan DefaultInactivityTimeout { get; } = TimeSpan.FromSeconds(10);

    public async Task<RdmCaptureReport> CaptureOnceAsync(
        int port,
        string profile,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        using var inactivity = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            inactivity.Token);
        inactivity.CancelAfter(_inactivityTimeout);
        try
        {
            using var listener = new TcpListener(_listenAddress, port);
            listener.Start();
            using var client = await listener.AcceptTcpClientAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            listener.Stop();
            inactivity.CancelAfter(_inactivityTimeout);
            await using var networkStream = client.GetStream();
            var stream = new ProgressTimeoutStream(networkStream, inactivity, _inactivityTimeout);
            var handshake = await ArdProbeServerHandshake.AcceptAsync(stream, linkedCancellation.Token)
                .ConfigureAwait(false);
            await WriteSyntheticServerInitAsync(stream, linkedCancellation.Token).ConfigureAwait(false);
            var declaration = await RfbClientDeclarationReader.ReadAsync(stream, linkedCancellation.Token)
                .ConfigureAwait(false);

            return new RdmCaptureReport(
                1,
                profile,
                handshake.Version.Banner.TrimEnd('\n'),
                handshake.ClientInit,
                declaration.PixelFormat,
                declaration.Encodings,
                declaration.Messages,
                declaration.ReachedFramebufferRequest,
                declaration.StoppedAtUnknownMessageType);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && inactivity.IsCancellationRequested)
        {
            throw new TimeoutException("RDM capture made no progress for 10 seconds.", exception);
        }
    }

    private static async Task WriteSyntheticServerInitAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var displayName = Encoding.UTF8.GetBytes(SyntheticDisplayName);
        var extendedName = new byte[23 + displayName.Length];
        BinaryPrimitives.WriteUInt32BigEndian(
            extendedName.AsSpan(2),
            (uint)ArdServerFlags.MayControl);
        displayName.CopyTo(extendedName, 23);

        var serverInit = new byte[24 + extendedName.Length];
        BinaryPrimitives.WriteUInt16BigEndian(serverInit, SyntheticWidth);
        BinaryPrimitives.WriteUInt16BigEndian(serverInit.AsSpan(2), SyntheticHeight);
        PixelFormat.WinArdBgra32.ToWireBytes().CopyTo(serverInit, 4);
        BinaryPrimitives.WriteUInt32BigEndian(serverInit.AsSpan(20), checked((uint)extendedName.Length));
        extendedName.CopyTo(serverInit, 24);
        await stream.WriteAsync(serverInit, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ProgressTimeoutStream(
        Stream inner,
        CancellationTokenSource inactivity,
        TimeSpan inactivityTimeout) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                inactivity.CancelAfter(inactivityTimeout);
            }

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            inactivity.CancelAfter(inactivityTimeout);
        }
    }
}
