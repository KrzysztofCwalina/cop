using Cop.Lang.Parser;
using Cop.Lang.Ast;

var source = File.ReadAllText(@"packages\dotnet\csharp-checks\src\csharp-checks.cop");
var module = CopParser.Parse(source, "test");
Console.WriteLine($"Total declarations: {module.Declarations.Count}");
var cmds = module.Declarations.OfType<CommandDecl>().ToList();
Console.WriteLine($"Commands: {cmds.Count}");
foreach (var c in cmds) Console.WriteLine($"  Command: {c.Name} at line {c.Line}");
