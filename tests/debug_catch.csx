using Cop.Providers.SourceParsers;

var parser = new CSharpSourceParser();
var source = File.ReadAllText(@"C:\git\FoundryMachine\foundry_server\machine\azure\AzureFoundryMachine.cs");
var result = parser.Parse("AzureFoundryMachine.cs", source);
var catches = result.Statements.Where(s => s.Kind == "catch" && s.TypeName == "Exception").ToList();
foreach (var c in catches)
{
    Console.WriteLine($"Line {c.Line}: catch(Exception) HasRethrow={c.HasRethrow}");
}
