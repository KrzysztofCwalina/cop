using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

var source = @"function double(x) => x * 2
command main = double(5)";
var module = CopParser.Parse(source, "test.cop");

Console.WriteLine($"Module has {module.Declarations.Count} declarations:");
foreach (var d in module.Declarations)
{
    Console.WriteLine($"  - {d.GetType().Name}: {d}");
    if (d is FunctionDecl fd)
        Console.WriteLine($"    Name={fd.Name}, Params={fd.Params.Count}, Body={fd.Body?.GetType().Name}");
}

var evaluator = new Evaluator(null, "test.cop");
evaluator.EvalModule(module);
var result = evaluator.RunCommand("main");
Console.WriteLine($"\nResult: type={result.GetType().Name}, display={result.Display()}");
