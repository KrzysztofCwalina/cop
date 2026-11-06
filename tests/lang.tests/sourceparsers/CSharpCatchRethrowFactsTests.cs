using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public sealed class CSharpCatchRethrowFactsTests
{
    [TestCase("throw;", true, true)]
    [TestCase("Record(ex); throw;", true, true)]
    [TestCase("if (flag) throw; else throw;", true, true)]
    [TestCase("if (flag) { Record(ex); throw; } else { throw; }", true, true)]
    [TestCase("if (flag) Record(ex); throw;", true, true)]
    [TestCase("switch (number) { case 0: throw; default: throw; }", true, true)]
    [TestCase("switch (number) { case 0: break; default: break; } throw;", true, true)]
    [TestCase("try { Record(ex); } finally { Record(ex); } throw;", true, true)]
    [TestCase("using (var resource = new Disposable()) { throw; }", true, true)]
    [TestCase("using var resource = new Disposable(); throw;", true, true)]
    [TestCase("using (var resource = new Disposable()) { if (flag) throw; }", false, true)]
    [TestCase("lock (this) { throw; }", true, true)]
    [TestCase("lock (this) { if (flag) throw; }", false, true)]
    [TestCase("try { throw; } finally { FailCleanup(); }", true, true)]
    [TestCase("try { throw; } finally { if (flag) FailCleanup(); }", true, true)]
    [TestCase("try { throw; } finally { if (flag) throw new System.Exception(); }",
        false, true)]
    [TestCase("if (true) throw;", true, true)]
    [TestCase("if (false) return; throw;", true, true)]
    [TestCase("if (flag) throw;", false, true)]
    [TestCase("if (flag) return; throw;", false, true)]
    [TestCase("if (flag) throw; else return;", false, true)]
    [TestCase("if (flag) throw; else throw new System.Exception();", false, true)]
    [TestCase("throw new System.Exception();", false, false)]
    [TestCase("throw ex;", false, false)]
    [TestCase("System.Action callback = () => { throw new System.Exception(); };",
        false, false)]
    [TestCase("void Callback() { throw new System.Exception(); } Callback();", false, false)]
    [TestCase("System.Action callback = () => { try { } catch { throw; } };", false, false)]
    [TestCase("void Callback() { try { } catch { throw; } } Callback();", false, false)]
    [TestCase("try { Record(ex); } catch { throw; }", false, false)]
    [TestCase("try { throw new System.Exception(); } catch { throw; }", false, false)]
    [TestCase("switch (number) { case 0: throw; }", false, true)]
    [TestCase("try { throw; } finally { throw new System.Exception(); }", false, true)]
    [TestCase("object replacement = value ?? throw new System.Exception(); throw;", false, true)]
    [TestCase("while (flag) { throw; }", false, true)]
    [TestCase("return;", false, false)]
    public void GuaranteedOriginalRethrowIsSeparateFromExistentialRethrows(
        string body, bool always, bool containsRethrow)
    {
        string source = $$"""
            sealed class Disposable : System.IDisposable
            {
                public void Dispose() => throw new System.InvalidOperationException();
            }
            class Example
            {
                bool flag;
                int number;
                object value;
                void Record(object error) { }
                void FailCleanup() => throw new System.InvalidOperationException();
                void Run()
                {
                    try { }
                    catch (System.Exception ex) { {{body}} }
                }
            }
            """;
        SourceFile file = new CSharpSourceParser().Parse("Example.cs", source)!;
        CSharpStatementInfo handler = file.Statements.OfType<CSharpStatementInfo>()
            .First(statement => statement.Kind == "catch");

        Assert.That(handler.IsOriginalExceptionAlwaysRethrown, Is.EqualTo(always), body);
        Assert.That(handler.HasRethrow, Is.EqualTo(containsRethrow),
            "The existing existential Rethrows contract must remain unchanged.");
        Assert.That(CSharpBindings.Build().Accessors["CSharpStatement"]
            ["IsOriginalExceptionAlwaysRethrown"](handler), Is.EqualTo(always));
    }
}
