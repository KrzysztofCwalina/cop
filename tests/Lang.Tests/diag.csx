using Cop.Lang;
using Cop.Tests.Lang;
using NUnit.Framework;

// Quick diagnostic — what does RunInlineCopChecks see?
var inlineCop = @"
import csharp-library-checks
import code-analysis

# Baseline
predicate baselineApi(Api) => isPublicApi && Api.File.Path:matches('[/\\]api[/\\]')
predicate sourceApi(Api) => isPublicApi && !Api.File.Path:matches('[/\\]api[/\\]')

let baselineSignatures = Code.Api:isCSharp:baselineApi.Select(item.Signature)
let currentSignatures = Code.Api:isCSharp:sourceApi.Select(item.Signature)

predicate removedApi(Api) => baselineApi && !Api.Signature:in(currentSignatures)
predicate addedApi(Api) => sourceApi && !Api.Signature:in(baselineSignatures)

export let api-removed = Code.Api:removedApi:toError('API REMOVED (breaking): {item.Signature}')
export let api-added = Code.Api:addedApi:toInfo('API ADDED: {item.Signature}')
export let api-compat = api-removed + api-added
";

var parsed = ScriptParser.Parse(inlineCop, "test.cop");
Console.WriteLine($"LetDeclarations: {parsed.LetDeclarations.Count}");
foreach (var l in parsed.LetDeclarations)
    Console.WriteLine($"  {l.Name}: IsExported={l.IsExported}, IsValueBinding={l.IsValueBinding}, IsCollectionUnion={l.IsCollectionUnion}, Base={l.BaseCollection}, Filters={l.Filters.Count}");
Console.WriteLine($"RunInvocations: {parsed.RunInvocations?.Count ?? 0}");
Console.WriteLine($"Predicates: {parsed.Predicates.Count}");
