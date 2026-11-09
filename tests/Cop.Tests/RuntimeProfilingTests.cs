using System.Reflection;
using System.Text.Json;
using Cop.Lang.Interpreter;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class RuntimeProfilingTests
{
    [Test]
    public void ExecutableDisablesDynamicPgoWithoutDisablingTieredCompilation()
    {
        string configurationPath = Path.ChangeExtension(
            typeof(Engine).Assembly.Location, ".runtimeconfig.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        JsonElement properties = document.RootElement
            .GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.That(properties.TryGetProperty("System.Runtime.TieredPGO", out JsonElement pgo),
            Is.True, "The executable must explicitly avoid dynamic PGO instrumentation.");
        Assert.That(pgo.GetBoolean(), Is.False);
        Assert.That(!properties.TryGetProperty("System.Runtime.TieredCompilation", out JsonElement tiering)
            || tiering.GetBoolean(), Is.True, "Long-lived modes retain ordinary tiered compilation.");
    }

    [Test]
    public void CancellationRestoresTheTraceCallback()
    {
        LanguageBridge bridge = new LanguageBridge();
        bridge.Evaluator.GlobalEnvironment.Define(
            "pending", new CopThunk(() => CopNull.Instance));
        Action<string> trace = _ => { };
        bridge.Evaluator.TraceLog = trace;
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        MethodInfo profile = typeof(Engine).GetMethod(
            "ProfileAllRulesAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The rule profiling entry point was not found.");
        ValueTask operation = (ValueTask)profile.Invoke(
            null, [bridge, 0L, cancellation.Token])!;

        OperationCanceledException? error = Assert.ThrowsAsync<OperationCanceledException>(
            async () => await operation);

        Assert.That(error!.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(bridge.Evaluator.TraceLog, Is.SameAs(trace));
    }
}
