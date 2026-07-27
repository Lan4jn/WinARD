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
            texture = createTexture()
                ?? throw new InvalidOperationException("Frame texture creation returned null.");
            swapChain = createSwapChain()
                ?? throw new InvalidOperationException("DXGI swap-chain creation returned null.");
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
                TryAttachCleanupFailures(primaryFailure, cleanupFailures);
            }

            throw;
        }
    }

#pragma warning disable CA1859
    internal static void TryAttachCleanupFailures(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        try
        {
            var data = primaryFailure.Data;
            if (!data.Contains(CleanupFailuresDataKey))
            {
                data[CleanupFailuresDataKey] = cleanupFailures.ToArray();
                return;
            }

            if (data[CleanupFailuresDataKey] is not Exception[] existingCleanupFailures)
            {
                return;
            }

            var combinedCleanupFailures =
                new Exception[existingCleanupFailures.Length + cleanupFailures.Count];
            Array.Copy(
                existingCleanupFailures,
                combinedCleanupFailures,
                existingCleanupFailures.Length);
            for (var index = 0; index < cleanupFailures.Count; index++)
            {
                combinedCleanupFailures[existingCleanupFailures.Length + index] =
                    cleanupFailures[index];
            }

            data[CleanupFailuresDataKey] = combinedCleanupFailures;
        }
        catch (Exception)
        {
            // Diagnostic attachment must never replace the primary failure.
        }
    }
#pragma warning restore CA1859
}
