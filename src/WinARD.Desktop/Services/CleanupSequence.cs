namespace WinARD.Desktop.Services;

public static class CleanupSequence
{
    public static async Task RunAsync(params Func<Task>[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        List<Exception>? failures = null;
        foreach (var operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is [var only])
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(only).Throw();
        }
        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }
}
