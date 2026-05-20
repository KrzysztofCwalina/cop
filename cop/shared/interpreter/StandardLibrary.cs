namespace Cop.Lang.Interpreter;

/// <summary>
/// Registers standard intrinsic functions and collection methods into the FFI registry.
/// These are the built-in functions that .cop files declare as `= intrinsic`.
///
/// Design: ALL built-in behavior comes through here. The evaluator has zero hardcoded functions.
/// The runtime can replace or extend these by registering additional functions.
/// </summary>
public static class StandardLibrary
{
    /// <summary>
    /// Register all standard intrinsic functions into the FFI registry.
    /// </summary>
    public static void Register(ForeignFunctionRegistry ffi, Action<string>? outputHandler = null)
    {
        // Output functions
        ffi.Register("print", (args, env) =>
        {
            var text = args.Count > 0 ? args[0].Display() : "";
            outputHandler?.Invoke(text);
            return CopNull.Instance;
        });

        ffi.Register("debug", (args, env) =>
        {
            var text = args.Count > 0 ? args[0].Display() : "";
            outputHandler?.Invoke($"[debug] {text}");
            return CopNull.Instance;
        });

        // Assertion / error functions
        ffi.Register("assert", (args, env) =>
        {
            if (args.Count > 0 && !args[0].IsTruthy)
            {
                var msg = args.Count > 1 ? args[1].Display() : "Assertion failed";
                throw new CopEvaluationException(msg);
            }
            return CopNull.Instance;
        });

        ffi.Register("fail", (args, env) =>
        {
            var msg = args.Count > 0 ? args[0].Display() : "fail";
            throw new CopEvaluationException(msg);
        });

        ffi.Register("error", (args, env) =>
        {
            var msg = args.Count > 0 ? args[0].Display() : "error";
            return new CopString(msg);
        });

        // String functions
        ffi.Register("text", (args, env) =>
        {
            if (args.Count == 0) return new CopString("");
            var separator = args.Count > 1 ? args[1].Display() : ", ";

            if (args[0] is CopList list)
                return new CopString(string.Join(separator, list.Items.Select(i => i.Display())));
            if (args[0] is CopLazyCollection lazy)
                return new CopString(string.Join(separator, lazy.Enumerate().Select(i => i.Display())));

            return new CopString(args[0].Display());
        });

        // I/O functions
        ffi.Register("read", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var path = args[0].Display();
            try
            {
                return new CopString(File.ReadAllText(path));
            }
            catch
            {
                return CopNull.Instance;
            }
        });

        ffi.Register("save", (args, env) =>
        {
            if (args.Count < 2) return CopNull.Instance;
            var path = args[0].Display();
            var content = args[1].Display();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, content + System.Environment.NewLine);
            }
            catch { /* silently fail for now */ }
            return CopNull.Instance;
        });

        // Path matching
        ffi.Register("pathMatches", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var path = args[0].Display();
            var pattern = args[1].Display();
            var matches = PathMatchesGlob(path, pattern);
            return CopBool.Of(matches);
        });

        // Collection methods (registered as functions that operate on collections)
        RegisterCollectionMethods(ffi);

        // String predicate methods
        RegisterStringPredicates(ffi);
    }

    private static void RegisterCollectionMethods(ForeignFunctionRegistry ffi)
    {
        // count: collection.count() or count(collection) or collection.count(predicate)
        ffi.Register("count", (args, env) =>
        {
            if (args.Count == 0) return new CopInt(0);
            var collection = args[0];
            return collection switch
            {
                CopList list => new CopInt(list.Items.Count),
                CopLazyCollection lazy => new CopInt(lazy.Enumerate().Count()),
                _ => new CopInt(0)
            };
        });

        // any: collection.any(predicate) — returns bool
        ffi.Register("any", (args, env) =>
        {
            if (args.Count == 0) return CopBool.False;
            var items = CoerceToEnumerable(args[0]);
            return CopBool.Of(items.Any());
        });

        // none: collection.none(predicate) — returns bool
        ffi.Register("none", (args, env) =>
        {
            if (args.Count == 0) return CopBool.True;
            var items = CoerceToEnumerable(args[0]);
            return CopBool.Of(!items.Any());
        });

        // first: collection.first() — returns first item or null
        ffi.Register("first", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            return items.FirstOrDefault() ?? CopNull.Instance;
        });

        // last: collection.last() — returns last item or null
        ffi.Register("last", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            return items.LastOrDefault() ?? CopNull.Instance;
        });
    }

    private static void RegisterStringPredicates(ForeignFunctionRegistry ffi)
    {
        ffi.Register("startsWith", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var str = args[0].Display();
            var prefix = args[1].Display();
            return CopBool.Of(str.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("endsWith", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var str = args[0].Display();
            var suffix = args[1].Display();
            return CopBool.Of(str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("contains", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var str = args[0].Display();
            var sub = args[1].Display();
            return CopBool.Of(str.Contains(sub, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("equals", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var str = args[0].Display();
            var other = args[1].Display();
            return CopBool.Of(str.Equals(other, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("matches", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var str = args[0].Display();
            var pattern = args[1].Display();
            try
            {
                return CopBool.Of(System.Text.RegularExpressions.Regex.IsMatch(str, pattern));
            }
            catch
            {
                return CopBool.False;
            }
        });

        ffi.Register("length", (args, env) =>
        {
            if (args.Count == 0) return new CopInt(0);
            return new CopInt(args[0].Display().Length);
        });
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static IEnumerable<CopValue> CoerceToEnumerable(CopValue value)
    {
        return value switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate(),
            _ => [value]
        };
    }

    private static bool PathMatchesGlob(string path, string pattern)
    {
        // Simple glob matching: * matches anything in a segment, ** matches across segments
        var normalized = path.Replace('\\', '/');
        var normalizedPattern = pattern.Replace('\\', '/');

        if (normalizedPattern.Contains("**"))
        {
            var parts = normalizedPattern.Split("**");
            if (parts.Length == 2)
            {
                var prefix = parts[0].TrimEnd('/');
                var suffix = parts[1].TrimStart('/');
                if (!string.IsNullOrEmpty(prefix) && !normalized.Contains(prefix.Trim('/')))
                    return false;
                if (!string.IsNullOrEmpty(suffix) && !normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
        }

        // Simple wildcard: * matches any segment content
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(normalizedPattern)
            .Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
