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
            var fields = new Dictionary<string, CopValue>
            {
                ["Type"] = new CopString("Error"),
                ["Message"] = new CopString(msg)
            };
            return new CopObject(fields) { TypeName = "Error" };
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

        // Numeric aggregate methods
        RegisterNumericAggregates(ffi);

        // String predicate methods
        RegisterStringPredicates(ffi);

        // Bitwise flag predicates
        RegisterFlagPredicates(ffi);

        // provider intrinsic — returns a proxy that resolves member access to provider collections
        ffi.Register("provider", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var providerName = args[0].Display();
            return new CopProviderProxy(providerName, env);
        });
    }

    private static void RegisterCollectionMethods(ForeignFunctionRegistry ffi)
    {
        // count: collection.count() or count(collection) or collection.count(predicate)
        ffi.RegisterEx("count", (args, evaluator, env) =>
        {
            if (args.Count == 0) return new CopInt(0);
            var collection = args[0];
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                var items = CoerceToEnumerable(collection);
                var count = items.Count(item => predicate.Invoke([item], evaluator, env).IsTruthy);
                return new CopInt(count);
            }
            return collection switch
            {
                CopList list => new CopInt(list.Items.Count),
                CopLazyCollection lazy => new CopInt(lazy.Enumerate().Count()),
                _ => new CopInt(0)
            };
        });

        // any: collection.any() or collection.any(predicate) — returns bool
        ffi.RegisterEx("any", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopBool.False;
            var items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
                return CopBool.Of(items.Any(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            return CopBool.Of(items.Any());
        });

        // all: collection.all(predicate) — returns bool
        ffi.RegisterEx("all", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopBool.True;
            var items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
                return CopBool.Of(items.All(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            return CopBool.Of(items.All(item => item.IsTruthy));
        });

        // none: collection.none(predicate) — returns bool
        ffi.RegisterEx("none", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopBool.True;
            var items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
                return CopBool.Of(!items.Any(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            return CopBool.Of(!items.Any());
        });

        // where: collection.where(predicate) — filter
        ffi.RegisterEx("where", (args, evaluator, env) =>
        {
            if (args.Count < 2) return args.Count > 0 ? args[0] : CopNull.Instance;
            var collection = args[0];
            var predicate = args[1];
            if (predicate is not ICopCallable pred)
                return collection;
            return new CopLazyCollection(() =>
                CoerceToEnumerable(collection).Where(item =>
                    pred.Invoke([item], evaluator, env).IsTruthy));
        });

        // select: collection.select(transform) — map/project
        ffi.RegisterEx("select", (args, evaluator, env) =>
        {
            if (args.Count < 2) return args.Count > 0 ? args[0] : CopNull.Instance;
            var collection = args[0];
            var transform = args[1];
            if (transform is not ICopCallable fn)
                return collection;
            return new CopLazyCollection(() =>
                CoerceToEnumerable(collection).Select(item =>
                    fn.Invoke([item], evaluator, env)));
        });

        // orderBy: collection.orderBy(keySelector) — sort ascending
        ffi.RegisterEx("orderBy", (args, evaluator, env) =>
        {
            if (args.Count < 2) return args.Count > 0 ? args[0] : CopNull.Instance;
            var collection = args[0];
            var keySelector = args[1];
            if (keySelector is not ICopCallable keySel)
                return collection;
            var items = CoerceToEnumerable(collection).ToList();
            items.Sort((a, b) =>
            {
                var ka = keySel.Invoke([a], evaluator, env);
                var kb = keySel.Invoke([b], evaluator, env);
                return CompareValues(ka, kb);
            });
            return new CopList(items);
        });

        // distinct: collection.distinct() — deduplicate
        ffi.Register("distinct", (args, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var seen = new HashSet<string>();
            var result = new List<CopValue>();
            foreach (var item in items)
            {
                if (seen.Add(item.Display()))
                    result.Add(item);
            }
            return new CopList(result);
        });

        // take: collection.take(n) — first n items
        ffi.Register("take", (args, env) =>
        {
            if (args.Count < 2) return args.Count > 0 ? args[0] : CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var n = args[1] is CopInt i ? i.Value : 0;
            return new CopList(items.Take(n).ToList());
        });

        // skip: collection.skip(n) — skip first n items
        ffi.Register("skip", (args, env) =>
        {
            if (args.Count < 2) return args.Count > 0 ? args[0] : CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var n = args[1] is CopInt i ? i.Value : 0;
            return new CopList(items.Skip(n).ToList());
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

        // text: collection.text(separator) — join items with separator
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
    }

    private static int CompareValues(CopValue a, CopValue b)
    {
        return (a, b) switch
        {
            (CopInt ia, CopInt ib) => ia.Value.CompareTo(ib.Value),
            (CopNumber na, CopNumber nb) => na.Value.CompareTo(nb.Value),
            (CopString sa, CopString sb) => string.Compare(sa.Value, sb.Value, StringComparison.Ordinal),
            _ => string.Compare(a.Display(), b.Display(), StringComparison.Ordinal)
        };
    }

    private static void RegisterNumericAggregates(ForeignFunctionRegistry ffi)
    {
        // sum: collection.sum() or sum(collection, projection)
        ffi.RegisterEx("sum", (args, evaluator, env) =>
        {
            if (args.Count == 0) return new CopInt(0);
            var items = CoerceToEnumerable(args[0]);
            var projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            bool hasDouble = false;
            foreach (var item in items)
            {
                var val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                if (val is CopInt i) total += i.Value;
                else if (val is CopNumber n) { total += n.Value; hasDouble = true; }
            }
            return hasDouble ? new CopNumber(total) : new CopInt((int)total);
        });

        // min: collection.min() or min(collection, projection)
        ffi.RegisterEx("min", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? minVal = null;
            foreach (var item in items)
            {
                var val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                var num = ToDouble(val);
                if (num is not null && (minVal is null || num.Value < minVal.Value))
                {
                    minVal = num.Value;
                    result = val;
                }
            }
            return result ?? CopNull.Instance;
        });

        // max: collection.max() or max(collection, projection)
        ffi.RegisterEx("max", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? maxVal = null;
            foreach (var item in items)
            {
                var val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                var num = ToDouble(val);
                if (num is not null && (maxVal is null || num.Value > maxVal.Value))
                {
                    maxVal = num.Value;
                    result = val;
                }
            }
            return result ?? CopNull.Instance;
        });

        // average: collection.average() or average(collection, projection)
        ffi.RegisterEx("average", (args, evaluator, env) =>
        {
            if (args.Count == 0) return CopNull.Instance;
            var items = CoerceToEnumerable(args[0]);
            var projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            int count = 0;
            foreach (var item in items)
            {
                var val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                var num = ToDouble(val);
                if (num is not null) { total += num.Value; count++; }
            }
            return count > 0 ? new CopNumber(total / count) : CopNull.Instance;
        });
    }

    private static double? ToDouble(CopValue v) => v switch
    {
        CopInt i => i.Value,
        CopNumber n => n.Value,
        _ => null
    };

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

        ffi.Register("notEquals", (args, env) =>
        {
            if (args.Count < 2) return CopBool.True;
            var str = args[0].Display();
            var other = args[1].Display();
            return CopBool.Of(!str.Equals(other, StringComparison.OrdinalIgnoreCase));
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

    private static void RegisterFlagPredicates(ForeignFunctionRegistry ffi)
    {
        // isSet(value, mask) → (value & mask) != 0
        ffi.Register("isSet", (args, env) =>
        {
            if (args.Count < 2) return CopBool.False;
            var value = ToLong(args[0]);
            var mask = ToLong(args[1]);
            return CopBool.Of((value & mask) != 0);
        });

        // isClear(value, mask) → (value & mask) == 0
        ffi.Register("isClear", (args, env) =>
        {
            if (args.Count < 2) return CopBool.True;
            var value = ToLong(args[0]);
            var mask = ToLong(args[1]);
            return CopBool.Of((value & mask) == 0);
        });
    }

    private static long ToLong(CopValue v) => v switch
    {
        CopInt i => i.Value,
        CopNumber n => (long)n.Value,
        _ => 0
    };

    // ========================================================================
    // Helpers
    // ========================================================================

    private static IEnumerable<CopValue> CoerceToEnumerable(CopValue value)
    {
        // Force thunks before coercing
        if (value is CopThunk thunk)
            value = thunk.Force();

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
