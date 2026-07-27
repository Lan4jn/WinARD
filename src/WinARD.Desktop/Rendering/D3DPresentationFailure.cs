using SharpGen.Runtime;

namespace WinARD.Desktop.Rendering;

internal enum D3DPresentationStage
{
    CreateDevice,
    CreateTexture2D,
    CreateSwapChainForComposition,
    SetSwapChain,
    GetBuffer,
    UpdateSubresource,
    CopySubresourceRegion,
    Present1,
    RecoveryDetachSwapChain,
    RecoveryCreateSwapChain,
    RecoverySetSwapChain,
    RecoveryGetBuffer,
    RecoveryPresent,
}

internal sealed class D3DPresentationException : Exception
{
    internal const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);

    public D3DPresentationException(
        D3DPresentationStage stage,
        Exception innerException)
        : base($"D3D presentation operation '{stage}' failed.", innerException)
    {
        Stage = stage;
        HResult = innerException.HResult;
    }

    public D3DPresentationStage Stage { get; }

    public bool IsPresent1InvalidCall =>
        Stage == D3DPresentationStage.Present1 &&
        HResult == DxgiErrorInvalidCall;
}

internal delegate void D3DPresentationSpanOperation(ReadOnlySpan<byte> bytes);

internal static class D3DPresentationOperation
{
    public static void Run(D3DPresentationStage stage, Action operation)
    {
        try
        {
            operation();
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }

    public static T Run<T>(D3DPresentationStage stage, Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }

    public static void Run(
        D3DPresentationStage stage,
        ReadOnlySpan<byte> bytes,
        D3DPresentationSpanOperation operation)
    {
        try
        {
            operation(bytes);
        }
        catch (SharpGenException exception)
        {
            throw new D3DPresentationException(stage, exception);
        }
    }
}
