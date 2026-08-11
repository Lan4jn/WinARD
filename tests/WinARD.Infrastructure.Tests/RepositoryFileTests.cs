using Xunit;

#pragma warning disable CA1707

namespace WinARD.Infrastructure.Tests;

public sealed class RepositoryFileTests
{
    [Fact]
    public void Find_skips_solution_marker_when_requested_file_is_missing()
    {
        using var tree = new RepositoryTree();
        var expected = tree.AddFile("src", "target.cs");
        tree.AddFile("WinARD.sln");
        var falseCheckout = tree.AddDirectory("nested", "checkout");
        File.WriteAllText(Path.Combine(falseCheckout, "WinARD.sln"), string.Empty);
        var startDirectory = Directory.CreateDirectory(Path.Combine(falseCheckout, "artifacts", "bin")).FullName;

        var actual = RepositoryFile.FindFrom(startDirectory, "src", "target.cs");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Find_failure_reports_only_the_safe_relative_target()
    {
        using var tree = new RepositoryTree();
        tree.AddFile("WinARD.sln");
        var startDirectory = tree.AddDirectory("artifacts", "bin");
        var relativeTarget = Path.Combine("src", "missing.cs");

        var exception = Assert.Throws<FileNotFoundException>(() =>
            RepositoryFile.FindFrom(startDirectory, "src", "missing.cs"));

        Assert.Equal($"Could not locate repository file '{relativeTarget}'.", exception.Message);
        Assert.DoesNotContain(tree.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AppContext.BaseDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RepositoryTree : IDisposable
    {
        public RepositoryTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "WinARD.Tests", "RepositoryFile", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string AddDirectory(params string[] segments) =>
            Directory.CreateDirectory(Path.Combine([Root, .. segments])).FullName;

        public string AddFile(params string[] segments)
        {
            var path = Path.Combine([Root, .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
