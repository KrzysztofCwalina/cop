using Cop.Lang.Parser;
using Cop.Lang.Ast;

var source = @"import core

export type Request
    Path : string
    Method : string
    Body : bytes?

export enum ContentType = 'application/json' | 'text/plain'

export function isGet(r : Request) : bool = r.Method == 'GET'

export let AllRequests : [Request] = object('http').Requests

export let GetRequests = AllRequests:isGet

command main = print('hello')";
var module = CopParser.Parse(source, "test.cop");
Console.WriteLine($"Declarations: {module.Declarations.Count}");
foreach (var decl in module.Declarations)
{
    Console.WriteLine($"  {decl.GetType().Name}: {decl switch { ImportDecl i => i.ModuleName, TypeDecl t => t.Name, EnumDecl e => e.Name, FunctionDecl f => f.Name, LetDecl l => l.Name, CommandDecl c => c.Name, _ => "?" }}");
}
