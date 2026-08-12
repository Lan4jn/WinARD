using System.Reflection;
using System.Runtime.CompilerServices;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Errors;

public sealed class RfbProtocolFailureInfoTests
{
    [Fact]
    public void New_protocol_failure_kind_is_appended_for_compatibility()
    {
        Assert.Equal(11, (int)RfbProtocolFailureKind.MalformedHandshake);
    }

    [Fact]
    public void Ard_encrypted_packet_enum_values_are_stable()
    {
        Assert.Equal(0, (int)ArdEncryptedPacketDirection.Send);
        Assert.Equal(1, (int)ArdEncryptedPacketDirection.Receive);

        Assert.Equal(0, (int)ArdEncryptedPacketFailureStage.OuterLength);
        Assert.Equal(1, (int)ArdEncryptedPacketFailureStage.TruncatedCiphertext);
        Assert.Equal(2, (int)ArdEncryptedPacketFailureStage.CbcDecrypt);
        Assert.Equal(3, (int)ArdEncryptedPacketFailureStage.PlaintextTooShort);
        Assert.Equal(4, (int)ArdEncryptedPacketFailureStage.PayloadLength);
        Assert.Equal(5, (int)ArdEncryptedPacketFailureStage.Padding);
        Assert.Equal(6, (int)ArdEncryptedPacketFailureStage.Integrity);
        Assert.Equal(7, (int)ArdEncryptedPacketFailureStage.StateCommit);
    }

    [Fact]
    public void Kind_only_constructor_defaults_optional_context_to_null()
    {
        var failure = new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead);

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, failure.Kind);
        Assert.Null(failure.ReadStage);
        Assert.Null(failure.ServerMessageType);
        Assert.Null(failure.EncodingId);
        Assert.Null(failure.RectangleIndex);
        Assert.Null(failure.ArdEncryptionStage);
        Assert.Null(failure.ArdEncryptionDirection);
        Assert.Null(failure.ArdEncryptionSequence);
        Assert.Null(failure.ArdCiphertextLength);
        Assert.Null(failure.HandshakeStage);
        Assert.Null(failure.ExpectedByteCount);
        Assert.Null(failure.ActualByteCount);
    }

    [Fact]
    public void FillMissingFrom_preserves_inner_values_and_fills_null_values_from_outer_context()
    {
        var inner = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.DecoderFailure,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            null,
            16,
            null);
        var outer = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedFramebufferUpdate,
            RfbProtocolReadStage.FramebufferHeader,
            0,
            5,
            3);

        var result = inner.FillMissingFrom(outer);

        Assert.Equal(RfbProtocolFailureKind.DecoderFailure, result.Kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, result.ReadStage);
        Assert.Equal((byte)0, result.ServerMessageType);
        Assert.Equal(16, result.EncodingId);
        Assert.Equal(3, result.RectangleIndex);
    }

    [Fact]
    public void FillMissingFrom_preserves_inner_ard_packet_context_and_fills_read_stage_from_outer_context()
    {
        var inner = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptionStage: ArdEncryptedPacketFailureStage.Padding,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
            ArdEncryptionSequence: 1,
            ArdCiphertextLength: 48);
        var outer = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.ArdEncryptionPacket,
            RfbProtocolReadStage.ArdStateChangePayload,
            ArdEncryptionStage: ArdEncryptedPacketFailureStage.OuterLength,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Send,
            ArdEncryptionSequence: 9,
            ArdCiphertextLength: 64);

        var result = inner.FillMissingFrom(outer);

        Assert.Equal(RfbProtocolReadStage.ArdStateChangePayload, result.ReadStage);
        Assert.Equal(ArdEncryptedPacketFailureStage.Padding, result.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, result.ArdEncryptionDirection);
        Assert.Equal((uint)1, result.ArdEncryptionSequence);
        Assert.Equal(48, result.ArdCiphertextLength);
    }

    [Fact]
    public void FillMissingFrom_fills_missing_inner_ard_packet_context_from_outer_context()
    {
        var inner = new RfbProtocolFailureInfo(RfbProtocolFailureKind.ArdEncryptionPacket);
        var outer = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.ArdEncryptionPacket,
            ArdEncryptionStage: ArdEncryptedPacketFailureStage.Padding,
            ArdEncryptionDirection: ArdEncryptedPacketDirection.Receive,
            ArdEncryptionSequence: 1,
            ArdCiphertextLength: 48);

        var result = inner.FillMissingFrom(outer);

        Assert.Equal(ArdEncryptedPacketFailureStage.Padding, result.ArdEncryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, result.ArdEncryptionDirection);
        Assert.Equal((uint)1, result.ArdEncryptionSequence);
        Assert.Equal(48, result.ArdCiphertextLength);
    }

    [Fact]
    public void FillMissingFrom_preserves_read_counts_and_fills_handshake_stage()
    {
        var inner = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            ExpectedByteCount: 12,
            ActualByteCount: 10);
        var outer = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            HandshakeStage: RfbHandshakeStage.VersionBanner,
            ExpectedByteCount: 99,
            ActualByteCount: 98);

        var result = inner.FillMissingFrom(outer);

        Assert.Equal(RfbHandshakeStage.VersionBanner, result.HandshakeStage);
        Assert.Equal(12, result.ExpectedByteCount);
        Assert.Equal(10, result.ActualByteCount);
    }

    [Fact]
    public void WithContext_uses_context_when_exception_has_no_failure()
    {
        var exception = new RfbProtocolException("failure");
        var context = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.UnsupportedEncoding,
            RfbProtocolReadStage.FramebufferRectangleHeader,
            0,
            -239,
            2);

        var wrapped = exception.WithContext(context);

        Assert.NotSame(exception, wrapped);
        Assert.Equal(exception.Message, wrapped.Message);
        Assert.Same(exception, wrapped.InnerException);
        Assert.Same(context, wrapped.Failure);
    }

    [Fact]
    public void WithContext_preserves_existing_failure_values_and_fills_missing_context()
    {
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            null,
            null,
            null,
            null);
        var exception = RfbProtocolException.Create("failure", failure);
        var context = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedFramebufferUpdate,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            0,
            16,
            4);

        var wrapped = exception.WithContext(context);

        Assert.Equal(
            new RfbProtocolFailureInfo(
                RfbProtocolFailureKind.TruncatedRead,
                RfbProtocolReadStage.FramebufferRectanglePayload,
                0,
                16,
                4),
            wrapped.Failure);
        Assert.NotSame(exception, wrapped);
        Assert.Same(exception, wrapped.InnerException);
    }

    [Fact]
    public void WithContext_returns_same_exception_when_context_adds_no_fields()
    {
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedClipboard,
            RfbProtocolReadStage.ClipboardPayload,
            3);
        var exception = RfbProtocolException.Create("failure", failure);
        var redundantContext = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.DecoderFailure,
            RfbProtocolReadStage.ClipboardHeader,
            3);

        var result = exception.WithContext(redundantContext);

        Assert.Same(exception, result);
        Assert.Same(failure, result.Failure);
        Assert.Null(result.InnerException);
    }

    [Fact]
    public void Existing_constructors_remain_compatible()
    {
        var inner = new InvalidOperationException("inner");
        var legacyFailure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            RfbProtocolReadStage.FramebufferHeader,
            0,
            16,
            2);

        var messageOnly = new RfbProtocolException("message");
        var withInner = new RfbProtocolException("message", inner);

        Assert.Equal("message", messageOnly.Message);
        Assert.Null(messageOnly.InnerException);
        Assert.Null(messageOnly.Failure);
        Assert.Equal("message", withInner.Message);
        Assert.Same(inner, withInner.InnerException);
        Assert.Null(withInner.Failure);
        Assert.Null(legacyFailure.ArdEncryptionStage);
        Assert.Null(legacyFailure.ArdEncryptionDirection);
        Assert.Null(legacyFailure.ArdEncryptionSequence);
        Assert.Null(legacyFailure.ArdCiphertextLength);
    }

    [Fact]
    public void Five_parameter_failure_info_constructor_is_preserved_for_binary_compatibility()
    {
        var constructor = typeof(RfbProtocolFailureInfo).GetConstructor(
            [
                typeof(RfbProtocolFailureKind),
                typeof(RfbProtocolReadStage?),
                typeof(byte?),
                typeof(int?),
                typeof(int?),
            ]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public void Nine_parameter_failure_info_constructor_is_preserved_for_binary_compatibility()
    {
        var constructor = typeof(RfbProtocolFailureInfo).GetConstructor(
            [
                typeof(RfbProtocolFailureKind),
                typeof(RfbProtocolReadStage?),
                typeof(byte?),
                typeof(int?),
                typeof(int?),
                typeof(ArdEncryptedPacketFailureStage?),
                typeof(ArdEncryptedPacketDirection?),
                typeof(uint?),
                typeof(int?),
            ]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public void Twelve_parameter_failure_info_constructor_is_preserved_for_binary_compatibility()
    {
        var constructor = typeof(RfbProtocolFailureInfo).GetConstructor(
            [
                typeof(RfbProtocolFailureKind),
                typeof(RfbProtocolReadStage?),
                typeof(byte?),
                typeof(int?),
                typeof(int?),
                typeof(ArdEncryptedPacketFailureStage?),
                typeof(ArdEncryptedPacketDirection?),
                typeof(uint?),
                typeof(int?),
                typeof(RfbHandshakeStage?),
                typeof(int?),
                typeof(int?),
            ]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public void Five_element_failure_info_deconstruction_remains_source_compatible()
    {
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.TruncatedRead,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            0,
            16,
            3);

        var (kind, readStage, serverMessageType, encodingId, rectangleIndex) = failure;

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, readStage);
        Assert.Equal((byte)0, serverMessageType);
        Assert.Equal(16, encodingId);
        Assert.Equal(3, rectangleIndex);
    }

    [Fact]
    public void Nine_element_failure_info_deconstruction_remains_source_compatible()
    {
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.ArdEncryptionPacket,
            RfbProtocolReadStage.ServerMessageType,
            0,
            16,
            3,
            ArdEncryptedPacketFailureStage.Padding,
            ArdEncryptedPacketDirection.Receive,
            1,
            48);

        var (
            kind,
            readStage,
            serverMessageType,
            encodingId,
            rectangleIndex,
            encryptionStage,
            encryptionDirection,
            encryptionSequence,
            ciphertextLength) = failure;

        Assert.Equal(RfbProtocolFailureKind.ArdEncryptionPacket, kind);
        Assert.Equal(RfbProtocolReadStage.ServerMessageType, readStage);
        Assert.Equal((byte)0, serverMessageType);
        Assert.Equal(16, encodingId);
        Assert.Equal(3, rectangleIndex);
        Assert.Equal(ArdEncryptedPacketFailureStage.Padding, encryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, encryptionDirection);
        Assert.Equal((uint)1, encryptionSequence);
        Assert.Equal(48, ciphertextLength);
    }

    [Fact]
    public void Twelve_element_failure_info_deconstruction_remains_source_compatible()
    {
        var failure = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedHandshake,
            RfbProtocolReadStage.FramebufferRectanglePayload,
            0,
            6,
            2,
            ArdEncryptedPacketFailureStage.Integrity,
            ArdEncryptedPacketDirection.Receive,
            7,
            48,
            RfbHandshakeStage.SecurityTypes,
            1024,
            512,
            RfbDecoderFailureReason.InvalidCompressedStream);

        var (
            kind,
            readStage,
            serverMessageType,
            encodingId,
            rectangleIndex,
            encryptionStage,
            encryptionDirection,
            encryptionSequence,
            ciphertextLength,
            handshakeStage,
            expectedByteCount,
            actualByteCount) = failure;

        Assert.Equal(RfbProtocolFailureKind.MalformedHandshake, kind);
        Assert.Equal(RfbProtocolReadStage.FramebufferRectanglePayload, readStage);
        Assert.Equal((byte)0, serverMessageType);
        Assert.Equal(6, encodingId);
        Assert.Equal(2, rectangleIndex);
        Assert.Equal(ArdEncryptedPacketFailureStage.Integrity, encryptionStage);
        Assert.Equal(ArdEncryptedPacketDirection.Receive, encryptionDirection);
        Assert.Equal((uint)7, encryptionSequence);
        Assert.Equal(48, ciphertextLength);
        Assert.Equal(RfbHandshakeStage.SecurityTypes, handshakeStage);
        Assert.Equal(1024, expectedByteCount);
        Assert.Equal(512, actualByteCount);
    }

    [Fact]
    public void Existing_constructors_preserve_null_argument_behavior()
    {
        var messageOnly = new RfbProtocolException(null!);
        var nullInner = new RfbProtocolException("message", (Exception)null!);

        Assert.NotNull(messageOnly);
        Assert.Null(messageOnly.InnerException);
        Assert.Null(nullInner.InnerException);
    }

    [Fact]
    public void Structured_failure_api_rejects_null_arguments()
    {
        var failure = new RfbProtocolFailureInfo(RfbProtocolFailureKind.DecoderFailure, null, null, null, null);
        var inner = new InvalidOperationException();

        Assert.Throws<ArgumentNullException>(() => RfbProtocolException.Create(null!, failure));
        Assert.Throws<ArgumentNullException>(() => RfbProtocolException.Create("message", null!));
        Assert.Throws<ArgumentNullException>(() => new RfbProtocolException("message", inner, null!));
        Assert.Throws<ArgumentNullException>(() => new RfbProtocolException("message", null!, failure));
        Assert.Throws<ArgumentNullException>(() => new RfbProtocolException(null!, inner, failure));

        var exception = new RfbProtocolException("message");
        Assert.Throws<ArgumentNullException>(() => exception.WithContext(null!));
        Assert.Throws<ArgumentNullException>(() => failure.FillMissingFrom(null!));
    }

    [Fact]
    public void Failure_info_is_immutable_and_has_value_equality()
    {
        var first = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedClipboard,
            RfbProtocolReadStage.ClipboardPayload,
            3,
            null,
            null,
            ArdEncryptedPacketFailureStage.Integrity,
            ArdEncryptedPacketDirection.Receive,
            7,
            32);
        var second = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedClipboard,
            RfbProtocolReadStage.ClipboardPayload,
            3,
            null,
            null,
            ArdEncryptedPacketFailureStage.Integrity,
            ArdEncryptedPacketDirection.Receive,
            7,
            32);

        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        Assert.All(
            typeof(RfbProtocolFailureInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Contains(
                typeof(IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }
}
