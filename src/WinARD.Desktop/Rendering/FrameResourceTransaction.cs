namespace WinARD.Desktop.Rendering;

internal readonly record struct FrameResourcePair<TTexture, TSwapChain>(
    TTexture Texture,
    TSwapChain SwapChain)
    where TTexture : class, IDisposable
    where TSwapChain : class, IDisposable;

internal static class FrameResourceTransaction
{
    internal const string CleanupFailuresDataKey = "WinARD.FrameResourceCleanupFailures";

    public static FrameResourcePair<TTexture, TSwapChain> Create<TTexture, TSwapChain>(
        Func<TTexture> createTexture,
        Func<TSwapChain> createSwapChain,
        Action<TSwapChain> bindSwapChain)
        where TTexture : class, IDisposable
        where TSwapChain : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(createTexture);
        ArgumentNullException.ThrowIfNull(createSwapChain);
        ArgumentNullException.ThrowIfNull(bindSwapChain);

        TTexture? texture = null;
        TSwapChain? swapChain = null;

        try
        {
            texture = createTexture();
            swapChain = createSwapChain();
            bindSwapChain(swapChain);

            var resources = new FrameResourcePair<TTexture, TSwapChain>(texture, swapChain);
            texture = null;
            swapChain = null;
            return resources;
        }
        catch (Exception primaryFailure)
        {
            List<Exception>? cleanupFailures = null;

            try
            {
                swapChain?.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures = [cleanupFailure];
            }

            try
            {
                texture?.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures ??= [];
                cleanupFailures.Add(cleanupFailure);
            }

            if (cleanupFailures is not null)
            {
                primaryFailure.Data[CleanupFailuresDataKey] = cleanupFailures.ToArray();
            }

            throw;
        }
    }
}
