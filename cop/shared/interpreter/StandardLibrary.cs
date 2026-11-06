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
    private static readonly Lazy<ModuleNode> s_coreModule = new(() =>
    {
        using Stream stream = typeof(StandardLibrary).Assembly.GetManifestResourceStream("Cop.Lang.Core.cop")
            ?? throw new InvalidOperationException("Embedded core package declarations are missing.");
        using StreamReader reader = new StreamReader(stream);
        return CopParser.Parse(reader.ReadToEnd(), "core/intrinsics.cop");
    });

    internal static void RegisterPackageFunctions(Evaluator evaluator)
    {
        foreach (Declaration declaration in s_coreModule.Value.Declarations)
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
        ffi.RegisterAsync("print", async (args, env, cancellationToken) =>
        {
            string text = args.Count > 0 ? (await args[0].DisplayAsync(cancellationToken: cancellationToken)) : "";
            outputHandler?.Invoke(text);
            return CopNull.Instance;
        });

        ffi.RegisterAsync("debug", async (args, env, cancellationToken) =>
        {
            string text = args.Count > 0 ? (await args[0].DisplayAsync(cancellationToken: cancellationToken)) : "";
            outputHandler?.Invoke($"[debug] {text}");
            return CopNull.Instance;
        });

        // Assertion / error functions
        ffi.RegisterAsync("assert", async (args, env, cancellationToken) =>
        {
            if (args.Count > 0 && !(await args[0].IsTruthyAsync(cancellationToken)))
            {
                string msg = args.Count > 1 ? (await args[1].DisplayAsync(cancellationToken: cancellationToken)) : "Assertion failed";
                throw new CopEvaluationException(msg);
            }
            return CopNull.Instance;
        });

        ffi.RegisterAsync("fail", async (args, env, cancellationToken) =>
        {
            string msg = args.Count > 0 ? (await args[0].DisplayAsync(cancellationToken: cancellationToken)) : "fail";
            throw new CopEvaluationException(msg);
        });

        ffi.RegisterAsync("error", async (args, env, cancellationToken) =>
        {
            string msg = args.Count > 0 ? (await args[0].DisplayAsync(cancellationToken: cancellationToken)) : "error";
            Dictionary<string, CopValue> fields = new Dictionary<string, CopValue>
            {
                ["Type"] = new CopString("Error"),
                ["Message"] = new CopString(msg)
            };
            return new CopObject(fields) { TypeName = "Error" };
        });

        // I/O functions
        ffi.RegisterAsync("read", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                throw new CopEvaluationException("read() requires a path argument");
            }

            string path = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            try
            {
                return new CopString(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                throw new CopEvaluationException($"read('{path}') failed: {ex.Message}");
            }
        });

        ffi.RegisterAsync("save", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopNull.Instance;
            }

            string path = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string content = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
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
        ffi.RegisterAsync("pathMatches", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string path = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string pattern = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            bool matches = PathMatchesGlob(path, pattern);
            return CopBool.Of(matches);
        });

        // Capital alias used as `item.Name:Matches('rx')`.
        ffi.RegisterAsync("Matches", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            try { return CopBool.Of(RegexCache.IsMatch((await args[0].DisplayAsync(cancellationToken: cancellationToken)), (await args[1].DisplayAsync(cancellationToken: cancellationToken)))); }
            catch (Exception ex) { throw new CopEvaluationException($"Invalid regex pattern '{(await args[1].DisplayAsync(cancellationToken: cancellationToken))}': {ex.Message}"); }
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
        ffi.RegisterAsync("provider", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            string providerName = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
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
        ffi.RegisterExAsync("count", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            CopValue collection = (await Evaluator.ForceValueAsync(args[0], cancellationToken: cancellationToken));
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken);
                int count = (await items.CountAsync(async (item, itemCancellationToken) => (await (await predicate.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken)), cancellationToken: cancellationToken));
                return new CopInt(count);
            }
            return collection switch
            {
                CopList list => new CopInt(list.Items.Count),
                CopLazyCollection lazy => new CopInt((await lazy.EnumerateAsync(cancellationToken: cancellationToken).CountAsync(cancellationToken: cancellationToken))),
                CopQueryable query => new CopInt((await query.EnumerateAsync(cancellationToken: cancellationToken).CountAsync(cancellationToken: cancellationToken))),
                _ => new CopInt(0)
            };
        });

        // any: collection.any() or collection.any(predicate) — returns bool
        ffi.RegisterExAsync("any", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopBool.False;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of((await items.AnyAsync(async (item, itemCancellationToken) => (await (await predicate.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken)), cancellationToken: cancellationToken)));
            }

            return CopBool.Of((await items.AnyAsync(cancellationToken: cancellationToken)));
        });

        // all: collection.all(predicate) — returns bool
        ffi.RegisterExAsync("all", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of((await items.AllAsync(async (item, itemCancellationToken) => (await (await predicate.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken)), cancellationToken: cancellationToken)));
            }

            return CopBool.Of((await items.AllAsync(async (item, itemCancellationToken) => (await item.IsTruthyAsync(itemCancellationToken)), cancellationToken: cancellationToken)));
        });

        // none: collection.none(predicate) — returns bool
        ffi.RegisterExAsync("none", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                return CopBool.Of(!(await items.AnyAsync(async (item, itemCancellationToken) => (await (await predicate.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken)), cancellationToken: cancellationToken)));
            }

            return CopBool.Of(!(await items.AnyAsync(cancellationToken: cancellationToken)));
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

            return new CopLazyCollection((deferredCancellationToken) =>
                CoerceToEnumerableAsync(collection, cancellationToken: deferredCancellationToken).Where(async (item, itemCancellationToken) =>
                    (await (await pred.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken))));
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

            return new CopLazyCollection((deferredCancellationToken) =>
                CoerceToEnumerableAsync(collection, cancellationToken: deferredCancellationToken).Select(async (item, itemCancellationToken) =>
                    (await fn.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken))));
        });

        // orderBy: collection.orderBy(keySelector) — sort ascending
        ffi.RegisterExAsync("orderBy", async (args, evaluator, env, cancellationToken) =>
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

            List<CopValue> items = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            await items.SortAsync(async (a, b, comparisonCancellationToken) =>
            {
                CopValue ka = (await keySel.InvokeAsync([a], evaluator, env, cancellationToken: comparisonCancellationToken));
                CopValue kb = (await keySel.InvokeAsync([b], evaluator, env, cancellationToken: comparisonCancellationToken));
                return (await CompareValuesAsync(ka, kb, cancellationToken: comparisonCancellationToken));
            }, cancellationToken: cancellationToken);
            return new CopList(items);
        });

        // distinct: collection.distinct() — deduplicate
        ffi.RegisterAsync("distinct", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            if (args[0] is CopQueryable or CopLazyCollection)
            {
                return new CopLazyCollection((deferredCancellationToken) => CoerceToEnumerableAsync(args[0], cancellationToken: deferredCancellationToken).DistinctBy(item => item.Display()));
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            HashSet<string> seen = new HashSet<string>();
            List<CopValue> result = new List<CopValue>();
            await foreach (CopValue item in items)
            {
                if (seen.Add((await item.DisplayAsync(cancellationToken: cancellationToken))))
                {
                    result.Add(item);
                }
            }
            return new CopList(result);
        });

        // take: collection.take(n) — first n items
        ffi.RegisterAsync("take", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken);
            int n = args[1] is CopInt i ? i.Value : 0;
            return args[0] is CopList
                ? new CopList(await items.Take(n).ToListAsync(cancellationToken))
                : new CopLazyCollection(token => CoerceToEnumerableAsync(args[0], token).Take(n));
        });

        // skip: collection.skip(n) — skip first n items
        ffi.RegisterAsync("skip", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken);
            int n = args[1] is CopInt i ? i.Value : 0;
            return args[0] is CopList
                ? new CopList(await items.Skip(n).ToListAsync(cancellationToken))
                : new CopLazyCollection(token => CoerceToEnumerableAsync(args[0], token).Skip(n));
        });

        // first: collection.first() — returns first item or null
        ffi.RegisterAsync("first", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            return (await items.FirstOrDefaultAsync(cancellationToken: cancellationToken)) ?? CopNull.Instance;
        });

        // last: collection.last() — returns last item or null
        ffi.RegisterAsync("last", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            return (await items.LastOrDefaultAsync(cancellationToken: cancellationToken)) ?? CopNull.Instance;
        });

        // text: collection.text(separator) — join items with separator
        ffi.RegisterAsync("text", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return new CopString("");
            }

            string separator = args.Count > 1 ? (await args[1].DisplayAsync(cancellationToken: cancellationToken)) : ", ";

            CopValue collection = (await Evaluator.ForceValueAsync(args[0], cancellationToken: cancellationToken));
            if (collection is CopList list)
            {
                return new CopString(string.Join(separator, (await list.Items.ToAsyncEnumerable().Select(async (i, itemCancellationToken) => (await i.DisplayAsync(cancellationToken: itemCancellationToken))).ToListAsync(cancellationToken))));
            }

            if (collection is CopLazyCollection lazy)
            {
                return new CopString(string.Join(separator, (await lazy.EnumerateAsync(cancellationToken: cancellationToken).Select(i => i.Display()).ToListAsync(cancellationToken))));
            }

            if (collection is CopQueryable query)
            {
                return new CopString(string.Join(separator, (await query.EnumerateAsync(cancellationToken: cancellationToken).Select(i => i.Display()).ToListAsync(cancellationToken))));
            }

            return new CopString((await collection.DisplayAsync(cancellationToken: cancellationToken)));
        });

        // Text(expr): documented scalar conversion to text — also usable via colon pipe (expr:Text).
        // The per-item collection form (Items.Text(template)) is intercepted earlier in the evaluator.
        ffi.RegisterAsync("Text", async (args, env, cancellationToken) =>
            args.Count == 0 ? new CopString("") : new CopString((await args[0].DisplayAsync(cancellationToken: cancellationToken))));

        // concat: collection.concat(other) — join two collections
        ffi.RegisterAsync("concat", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            CopValue left = (await Evaluator.ForceValueAsync(args[0], cancellationToken: cancellationToken));
            CopValue right = (await Evaluator.ForceValueAsync(args[1], cancellationToken: cancellationToken));
            return left is CopQueryable || right is CopQueryable
                ? CopQueryable.Concat(left, right)
                : new CopList((await CoerceToEnumerableAsync(left, cancellationToken: cancellationToken).Concat(CoerceToEnumerableAsync(right, cancellationToken: cancellationToken)).ToListAsync(cancellationToken: cancellationToken)));
        });

        // push: collection.push(item) — append to end
        ffi.RegisterAsync("push", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            List<CopValue> items = (await CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            items.Add(args[1]);
            return new CopList(items);
        });

        // enqueue: collection.enqueue(item) — prepend to front
        ffi.RegisterAsync("enqueue", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            List<CopValue> items = (await CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            items.Insert(0, args[1]);
            return new CopList(items);
        });

        // pop: collection.pop() — remove last item
        ffi.RegisterAsync("pop", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return new CopList([]);
            }

            List<CopValue> items = (await CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            if (items.Count <= 1)
            {
                return new CopList([]);
            }

            items.RemoveAt(items.Count - 1);
            return new CopList(items);
        });

        // elementAt: collection.elementAt(index) — item at zero-based index
        ffi.RegisterAsync("elementAt", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopNull.Instance;
            }

            List<CopValue> items = (await CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            int index = args[1] is CopInt i ? i.Value : 0;
            if (index < 0 || index >= items.Count)
            {
                return CopNull.Instance;
            }

            return items[index];
        });

        // empty: collection.empty() — true if collection has no items
        ffi.RegisterAsync("empty", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopBool.True;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            return CopBool.Of(!(await items.AnyAsync(cancellationToken: cancellationToken)));
        });

        // Object operations (issue #43): obj.Get('key') / obj:containsKey('key') — dynamic,
        // case-insensitive property access on object literals and provider objects.
        ffi.RegisterAsync("Get", async (args, env, cancellationToken) =>
        {
            if (args.Count is not (2 or 3))
            {
                throw new CopEvaluationException("Get() requires an object, a key, and an optional caseSensitive boolean");
            }

            if (args.Count == 3 && args[2] is not CopBool)
            {
                throw new CopEvaluationException("Get() caseSensitive argument must be a boolean");
            }

            bool caseSensitive = args.Count == 3 && args[2] is CopBool { IsTrue: true };
            return (await GetObjectKeyAsync(args[0], (await args[1].DisplayAsync(cancellationToken: cancellationToken)), caseSensitive, cancellationToken: cancellationToken));
        });
        ffi.RegisterAsync("containsKey", async (args, env, cancellationToken) =>
        {
            if (args.Count != 2)
            {
                throw new CopEvaluationException("containsKey() requires an object and a key");
            }

            return CopBool.Of((await FindObjectKeyAsync(args[0], (await args[1].DisplayAsync(cancellationToken: cancellationToken)), caseSensitive: false, cancellationToken: cancellationToken)) is not null);
        });

        // single: collection.single(predicate?) — single matching item or null
        ffi.RegisterExAsync("single", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            List<CopValue> items = (await CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            if (args.Count > 1 && args[1] is ICopCallable predicate)
            {
                items = (await items.ToAsyncEnumerable().Where(async (item, itemCancellationToken) => (await (await predicate.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken))).ToListAsync(cancellationToken: cancellationToken));
            }

            return items.Count == 1 ? items[0] : CopNull.Instance;
        });

        // groupBy: collection.groupBy(keySelector) — group by key
        ffi.RegisterExAsync("groupBy", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return args.Count > 0 ? args[0] : CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? keySel = args[1] as ICopCallable;
            if (keySel is null)
            {
                return new CopList((await items.ToListAsync(cancellationToken: cancellationToken)));
            }

            IAsyncEnumerable<IGrouping<string, CopValue>> groups = items.GroupBy(async (item, itemCancellationToken) => (await keySel.InvokeAsync([item], evaluator, env, cancellationToken: itemCancellationToken)).Display());
            List<CopValue> result = (await groups.Select(g => new CopList(g.ToList()) as CopValue).ToListAsync(cancellationToken: cancellationToken));
            return new CopList(result);
        });

        // orderByDescending: collection.orderByDescending(keySelector)
        ffi.RegisterExAsync("orderByDescending", async (args, evaluator, env, cancellationToken) =>
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

            List<CopValue> items = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            await items.SortAsync(async (a, b, comparisonCancellationToken) =>
            {
                CopValue ka = (await keySel.InvokeAsync([a], evaluator, env, cancellationToken: comparisonCancellationToken));
                CopValue kb = (await keySel.InvokeAsync([b], evaluator, env, cancellationToken: comparisonCancellationToken));
                return (await CompareValuesAsync(kb, ka, cancellationToken: comparisonCancellationToken)); // reversed for descending
            }, cancellationToken: cancellationToken);
            return new CopList(items);
        });
    }

    private static async ValueTask<int> CompareValuesAsync(CopValue a, CopValue b, CancellationToken cancellationToken = default)
    {
        return (a, b) switch
        {
            (CopInt ia, CopInt ib) => ia.Value.CompareTo(ib.Value),
            (CopNumber na, CopNumber nb) => na.Value.CompareTo(nb.Value),
            (CopString sa, CopString sb) => string.Compare(sa.Value, sb.Value, StringComparison.Ordinal),
            _ => string.Compare((await a.DisplayAsync(cancellationToken: cancellationToken)), (await b.DisplayAsync(cancellationToken: cancellationToken)), StringComparison.Ordinal)
        };
    }

    private static void RegisterNumericAggregates(ForeignFunctionRegistry ffi)
    {
        // reduce: the fundamental fold — only true intrinsic needed for aggregation
        // reduce(collection, (acc, item) => expr, initialValue)
        ffi.RegisterExAsync("reduce", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count < 3)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? fn = args[1] as ICopCallable;
            CopValue acc = args[2];
            if (fn is null)
            {
                return acc;
            }

            await foreach (CopValue item in items)
            {
                acc = (await fn.InvokeAsync([acc, item], evaluator, env, cancellationToken: cancellationToken));
            }

            return acc;
        });

        // sum: collection.sum() or sum(collection, projection)
        ffi.RegisterExAsync("sum", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            bool hasDouble = false;
            await foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? (await projection.InvokeAsync([item], evaluator, env, cancellationToken: cancellationToken)) : item;
                if (val is CopInt i)
                {
                    total += i.Value;
                }
                else if (val is CopNumber n) { total += n.Value; hasDouble = true; }
            }
            return hasDouble ? new CopNumber(total) : new CopInt((int)total);
        });

        // min: collection.min() or min(collection, projection)
        ffi.RegisterExAsync("min", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? minVal = null;
            await foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? (await projection.InvokeAsync([item], evaluator, env, cancellationToken: cancellationToken)) : item;
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
        ffi.RegisterExAsync("max", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            CopValue? result = null;
            double? maxVal = null;
            await foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? (await projection.InvokeAsync([item], evaluator, env, cancellationToken: cancellationToken)) : item;
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
        ffi.RegisterExAsync("average", async (args, evaluator, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return CopNull.Instance;
            }

            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(args[0], cancellationToken: cancellationToken);
            ICopCallable? projection = args.Count > 1 ? args[1] as ICopCallable : null;

            double total = 0;
            int count = 0;
            await foreach (CopValue item in items)
            {
                CopValue val = projection is not null ? (await projection.InvokeAsync([item], evaluator, env, cancellationToken: cancellationToken)) : item;
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
        ffi.RegisterAsync("startsWith", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string prefix = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            return CopBool.Of(str.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.RegisterAsync("endsWith", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string suffix = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            return CopBool.Of(str.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        });

        ffi.RegisterAsync("contains", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string sub = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            return CopBool.Of(str.Contains(sub, StringComparison.OrdinalIgnoreCase));
        });

        ffi.RegisterAsync("equals", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string other = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            return CopBool.Of(str.Equals(other, StringComparison.OrdinalIgnoreCase));
        });

        ffi.RegisterAsync("notEquals", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.True;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string other = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            return CopBool.Of(!str.Equals(other, StringComparison.OrdinalIgnoreCase));
        });

        ffi.RegisterAsync("matches", async (args, env, cancellationToken) =>
        {
            if (args.Count < 2)
            {
                return CopBool.False;
            }

            string str = (await args[0].DisplayAsync(cancellationToken: cancellationToken));
            string pattern = (await args[1].DisplayAsync(cancellationToken: cancellationToken));
            try
            {
                return CopBool.Of(RegexCache.IsMatch(str, pattern));
            }
            catch (Exception ex)
            {
                throw new CopEvaluationException($"Invalid regex pattern '{pattern}': {ex.Message}");
            }
        });

        ffi.RegisterAsync("length", async (args, env, cancellationToken) =>
        {
            if (args.Count == 0)
            {
                return new CopInt(0);
            }

            return new CopInt((await args[0].DisplayAsync(cancellationToken: cancellationToken)).Length);
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

    private static async ValueTask<CopValue> GetObjectKeyAsync(CopValue obj, string key, bool caseSensitive, CancellationToken cancellationToken = default)
    {
        if (obj is CopThunk thunk)
        {
            obj = (await thunk.ForceAsync(cancellationToken: cancellationToken));
        }

        string? name = (await FindObjectKeyAsync(obj, key, caseSensitive, cancellationToken: cancellationToken));
        if (name is null)
        {
            return CopNull.Instance;
        }

        return obj switch
        {
            CopObject co => co.GetField(name),
            CopDynamicObject dyn => (await dyn.GetFieldAsync(name, cancellationToken: cancellationToken)),
            _ => CopNull.Instance
        };
    }

    private static async ValueTask<string?> FindObjectKeyAsync(CopValue obj, string key, bool caseSensitive, CancellationToken cancellationToken = default)
    {
        if (obj is CopThunk thunk)
        {
            obj = (await thunk.ForceAsync(cancellationToken: cancellationToken));
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

    private static IAsyncEnumerable<CopValue> CoerceToEnumerableAsync(CopValue value,
        CancellationToken cancellationToken = default) =>
        CollectionValues.EnumerateAsync(value, cancellationToken);

    private static bool PathMatchesGlob(string path, string pattern) => PathGlob.IsMatch(path, pattern);
}
