using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class DocPatchFormatterTests
{
    [Fact]
    public void Format_SingleLine_HasCorrectHeaders()
    {
        var result = DocPatchFormatter.Format("/// Old comment", "/// New comment", "src/Foo.cs", 5);

        Assert.Contains("--- a/src/Foo.cs", result);
        Assert.Contains("+++ b/src/Foo.cs", result);
        Assert.Contains("@@ -5,1 +5,1 @@", result);
    }

    [Fact]
    public void Format_SingleLine_HasMinusAndPlusLines()
    {
        var result = DocPatchFormatter.Format("/// Old", "/// New", "src/Foo.cs", 1);

        Assert.Contains("-/// Old", result);
        Assert.Contains("+/// New", result);
    }

    [Fact]
    public void Format_MultiLineOriginal_CountsCorrectly()
    {
        var original  = "/// Line one\n/// Line two\n/// Line three";
        var suggested = "/// Replacement";

        var result = DocPatchFormatter.Format(original, suggested, "src/Bar.cs", 10);

        Assert.Contains("@@ -10,3 +10,1 @@", result);
    }

    [Fact]
    public void Format_MultiLineSuggested_CountsCorrectly()
    {
        var original  = "/// Old";
        var suggested = "/// New line one\n/// New line two";

        var result = DocPatchFormatter.Format(original, suggested, "src/Bar.cs", 3);

        Assert.Contains("@@ -3,1 +3,2 @@", result);
    }

    [Fact]
    public void Format_AllOriginalLinesHaveMinusPrefix()
    {
        var original = "/// A\n/// B\n/// C";
        var result   = DocPatchFormatter.Format(original, "/// X", "src/F.cs", 1);

        Assert.Contains("-/// A", result);
        Assert.Contains("-/// B", result);
        Assert.Contains("-/// C", result);
    }

    [Fact]
    public void Format_AllSuggestedLinesHavePlusPrefix()
    {
        var suggested = "/// X\n/// Y";
        var result    = DocPatchFormatter.Format("/// Old", suggested, "src/F.cs", 1);

        Assert.Contains("+/// X", result);
        Assert.Contains("+/// Y", result);
    }

    [Fact]
    public void Format_FilePath_AppearsInBothHeaders()
    {
        var result = DocPatchFormatter.Format("old", "new", "deep/path/File.ts", 42);

        Assert.Contains("--- a/deep/path/File.ts", result);
        Assert.Contains("+++ b/deep/path/File.ts", result);
    }
}
