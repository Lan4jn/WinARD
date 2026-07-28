using System.Reflection;
using System.Runtime.CompilerServices;
using WinARD.Remote.Protocol.Errors;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Remote.Protocol.Tests.Errors;

public sealed class RfbProtocolFailureInfoTests
{
    [Fact]
    public void Kind_only_constructor_defaults_optional_context_to_null()
    {
        var failure = new RfbProtocolFailureInfo(RfbProtocolFailureKind.TruncatedRead);

        Assert.Equal(RfbProtocolFailureKind.TruncatedRead, failure.Kind);
        Assert.Null(failure.ReadStage);
        Assert.Null(failure.ServerMessageType);
        Assert.Null(failure.EncodingId);
        Assert.Null(failure.RectangleIndex);
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
        Assert.Same(exception, wrapped.InnerException);
    }

    [Fact]
    public void Existing_constructors_remain_compatible()
    {
        var inner = new InvalidOperationException("inner");

        var messageOnly = new RfbProtocolException("message");
        var withInner = new RfbProtocolException("message", inner);

        Assert.Equal("message", messageOnly.Message);
        Assert.Null(messageOnly.InnerException);
        Assert.Null(messageOnly.Failure);
        Assert.Equal("message", withInner.Message);
        Assert.Same(inner, withInner.InnerException);
        Assert.Null(withInner.Failure);
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
            null);
        var second = new RfbProtocolFailureInfo(
            RfbProtocolFailureKind.MalformedClipboard,
            RfbProtocolReadStage.ClipboardPayload,
            3,
            null,
            null);

        Assert.Equal(first, second);
        Assert.NotSame(first, second);
        Assert.All(
            typeof(RfbProtocolFailureInfo).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Contains(
                typeof(IsExternalInit),
                property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()));
    }
}
