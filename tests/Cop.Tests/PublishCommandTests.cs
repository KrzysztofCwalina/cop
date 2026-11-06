using System.CommandLine;
using System.Text.Json;
using Cop.Cli.Commands;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public sealed class PublishCommandTests
{
    private string _root = null!;
    private string _previousDirectory = null!;
    private TextWriter _previousOutput = null!;
    private TextWriter _previousError = null!;
    private StringWriter _output = null!;
    private StringWriter _errors = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cop-publish-{Guid.NewGuid():N}");
        string package = Path.Combine(_root, "packages", "widget");
        foreach (string directory in new[] { "instructions", "skills", "src", "tests" })
        {
            Directory.CreateDirectory(Path.Combine(package, directory));
        }
        File.WriteAllText(Path.Combine(package, PackageMetadata.MetadataFileName),
            JsonSerializer.Serialize(new
            {
                name = "widget",
                version = "1.2.3",
                title = "Widget",
                description = "Publication test",
                authors = "Cop Tests"
            }));
        _previousDirectory = Directory.GetCurrentDirectory();
        _previousOutput = Console.Out;
        _previousError = Console.Error;
        _output = new StringWriter();
        _errors = new StringWriter();
        Directory.SetCurrentDirectory(_root);
        Console.SetOut(_output);
        Console.SetError(_errors);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        Console.SetOut(_previousOutput);
        Console.SetError(_previousError);
        _output.Dispose();
        _errors.Dispose();
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task FailedTagInspectionIsNotTreatedAsAnAbsentTag()
    {
        int exitCode = await PublishCommand.Create().Parse(["widget"]).InvokeAsync();
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_errors.ToString(), Does.StartWith("Error: Cannot inspect existing git tags:"));
        Assert.That(_errors.ToString(), Does.Not.Contain("Failed to create git tag"));
        Assert.That(_output.ToString(), Is.Empty);
    }

    [Test]
    public async Task CreatesOneTagAndRejectsAnExistingVersion()
    {
        await Git("init");
        await Git("config", "commit.gpgsign", "false");
        await Git("-c", "user.email=cop-tests@example.invalid", "-c", "user.name=Cop Tests",
            "commit", "--allow-empty", "-m", "Seed");

        int firstExit = await PublishCommand.Create().Parse(["widget"]).InvokeAsync();
        Assert.That(firstExit, Is.EqualTo(0), _errors.ToString());
        Assert.That(_output.ToString(), Is.EqualTo(
            $"Published widget v1.2.3 (tag: widget/1.2.3){Environment.NewLine}" +
            $"Run 'git push origin widget/1.2.3' to push the tag{Environment.NewLine}"));
        Assert.That(_errors.ToString(), Is.Empty);
        Assert.That((await Git("tag", "-l")).Trim(), Is.EqualTo("widget/1.2.3"));

        _output.GetStringBuilder().Clear();
        int secondExit = await PublishCommand.Create().Parse(["widget"]).InvokeAsync();
        Assert.That(secondExit, Is.EqualTo(1));
        Assert.That(_errors.ToString(), Is.EqualTo(
            $"Error: Version 1.2.3 already published (tag 'widget/1.2.3' exists).{Environment.NewLine}"));
        Assert.That(_output.ToString(), Is.Empty);
        Assert.That((await Git("tag", "-l")).Trim(), Is.EqualTo("widget/1.2.3"));
    }

    private async Task<string> Git(params string[] arguments)
    {
        string executable = ToolchainProcess.FindExecutable("git")
            ?? throw new InvalidOperationException("Git is required for publication tests.");
        ToolchainResult result = await ToolchainProcess.RunAsync(
            new ToolchainCommand(executable, arguments, _root));
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.Diagnostic);
        return result.StandardOutput;
    }
}
