using System.Buffers.Binary;
using System.Security.Cryptography;
using WinARD.Remote.Protocol.Encodings.Mvs;
using WinARD.Remote.Protocol.Errors;
using WinARD.Remote.Protocol.Framebuffer;
using WinARD.Remote.Protocol.IO;
using FramebufferModel = WinARD.Remote.Protocol.Framebuffer.Framebuffer;

namespace WinARD.Remote.Protocol.Encodings;

/// <summary>
/// 受控隔离的 Apple MVS (Encoding 1011) 协议解码器。
/// 严格管理会话生命周期中的 Setup 双量化表。
/// 注意：图像切片的位流熵解码与频域反变换尚未完成真实验收（E2b 阻塞）；
/// 本解码器拒绝生成未经证实的伪造像素，遇到切片时显式抛出协议异常。
/// </summary>
public sealed class AppleMvsDecoder : IRfbEncodingDecoder
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024; // 16 MiB 硬门禁
    public const int SetupPayloadLength = 129;
    public const int SliceHeaderLength = 6;
    public const int TableSize = 64;

    private byte[]? _luminanceTable;
    private byte[]? _chrominanceTable;
    private bool _hasSetup;

    public int EncodingId => (int)RfbEncodingType.AppleMvs;

    public bool HasSetup => _hasSetup;

    public ReadOnlySpan<byte> LuminanceTable => _luminanceTable;
    public ReadOnlySpan<byte> ChrominanceTable => _chrominanceTable;

    public void ResetState()
    {
        if (_luminanceTable is not null)
        {
            CryptographicOperations.ZeroMemory(_luminanceTable);
            _luminanceTable = null;
        }

        if (_chrominanceTable is not null)
        {
            CryptographicOperations.ZeroMemory(_chrominanceTable);
            _chrominanceTable = null;
        }

        _hasSetup = false;
    }

    public async ValueTask<EncodingDecodeResult> DecodeAsync(
        RfbReader reader,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framebuffer);

        bool isControlSetup = rectangle.X == 0 && rectangle.Y == 0 && rectangle.Width == 0 && rectangle.Height == 0;
        if (isControlSetup)
        {
            return await DecodeSetupAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        return await DecodeSliceAsync(reader, framebuffer, rectangle, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<EncodingDecodeResult> DecodeSetupAsync(
        RfbReader reader,
        CancellationToken cancellationToken)
    {
        reader.ReserveFramebufferUpdateBytes(sizeof(uint));
        uint payloadLengthValue = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

        if (payloadLengthValue > MaxPayloadBytes)
        {
            throw new RfbProtocolException(
                $"Apple MVS Setup payload length {payloadLengthValue} exceeds the limit of {MaxPayloadBytes} bytes.");
        }

        if (payloadLengthValue != SetupPayloadLength)
        {
            throw new RfbProtocolException(
                $"Invalid Apple MVS Setup payload length: {payloadLengthValue}. Expected {SetupPayloadLength} bytes.");
        }

        int payloadLength = checked((int)payloadLengthValue);
        reader.ReserveFramebufferUpdateBytes(payloadLength);
        byte[] payload = await reader.ReadFramebufferPayloadBytesAsync(payloadLength, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            byte tableCount = payload[0];
            if (tableCount != 2)
            {
                throw new RfbProtocolException(
                    $"Unsupported Apple MVS table count: {tableCount}. Expected 2 (Luma + Chroma).");
            }

            ResetState();

            _luminanceTable = new byte[TableSize];
            _chrominanceTable = new byte[TableSize];

            Array.Copy(payload, 1, _luminanceTable, 0, TableSize);
            Array.Copy(payload, 1 + TableSize, _chrominanceTable, 0, TableSize);
            _hasSetup = true;

            return new EncodingDecodeResult(
                [],
                [],
                new RectangleTransferStatistics(
                    EncodingId,
                    sizeof(uint) + payloadLength,
                    0,
                    0,
                    false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async ValueTask<EncodingDecodeResult> DecodeSliceAsync(
        RfbReader reader,
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        CancellationToken cancellationToken)
    {
        if (!_hasSetup || _luminanceTable is null || _chrominanceTable is null)
        {
            throw new RfbProtocolException(
                "Apple MVS image slice received before a valid Setup record was established.");
        }

        rectangle.ValidateWithin(framebuffer.Width, framebuffer.Height);

        reader.ReserveFramebufferUpdateBytes(sizeof(uint));
        uint payloadLengthValue = await reader.ReadUInt32Async(cancellationToken).ConfigureAwait(false);

        if (payloadLengthValue > MaxPayloadBytes)
        {
            throw new RfbProtocolException(
                $"Apple MVS Slice payload length {payloadLengthValue} exceeds the limit of {MaxPayloadBytes} bytes.");
        }

        if (payloadLengthValue < SliceHeaderLength)
        {
            throw new RfbProtocolException(
                $"Apple MVS Slice payload length {payloadLengthValue} is too short. Minimum header size is {SliceHeaderLength} bytes.");
        }

        int payloadLength = checked((int)payloadLengthValue);
        reader.ReserveFramebufferUpdateBytes(payloadLength);
        byte[] payload = await reader.ReadFramebufferPayloadBytesAsync(payloadLength, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DecodeAndApplySlice(framebuffer, rectangle, payload, _luminanceTable, _chrominanceTable);

            return new EncodingDecodeResult(
                [rectangle],
                [rectangle],
                new RectangleTransferStatistics(
                    EncodingId,
                    sizeof(uint) + payloadLength,
                    payloadLength,
                    rectangle.Width * rectangle.Height,
                    true));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void DecodeAndApplySlice(
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> luminanceTable,
        ReadOnlySpan<byte> chrominanceTable)
    {
        if (rectangle.Width == 0 || rectangle.Height == 0)
        {
            throw new RfbProtocolException("Apple MVS slice rectangle dimensions must not be empty.");
        }

        // 单切片模式 (16x16 或边缘局部宏块)
        if (rectangle.Width <= MvsMacroblockParser.MacroblockWidth &&
            rectangle.Height <= MvsMacroblockParser.MacroblockHeight)
        {
            DecodeAndApplySingleMacroblock(framebuffer, rectangle, payload, luminanceTable, chrominanceTable);
            return;
        }

        // 大矩形多宏块流式分块解码
        int mbCols = (rectangle.Width + MvsMacroblockParser.MacroblockWidth - 1) / MvsMacroblockParser.MacroblockWidth;
        int mbRows = (rectangle.Height + MvsMacroblockParser.MacroblockHeight - 1) / MvsMacroblockParser.MacroblockHeight;

        int offset = 0;
        for (int row = 0; row < mbRows; row++)
        {
            for (int col = 0; col < mbCols; col++)
            {
                if (offset + 4 > payload.Length)
                {
                    throw new RfbProtocolException(
                        $"Apple MVS multi-block payload truncated at block ({col},{row}). Remaining: {payload.Length - offset} bytes.");
                }

                uint subSliceLength = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                offset += 4;

                if (subSliceLength < SliceHeaderLength ||
                    offset + (int)subSliceLength > payload.Length)
                {
                    throw new RfbProtocolException(
                        $"Invalid Apple MVS sub-slice length {subSliceLength} at block ({col},{row}).");
                }

                ReadOnlySpan<byte> subSlicePayload = payload.Slice(offset, (int)subSliceLength);
                offset += (int)subSliceLength;

                int curX = rectangle.X + (col * MvsMacroblockParser.MacroblockWidth);
                int curY = rectangle.Y + (row * MvsMacroblockParser.MacroblockHeight);
                int curW = Math.Min(MvsMacroblockParser.MacroblockWidth, rectangle.X + rectangle.Width - curX);
                int curH = Math.Min(MvsMacroblockParser.MacroblockHeight, rectangle.Y + rectangle.Height - curY);
                FramebufferRect curRect = new(curX, curY, curW, curH);

                DecodeAndApplySingleMacroblock(framebuffer, curRect, subSlicePayload, luminanceTable, chrominanceTable);
            }
        }
    }

    private static void DecodeAndApplySingleMacroblock(
        FramebufferModel framebuffer,
        FramebufferRect rectangle,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> luminanceTable,
        ReadOnlySpan<byte> chrominanceTable)
    {
        ReadOnlySpan<byte> effectivePayload = payload;
        if (payload.Length >= 10 &&
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]) + 4 == (uint)payload.Length)
        {
            effectivePayload = payload[4..];
        }

        // 固定 1024 字节栈缓冲，杜绝大尺寸栈分配风险
        Span<byte> macroblockBgra = stackalloc byte[MvsMacroblockParser.MacroblockPixels * 4];

        try
        {
            MvsMacroblockParser.DecodeMacroblock(
                effectivePayload,
                luminanceTable,
                chrominanceTable,
                macroblockBgra);
        }
        catch (Exception ex)
        {
            throw new RfbProtocolException($"Apple MVS macroblock decoding failed: {ex.Message}", ex);
        }

        if (rectangle.Width == MvsMacroblockParser.MacroblockWidth &&
            rectangle.Height == MvsMacroblockParser.MacroblockHeight)
        {
            // 完整 16x16 宏块直接写入
            framebuffer.ApplyRaw(rectangle, macroblockBgra);
        }
        else
        {
            // 屏幕边缘不对齐 16 像素时执行安全裁剪
            Span<byte> clipped = stackalloc byte[rectangle.Width * rectangle.Height * 4];
            int sourceStride = MvsMacroblockParser.MacroblockWidth * 4;
            int destStride = rectangle.Width * 4;

            for (int row = 0; row < rectangle.Height; row++)
            {
                ReadOnlySpan<byte> srcRow = macroblockBgra.Slice(row * sourceStride, destStride);
                Span<byte> dstRow = clipped.Slice(row * destStride, destStride);
                srcRow.CopyTo(dstRow);
            }

            framebuffer.ApplyRaw(rectangle, clipped);
        }
    }
}
