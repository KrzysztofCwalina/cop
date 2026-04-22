using NUnit.Framework;
using Cop.Lang;

namespace Lang.Tests.Lang;

[TestFixture]
public class AnsiRendererTests
{
    private const string Reset = "\x1b[0m";
    private const string Red = "\x1b[31m";
    private const string Yellow = "\x1b[33m";
    private const string Cyan = "\x1b[36m";
    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";

    [Test]
    public void Render_PlainText_NoAnsiCodes()
    {
        var rich = new RichString("hello world");
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo("hello world"));
    }

    [Test]
    public void Render_StaticRedAnnotation_EmitsRedAnsi()
    {
        var spans = new[] { new TextSpan("Error:", RichString.ParseAnnotation("red")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Red}Error:{Reset}"));
    }

    [Test]
    public void Render_BoldWeight_EmitsBoldAnsi()
    {
        var spans = new[] { new TextSpan("Warning:", RichString.ParseAnnotation("bold")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Bold}Warning:{Reset}"));
    }

    [Test]
    public void Render_AutoColor_ErrorMapsToRed()
    {
        var spans = new[] { new TextSpan("error", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Red}error{Reset}"));
    }

    [Test]
    public void Render_AutoColor_WarningMapsToYellow()
    {
        var spans = new[] { new TextSpan("warning", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Yellow}warning{Reset}"));
    }

    [Test]
    public void Render_AutoColor_InfoMapsToCyan()
    {
        var spans = new[] { new TextSpan("info", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Cyan}info{Reset}"));
    }

    [Test]
    public void Render_AutoColor_CaseInsensitive()
    {
        var spans = new[] { new TextSpan("ERROR", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Red}ERROR{Reset}"));
    }

    [Test]
    public void Render_AutoColor_NamedColor_UsesDirectLookup()
    {
        // "red" is both a named color and would match auto — auto should find it via ColorCodes
        var spans = new[] { new TextSpan("red", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Red}red{Reset}"));
    }

    [Test]
    public void Render_AutoColor_UnknownValue_NoColorCodes()
    {
        var spans = new[] { new TextSpan("unknown", RichString.ParseAnnotation("auto")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo("unknown"));
    }

    [Test]
    public void Render_AutoColor_WithWeightAnnotation()
    {
        // auto + bold combined
        var annotations = new Dictionary<string, string> { ["color"] = "auto", ["weight"] = "bold" };
        var spans = new[] { new TextSpan("error", annotations) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Red}{Bold}error{Reset}"));
    }

    [Test]
    public void Render_DimWeight_EmitsDimAnsi()
    {
        var spans = new[] { new TextSpan("file.cs(10)", RichString.ParseAnnotation("dim")) };
        var rich = new RichString(spans);
        Assert.That(AnsiRenderer.Render(rich), Is.EqualTo($"{Dim}file.cs(10){Reset}"));
    }

    [Test]
    public void Render_MixedSpans_CheckLikeOutput()
    {
        // Simulates: file.cs(10): error: message
        var spans = new TextSpan[]
        {
            new("src/file.cs", RichString.ParseAnnotation("dim")),
            new("("),
            new("10", RichString.ParseAnnotation("dim")),
            new("): "),
            new("error", RichString.ParseAnnotation("auto")),
            new(": something went wrong"),
        };
        var rich = new RichString(spans);
        var rendered = AnsiRenderer.Render(rich);
        Assert.That(rendered, Is.EqualTo(
            $"{Dim}src/file.cs{Reset}({Dim}10{Reset}): {Red}error{Reset}: something went wrong"));
    }
}
