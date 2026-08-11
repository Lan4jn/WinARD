namespace WinARD.Infrastructure.Tests;

internal static class RepositoryFile
{
    public static string Find(params string[] segments) =>
        FindFrom(AppContext.BaseDirectory, segments);

    internal static string FindFrom(string startDirectory, params string[] segments)
    {
        var relativeTarget = Path.Combine(segments);
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativeTarget);
            if (File.Exists(Path.Combine(directory.FullName, "WinARD.sln")) && File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativeTarget}'.");
    }
}
