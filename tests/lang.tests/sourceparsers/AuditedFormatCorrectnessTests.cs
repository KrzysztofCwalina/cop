using Cop.Core;
using Cop.Providers;
using Cop.Providers.Bash;
using Cop.Providers.Dockerfile;
using Cop.Providers.PowerShell;
using Cop.Providers.Sql;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class AuditedFormatCorrectnessTests
{
    [Test]
    public void FormatSnapshot_IsLazyPerFileAndReusesParsedObjectsAcrossCollections()
    {
        int discovered = 0, parsed = 0;
        var snapshot = new DeferredFormatSnapshot(() => { discovered++; return ["b", "a"]; }, path =>
        {
            parsed++;
            var item = new object();
            return new() { ["Commands"] = [item], ["Scripts"] = [item] };
        });
        var schema = new ProviderSchema
        {
            Collections = [new() { Name = "Commands", ItemType = "Item" }, new() { Name = "Scripts", ItemType = "Item" }]
        };
        var result = snapshot.Query(new ProviderQuery(), schema, new RuntimeBindings());
        Assert.That((discovered, parsed), Is.EqualTo((0, 0)));
        var first = result.Collections["Commands"].First();
        Assert.That((discovered, parsed), Is.EqualTo((1, 1)));
        Assert.That(result.Collections["Scripts"].First(), Is.SameAs(first));
        Assert.That((discovered, parsed), Is.EqualTo((1, 1)));
        Assert.That(result.Collections["Scripts"].Count(), Is.EqualTo(2));
        Assert.That(result.Collections["Commands"].Count(), Is.EqualTo(2));
        Assert.That((discovered, parsed), Is.EqualTo((1, 2)));
    }

    [Test]
    public void FormatSnapshot_ReportsReadFailuresInsteadOfSilentlyDroppingFiles()
    {
        ProviderErrors.Clear();
        try
        {
            var snapshot = new DeferredFormatSnapshot(() => ["missing.sql"], path => throw new IOException("locked"));
            var result = snapshot.Query(new ProviderQuery(), new ProviderSchema
            {
                Collections = [new() { Name = "Statements", ItemType = "SqlStatement" }]
            }, new RuntimeBindings());
            Assert.That(result.Collections["Statements"].Count(), Is.EqualTo(0));
            Assert.That(ProviderErrors.Drain(), Is.EqualTo(new[] { "Failed to read 'missing.sql': locked" }));
        }
        finally { ProviderErrors.Clear(); }
    }

    [TestCase("bash", "sample.sh", "echo first", "Commands", "Scripts")]
    [TestCase("powershell", "sample.ps1", "Write-Output 'first'", "Commands", "Scripts")]
    [TestCase("dockerfile", "Dockerfile", "FROM first", "Instructions", "Stages")]
    [TestCase("sql", "sample.sql", "SELECT id FROM first", "Statements", "Statements")]
    public void FormatProviders_DeferReadAndShareTheExecutionSnapshot(string language, string name, string text, string first, string second)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-format-lazy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, name);
            DataProvider provider = language switch
            {
                "bash" => new BashProvider(), "powershell" => new PowerShellProvider(),
                "dockerfile" => new DockerfileProvider(), _ => new SqlProvider()
            };
            using var execution = new ToolchainExecutionScope();
            var result = (DeferredProviderResult)provider.QueryDeferred(new ProviderQuery { RootPath = root })!;
            File.WriteAllText(path, text);
            var item = result.Collections[first].Single();
            File.Delete(path);
            var repeated = (DeferredProviderResult)provider.QueryDeferred(new ProviderQuery { RootPath = root })!;
            Assert.That(repeated.Collections[first].Single(), Is.SameAs(item));
            Assert.That(repeated.Collections[second].Count(), Is.EqualTo(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Bash_ConditionalCommandsAndEmbeddedHashes_AreNotLost()
    {
        var parsed = BashParser.Parse("""
            if curl https://host/#path; then echo done; fi
            while read item; do printf '%s' "$item"; done
            """);
        Assert.That(parsed.Commands.Select(c => c.Name), Is.EqualTo(new[] { "curl", "echo", "read", "printf" }));
        Assert.That(parsed.Commands[0].Text, Does.Contain("https://host/#path"));
    }

    [TestCase("set -e", false)]
    [TestCase("set -eu", false)]
    [TestCase("set -euo pipefail", true)]
    [TestCase("set -e\nset -u\nset -o pipefail", true)]
    [TestCase("set -euo pipefail\nset +u", false)]
    [TestCase("set -o errexit -o nounset -o pipefail", true)]
    public void Bash_StrictModeRequiresAllThreeOptions(string text, bool expected) =>
        Assert.That(BashParser.Parse(text).Script.HasStrictMode, Is.EqualTo(expected));

    [Test]
    public void Bash_StrictModeIsNotLimitedToFirstTenLines() =>
        Assert.That(BashParser.Parse(string.Concat(Enumerable.Repeat("echo setup\n", 12)) + "set -euo pipefail").Script.HasStrictMode, Is.True);

    [Test]
    public void PowerShell_FallbackFindsAssignmentAndNestedCommandsButIgnoresHereStrings()
    {
        var parsed = PowerShellParser.Parse("""
            $result = Invoke-Expression $text
            Write-Output (Get-Content 'input')
            $literal = @'
            Invoke-Expression 'not code'
            '@
            Write-Output 'after'
            """);
        Assert.That(parsed.Commands.Select(c => (c.Name, c.Line)), Is.EqualTo(new[]
        {
            ("Invoke-Expression", 1), ("Write-Output", 2), ("Get-Content", 2), ("Write-Output", 6)
        }));
    }

    [Test]
    public void PowerShell_NativeAstFindsSubexpressionsAndDoesNotExecuteSource()
    {
        if (ToolchainProcess.FindExecutable("pwsh") is null && ToolchainProcess.FindExecutable("powershell") is null)
            Assert.Ignore("Native PowerShell is not installed; fallback is tested independently.");
        var root = Path.Combine(Path.GetTempPath(), "cop-powershell-ast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "sample.ps1"), """
                $result = Invoke-Expression 'throw "must never run"'
                Write-Output "$(Get-Content 'missing')"
                $literal = @'
                Invoke-Expression 'not code'
                '@
                Write-Output 'café'
                """);
            using var execution = new ToolchainExecutionScope();
            var result = (Dictionary<string, List<object>>)new PowerShellProvider().Query(new ProviderQuery { RootPath = root })!;
            var commands = result["Commands"].Cast<PowerShellCommandInfo>().ToArray();
            Assert.That(commands.Select(c => (c.Name, c.Line)), Is.EqualTo(new[]
            {
                ("Invoke-Expression", 1), ("Write-Output", 2), ("Get-Content", 2), ("Write-Output", 6)
            }));
            Assert.That(commands[^1].Text, Is.EqualTo("Write-Output 'café'"));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestCase("SELECT * FROM t", true)]
    [TestCase("SELECT t.* FROM t", true)]
    [TestCase("SELECT id, t.* FROM t", true)]
    [TestCase("SELECT DISTINCT t.* FROM t", true)]
    [TestCase("SELECT COUNT(*) FROM t", false)]
    [TestCase("SELECT price * quantity FROM t", false)]
    [TestCase("SELECT '*' FROM t", false)]
    public void Sql_ProjectionWildcardsAreStructural(string text, bool expected) =>
        Assert.That(SqlParser.Parse(text).Statements.Single().SelectsStar, Is.EqualTo(expected));

    [Test]
    public void Sql_CtesDollarBodiesGoAndQuotedSeparatorsPreserveKindsAndLines()
    {
        var result = SqlParser.Parse("""
            -- comment
            WITH c AS (SELECT * FROM t) SELECT c.*, 2 FROM c WHERE id = 1;
            CREATE FUNCTION f() RETURNS void AS $body$
            BEGIN PERFORM 'a;b'; END;
            $body$ LANGUAGE plpgsql;
            GO
            SELECT [semi;colon], "quoted;name", `back;tick` FROM t;
            WITH c AS (SELECT 1) UPDATE t SET value = 2;
            """);
        Assert.That(result.Statements.Select(s => (s.Kind, s.Line, s.SelectsStar, s.HasWhere)), Is.EqualTo(new[]
        {
            ("SELECT", 2, true, true), ("CREATE", 3, false, false),
            ("SELECT", 7, false, false), ("UPDATE", 8, false, false)
        }));
    }

    [Test]
    public void Dockerfile_PreservesInlineHashesEscapeDirectivesAndHeredocBodies()
    {
        var result = DockerfileParser.Parse("""
            # escape=`
            FROM base AS build
            RUN echo hello # remains argument
            RUN echo first `
                second
            RUN <<EOF
            FROM not-an-instruction
            # heredoc content
            EOF
            CMD ["echo", "#literal"]
            """);
        Assert.That(result.Instructions.Select(i => (i.Instruction, i.Line)),
            Is.EqualTo(new[] { ("FROM", 2), ("RUN", 3), ("RUN", 4), ("RUN", 6), ("CMD", 10) }));
        Assert.That(result.Instructions[1].Argument, Is.EqualTo("echo hello # remains argument"));
        Assert.That(result.Instructions[2].Argument, Is.EqualTo("echo first second"));
        Assert.That(result.Instructions[3].Argument, Is.EqualTo("<<EOF\nFROM not-an-instruction\n# heredoc content"));
        Assert.That(result.Instructions[4].IsJsonForm, Is.True);
        Assert.That(result.Instructions[4].Arguments, Is.EqualTo(new[] { "echo", "#literal" }));
        Assert.That(result.Stages, Has.Count.EqualTo(1));
    }

    [Test]
    public void Dockerfile_ResolvesNamedNumericAndInheritedStageReferences()
    {
        var result = DockerfileParser.Parse("""
            FROM base AS builder
            RUN build
            FROM builder AS final
            COPY --from=builder /out /app
            COPY --from=0 /other /app
            COPY --from=external:image /thing /app
            """);
        Assert.That(result.Instructions.Select(i => i.ReferencedStage), Is.EqualTo(new[] { -1, -1, 0, 0, 0, -1 }));
        Assert.That(result.Instructions.Select(i => i.From), Is.EqualTo(new string?[] { null, null, null, "builder", "0", "external:image" }));
    }
}
