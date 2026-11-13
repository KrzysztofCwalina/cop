// Rejects source-text approximations in checks unless explicitly approved.

using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using Expression = Cop.Lang.Ast.Expression;
using IdentifierExpr = Cop.Lang.Ast.IdentifierExpr;
using CallExpr = Cop.Lang.Ast.CallExpr;
using LiteralExpr = Cop.Lang.Ast.LiteralExpr;
using BinaryExpr = Cop.Lang.Ast.BinaryExpr;
using ConditionalExpr = Cop.Lang.Ast.ConditionalExpr;
using MatchExpr = Cop.Lang.Ast.MatchExpr;
using MatchArm = Cop.Lang.Ast.MatchArm;

namespace Cop.Cli.Commands;

/// <summary>Conservative authoring policy, separate from the general-purpose language type system.</summary>
internal sealed class StructuralCheckPolicy
{
    private sealed record Entry(Declaration Declaration, string File, string Source, bool Local);

    private static readonly HashSet<string> s_rawMembers = new(StringComparer.Ordinal)
    {
        "Source", "Text", "PreviousText", "NextText", "Content", "ContentHash",
        "Condition", "Expression", "Arguments", "Signature", "ApiAsText", "DefaultValue",
        "Documentation", "Message"
    };

    private static readonly HashSet<string> s_messageFunctions = new(StringComparer.Ordinal)
    {
        "toError", "toWarning", "toInfo", "toViolation"
    };
    private static readonly HashSet<string> s_textOperations = IntrinsicRegistry.NameSet(op =>
        op.Kind is IntrinsicKind.StringPredicate or IntrinsicKind.StringTransform or IntrinsicKind.StringProperty);

    private readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exceptions;
    private readonly HashSet<Entry> _approvedEntries = [];
    private readonly HashSet<(Entry Entry, bool Reporting, bool Approved)> _visited = [];
    private readonly List<CopDiagnostic> _diagnostics = [];
    private readonly SemanticModel _model;

    private StructuralCheckPolicy(
        IReadOnlyList<(ModuleNode Module, string FilePath, string Source)> local,
        IReadOnlyList<(ModuleNode Module, string FilePath, string Source)> imported,
        IEnumerable<string> exceptions)
    {
        _exceptions = new(exceptions, StringComparer.Ordinal);
        HashSet<string> localPaths = new(local.Select(f => Path.GetFullPath(f.FilePath)),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        (ModuleNode Module, string FilePath, string Source)[] importedOnly =
            imported.Where(f => !localPaths.Contains(Path.GetFullPath(f.FilePath))).ToArray();
        _model = SemanticModel.Build(local.Select(f => f.Module).Concat(importedOnly.Select(f => f.Module)));
        Add(local, true);
        Add(importedOnly, false);
    }

    internal static IReadOnlyList<CopDiagnostic> Check(
        IReadOnlyList<(ModuleNode Module, string FilePath, string Source)> local,
        IReadOnlyList<(ModuleNode Module, string FilePath, string Source)> imported,
        IEnumerable<string> exceptions)
    {
        StructuralCheckPolicy policy = new StructuralCheckPolicy(local, imported, exceptions);
        policy.Check();
        return policy._diagnostics;
    }

    private void Add(IEnumerable<(ModuleNode Module, string FilePath, string Source)> files, bool local)
    {
        foreach ((ModuleNode? module, string? file, string? source) in files)
        {
            foreach (Declaration declaration in module.Declarations)
            {
                if (NameOf(declaration) is { } name)
                {
                    if (!_entries.TryGetValue(name, out List<Entry>? entries))
                    {
                        _entries[name] = entries = [];
                    }

                    entries.Add(new(declaration, file, source, local));
                }
                if (declaration is TypeDecl type)
                {
                    foreach (PropertyDecl? property in type.Properties.Where(p => p.ComputedExpr is not null))
                    {
                        if (!_entries.TryGetValue(property.Name, out List<Entry>? properties))
                        {
                            _entries[property.Name] = properties = [];
                        }

                        properties.Add(new(new FunctionDecl(property.Name,
                            [new("item", new(type.Name))], property.Type,
                            new ExpressionBody(property.ComputedExpr!), Line: property.Line), file, source, local));
                    }
                }
            }
        }
    }

    private void Check()
    {
        foreach (string name in _exceptions)
        {
            if (!_entries.TryGetValue(name, out List<Entry>? entries))
            {
                _diagnostics.Add(new(CopDiagnosticSeverity.Error,
                    $"Unknown text-check exception '{name}'. " +
                    "Name a check binding or predicate in this program or its imports; " +
                    "wildcards are not supported."));
                continue;
            }

            List<Entry> localEntries = entries.Where(e => e.Local).ToList();
            if (localEntries.Count > 0)
            {
                _approvedEntries.UnionWith(localEntries);
            }
            else if (entries.Select(e => e.File).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                _approvedEntries.UnionWith(entries);
            }
            else
            {
                _diagnostics.Add(new(CopDiagnosticSeverity.Error,
                    $"Ambiguous text-check exception '{name}' is defined by multiple imports. " +
                    "Approve a uniquely named check instead."));
            }
        }

        List<Entry> local = _entries.Values.SelectMany(e => e).Where(e => e.Local).ToList();
        HashSet<string> referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry? entry in local)
        {
            ReferenceCollector references = new ReferenceCollector();
            references.Visit(entry.Declaration);
            referenced.UnionWith(references.Names);
        }

        // Start at consumers, so a named exception covers its private helpers but not other callers.
        foreach (Entry? entry in local.Where(e => !referenced.Contains(NameOf(e.Declaration)!)))
        {
            VisitEntry(entry, reporting: false, approved: false);
        }

        foreach (Entry? entry in local)
        {
            if (!_visited.Any(v => ReferenceEquals(v.Entry, entry)))
            {
                VisitEntry(entry, reporting: false, approved: false);
            }
        }
    }

    private void VisitEntry(Entry entry, bool reporting, bool approved)
    {
        approved |= _approvedEntries.Contains(entry);
        if (!_visited.Add((entry, reporting, approved)))
        {
            return;
        }

        new Walker(this, entry, reporting, approved).Visit(entry.Declaration);
    }

    private void Follow(string name, bool reporting, bool approved)
    {
        if (_entries.TryGetValue(name, out List<Entry>? entries))
        {
            // Resolve conservatively across overloads/namespaces rather than missing a text-reading helper.
            foreach (Entry entry in entries)
            {
                VisitEntry(entry, reporting || (!entry.Local && name == "CHECK"), approved);
            }
        }
    }

    private static string? NameOf(Declaration declaration) => declaration switch
    {
        LetDecl l => l.Name,
        FunctionDecl f => f.Name,
        CommandDecl c => c.Name,
        _ => null
    };

    private sealed class ReferenceCollector : AstWalker
    {
        internal HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            if (node.Body is BlockBody block)
            {
                foreach (Statement statement in block.Statements)
                {
                    Visit(statement);
                }
            }

            return base.VisitFunctionDecl(node);
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is ForEachExpr loop)
            {
                Visit(loop.Loop);
            }

            return null;
        }
        public override object? VisitIdentifierExpr(IdentifierExpr node)
        {
            Names.Add(node.Name);
            return null;
        }
        public override object? VisitMemberExpr(MemberExpr node)
        {
            Names.Add(node.Member);
            return base.VisitMemberExpr(node);
        }
    }

    private sealed class Walker(StructuralCheckPolicy policy, Entry entry, bool reporting, bool approved) : AstWalker
    {
        private Dictionary<string, TypeInfo> _locals = new(StringComparer.Ordinal);
        private bool _reporting = reporting;
        private bool _implicitReceiver;
        private readonly HashSet<(int Line, string Member)> _reported = [];

        private TypeInfo? Infer(Expression expression) => policy._model.InferExpressionType(expression, _locals);

        private bool IsRawMember(string member, Expression? receiver = null)
        {
            TypeInfo? type = receiver is null
                ? (_locals.TryGetValue("item", out TypeInfo item) ? item : null)
                : Infer(receiver);
            return s_rawMembers.Contains(member) &&
                !(member == "Content" && type is { Name: { } name } &&
                    name.TrimEnd('?') == "Comment");
        }

        private void CheckTextConversion(Expression expression, bool requireKnown = false)
        {
            TypeInfo? type = expression is IdentifierExpr id && _locals.TryGetValue(id.Name, out TypeInfo local)
                ? local : Infer(expression);
            if (type is { } inferred && inferred.Name.TrimEnd('?') is "Line" or "Api" or "TypeReference")
            {
                Reject(expression, $"implicit {inferred.Name} text conversion");
            }
            else if (type is null && requireKnown && !_reported.Any(r => r.Line == expression.Line))
            {
                Reject(expression, "unresolved text conversion (add a semantic type annotation)");
            }
        }

        private void Reject(AstNode node, string member)
        {
            if (approved || _reporting || !_reported.Add((node.Line, member)))
            {
                return;
            }

            policy._diagnostics.Add(new(CopDiagnosticSeverity.Error,
                $"Strict structural check: '{member}' reads raw text instead of parsed code facts. " +
                "Use Types, Methods, Statements, Calls, modifiers, names, or parent/child relationships. " +
                "If the model lacks the required fact, report the gap and ASK THE USER before writing a text-based check. " +
                $"After approval, use --allow-text-check {NameOf(entry.Declaration)} for the named check only.",
                entry.File, node.Line, SourceLine: ParseException.GetSourceLine(entry.Source, node.Line)));
        }

        private void InScope(TypeInfo? item, Action action)
        {
            Dictionary<string, TypeInfo> saved = _locals;
            _locals = new(saved, StringComparer.Ordinal);
            if (item is { } type)
            {
                _locals["item"] = new(type.Name.TrimEnd('?'), false);
            }

            action();
            _locals = saved;
        }

        public override object? VisitFunctionDecl(FunctionDecl node)
        {
            foreach (Parameter parameter in node.Params)
            {
                if (parameter.Type is { } type)
                {
                    _locals[parameter.Name] = new(type.Name.TrimEnd('?'), type.IsCollection);
                }
            }

            if (node.Params.FirstOrDefault()?.Type is { } first)
            {
                _locals["item"] = new(first.Name.TrimEnd('?'), first.IsCollection);
            }

            if (node.Body is BlockBody block)
            {
                foreach (Statement statement in block.Statements)
                {
                    Visit(statement);
                }
            }

            return base.VisitFunctionDecl(node);
        }

        public override object? VisitIdentifierExpr(IdentifierExpr node)
        {
            if (node.Name == "read")
            {
                Reject(node, "read()");
            }

            if (IsRawMember(node.Name) && !_locals.ContainsKey(node.Name) &&
                !policy._entries.ContainsKey(node.Name))
            {
                Reject(node, node.Name);
            }

            if (!_locals.ContainsKey(node.Name))
            {
                policy.Follow(node.Name, _reporting, approved);
            }

            return null;
        }

        public override object? VisitMemberExpr(MemberExpr node)
        {
            if (IsRawMember(node.Member, node.Object))
            {
                Reject(node, node.Member);
            }

            if (s_textOperations.Contains(node.Member))
            {
                CheckTextConversion(node.Object, requireKnown: true);
            }

            Visit(node.Object);
            // Qualified package functions and computed property helpers are followed as well.
            policy.Follow(node.Member, _reporting, approved);
            return null;
        }

        public override object? VisitIndexExpr(IndexExpr node)
        {
            if (node.Index is LiteralExpr { Value: string key })
            {
                if (IsRawMember(key, node.Object))
                {
                    Reject(node, key);
                }
            }
            else if (Infer(node.Object) is not { IsCollection: true } and not { Name: "string" })
            {
                Reject(node, "dynamic object indexing");
            }
            return base.VisitIndexExpr(node);
        }

        public override object? VisitFilterExpr(FilterExpr node)
        {
            bool savedReporting = _reporting;
            _reporting = false;
            Visit(node.Collection);
            string? name = node.Predicate switch
            {
                IdentifierExpr id => id.Name,
                CallExpr { Callee: IdentifierExpr id } => id.Name,
                _ => null
            };
            if (name is not null && (s_textOperations.Contains(name) || name is "text" or "string" ||
                policy._model.Callable(name)?.ParamTypes.FirstOrDefault() == "string"))
            {
                CheckTextConversion(node.Collection, requireKnown: true);
            }

            InScope(Infer(node.Collection), () =>
            {
                bool saved = _implicitReceiver;
                _implicitReceiver = true;
                Visit(node.Predicate);
                _implicitReceiver = saved;
            });
            _reporting = savedReporting;
            return null;
        }

        public override object? VisitCallExpr(CallExpr node)
        {
            string? name = node.Callee switch
            {
                IdentifierExpr id => id.Name,
                MemberExpr member => member.Member,
                _ => null
            };
            if (name == "read")
            {
                Reject(node, "read()");
            }

            if (name is "text" or "string")
            {
                foreach (Expression argument in node.Args)
                {
                    CheckTextConversion(argument, requireKnown: true);
                }
            }

            if (name == "Get" && node.Args.FirstOrDefault() is { } key)
            {
                if (key is LiteralExpr { Value: string field })
                {
                    if (IsRawMember(field, node.Callee is MemberExpr member ? member.Object : null))
                    {
                        Reject(node, field);
                    }
                }
                else
                {
                    Reject(node, "dynamic object field lookup");
                }
            }
            Visit(node.Callee);
            TypeInfo? receiver = node.Callee is MemberExpr m ? Infer(m.Object) : null;
            int messageIndex = _implicitReceiver || node.Callee is MemberExpr ? 0 : 1;
            bool implicitReceiver = _implicitReceiver;
            _implicitReceiver = false;
            InScope(receiver, () =>
            {
                for (int i = 0; i < node.Args.Count; i++)
                {
                    bool saved = _reporting;
                    _reporting = name is not null && s_messageFunctions.Contains(name) &&
                        (!policy._entries.TryGetValue(name, out List<Entry>? overloads) || !overloads.Any(e => e.Local)) &&
                        i == messageIndex;
                    if (name is not null && policy._model.Callable(name) is { } callable)
                    {
                        int parameterIndex = i + (messageIndex == 0 ? 1 : 0);
                        if (parameterIndex < callable.ParamTypes.Count && callable.ParamTypes[parameterIndex] == "string")
                        {
                            CheckTextConversion(node.Args[i], requireKnown: true);
                        }
                    }
                    Visit(node.Args[i]);
                    _reporting = saved;
                }
            });
            _implicitReceiver = implicitReceiver;
            return null;
        }

        public override object? VisitInterpolatedStringExpr(InterpolatedStringExpr node)
        {
            foreach (ExpressionPart part in node.Parts.OfType<ExpressionPart>())
            {
                CheckTextConversion(part.Expr);
            }

            return base.VisitInterpolatedStringExpr(node);
        }

        public override object? VisitBinaryExpr(BinaryExpr node)
        {
            bool savedReporting = _reporting;
            if (node.Op != BinaryOp.Add)
            {
                _reporting = false;
            }

            bool nullCheck = node.Op is BinaryOp.Equal or BinaryOp.NotEqual &&
                (node.Left is LiteralExpr { Value: null } ||
                 node.Right is LiteralExpr { Value: null });
            if (!nullCheck)
            {
                CheckTextConversion(node.Left);
                CheckTextConversion(node.Right);
            }
            base.VisitBinaryExpr(node);
            _reporting = savedReporting;
            return null;
        }

        public override object? VisitConditionalExpr(ConditionalExpr node)
        {
            bool savedReporting = _reporting;
            _reporting = false;
            Visit(node.Condition);
            _reporting = savedReporting;
            Visit(node.Then);
            Visit(node.Else);
            return null;
        }

        public override object? VisitMatchExpr(MatchExpr node)
        {
            bool savedReporting = _reporting;
            _reporting = false;
            Visit(node.Discriminant);
            _reporting = savedReporting;
            foreach (MatchArm arm in node.Arms)
            {
                Visit(arm.Body);
            }

            return null;
        }

        public override object? VisitObjectExpr(ObjectExpr node)
        {
            foreach (FieldInit field in node.Fields)
            {
                bool saved = _reporting;
                _reporting |= node.TypeHint == "Violation" && field.Name is "Message" or "Source";
                Visit(field.Value);
                _reporting = saved;
            }
            return null;
        }

        public override object? VisitLetStatement(LetStatement node)
        {
            bool savedReporting = _reporting;
            _reporting = false;
            Visit(node.Value);
            _reporting = savedReporting;
            if (node.TypeAnnotation is { } annotation)
            {
                _locals[node.Name] = new(annotation.Name, annotation.IsCollection);
            }
            else if (Infer(node.Value) is { } type)
            {
                _locals[node.Name] = type;
            }

            return null;
        }

        public override object? VisitLambdaExpr(LambdaExpr node)
        {
            Dictionary<string, TypeInfo> saved = _locals;
            _locals = new(saved, StringComparer.Ordinal);
            foreach (Parameter parameter in node.Params)
            {
                if (parameter.Type is { } type)
                {
                    _locals[parameter.Name] = new(type.Name, type.IsCollection);
                }
                else if (parameter == node.Params[0] && _locals.TryGetValue("item", out TypeInfo item))
                {
                    _locals[parameter.Name] = item;
                }
            }

            Visit(node.Body);
            _locals = saved;
            return null;
        }

        public override object? VisitForEachStatement(ForEachStatement node)
        {
            bool savedReporting = _reporting;
            _reporting = false;
            Visit(node.Collection);
            InScope(Infer(node.Collection), () =>
            {
                if (_locals.TryGetValue("item", out TypeInfo type))
                {
                    _locals[node.Variable] = type;
                }

                foreach (Statement statement in node.Body)
                {
                    _reporting = statement is ExpressionStatement;
                    Visit(statement);
                }
            });
            _reporting = savedReporting;
            return null;
        }

        protected override object? DefaultVisit(AstNode node)
        {
            if (node is ForEachExpr loop)
            {
                Visit(loop.Loop);
            }

            return null;
        }
    }
}
