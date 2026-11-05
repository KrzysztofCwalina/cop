// Registers the language intrinsics implemented by the host runtime.

using Cop.Core;
using Cop.Lang.Ast;
using Cop.Lang.Parser;

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
    private static readonly Lazy<ModuleNode> CoreModule = new(() =>
    {
        using Stream stream = typeof(StandardLibrary).Assembly.GetManifestResourceStream("Cop.Lang.Core.cop")
            ?? throw new InvalidOperationException("Embedded core package declarations are missing.");
        using StreamReader reader = new StreamReader(stream);
        return CopParser.Parse(reader.ReadToEnd(), "core/intrinsics.cop");
    });

    internal static void RegisterPackageFunctions(Evaluator evaluator)
    {
        foreach (Declaration declaration in CoreModule.Value.Declarations)
        {
            if (declaration is FunctionDecl { IsExported: true } function
                && function.Body is not IntrinsicBody)
            {
                evaluator.GlobalEnvironment.Define(function.Name, new CopFunction(function, evaluator.GlobalEnvironment));
            }
        }
    }

    /// <summary>
    /// Register all standard intrinsic functions into the FFI registry.
    /// </summary>
    public static void Register(ForeignFunctionRegistry ffi, Action<string>? outputHandler = null)
    {
        // Output functions
        ffi.Register("print", (args, env) =>
        {
            string text = args.Count > 0 ? args[0].Display() : "";
            outputHandler?.Invoke(text);
            return CopNull.Instance;
        });

        ffi.Register("debug", (args, env) =>
        {
            string text = args.Count > 0 ? args[0].Display() : "";
            outputHandler?.Invoke($"[debug] {text}");
            return CopNull.Instance;
        });

        // Assertion / error functions
        ffi.Register("assert", (args, env) =>
        {
            if (args.Count > 0 && !args[0].IsTruthy)
            {
                string msg = args.Count > 1 ? args[1].Display() : "Assertion failed";
                throw new CopEvaluationException(msg);
            }
            return CopNull.Instance;
        });

        ffi.Register("fail", (args, env) =>
        {
            string msg = args.Count > 0 ? args[0].Display() : "fail";
            throw new CopEvaluationException(msg);
        });

        ffi.Register("error", (args, env) =>
        {
            string msg = args.Count > 0 ? args[0].Display() : "error";
            Dictionary<string, CopValue> fields = new Dictionary<string, CopValue>
            {
                ["Type"] = new CopString("Error"),
                ["Message"] = new CopString(msg)
            };
            return new CopObject(fields) { TypeName = "Error" };
        });

        // I/O functions
        ffi.Register("read", (args, env) =>
        {
            if (args.Count == 0)
            {
                throw new CopEvaluationException("read() requires a path argument");
            }

            string path = args[0].Display();
            try
            {
                return new CopString(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new CopEvaluationException($"read('{path}') failed: {ex.Message}");
            }
        });

        ffi.Register("save", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopNull.Instance;
            }

            string path = args[0].Display();
            string content = args[1].Display();
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(path, content + System.Environment.NewLine);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new CopEvaluationException($"save('{path}') failed: {ex.Message}");
            }
            return CopNull.Instance;
        });

        // Path matching
        ffi.Register("pathMatches", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string path = args[0].Display();
            string pattern = args[1].Display();
            bool matches = PathMatchesGlob(path, pattern);
            return CopBool.Of(matches);
        });

        // Capital alias used as `item.Name:Matches('rx')`.
        ffi.Register("Matches", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            try { return CopBool.Of(RegexCache.IsMatch(args[0].Display(), args[1].Display())); }
            catch (Exception ex) { throw new CopEvaluationException($"Invalid regex pattern '{args[1].Display()}': {ex.Message}"); }
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
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            string providerName = args[0].Display();
            return new CopProviderProxy(providerName, env);
        });

        // Register short-form aliases (sw, ew, ct, eq, ne, rx, ...) for built-in predicates from the
        // single IntrinsicRegistry, so e.g. `x:eq('y')` evaluates per-item exactly as it compiles for
        // provider pushdown. Only adds aliases whose canonical is a registered FFI function and that
        // are not already registered (skips .cop-defined ops and pre-registered short forms).
        foreach ((string? canonical, string? alias) in IntrinsicRegistry.AliasPairs)
        {
            ffi.Alias(alias, canonical);
        }
    }

    private static void RegisterCollectionMethods(ForeignFunctionRegistry ffi)
    {
        // count: collection.count() or count(collection) or collection.count(predicate)
        ffi.RegisterEx("count", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            CopValue collection = Evaluator.ForceValue(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                IEnumerable<CopValue> items = CoerceToEnumerable(collection);
                int count = items.Count(item => predicate.Invoke([item], evaluator, env).IsTruthy);
                return new CopInt(count);
            }
            return collection switch
            {
                CopList list => new CopInt(list.Items.Count),
                CopLazyCollection lazy => new CopInt(lazy.Enumerate().Count()),
                CopQueryable query => new CopInt(query.Enumerate().Count()),
                _ => new CopInt(0)
            };
        });

        // any: collection.any() or collection.any(predicate) — returns bool
        ffi.RegisterEx("any", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return CopBool.False;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of(items.Any(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            }

            return CopBool.Of(items.Any());
        });

        // all: collection.all(predicate) — returns bool
        ffi.RegisterEx("all", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of(items.All(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            }

            return CopBool.Of(items.All(item => item.IsTruthy));
        });

        // none: collection.none(predicate) — returns bool
        ffi.RegisterEx("none", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of(!items.Any(item => predicate.Invoke([item], evaluator, env).IsTruthy));
            }

            return CopBool.Of(!items.Any());
        });

        // where: collection.where(predicate) — filter
        ffi.RegisterEx("where", (args, evaluator, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue collection = args[0];
            CopValue predicate = args[1];
            if (predicate is not ICopCallable pred)
            {
                return collection;
            }

            return new CopLazyCollection(() =>
                CoerceToEnumerable(collection).Where(item =>
                    pred.Invoke([item], evaluator, env).IsTruthy));
        });

        // select: collection.select(transform) — map/project
        ffi.RegisterEx("select", (args, evaluator, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue collection = args[0];
            CopValue transform = args[1];
            if (transform is not ICopCallable fn)
            {
                return collection;
            }

            return new CopLazyCollection(() =>
                CoerceToEnumerable(collection).Select(item =>
                    fn.Invoke([item], evaluator, env)));
        });

        // orderBy: collection.orderBy(keySelector) — sort ascending
        ffi.RegisterEx("orderBy", (args, evaluator, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue collection = args[0];
            CopValue keySelector = args[1];
            if (keySelector is not ICopCallable keySel)
            {
                return collection;
            }

            List<CopValue> items = CoerceToEnumerable(collection).ToList();
            items.Sort((a, b) =>
            {
                CopValue ka = keySel.Invoke([a], evaluator, env);
                CopValue kb = keySel.Invoke([b], evaluator, env);
                return CompareValues(ka, kb);
            });
            return new CopList(items);
        });

        // distinct: collection.distinct() — deduplicate
        ffi.Register("distinct", (args, env) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            if (args[0] is CopQueryable or CopLazyCollection)
            {
                return new CopLazyCollection(() => CoerceToEnumerable(args[0]).DistinctBy(item => item.Display()));
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            HashSet<string> seen = new HashSet<string>();
            List<CopValue> result = new List<CopValue>();
            foreach (CopValue item in items)
            {
                if (seen.Add(item.Display()))
                {
                    result.Add(item);
                }
            }
            return new CopList(result);
        });

        // take: collection.take(n) — first n items
        ffi.Register("take", (args, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            int n = args[1] is CopInt i ? i.Value : 0;
            return args[0] is CopList ? new CopList(items.Take(n).ToList()) : new CopLazyCollection(() => items.Take(n));
        });

        // skip: collection.skip(n) — skip first n items
        ffi.Register("skip", (args, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            int n = args[1] is CopInt i ? i.Value : 0;
            return args[0] is CopList ? new CopList(items.Skip(n).ToList()) : new CopLazyCollection(() => items.Skip(n));
        });

        // first: collection.first() — returns first item or null
        ffi.Register("first", (args, env) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            return items.FirstOrDefault() ?? CopNull.Instance;
        });

        // last: collection.last() — returns last item or null
        ffi.Register("last", (args, env) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            return items.LastOrDefault() ?? CopNull.Instance;
        });

        // text: collection.text(separator) — join items with separator
        ffi.Register("text", (args, env) =>
        {
            if (args.Count == 0)
            {
                return new CopString("");
            }

            string separator = args.Count > 1 ? args[1].Display() : ", ";

            CopValue collection = Evaluator.ForceValue(args[0]);
            if (collection is CopList list)
            {
                return new CopString(string.Join(separator, list.Items.Select(i => i.Display())));
            }

            if (collection is CopLazyCollection lazy)
            {
                return new CopString(string.Join(separator, lazy.Enumerate().Select(i => i.Display())));
            }

            if (collection is CopQueryable query)
            {
                return new CopString(string.Join(separator, query.Enumerate().Select(i => i.Display())));
            }

            return new CopString(collection.Display());
        });

        // Text(expr): documented scalar conversion to text — also usable via colon pipe (expr:Text).
        // The per-item collection form (Items.Text(template)) is intercepted earlier in the evaluator.
        ffi.Register("Text", (args, env) =>
            args.Count == 0 ? new CopString("") : new CopString(args[0].Display()));

        // concat: collection.concat(other) — join two collections
        ffi.Register("concat", (args, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue left = Evaluator.ForceValue(args[0]);
            CopValue right = Evaluator.ForceValue(args[1]);
            return left is CopQueryable || right is CopQueryable
                ? CopQueryable.Concat(left, right)
                : new CopList(CoerceToEnumerable(left).Concat(CoerceToEnumerable(right)).ToList());
        });

        // push: collection.push(item) — append to end
        ffi.Register("push", (args, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            List<CopValue> items = CoerceToEnumerable(args[0]).ToList();
            items.Add(args[1]);
            return new CopList(items);
        });

        // enqueue: collection.enqueue(item) — prepend to front
        ffi.Register("enqueue", (args, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            List<CopValue> items = CoerceToEnumerable(args[0]).ToList();
            items.Insert(0, args[1]);
            return new CopList(items);
        });

        // pop: collection.pop() — remove last item
        ffi.Register("pop", (args, env) =>
        {
            if (args.Count == 0)
            {
                return new CopList([]);
            }

            List<CopValue> items = CoerceToEnumerable(args[0]).ToList();
            if (items.Count <= 1)
            {
                return new CopList([]);
            }

            items.RemoveAt(items.Count - 1);
            return new CopList(items);
        });

        // elementAt: collection.elementAt(index) — item at zero-based index
        ffi.Register("elementAt", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopNull.Instance;
            }

            List<CopValue> items = CoerceToEnumerable(args[0]).ToList();
            int index = args[1] is CopInt i ? i.Value : 0;
            if (index < 0 || index >= items.Count)
            {
                return CopNull.Instance;
            }

            return items[index];
        });

        // empty: collection.empty() — true if collection has no items
        ffi.Register("empty", (args, env) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            return CopBool.Of(!items.Any());
        });

        // Object operations (issue #43): obj.Get('key') / obj:containsKey('key') — dynamic,
        // case-insensitive property access on object literals and provider objects.
        ffi.Register("Get", (args, env) =>
        {
            if (args.Count is not (2 or 3))
            {
                throw new CopEvaluationException("Get() requires an object, a key, and an optional caseSensitive boolean");
            }

            if (args.Count == 3 && args[2] is not CopBool)
            {
                throw new CopEvaluationException("Get() caseSensitive argument must be a boolean");
            }

            bool caseSensitive = args.Count == 3 && args[2] is CopBool { Value: true };
            return GetObjectKey(args[0], args[1].Display(), caseSensitive);
        });
        ffi.Register("containsKey", (args, env) =>
        {
            if (args.Count != 2)
            {
                throw new CopEvaluationException("containsKey() requires an object and a key");
            }

            return CopBool.Of(FindObjectKey(args[0], args[1].Display(), caseSensitive: false) is not null);
        });

        // single: collection.single(predicate?) — single matching item or null
        ffi.RegisterEx("single", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            List<CopValue> items = CoerceToEnumerable(args[0]).ToList();
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                items = items.Where(item => predicate.Invoke([item], evaluator, env).IsTruthy).ToList();
            }

            return items.Count == 1 ? items[0] : CopNull.Instance;
        });

        // groupBy: collection.groupBy(keySelector) — group by key
        ffi.RegisterEx("groupBy", (args, evaluator, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? keySel = args[1] as ICopCallable;
            if (keySel is null)
            {
                return new CopList(items.ToList());
            }

            IEnumerable<IGrouping<string, CopValue>> groups = items.GroupBy(item => keySel.Invoke([item], evaluator, env).Display());
            List<CopValue> result = groups.Select(g => new CopList(g.ToList()) as CopValue).ToList();
            return new CopList(result);
        });

        // orderByDescending: collection.orderByDescending(keySelector)
        ffi.RegisterEx("orderByDescending", (args, evaluator, env) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue collection = args[0];
            CopValue keySelector = args[1];
            if (keySelector is not ICopCallable keySel)
            {
                return collection;
            }

            List<CopValue> items = CoerceToEnumerable(collection).ToList();
            items.Sort((a, b) =>
            {
                CopValue ka = keySel.Invoke([a], evaluator, env);
                CopValue kb = keySel.Invoke([b], evaluator, env);
                return CompareValues(kb, ka); // reversed for descending
            });
            return new CopList(items);
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
        // reduce: the fundamental fold — only true intrinsic needed for aggregation
        // reduce(collection, (acc, item) => expr, initialValue)
        ffi.RegisterEx("reduce", (args, evaluator, env) =>
        {
            if (args.Count < 3)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? fn = args[1] as ICopCallable;
            CopValue acc = args[2];
            if (fn is null)
            {
                return acc;
            }

            foreach (CopValue item in items)
            {
                acc = fn.Invoke([acc, item], evaluator, env);
            }

            return acc;
        });

        // sum: collection.sum() or sum(collection, projection)
        ffi.RegisterEx("sum", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            bool hasDouble = false;
            foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                if (val is CopInt i)
                {
                    total += i.Value;
                }
                else if (val is CopNumber n) { total += n.Value; hasDouble = true; }
            }
            return hasDouble ? new CopNumber(total) : new CopInt((int)total);
        });

        // min: collection.min() or min(collection, projection)
        ffi.RegisterEx("min", (args, evaluator, env) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? minVal = null;
            foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                double? num = ToDouble(val);
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
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? maxVal = null;
            foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                double? num = ToDouble(val);
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
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IEnumerable<CopValue> items = CoerceToEnumerable(args[0]);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            int count = 0;
            foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? projection.Invoke([item], evaluator, env) : item;
                double? num = ToDouble(val);
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
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = args[0].Display();
            string prefix = args[1].Display();
            return CopBool.Of(str.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("endsWith", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = args[0].Display();
            string suffix = args[1].Display();
            return CopBool.Of(str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("contains", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = args[0].Display();
            string sub = args[1].Display();
            return CopBool.Of(str.Contains(sub, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("equals", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = args[0].Display();
            string other = args[1].Display();
            return CopBool.Of(str.Equals(other, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("notEquals", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.True;
            }

            string str = args[0].Display();
            string other = args[1].Display();
            return CopBool.Of(!str.Equals(other, StringComparison.OrdinalIgnoreCase));
        });

        ffi.Register("matches", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = args[0].Display();
            string pattern = args[1].Display();
            try
            {
                return CopBool.Of(RegexCache.IsMatch(str, pattern));
            }
            catch (Exception ex)
            {
                throw new CopEvaluationException($"Invalid regex pattern '{pattern}': {ex.Message}");
            }
        });

        ffi.Register("length", (args, env) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            return new CopInt(args[0].Display().Length);
        });
    }

    private static void RegisterFlagPredicates(ForeignFunctionRegistry ffi)
    {
        // isSet(value, mask) → (value & mask) != 0
        ffi.Register("isSet", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            long value = ToLong(args[0]);
            long mask = ToLong(args[1]);
            return CopBool.Of((value & mask) != 0);
        });

        // isClear(value, mask) → (value & mask) == 0
        ffi.Register("isClear", (args, env) =>
        {
            if (args.Count < 2)
            {
                return CopBool.True;
            }

            long value = ToLong(args[0]);
            long mask = ToLong(args[1]);
            return CopBool.Of((value & mask) == 0);
        });
    }

    private static long ToLong(CopValue v) => v switch
    {
        CopInt i => i.Value,
        CopNumber n => (long)n.Value,
        _ => 0
    };

    private static CopValue GetObjectKey(CopValue obj, string key, bool caseSensitive)
    {
        if (obj is CopThunk thunk)
        {
            obj = thunk.Force();
        }

        string? name = FindObjectKey(obj, key, caseSensitive);
        if (name is null)
        {
            return CopNull.Instance;
        }

        return obj switch
        {
            CopObject co => co.GetField(name),
            CopDynamicObject dyn => dyn.GetField(name),
            _ => CopNull.Instance
        };
    }

    private static string? FindObjectKey(CopValue obj, string key, bool caseSensitive)
    {
        if (obj is CopThunk thunk)
        {
            obj = thunk.Force();
        }

        IEnumerable<string> names = obj switch
        {
            CopObject co => co.Fields.Keys,
            CopDynamicObject dyn => dyn.FieldNames,
            _ => []
        };
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return names.FirstOrDefault(name => string.Equals(name, key, comparison));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static IEnumerable<CopValue> CoerceToEnumerable(CopValue value) => CollectionValues.Enumerate(value);

    private static bool PathMatchesGlob(string path, string pattern) => PathGlob.IsMatch(path, pattern);
}
