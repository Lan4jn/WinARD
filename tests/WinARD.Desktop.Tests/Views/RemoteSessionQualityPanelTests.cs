using System.Xml.Linq;
using Xunit;

#pragma warning disable CA1707

namespace WinARD.Desktop.Tests.Views;

public sealed class RemoteSessionQualityPanelTests
{
    [Fact]
    public void Flyout_exposes_stable_desired_and_applied_automation_ids()
    {
        var document = XDocument.Parse(File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml")));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        AssertQualityTextBlock(
            document,
            presentation,
            x,
            "QualityDesiredText",
            "RemoteQualityDesiredText");
        AssertQualityTextBlock(
            document,
            presentation,
            x,
            "QualityAppliedText",
            "RemoteQualityAppliedText");
        AssertQualityTextBlock(
            document,
            presentation,
            x,
            "QualityEncodingText",
            "RemoteQualityEncodingText");
    }

    [Fact]
    public void Refresh_keeps_saved_controls_desired_but_prioritizes_actual_summary_and_automation()
    {
        var source = File.ReadAllText(RepositoryFile(
            "src", "WinARD.Desktop", "Views", "RemoteSessionWindow.xaml.cs"));

        Assert.Contains("QualityColorComboBox.SelectedItem", source, StringComparison.Ordinal);
        Assert.Contains("option.Value == profile.Color", source, StringComparison.Ordinal);
        Assert.Contains("QualityPresentation.DesiredText", source, StringComparison.Ordinal);
        Assert.Contains("QualityPresentation.AppliedText", source, StringComparison.Ordinal);
        Assert.Contains("QualityPresentation.StatusFor(presentation)", source, StringComparison.Ordinal);
        Assert.Contains("QualityDesiredText.Text = desired", source, StringComparison.Ordinal);
        Assert.Contains("QualityAppliedText.Text = applied", source, StringComparison.Ordinal);
        Assert.Contains("QualityEncodingText.Text = encoding", source, StringComparison.Ordinal);
        Assert.Contains("QualitySummaryButton.Content = $\"画质  {applied}\"", source, StringComparison.Ordinal);
        Assert.Contains("QualityPresentation.AutomationName(desired, applied, status)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "performance.PrimaryFramebufferEncoding",
            source,
            StringComparison.Ordinal);
    }

    private static void AssertQualityTextBlock(
        XDocument document,
        XNamespace presentation,
        XNamespace x,
        string expectedName,
        string expectedAutomationId)
    {
        var element = Assert.Single(
            document.Descendants(presentation + "TextBlock"),
            item => (string?)item.Attribute(x + "Name") == expectedName);
        Assert.Equal(
            expectedAutomationId,
            (string?)element.Attribute("AutomationProperties.AutomationId"));
        Assert.Single(
            document.Descendants(),
            item => (string?)item.Attribute("AutomationProperties.AutomationId") == expectedAutomationId);
    }

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinARD.sln")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new FileNotFoundException("Could not locate repository root.")
            : Path.Combine([directory.FullName, .. segments]);
    }
}
