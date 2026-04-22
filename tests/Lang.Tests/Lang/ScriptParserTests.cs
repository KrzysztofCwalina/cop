using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CheckFileParserTests
{
    [Test]
    public void Parse_SinglePredicate()
    {
        var file = ScriptParser.Parse(
            "predicate IsClient(Type) => Type.Name:endsWith('Client')", "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Predicates[0].Name, Is.EqualTo("IsClient"));
        Assert.That(file.Predicates[0].ParameterType, Is.EqualTo("Type"));
    }

    [Test]
    public void Parse_PredicateWithMemberAccessChain()
    {
        var file = ScriptParser.Parse(
            "predicate IsClient(Type) => Type.Name:endsWith('Client')", "test.cop");
        var body = file.Predicates[0].Body;
        // Type.Name:endsWith("Client") → PredicateCallExpr(MemberAccessExpr(Identifier("Type"), "Name"), "endsWith", [Literal("Client")])
        Assert.That(body, Is.TypeOf<PredicateCallExpr>());
        var call = (PredicateCallExpr)body;
        Assert.That(call.Name, Is.EqualTo("endsWith"));
        Assert.That(call.Target, Is.TypeOf<MemberAccessExpr>());
    }

    [Test]
    public void Parse_PrintWithAllParts()
    {
        var source = """
            foreach Statements:csharp:isVar:!Path('**/Tests/**') => PRINT('ERROR: Don\'t use var for {Statement.MemberName}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        var check = file.Commands[0];
        Assert.That(check.Name, Is.EqualTo("Statements.csharp"));
        Assert.That(check.Collection, Is.EqualTo("Statements"));
        Assert.That(check.Filters, Has.Count.EqualTo(3)); // csharp, isVar, !Path(...)
        Assert.That(check.ActionName, Is.EqualTo("PRINT"));
        Assert.That(check.MessageTemplate, Is.EqualTo("ERROR: Don't use var for {Statement.MemberName}"));
    }

    [Test]
    public void Parse_InlineFilterChain()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            foreach Types:csharp:isClient => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Filters, Has.Count.EqualTo(2)); // csharp, isClient
        var filter = file.Commands[0].Filters[0];
        Assert.That(filter, Is.TypeOf<IdentifierExpr>());
        Assert.That(((IdentifierExpr)filter).Name, Is.EqualTo("csharp"));
    }

    [Test]
    public void Parse_MultipleInlineFilters()
    {
        var source = """
            foreach Types:csharp:IsClient:!IsOptions => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var check = file.Commands[0];
        Assert.That(check.Filters, Has.Count.EqualTo(3)); // csharp, IsClient, !IsOptions
        Assert.That(check.Filters[0], Is.TypeOf<IdentifierExpr>());
        Assert.That(check.Filters[1], Is.TypeOf<IdentifierExpr>());
        Assert.That(check.Filters[2], Is.TypeOf<UnaryExpr>());
    }

    [Test]
    public void Parse_PredicateConstraint()
    {
        var source = """
            predicate IsClient(Type:csharp) => Type.Name:endsWith('Client')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Predicates[0].Constraint, Is.EqualTo("csharp"));
        Assert.That(file.Predicates[0].ParameterType, Is.EqualTo("Type"));
    }

    [Test]
    public void Parse_FilterInChain_PrintStatement()
    {
        var source = """
            predicate isAlways(Type) => true
            foreach Types:python:isAlways => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Filters, Has.Count.EqualTo(2)); // python, isAlways
        Assert.That(file.Commands[0].Collection, Is.EqualTo("Types"));
    }

    [Test]
    public void Parse_NoConstraint_ConstraintIsNull()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            foreach Types:isClient => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates[0].Constraint, Is.Null);
    }

    [Test]
    public void Parse_MultiplePredicatesAndPrints()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            predicate isOptions(Type) => Type.Name:endsWith('Options')
            foreach Types:isClient => PRINT('WARNING: msg1')
            foreach Types:isOptions => PRINT('INFO: msg2')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(2));
        Assert.That(file.Commands, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_Error_ReportsLineNumber()
    {
        var source = "PRINT('bad syntax'";  // missing closing paren
        var ex = Assert.Throws<ParseException>(() =>
            ScriptParser.Parse(source, "test.cop"));
        Assert.That(ex!.Message, Does.Contain("test.cop"));
    }

    [Test]
    public void Parse_FunctionCall_Path()
    {
        var source = """
            foreach Lines:python:Matches('\bprint\s*\('):!Path('**/tests/**') => PRINT('WARNING: no print')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var check = file.Commands[0];
        Assert.That(check.Filters, Has.Count.EqualTo(3));
        Assert.That(check.Filters[1], Is.TypeOf<FunctionCallExpr>());
        var fc = (FunctionCallExpr)check.Filters[1];
        Assert.That(fc.Name, Is.EqualTo("Matches"));
    }

    [Test]
    public void Parse_PredicateWithEquality()
    {
        var file = ScriptParser.Parse(
            "predicate IsVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')",
            "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<BinaryExpr>());
        var bin = (BinaryExpr)body;
        Assert.That(bin.Operator, Is.EqualTo("&&"));
    }

    [Test]
    public void Parse_DocComment_AttachesToPrint()
    {
        var source = """
            ## Disallow implicit typing
            foreach Statements:isVar => PRINT('ERROR: no var')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].DocComment, Is.EqualTo("Disallow implicit typing"));
    }

    [Test]
    public void Parse_MultiLineDocComment_MergesLines()
    {
        var source = """
            ## Line one
            ## Line two
            foreach Statements:isVar => PRINT('ERROR: no var')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].DocComment, Is.EqualTo("Line one\nLine two"));
    }

    [Test]
    public void Parse_NoDocComment_IsNull()
    {
        var source = """
            foreach Statements:isVar => PRINT('ERROR: no var')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].DocComment, Is.Null);
    }

    [Test]
    public void Parse_DocComment_OnlyAttachesToNextPrint()
    {
        var source = """
            ## This describes the print
            predicate isVar(Statement) => Statement.Kind == 'declaration'
            foreach Statements:isVar => PRINT('ERROR: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        // Doc comment was before predicate, not PRINT — discarded
        Assert.That(file.Commands[0].DocComment, Is.Null);
    }

    [Test]
    public void Parse_HashComments_Ignored()
    {
        var source = """
            # This is a regular comment
            predicate isVar(Statement) => Statement.Kind == 'declaration'
            foreach Statements:isVar => PRINT('ERROR: no var')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Commands, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_DerivedCollection_ParsedAsPredicate()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            predicate Clients(Types) => isClient
            foreach Clients:csharp:isClient => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(2));
        Assert.That(file.Predicates[1].Name, Is.EqualTo("Clients"));
        Assert.That(file.Predicates[1].ParameterType, Is.EqualTo("Types"));
        Assert.That(file.Commands[0].Collection, Is.EqualTo("Clients"));
    }

    [Test]
    public void Parse_PrintWithOnlyFilters()
    {
        var source = """
            predicate notSealed(Type) => !Type.Sealed
            foreach Types:csharp:notSealed => PRINT('WARNING: {Type.Name} should be sealed')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var check = file.Commands[0];
        Assert.That(check.Filters, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_PrintWithNoFilters_UsesCollectionAsName()
    {
        var source = """
            foreach ClientsMissingOptions => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var check = file.Commands[0];
        Assert.That(check.Name, Is.EqualTo("ClientsMissingOptions"));
        Assert.That(check.Filters, Has.Count.EqualTo(0));
    }

    [Test]
    public void Parse_PrintAction_MessageIsLiteral()
    {
        var source = """foreach Types:isVar => PRINT('ERROR: no var')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("PRINT"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("ERROR: no var"));
    }

    [Test]
    public void Parse_PrintAction_NoPrefix()
    {
        var source = """foreach Types:isVar => PRINT('no severity prefix')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("PRINT"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("no severity prefix"));
    }

    [Test]
    public void Parse_ErrorAction()
    {
        var source = """foreach Types:isVar => ERROR('Do not use var')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("ERROR"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("Do not use var"));
    }

    [Test]
    public void Parse_WarningAction()
    {
        var source = """foreach Types:isVar => WARNING('Should be sealed')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("WARNING"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("Should be sealed"));
    }

    [Test]
    public void Parse_InfoAction()
    {
        var source = """foreach Types:isVar => INFO('Found item')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("INFO"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("Found item"));
    }

    [Test]
    public void Parse_ErrorAction_InCommand()
    {
        var source = """command NO-VAR = foreach Types:isVar => ERROR('Do not use var')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("ERROR"));
        Assert.That(file.Commands[0].Name, Is.EqualTo("NO-VAR"));
        Assert.That(file.Commands[0].IsCommand, Is.True);
    }

    [Test]
    public void Parse_WarningAction_InLetCommand()
    {
        var source = """let my-check = foreach Types:isClient => WARNING('msg')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("WARNING"));
        Assert.That(file.Commands[0].Name, Is.EqualTo("my-check"));
    }

    [Test]
    public void Parse_ErrorAction_CollectionFirst()
    {
        var source = """foreach Types:isVar => ERROR('Do not use var')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("ERROR"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("Do not use var"));
        Assert.That(file.Commands[0].Collection, Is.EqualTo("Types"));
    }

    [Test]
    public void Parse_ErrorAction_BareNoCollection()
    {
        var source = """ERROR('Something is wrong')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].ActionName, Is.EqualTo("ERROR"));
        Assert.That(file.Commands[0].Collection, Is.Null);
    }

    [Test]
    public void Parse_RuleId_DerivedFromCollectionAndFirstFilter()
    {
        var source = """foreach Clients:csharp:missingOptions:notSealed => PRINT('WARNING: msg')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].Name, Is.EqualTo("Clients.csharp"));
    }

    [Test]
    public void Parse_RuleId_NegatedFilterUsesInnerName()
    {
        var source = """foreach Types:!isSealed => PRINT('WARNING: msg')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].Name, Is.EqualTo("Types.isSealed"));
    }

    [Test]
    public void Parse_BarePrint_NoCollection()
    {
        var source = """PRINT('Hello World')""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        var check = file.Commands[0];
        Assert.That(check.Collection, Is.Null);
        Assert.That(check.Filters, Has.Count.EqualTo(0));
        Assert.That(check.MessageTemplate, Is.EqualTo("Hello World"));
        Assert.That(check.Name, Does.StartWith("action_"));
    }

    // --- Type System Tests ---

    [Test]
    public void Parse_TypeDefinition_RecordType()
    {
        var source = """type Foo = { Name : string, Age : int }""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.TypeDefinitions, Has.Count.EqualTo(1));
        var td = file.TypeDefinitions[0];
        Assert.That(td.Name, Is.EqualTo("Foo"));
        Assert.That(td.BaseType, Is.Null);
        Assert.That(td.Properties, Has.Count.EqualTo(2));
        Assert.That(td.Properties[0].Name, Is.EqualTo("Name"));
        Assert.That(td.Properties[0].TypeName, Is.EqualTo("string"));
        Assert.That(td.Properties[1].Name, Is.EqualTo("Age"));
        Assert.That(td.Properties[1].TypeName, Is.EqualTo("int"));
    }

    [Test]
    public void Parse_TypeDefinition_EmptyRecord()
    {
        var source = """type Object = {}""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.TypeDefinitions, Has.Count.EqualTo(1));
        Assert.That(file.TypeDefinitions[0].Name, Is.EqualTo("Object"));
        Assert.That(file.TypeDefinitions[0].Properties, Has.Count.EqualTo(0));
    }

    [Test]
    public void Parse_TypeDefinition_SubtypeWithIntersection()
    {
        var source = """type Constructor = Method & {}""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.TypeDefinitions, Has.Count.EqualTo(1));
        var td = file.TypeDefinitions[0];
        Assert.That(td.Name, Is.EqualTo("Constructor"));
        Assert.That(td.BaseType, Is.EqualTo("Method"));
        Assert.That(td.Properties, Has.Count.EqualTo(0));
    }

    [Test]
    public void Parse_TypeDefinition_OptionalProperty()
    {
        var source = """type Foo = { Name : string?, Value : int }""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.TypeDefinitions[0].Properties[0].IsOptional, Is.True);
        Assert.That(file.TypeDefinitions[0].Properties[1].IsOptional, Is.False);
    }

    [Test]
    public void Parse_TypeDefinition_CollectionProperty()
    {
        var source = """type Foo = { Items : [string], Count : int }""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.TypeDefinitions[0].Properties[0].IsCollection, Is.True);
        Assert.That(file.TypeDefinitions[0].Properties[0].TypeName, Is.EqualTo("string"));
        Assert.That(file.TypeDefinitions[0].Properties[1].IsCollection, Is.False);
    }

    [Test]
    public void Parse_CollectionDeclaration()
    {
        var source = """collection Types : [Type]""";
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.CollectionDeclarations, Has.Count.EqualTo(1));
        Assert.That(file.CollectionDeclarations[0].Name, Is.EqualTo("Types"));
        Assert.That(file.CollectionDeclarations[0].ItemType, Is.EqualTo("Type"));
    }

    [Test]
    public void Parse_ImportStatement()
    {
        var source = """
            import code
            predicate isClient(Type) => Type.Name:endsWith('Client')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Imports, Has.Count.EqualTo(1));
        Assert.That(file.Imports[0], Is.EqualTo("code"));
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_ImportAfterPredicate_Throws()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            import code
            """;
        Assert.Throws<ParseException>(() =>
            ScriptParser.Parse(source, "test.cop"));
    }

    [Test]
    public void Parse_ListLiteral()
    {
        var source = """predicate test(Type) => Type.Name == [1, 2, 3]""";
        var file = ScriptParser.Parse(source, "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.InstanceOf<BinaryExpr>());
        var right = ((BinaryExpr)body).Right;
        Assert.That(right, Is.InstanceOf<ListLiteralExpr>());
        Assert.That(((ListLiteralExpr)right).Elements, Has.Count.EqualTo(3));
    }

    [Test]
    public void Parse_EmptyListLiteral()
    {
        var source = """predicate test(Type) => Type.Name == []""";
        var file = ScriptParser.Parse(source, "test.cop");
        var body = file.Predicates[0].Body;
        var right = ((BinaryExpr)body).Right;
        Assert.That(right, Is.InstanceOf<ListLiteralExpr>());
        Assert.That(((ListLiteralExpr)right).Elements, Has.Count.EqualTo(0));
    }

    [Test]
    public void Parse_CodeAlanFile()
    {
        var source = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "..", "packages", "code", "src", "code.cop"));
        var file = ScriptParser.Parse(source, "code.cop");
        Assert.That(file.TypeDefinitions.Count, Is.GreaterThanOrEqualTo(8));
        Assert.That(file.LetDeclarations.Count, Is.GreaterThanOrEqualTo(4));

        // Object is a core primitive, not defined in code.cop
        Assert.That(file.TypeDefinitions.Any(t => t.Name == "Object"), Is.False);

        // Verify Constructor subtype
        var ctor = file.TypeDefinitions.First(t => t.Name == "Constructor");
        Assert.That(ctor.BaseType, Is.EqualTo("Method"));
    }

    [Test]
    public void Parse_LetCommand_BasicNamedPrint()
    {
        var source = """
            let list-types = foreach Types => PRINT('{Type.Name}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Name, Is.EqualTo("list-types"));
        Assert.That(file.Commands[0].IsCommand, Is.True);
        Assert.That(file.Commands[0].Collection, Is.EqualTo("Types"));
        Assert.That(file.Commands[0].MessageTemplate, Is.EqualTo("{Type.Name}"));
    }

    [Test]
    public void Parse_LetCommand_WithFilters()
    {
        var source = """
            predicate isClient(Type) => Type.Name:endsWith('Client')
            let check-clients = foreach Types:isClient => PRINT('WARNING: {Type.Name}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Name, Is.EqualTo("check-clients"));
        Assert.That(file.Commands[0].IsCommand, Is.True);
        Assert.That(file.Commands[0].Filters, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_LetCommand_CoexistsWithUnnamedPrint()
    {
        var source = """
            let list-types = foreach Types => PRINT('{Type.Name}')
            foreach Types:csharp => PRINT('WARNING: check')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(2));
        Assert.That(file.Commands[0].IsCommand, Is.True);
        Assert.That(file.Commands[0].Name, Is.EqualTo("list-types"));
        Assert.That(file.Commands[1].IsCommand, Is.False);
    }

    [Test]
    public void Parse_LetCommand_DocComment()
    {
        var source = """
            ## Lists all types in the codebase
            let list-types = foreach Types => PRINT('{Type.Name}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands[0].DocComment, Is.EqualTo("Lists all types in the codebase"));
        Assert.That(file.Commands[0].IsCommand, Is.True);
    }

    [Test]
    public void Parse_RegularLet_NotCommand()
    {
        var source = """
            let Clients = Types:isClient
            foreach Clients => PRINT('WARNING: msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.LetDeclarations, Has.Count.EqualTo(1));
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].IsCommand, Is.False);
    }

    [Test]
    public void Parse_LetCommand_BarePrint()
    {
        var source = """
            let hello = PRINT('Hello, world!')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Name, Is.EqualTo("hello"));
        Assert.That(file.Commands[0].IsCommand, Is.True);
        Assert.That(file.Commands[0].Collection, Is.Null);
    }

    [Test]
    public void Parse_FunctionDefinition_Basic()
    {
        var source = """
            function error(Statement, message: string) => Violation {
                Severity = 'error',
                Message = message
            }
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Functions, Has.Count.EqualTo(1));
        var func = file.Functions[0];
        Assert.That(func.Name, Is.EqualTo("error"));
        Assert.That(func.InputType, Is.EqualTo("Statement"));
        Assert.That(func.ReturnType, Is.EqualTo("Violation"));
        Assert.That(func.Parameters, Has.Count.EqualTo(1));
        Assert.That(func.Parameters[0].Name, Is.EqualTo("message"));
        Assert.That(func.Parameters[0].TypeName, Is.EqualTo("string"));
        Assert.That(func.FieldMappings, Has.Count.EqualTo(2));
        Assert.That(func.IsExported, Is.False);
    }

    [Test]
    public void Parse_FunctionDefinition_Exported()
    {
        var source = """
            export function warning(Statement, message: string) => Violation {
                Severity = 'warning',
                Message = message
            }
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Functions, Has.Count.EqualTo(1));
        Assert.That(file.Functions[0].IsExported, Is.True);
        Assert.That(file.Functions[0].Name, Is.EqualTo("warning"));
    }

    [Test]
    public void Parse_FunctionDefinition_WithMemberAccessInBody()
    {
        var source = """
            function error(Statement, message: string) => Violation {
                Severity = 'error',
                Message = message,
                File = Statement.File.Path,
                Line = Statement.Line
            }
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var func = file.Functions[0];
        Assert.That(func.FieldMappings, Has.Count.EqualTo(4));
        Assert.That(func.FieldMappings.ContainsKey("File"), Is.True);
        Assert.That(func.FieldMappings.ContainsKey("Line"), Is.True);
        // File mapping should be a member access chain: Statement.File.Path
        Assert.That(func.FieldMappings["File"], Is.TypeOf<MemberAccessExpr>());
    }

    [Test]
    public void Parse_FunctionInFilterChain()
    {
        var source = """
            function error(Statement, message: string) => Violation {
                Severity = 'error',
                Message = message
            }
            predicate isVar(Statement) => Statement.MemberName == 'var'
            foreach Statements:isVar:error('Don\'t use var') => PRINT('{Violation.Message}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Functions, Has.Count.EqualTo(1));
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        // The check should have two filters: isVar (predicate) and error("Don't use var") (function)
        Assert.That(file.Commands[0].Filters, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_SetSubtraction_DecomposesIntoExclusions()
    {
        var source = """
            let Accepted = ['foo', 'bar']
            foreach Statements:isVar:toError('msg') - Accepted => PRINT('{Violation.Message}')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        var cmd = file.Commands[0];
        Assert.That(cmd.Collection, Is.EqualTo("Statements"));
        Assert.That(cmd.Filters, Has.Count.EqualTo(2)); // isVar, toError
        Assert.That(cmd.Exclusions, Is.Not.Null);
        Assert.That(cmd.Exclusions, Is.TypeOf<IdentifierExpr>());
        Assert.That(((IdentifierExpr)cmd.Exclusions!).Name, Is.EqualTo("Accepted"));
    }

    [Test]
    public void Parse_SetSubtraction_InLetDeclaration()
    {
        var source = """
            let Accepted = ['foo']
            let Filtered = Statements:isVar - Accepted
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecl = file.LetDeclarations.FirstOrDefault(l => l.Name == "Filtered");
        Assert.That(letDecl, Is.Not.Null);
        Assert.That(letDecl!.BaseCollection, Is.EqualTo("Statements"));
        Assert.That(letDecl.Filters, Has.Count.EqualTo(1)); // isVar
        Assert.That(letDecl.Exclusions, Is.Not.Null);
        Assert.That(letDecl.Exclusions, Is.TypeOf<IdentifierExpr>());
    }

    [Test]
    public void Parse_HyphenatedIdentifier_NotConfusedWithMinus()
    {
        var source = """
            command NO-EMPTY-FOLDERS = foreach Folders:isEmpty => PRINT('msg')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Name, Is.EqualTo("NO-EMPTY-FOLDERS"));
    }

    // ── Object Literal Tests ──

    [Test]
    public void Parse_ObjectLiteral_InLetExpression()
    {
        var source = """
            let config = { Name = 'my-app', Port = 8080 }
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.LetDeclarations, Has.Count.EqualTo(1));
        Assert.That(file.LetDeclarations[0].Name, Is.EqualTo("config"));
    }

    [Test]
    public void Parse_ObjectLiteral_WithBooleanFields()
    {
        var source = """
            let config = { Debug = true, Verbose = false }
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.LetDeclarations, Has.Count.EqualTo(1));
        var letExpr = file.LetDeclarations[0].ValueExpression;
        Assert.That(letExpr, Is.TypeOf<ObjectLiteralExpr>());
        var obj = (ObjectLiteralExpr)letExpr!;
        Assert.That(obj.Fields, Has.Count.EqualTo(2));
        Assert.That(obj.Fields.ContainsKey("Debug"), Is.True);
        Assert.That(obj.Fields.ContainsKey("Verbose"), Is.True);
    }

    [Test]
    public void TemplateParser_StyledLiteral()
    {
        var segments = TemplateParser.Parse("{error:@red}");
        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0], Is.TypeOf<AnnotatedLiteralSegment>());
        var ann = (AnnotatedLiteralSegment)segments[0];
        Assert.That(ann.Text, Is.EqualTo("error:"));
        Assert.That(ann.Annotation, Is.EqualTo("red"));
    }

    [Test]
    public void TemplateParser_StyledExpression()
    {
        var segments = TemplateParser.Parse("{Type.Name@bold}");
        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0], Is.TypeOf<ExpressionSegment>());
        var expr = (ExpressionSegment)segments[0];
        Assert.That(expr.PropertyPath, Is.EqualTo(new[] { "Type", "Name" }));
        Assert.That(expr.Annotation, Is.EqualTo("bold"));
    }

    [Test]
    public void TemplateParser_MixedStyledLiterals()
    {
        var segments = TemplateParser.Parse("{Hello @red} {World!@green}!");
        Assert.That(segments, Has.Count.EqualTo(4));
        Assert.That(segments[0], Is.TypeOf<AnnotatedLiteralSegment>());
        Assert.That(((AnnotatedLiteralSegment)segments[0]).Text, Is.EqualTo("Hello "));
        Assert.That(((AnnotatedLiteralSegment)segments[0]).Annotation, Is.EqualTo("red"));
        Assert.That(segments[1], Is.TypeOf<LiteralSegment>());
        Assert.That(((LiteralSegment)segments[1]).Text, Is.EqualTo(" "));
        Assert.That(segments[2], Is.TypeOf<AnnotatedLiteralSegment>());
        Assert.That(((AnnotatedLiteralSegment)segments[2]).Text, Is.EqualTo("World!"));
        Assert.That(((AnnotatedLiteralSegment)segments[2]).Annotation, Is.EqualTo("green"));
        Assert.That(segments[3], Is.TypeOf<LiteralSegment>());
        Assert.That(((LiteralSegment)segments[3]).Text, Is.EqualTo("!"));
    }

    [Test]
    public void TemplateParser_DoubleBrace_LiteralEscape()
    {
        var segments = TemplateParser.Parse("use {{braces}}");
        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0], Is.TypeOf<LiteralSegment>());
        Assert.That(((LiteralSegment)segments[0]).Text, Is.EqualTo("use {braces}"));
    }

    [Test]
    public void TemplateParser_PlainExpression()
    {
        var segments = TemplateParser.Parse("{Type.Name}");
        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0], Is.TypeOf<ExpressionSegment>());
        var expr = (ExpressionSegment)segments[0];
        Assert.That(expr.PropertyPath, Is.EqualTo(new[] { "Type", "Name" }));
        Assert.That(expr.Annotation, Is.Null);
    }

    [Test]
    public void TemplateParser_MultiStyle_KebabCase()
    {
        var segments = TemplateParser.Parse("{error:@red-bold}");
        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0], Is.TypeOf<AnnotatedLiteralSegment>());
        var ann = (AnnotatedLiteralSegment)segments[0];
        Assert.That(ann.Text, Is.EqualTo("error:"));
        Assert.That(ann.Annotation, Is.EqualTo("red-bold"));
    }

    // ── RUN + Parameterized Commands Tests ──

    [Test]
    public void Parse_RunInvocation_Simple()
    {
        var file = ScriptParser.Parse("RUN CHECK(violations)", "test.cop");
        Assert.That(file.RunInvocations, Has.Count.EqualTo(1));
        Assert.That(file.RunInvocations![0].CommandName, Is.EqualTo("CHECK"));
        Assert.That(file.RunInvocations[0].Arguments, Has.Count.EqualTo(1));
        Assert.That(file.RunInvocations[0].Arguments[0], Is.TypeOf<IdentifierExpr>());
        Assert.That(((IdentifierExpr)file.RunInvocations[0].Arguments[0]).Name, Is.EqualTo("violations"));
    }

    [Test]
    public void Parse_RunInvocation_WithSubtraction()
    {
        var file = ScriptParser.Parse("RUN CHECK(violations - Accepted)", "test.cop");
        Assert.That(file.RunInvocations, Has.Count.EqualTo(1));
        var arg = file.RunInvocations![0].Arguments[0];
        Assert.That(arg, Is.TypeOf<BinaryExpr>());
        var bin = (BinaryExpr)arg;
        Assert.That(bin.Operator, Is.EqualTo("-"));
    }

    [Test]
    public void Parse_ParameterizedCommand()
    {
        var file = ScriptParser.Parse("command CHECK(violations) = PRINT('{Violation.Message}', violations)", "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Parameters, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].Parameters![0], Is.EqualTo("violations"));
    }

    [Test]
    public void Parse_ExportParameterizedCommand()
    {
        var file = ScriptParser.Parse("export command CHECK(violations) = PRINT('{Violation.Message}', violations)", "test.cop");
        Assert.That(file.Commands, Has.Count.EqualTo(1));
        Assert.That(file.Commands[0].IsExported, Is.True);
        Assert.That(file.Commands[0].Parameters, Has.Count.EqualTo(1));
    }
}
