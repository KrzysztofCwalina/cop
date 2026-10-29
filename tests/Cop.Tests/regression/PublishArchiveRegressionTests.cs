using System.Diagnostics;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PublishArchiveRegressionTests
{
    [Test]
    [Platform("Win")]
    public void UnixHostPatch_PreservesAnArchiveWithAnOpenReadOnlyMapping()
    {
        var repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository) ?? throw new AssertionException("Cannot locate repository.");
        var root = Path.Combine(Path.GetTempPath(), "cop-mapped-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cop-linux.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("cop");
                entry.ExternalAttributes = unchecked((int)0x81ED0000);
                using var content = new StreamWriter(entry.Open());
                content.Write("standalone executable");
            }
            var original = File.ReadAllBytes(path);
            var central = original.AsSpan().IndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            Assert.That(central, Is.GreaterThanOrEqualTo(0));
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var mapping = MemoryMappedFile.CreateFromFile(stream, null, 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            Assert.That(view.ReadByte(central + 5), Is.Zero);
            var script = Path.Combine(repository, "install", "publish.ps1").Replace("'", "''");
            var command = $$"""
                $ErrorActionPreference = 'Stop'
                $tokens = $null; $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{script}}', [ref]$tokens, [ref]$errors)
                $function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-ZipUnixHostOS' }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
                Set-ZipUnixHostOS '{{path.Replace("'", "''")}}' | Out-Null
                """;
            var start = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Archive permission patch timed out.");
            }
            Assert.That(process.ExitCode, Is.Zero, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            Assert.That(view.ReadByte(central + 5), Is.EqualTo(3));
            Assert.That(new FileInfo(path).Length, Is.EqualTo(original.Length));
            using var verified = ZipFile.OpenRead(path);
            Assert.That(verified.Entries.Count, Is.EqualTo(1));
            Assert.That(verified.Entries[0].ExternalAttributes, Is.EqualTo(unchecked((int)0x81ED0000)));
            using var reader = new StreamReader(verified.Entries[0].Open());
            Assert.That(reader.ReadToEnd(), Is.EqualTo("standalone executable"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
