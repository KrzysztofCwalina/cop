using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>
/// Provider for the codeql-export package. Exposes functions for building
/// CodeQL .ql query files programmatically.
/// Import with: import codeql-export
/// </summary>
public class CodeqlExportProvider : ObjectProvider, ICapabilityProvider
{
    public override ObjectFormat SupportedFormats => ObjectFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => new ProviderSchema().ToJson();

    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query) => new();

    public void RegisterCapabilities(TypeRegistry registry, string rootPath)
    {
        registry.RegisterProviderFunction("codeql-export", "qlQuery", args =>
        {
            ValidateArgCount(args, 5, "qlQuery(name, description, from, where, select)");
            var name = ArgStr(args, 0, "name");
            var description = ArgStr(args, 1, "description");
            var from = ArgStr(args, 2, "from");
            var where = ArgStr(args, 3, "where");
            var select = ArgStr(args, 4, "select");
            return Task.FromResult<object?>(BuildQuery(name, description, from, where, select));
        });

        registry.RegisterProviderFunction("codeql-export", "qlQueryAll", args =>
        {
            ValidateArgCount(args, 4, "qlQueryAll(name, description, from, select)");
            var name = ArgStr(args, 0, "name");
            var description = ArgStr(args, 1, "description");
            var from = ArgStr(args, 2, "from");
            var select = ArgStr(args, 3, "select");
            return Task.FromResult<object?>(BuildQueryAll(name, description, from, select));
        });

        registry.RegisterProviderFunction("codeql-export", "qlFrom", args =>
        {
            ValidateArgCount(args, 2, "qlFrom(variable, qlType)");
            var variable = ArgStr(args, 0, "variable");
            var qlType = ArgStr(args, 1, "qlType");
            return Task.FromResult<object?>($"{qlType} {variable}");
        });

        registry.RegisterProviderFunction("codeql-export", "qlFrom2", args =>
        {
            ValidateArgCount(args, 4, "qlFrom2(var1, type1, var2, type2)");
            var var1 = ArgStr(args, 0, "var1");
            var type1 = ArgStr(args, 1, "type1");
            var var2 = ArgStr(args, 2, "var2");
            var type2 = ArgStr(args, 3, "type2");
            return Task.FromResult<object?>($"{type1} {var1}, {type2} {var2}");
        });

        registry.RegisterProviderFunction("codeql-export", "qlAnd", args =>
        {
            ValidateArgCount(args, 2, "qlAnd(left, right)");
            var left = ArgStr(args, 0, "left");
            var right = ArgStr(args, 1, "right");
            return Task.FromResult<object?>($"{left} and\n  {right}");
        });

        registry.RegisterProviderFunction("codeql-export", "qlOr", args =>
        {
            ValidateArgCount(args, 2, "qlOr(left, right)");
            var left = ArgStr(args, 0, "left");
            var right = ArgStr(args, 1, "right");
            return Task.FromResult<object?>($"({left} or {right})");
        });

        registry.RegisterProviderFunction("codeql-export", "qlNot", args =>
        {
            ValidateArgCount(args, 1, "qlNot(condition)");
            var condition = ArgStr(args, 0, "condition");
            return Task.FromResult<object?>($"not {condition}");
        });

        registry.RegisterProviderFunction("codeql-export", "qlNameMatches", args =>
        {
            ValidateArgCount(args, 2, "qlNameMatches(variable, pattern)");
            var variable = ArgStr(args, 0, "variable");
            var pattern = ArgStr(args, 1, "pattern");
            return Task.FromResult<object?>($"{variable}.getName().regexpMatch(\"{Escape(pattern)}\")");
        });

        registry.RegisterProviderFunction("codeql-export", "qlNameStartsWith", args =>
        {
            ValidateArgCount(args, 2, "qlNameStartsWith(variable, prefix)");
            var variable = ArgStr(args, 0, "variable");
            var prefix = ArgStr(args, 1, "prefix");
            return Task.FromResult<object?>($"{variable}.getName().indexOf(\"{Escape(prefix)}\") = 0");
        });

        registry.RegisterProviderFunction("codeql-export", "qlNameContains", args =>
        {
            ValidateArgCount(args, 2, "qlNameContains(variable, substring)");
            var variable = ArgStr(args, 0, "variable");
            var substring = ArgStr(args, 1, "substring");
            return Task.FromResult<object?>($"{variable}.getName().matches(\"%{Escape(substring)}%\")");
        });

        registry.RegisterProviderFunction("codeql-export", "qlModifier", args =>
        {
            ValidateArgCount(args, 2, "qlModifier(variable, modifier)");
            var variable = ArgStr(args, 0, "variable");
            var modifier = ArgStr(args, 1, "modifier").ToLowerInvariant();
            var predicate = modifier switch
            {
                "public" => "isPublic",
                "private" => "isPrivate",
                "protected" => "isProtected",
                "internal" => "isInternal",
                "static" => "isStatic",
                "sealed" or "final" => "isFinal",
                "abstract" => "isAbstract",
                "virtual" => "isVirtual",
                "async" => "isAsync",
                "override" => "isOverride",
                _ => $"is{char.ToUpperInvariant(modifier[0])}{modifier[1..]}"
            };
            return Task.FromResult<object?>($"{variable}.{predicate}()");
        });

        registry.RegisterProviderFunction("codeql-export", "qlExists", args =>
        {
            ValidateArgCount(args, 2, "qlExists(declaration, condition)");
            var declaration = ArgStr(args, 0, "declaration");
            var condition = ArgStr(args, 1, "condition");
            return Task.FromResult<object?>($"exists({declaration} | {condition})");
        });

        registry.RegisterProviderFunction("codeql-export", "qlSave", args =>
        {
            ValidateArgCount(args, 2, "qlSave(path, content)");
            var path = ArgStr(args, 0, "path");
            var content = ArgStr(args, 1, "content");
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(rootPath, path);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);
            return Task.FromResult<object?>($"Saved: {fullPath}");
        });
    }

    private static string BuildQuery(string name, string description, string from, string where, string select)
    {
        return $"""
            /**
             * @name {name}
             * @description {description}
             * @kind problem
             * @problem.severity warning
             */

            import csharp

            from {from}
            where {where}
            select {select}
            """;
    }

    private static string BuildQueryAll(string name, string description, string from, string select)
    {
        return $"""
            /**
             * @name {name}
             * @description {description}
             * @kind problem
             * @problem.severity warning
             */

            import csharp

            from {from}
            select {select}
            """;
    }

    private static void ValidateArgCount(List<object?> args, int expected, string signature)
    {
        if (args.Count < expected)
            throw new InvalidOperationException($"codeql-export.{signature} requires {expected} arguments, got {args.Count}");
    }

    private static string ArgStr(List<object?> args, int index, string paramName)
    {
        return args[index]?.ToString()
            ?? throw new InvalidOperationException($"codeql-export: '{paramName}' cannot be null");
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");

    public override string ToString() => "CodeqlExportProvider";
}
