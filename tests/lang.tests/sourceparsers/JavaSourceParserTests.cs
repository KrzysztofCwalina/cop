using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaSourceParserTests
{
    private readonly JavaSourceParser _parser = new();

    // ── Package / Namespace ───────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsPackage()
    {
        var source = """
            package com.example.service;
            public class MyService {}
            """;
        var result = _parser.Parse("MyService.java", source)!;
        Assert.That(result.Namespace, Is.EqualTo("com.example.service"));
    }

    [Test]
    public void Parse_NoPackage_NamespaceIsNull()
    {
        var source = "public class Foo {}";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.Namespace, Is.Null);
    }

    // ── Imports ───────────────────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsImports()
    {
        var source = """
            import java.util.List;
            import java.io.IOException;
            import java.util.Map;
            class Foo {}
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.Usings, Does.Contain("java.util.List"));
        Assert.That(result.Usings, Does.Contain("java.io.IOException"));
        Assert.That(result.Usings, Does.Contain("java.util.Map"));
    }

    [Test]
    public void Parse_ExtractsStaticImport()
    {
        var source = """
            import static java.util.Collections.emptyList;
            class Foo {}
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.Usings, Does.Contain("java.util.Collections.emptyList"));
    }

    [Test]
    public void Parse_ExtractsWildcardImport()
    {
        var source = """
            import java.util.*;
            class Foo {}
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.Usings, Does.Contain("java.util.*"));
    }

    [Test]
    public void Parse_NoImports_EmptyUsings()
    {
        var source = "public class Simple {}";
        var result = _parser.Parse("Simple.java", source)!;
        Assert.That(result.Usings, Is.Empty);
    }

    // ── Type declarations ────────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsClass()
    {
        var source = """
            package com.example;
            public class MyClass {}
            """;
        var result = _parser.Parse("MyClass.java", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("MyClass"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Class));
        Assert.That(result.Types[0].Modifiers.HasFlag(Modifier.Public), Is.True);
    }

    [Test]
    public void Parse_ExtractsInterface()
    {
        var source = "public interface IFoo {}";
        var result = _parser.Parse("IFoo.java", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("IFoo"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Interface));
    }

    [Test]
    public void Parse_ExtractsEnum()
    {
        var source = """
            public enum Status {
                ACTIVE, INACTIVE, PENDING
            }
            """;
        var result = _parser.Parse("Status.java", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Status"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Enum));
        Assert.That(result.Types[0].EnumValues, Is.EqualTo(new[] { "ACTIVE", "INACTIVE", "PENDING" }));
    }

    [Test]
    public void Parse_ExtractsRecord_AsStruct_WithIsRecord()
    {
        var source = """
            public record Point(int x, int y) {}
            """;
        var result = _parser.Parse("Point.java", source)!;
        Assert.That(result.Types, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Name, Is.EqualTo("Point"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Struct));
        var javaType = result.Types[0] as JavaTypeDeclaration;
        Assert.That(javaType, Is.Not.Null);
        Assert.That(javaType!.IsRecord, Is.True);
        Assert.That(javaType.IsEnum, Is.False);
    }

    [Test]
    public void Parse_ExtractsBaseTypes()
    {
        var source = """
            public class Dog extends Animal implements Walkable, Trainable {}
            """;
        var result = _parser.Parse("Dog.java", source)!;
        var type = result.Types[0];
        Assert.That(type.BaseTypes, Does.Contain("Animal"));
        Assert.That(type.BaseTypes, Does.Contain("Walkable"));
        Assert.That(type.BaseTypes, Does.Contain("Trainable"));
    }

    [Test]
    public void Parse_ExtractsAnnotationsAsDecorators()
    {
        var source = """
            @SuppressWarnings("unused")
            @Deprecated
            public class OldClass {}
            """;
        var result = _parser.Parse("OldClass.java", source)!;
        Assert.That(result.Types[0].Decorators, Does.Contain("SuppressWarnings"));
        Assert.That(result.Types[0].Decorators, Does.Contain("Deprecated"));
    }

    [Test]
    public void Parse_ExtractsNestedClass()
    {
        var source = """
            public class Outer {
                public static class Inner {}
            }
            """;
        var result = _parser.Parse("Outer.java", source)!;
        Assert.That(result.Types[0].NestedTypes, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].NestedTypes[0].Name, Is.EqualTo("Inner"));
        Assert.That(result.Types[0].NestedTypes[0].Kind, Is.EqualTo(TypeKind.Class));
    }

    [Test]
    public void Parse_SealedClass_IsSealed()
    {
        var source = "public sealed class Shape permits Circle, Rectangle {}";
        var result = _parser.Parse("Shape.java", source)!;
        var javaType = result.Types[0] as JavaTypeDeclaration;
        Assert.That(javaType, Is.Not.Null);
        Assert.That(javaType!.IsSealed, Is.True);
    }

    [Test]
    public void Parse_FinalClass_IsFinal()
    {
        var source = "public final class ImmutablePoint {}";
        var result = _parser.Parse("ImmutablePoint.java", source)!;
        var javaType = result.Types[0] as JavaTypeDeclaration;
        Assert.That(javaType, Is.Not.Null);
        Assert.That(javaType!.IsFinal, Is.True);
    }

    [Test]
    public void Parse_GenericClass_IsGeneric()
    {
        var source = "public class Box<T> {}";
        var result = _parser.Parse("Box.java", source)!;
        var javaType = result.Types[0] as JavaTypeDeclaration;
        Assert.That(javaType, Is.Not.Null);
        Assert.That(javaType!.IsGeneric, Is.True);
    }

    [Test]
    public void Parse_DocComment_HasDocComment()
    {
        var source = """
            /** A documented class. */
            public class Documented {}
            """;
        var result = _parser.Parse("Documented.java", source)!;
        Assert.That(result.Types[0].HasDocComment, Is.True);
    }

    // ── Methods / Constructors ────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsMethod()
    {
        var source = """
            public class Foo {
                public String getName() { return name; }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var methods = result.Types[0].Methods;
        Assert.That(methods, Has.Count.EqualTo(1));
        Assert.That(methods[0].Name, Is.EqualTo("getName"));
        Assert.That(methods[0].ReturnType!.Name, Is.EqualTo("String"));
        Assert.That(methods[0].Modifiers.HasFlag(Modifier.Public), Is.True);
    }

    [Test]
    public void Parse_ExtractsConstructor()
    {
        var source = """
            public class Person {
                public Person(String name) { this.name = name; }
                public String getName() { return name; }
            }
            """;
        var result = _parser.Parse("Person.java", source)!;
        Assert.That(result.Types[0].Constructors, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Constructors[0].Name, Is.EqualTo("<init>"));
        Assert.That(result.Types[0].Methods, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_ExtractsMethodParameters()
    {
        var source = """
            public class Calc {
                public int add(int a, int b) { return a + b; }
            }
            """;
        var result = _parser.Parse("Calc.java", source)!;
        var method = result.Types[0].Methods[0];
        Assert.That(method.Parameters, Has.Count.EqualTo(2));
        Assert.That(method.Parameters[0].Name, Is.EqualTo("a"));
        Assert.That(method.Parameters[1].Name, Is.EqualTo("b"));
    }

    [Test]
    public void Parse_ExtractsSynchronizedMethod()
    {
        var source = """
            public class Counter {
                public synchronized void increment() { count++; }
            }
            """;
        var result = _parser.Parse("Counter.java", source)!;
        var method = result.Types[0].Methods[0] as JavaMethodDeclaration;
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.IsSynchronized, Is.True);
    }

    [Test]
    public void Parse_ExtractsAbstractMethod()
    {
        var source = """
            public abstract class Shape {
                public abstract double area();
            }
            """;
        var result = _parser.Parse("Shape.java", source)!;
        var method = result.Types[0].Methods[0];
        Assert.That(method.Modifiers.HasFlag(Modifier.Abstract), Is.True);
    }

    // ── Statement extraction ──────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsMethodCall()
    {
        var source = """
            public class Foo {
                void bar() { System.out.println("hello"); }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var calls = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "println").ToList();
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].IsInMethod, Is.True);
    }

    [Test]
    public void Parse_ExtractsChainedCallWithTypeName()
    {
        var source = """
            public class Foo {
                void bar() { logger.warn("test"); }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var calls = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "warn").ToList();
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].TypeName, Is.EqualTo("logger"));
        Assert.That(calls[0].IsInMethod, Is.True);
    }

    [Test]
    public void Parse_ExtractsConstructorCall_NewKeyword()
    {
        var source = """
            public class Foo {
                void bar() { new ArrayList(); }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var calls = result.Statements.Where(s => s.Kind == "call" && s.MemberName == "<init>").ToList();
        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].TypeName, Is.EqualTo("ArrayList"));
    }

    // ── Exception handling ────────────────────────────────────────────────────────

    [Test]
    public void Parse_ExtractsThrowStatement_WithTypeName()
    {
        var source = """
            public class Foo {
                void bar() { throw new IOException("msg"); }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].TypeName, Is.EqualTo("IOException"));
        Assert.That(throws[0].IsInMethod, Is.True);
    }

    [Test]
    public void Parse_ThrowVariable_TypeNameIsNull()
    {
        var source = """
            public class Foo {
                void bar(Exception e) { throw e; }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var throws = result.Statements.Where(s => s.Kind == "throw").ToList();
        Assert.That(throws, Has.Count.EqualTo(1));
        Assert.That(throws[0].TypeName, Is.Null);
    }

    [Test]
    public void Parse_ExtractsCatch_TypeName()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (IOException e) { handle(e); }
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].TypeName, Is.EqualTo("IOException"));
    }

    [Test]
    public void Parse_CatchStatement_IsErrorHandler()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (IOException e) {}
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches, Has.Count.EqualTo(1));
        Assert.That(catches[0].IsErrorHandler, Is.True);
    }

    [Test]
    public void Parse_CatchException_IsGenericErrorHandler()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (Exception e) {}
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
    }

    [Test]
    public void Parse_CatchThrowable_IsGenericErrorHandler()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (Throwable t) {}
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].IsGenericErrorHandler, Is.True);
        Assert.That(catches[0].TypeName, Is.EqualTo("Throwable"));
    }

    [Test]
    public void Parse_CatchSpecificException_IsNotGenericErrorHandler()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (IOException e) {}
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].IsGenericErrorHandler, Is.False);
        Assert.That(catches[0].TypeName, Is.EqualTo("IOException"));
    }

    [Test]
    public void Parse_CatchWithRethrow_HasRethrow()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (Exception e) { throw e; }
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].HasRethrow, Is.True);
    }

    [Test]
    public void Parse_CatchWithWrappedThrow_NoRethrow()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (IOException e) { throw new RuntimeException(e); }
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].HasRethrow, Is.False);
    }

    [Test]
    public void Parse_MultiCatch_FirstTypeIsTypeName()
    {
        var source = """
            public class Foo {
                void bar() {
                    try { risky(); }
                    catch (IOException | RuntimeException e) {}
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var catches = result.Statements.Where(s => s.Kind == "catch").ToList();
        Assert.That(catches[0].TypeName, Is.EqualTo("IOException"));
        Assert.That(catches[0].IsErrorHandler, Is.True);
    }

    [Test]
    public void Parse_TryWithResources_IsTryWithResources()
    {
        var source = """
            public class Foo {
                void bar() {
                    try (InputStream is = new FileInputStream("f")) {
                        is.read();
                    }
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var tries = result.Statements.Where(s => s.Kind == "try").ToList();
        Assert.That(tries, Has.Count.EqualTo(1));
        var javaTry = tries[0] as JavaStatementInfo;
        Assert.That(javaTry!.IsTryWithResources, Is.True);
    }

    [Test]
    public void Parse_ForEach_IsEnhancedFor()
    {
        var source = """
            public class Foo {
                void bar(List<String> items) {
                    for (String item : items) { System.out.println(item); }
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var fors = result.Statements.Where(s => s.Kind == "for").ToList();
        Assert.That(fors, Has.Count.EqualTo(1));
        var javaFor = fors[0] as JavaStatementInfo;
        Assert.That(javaFor!.IsEnhancedFor, Is.True);
    }

    [Test]
    public void Parse_SynchronizedBlock_EmitsSynchronized()
    {
        var source = """
            public class Foo {
                void bar() { synchronized(this) { count++; } }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        var syncs = result.Statements.Where(s => s.Kind == "synchronized").ToList();
        Assert.That(syncs, Has.Count.EqualTo(1));
    }

    // ── Comment lines ─────────────────────────────────────────────────────────────

    [Test]
    public void Parse_CommentLines_LineComment()
    {
        var source = "// This is a comment\npublic class Foo {}\n// Another comment\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Contain(3));
        Assert.That(result.CommentLines, Does.Not.Contain(2));
    }

    [Test]
    public void Parse_CommentLines_BlockComment()
    {
        var source = "/* block */\npublic class Foo {}\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.CommentLines, Does.Contain(1));
        Assert.That(result.CommentLines, Does.Not.Contain(2));
    }

    [Test]
    public void Parse_CommentLines_DocComment()
    {
        var source = "/** doc */\npublic class Foo {}\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.CommentLines, Does.Contain(1));
    }

    // ── Strings / comments don't produce false statements ─────────────────────────

    [Test]
    public void Parse_SkipsLineComments_NoCallsInComments()
    {
        var source = """
            public class Foo {
                void bar() {
                    // System.out.println("commented out");
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.Statements.Where(s => s.MemberName == "println"), Is.Empty);
    }

    [Test]
    public void Parse_SkipsStrings_NoCallsInStringContent()
    {
        var source = """
            public class Foo {
                void bar() {
                    String s = "this is not a class Foo declaration";
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        // "class" inside the string must not produce a spurious type
        Assert.That(result.Types[0].NestedTypes, Is.Empty);
    }

    [Test]
    public void Parse_TextBlock_ContentNotParsedAsCode()
    {
        // Build Java source with a text block: the """ delimiters would conflict with C# raw strings,
        // so we construct the source with string concatenation.
        var tq = "\"\"\"";
        var source = "public class Foo {\n    void bar() {\n        String sql = " + tq + "\n"
                   + "            SELECT * FROM class WHERE throw = 1\n"
                   + "            " + tq + ";\n    }\n}\n";
        var result = _parser.Parse("Foo.java", source)!;
        // "throw" inside the text block must not produce a throw statement
        Assert.That(result.Statements.Where(s => s.Kind == "throw"), Is.Empty);
    }

    // ── Generics / modern Java ────────────────────────────────────────────────────

    [Test]
    public void Parse_GenericMethod_IsGeneric()
    {
        var source = """
            public class Util {
                public <T> List<T> wrap(T item) { return List.of(item); }
            }
            """;
        var result = _parser.Parse("Util.java", source)!;
        var method = result.Types[0].Methods[0] as JavaMethodDeclaration;
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.IsGeneric, Is.True);
    }

    [Test]
    public void Parse_ComplexGenericReturnType_ParsesCorrectly()
    {
        var source = """
            public class Repo {
                public Map<String, List<Integer>> getIndex() { return index; }
            }
            """;
        var result = _parser.Parse("Repo.java", source)!;
        Assert.That(result.Types[0].Methods, Has.Count.EqualTo(1));
        Assert.That(result.Types[0].Methods[0].Name, Is.EqualTo("getIndex"));
        // Parser must not crash on complex generics
        Assert.That(result.ParseErrors, Is.Empty);
    }

    // ── No false positives on valid modern Java ───────────────────────────────────

    [Test]
    public void Parse_ValidModernJava_EmptyParseErrors()
    {
        // Java text blocks (""") can't be embedded in C# raw strings directly;
        // build the toString() method body via concatenation.
        var tq = "\"\"\"";
        var source = @"package com.example;

import java.util.List;
import java.util.function.Function;

/** A generic sealed utility class with modern Java features. */
public sealed class GenericUtil<T extends Comparable<T>> permits SpecialUtil {
    private final T value;

    public GenericUtil(T value) {
        this.value = value;
    }

    public <R> R transform(Function<T, R> fn) {
        return fn.apply(value);
    }

    @Override
    public String toString() {
        var text = " + tq + @"
            Value: %s
            " + tq + @".formatted(value);
        return text;
    }

    public void process(List<? extends T> items) {
        for (var item : items) {
            System.out.println(item);
        }
    }

    public void safeRun(Runnable r) {
        try (var scope = new AutoCloseable() { public void close() {} }) {
            r.run();
        } catch (Exception e) {
            throw new RuntimeException(e);
        } finally {
            cleanup();
        }
    }

    private void cleanup() {}
}
";
        var result = _parser.Parse("GenericUtil.java", source)!;
        Assert.That(result.ParseErrors, Is.Empty,
            $"Unexpected parse errors: {string.Join("; ", result.ParseErrors)}");
        Assert.That(result.Namespace, Is.EqualTo("com.example"));
        Assert.That(result.Types[0].Name, Is.EqualTo("GenericUtil"));
        Assert.That(result.Types[0].Kind, Is.EqualTo(TypeKind.Class));
    }

    [Test]
    public void Parse_RecordWithCompactConstructor_EmptyParseErrors()
    {
        var source = """
            public record Range(int min, int max) {
                public Range {
                    if (min > max) throw new IllegalArgumentException("min > max");
                }
                public int size() { return max - min; }
            }
            """;
        var result = _parser.Parse("Range.java", source)!;
        Assert.That(result.ParseErrors, Is.Empty);
        Assert.That(result.Types[0].Name, Is.EqualTo("Range"));
    }

    [Test]
    public void Parse_AnnotationWithArrayValue_EmptyParseErrors()
    {
        var source = """
            @SuppressWarnings({"unchecked", "deprecation"})
            public class Annotated {
                @Override
                public String toString() { return "annotated"; }
            }
            """;
        var result = _parser.Parse("Annotated.java", source)!;
        Assert.That(result.ParseErrors, Is.Empty);
    }

    [Test]
    public void Parse_SwitchExpression_EmptyParseErrors()
    {
        var source = """
            public class Foo {
                int describe(String s) {
                    return switch (s) {
                        case "one" -> 1;
                        case "two" -> 2;
                        default -> {
                            int v = s.length();
                            yield v;
                        }
                    };
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Empty);
    }

    [Test]
    public void Parse_LambdaExpression_EmptyParseErrors()
    {
        var source = """
            import java.util.List;
            public class Foo {
                void bar(List<String> items) {
                    items.forEach(item -> System.out.println(item));
                    items.sort((a, b) -> a.compareTo(b));
                }
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Empty);
    }

    // ── NEGATIVE tests — malformed Java produces ParseErrors ─────────────────────

    [Test]
    public void Parse_UnterminatedStringLiteral_HasParseError()
    {
        var source = """
            public class Foo {
                String s = "unterminated
            }
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unterminated string literal")), Is.True,
            $"Expected 'Unterminated string literal' in errors: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_UnterminatedTextBlock_HasParseError()
    {
        var source = "public class Foo {\n    String s = \"\"\"\n        not closed\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unterminated text block")), Is.True,
            $"Expected 'Unterminated text block' in errors: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_UnterminatedBlockComment_HasParseError()
    {
        var source = """
            public class Foo {
                /* this comment is never closed
            """;
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unterminated block comment")), Is.True,
            $"Expected 'Unterminated block comment' in errors: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_MissingClosingBrace_HasParseError()
    {
        var source = "public class Foo {\n    void bar() {\n        int x = 1;\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unclosed '{'")), Is.True,
            $"Expected 'Unclosed' in errors: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_ExtraClosingBrace_HasParseError()
    {
        var source = "public class Foo {}\n}\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.Any(e => e.Contains("Unexpected '}'")), Is.True,
            $"Expected 'Unexpected' in errors: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_ParseErrors_ContainFilePath()
    {
        var source = "public class Foo {\n    void bar() {\n";
        var result = _parser.Parse("src/Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        Assert.That(result.ParseErrors.All(e => e.StartsWith("src/Foo.java(")), Is.True,
            $"All errors should start with file path: {string.Join("; ", result.ParseErrors)}");
    }

    [Test]
    public void Parse_ParseErrors_MatchFormat()
    {
        // Format: filePath(line,col): error: message
        var source = "public class Foo {\n";
        var result = _parser.Parse("Foo.java", source)!;
        Assert.That(result.ParseErrors, Is.Not.Empty);
        foreach (var err in result.ParseErrors)
        {
            Assert.That(err, Does.Match(@"^Foo\.java\(\d+,\d+\): error: .+"),
                $"Error does not match format: {err}");
        }
    }

    [Test]
    public void Parse_MalformedJava_StillProducesPartialModel()
    {
        // Even with errors the parser should produce a partial model without throwing
        var source = "public class Foo {\n    void bar() {\n        String s = \"unterminated\n";
        Assert.DoesNotThrow(() =>
        {
            var result = _parser.Parse("Foo.java", source)!;
            // Should still have parsed the class
            Assert.That(result.Types, Has.Count.EqualTo(1));
            Assert.That(result.Types[0].Name, Is.EqualTo("Foo"));
        });
    }
}
