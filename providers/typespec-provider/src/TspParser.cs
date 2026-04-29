namespace TypeSpecProvider;

/// <summary>
/// Recursive descent parser for TypeSpec source files.
/// Produces a TspSpec containing models, operations, interfaces, enums, unions, scalars.
/// </summary>
public class TspParser
{
    private readonly List<TspToken> _tokens;
    private int _pos;
    private readonly List<string> _currentNamespace = [];

    public TspParser(List<TspToken> tokens)
    {
        _tokens = tokens;
    }

    public static TspSpec Parse(string source)
    {
        var lexer = new TspLexer(source);
        var tokens = lexer.Tokenize();
        var parser = new TspParser(tokens);
        return parser.ParseSpec();
    }

    public static TspSpec ParseFiles(string directory, string pattern = "*.tsp")
    {
        var spec = new TspSpec();
        if (!Directory.Exists(directory)) return spec;

        var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var fileSpec = Parse(source);
            spec.Namespaces.AddRange(fileSpec.Namespaces);
            spec.Models.AddRange(fileSpec.Models);
            spec.Operations.AddRange(fileSpec.Operations);
            spec.Interfaces.AddRange(fileSpec.Interfaces);
            spec.Enums.AddRange(fileSpec.Enums);
            spec.Unions.AddRange(fileSpec.Unions);
            spec.Scalars.AddRange(fileSpec.Scalars);
        }
        return spec;
    }

    private TspSpec ParseSpec()
    {
        var spec = new TspSpec();
        while (Current.Kind != TspTokenKind.EndOfFile)
        {
            ParseTopLevel(spec);
        }
        return spec;
    }

    private void ParseTopLevel(TspSpec spec)
    {
        // Collect decorators
        var decorators = ParseDecorators();

        switch (Current.Kind)
        {
            case TspTokenKind.Import:
                ParseImport();
                break;
            case TspTokenKind.Using:
                ParseUsing();
                break;
            case TspTokenKind.Namespace:
                ParseNamespace(spec, decorators);
                break;
            case TspTokenKind.Model:
                spec.Models.Add(ParseModel(decorators));
                break;
            case TspTokenKind.Op:
                spec.Operations.Add(ParseOperation(decorators, interfaceName: null));
                break;
            case TspTokenKind.Interface:
                spec.Interfaces.Add(ParseInterface(decorators));
                break;
            case TspTokenKind.Enum:
                spec.Enums.Add(ParseEnum(decorators));
                break;
            case TspTokenKind.Union:
                spec.Unions.Add(ParseUnion(decorators));
                break;
            case TspTokenKind.Scalar:
                spec.Scalars.Add(ParseScalar(decorators));
                break;
            case TspTokenKind.Alias:
                SkipAlias();
                break;
            case TspTokenKind.Extern:
            case TspTokenKind.Dec:
            case TspTokenKind.Fn:
            case TspTokenKind.Const:
                SkipDeclaration();
                break;
            default:
                Advance(); // skip unknown token
                break;
        }
    }

    private void ParseNamespace(TspSpec spec, List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Namespace);
        var name = ParseDottedName();

        var ns = new TspNamespace
        {
            Name = name,
            FullName = _currentNamespace.Count > 0
                ? string.Join(".", _currentNamespace) + "." + name
                : name,
            Decorators = decorators,
        };
        spec.Namespaces.Add(ns);

        // Namespace with body
        if (Current.Kind == TspTokenKind.OpenBrace)
        {
            _currentNamespace.Add(name);
            Expect(TspTokenKind.OpenBrace);
            while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
            {
                ParseTopLevel(spec);
            }
            Expect(TspTokenKind.CloseBrace);
            _currentNamespace.RemoveAt(_currentNamespace.Count - 1);
        }
        else
        {
            // File-level namespace (applies to rest of file)
            _currentNamespace.Add(name);
            ConsumeOptional(TspTokenKind.Semicolon);
        }
    }

    private TspModel ParseModel(List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Model);
        var name = ExpectIdentifier();

        var model = new TspModel
        {
            Name = name,
            Namespace = CurrentNamespace,
            Decorators = decorators,
        };

        // Template parameters: model Foo<T>
        if (Current.Kind == TspTokenKind.Identifier && Current.Value == "<" ||
            // Check for < as unknown token (lexer may not tokenize < separately)
            // Actually, < isn't in our punctuation set. Use angle bracket detection via identifier.
            false)
        {
            // Skip for now - template params are advanced feature
        }

        // Extends
        if (Current.Kind == TspTokenKind.Extends)
        {
            Advance();
            model.BaseModel = ParseTypeReference();
        }

        // Is (type alias with constraint)
        if (Current.Kind == TspTokenKind.Is)
        {
            Advance();
            model.BaseModel = ParseTypeReference();
        }

        if (Current.Kind == TspTokenKind.OpenBrace)
        {
            Expect(TspTokenKind.OpenBrace);
            while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
            {
                var propDecorators = ParseDecorators();

                // Spread: ...ModelName
                if (Current.Kind == TspTokenKind.DotDotDot)
                {
                    Advance();
                    var spreadType = ParseTypeReference();
                    // Record as a property with spread marker
                    model.Properties.Add(new TspProperty
                    {
                        Name = $"...{spreadType}",
                        Type = spreadType,
                        Decorators = propDecorators,
                    });
                    ConsumeOptional(TspTokenKind.Semicolon);
                    continue;
                }

                var prop = ParseProperty(propDecorators);
                if (prop is not null)
                {
                    model.Properties.Add(prop);
                }
            }
            Expect(TspTokenKind.CloseBrace);
        }
        else
        {
            ConsumeOptional(TspTokenKind.Semicolon);
        }

        return model;
    }

    private TspProperty? ParseProperty(List<TspDecorator> decorators)
    {
        if (Current.Kind == TspTokenKind.CloseBrace || Current.Kind == TspTokenKind.EndOfFile)
            return null;

        var name = ExpectIdentifier();
        bool optional = ConsumeOptional(TspTokenKind.QuestionMark);
        Expect(TspTokenKind.Colon);
        var type = ParseTypeReference();

        string? defaultValue = null;
        if (ConsumeOptional(TspTokenKind.Equals))
        {
            defaultValue = ParseValueLiteral();
        }

        ConsumeOptional(TspTokenKind.Semicolon);
        ConsumeOptional(TspTokenKind.Comma);

        return new TspProperty
        {
            Name = name,
            Type = type,
            Optional = optional,
            Default = defaultValue,
            Decorators = decorators,
        };
    }

    private TspOperation ParseOperation(List<TspDecorator> decorators, string? interfaceName)
    {
        Expect(TspTokenKind.Op);
        var name = ExpectIdentifier();

        var op = new TspOperation
        {
            Name = name,
            Namespace = CurrentNamespace,
            Interface = interfaceName,
            Decorators = decorators,
        };

        Expect(TspTokenKind.OpenParen);
        while (Current.Kind != TspTokenKind.CloseParen && Current.Kind != TspTokenKind.EndOfFile)
        {
            var paramDecorators = ParseDecorators();

            // Spread: ...ModelName
            if (Current.Kind == TspTokenKind.DotDotDot)
            {
                Advance();
                var spreadType = ParseTypeReference();
                op.Parameters.Add(new TspProperty
                {
                    Name = $"...{spreadType}",
                    Type = spreadType,
                    Decorators = paramDecorators,
                });
                ConsumeOptional(TspTokenKind.Comma);
                continue;
            }

            var paramName = ExpectIdentifier();
            bool paramOptional = ConsumeOptional(TspTokenKind.QuestionMark);
            Expect(TspTokenKind.Colon);
            var paramType = ParseTypeReference();

            op.Parameters.Add(new TspProperty
            {
                Name = paramName,
                Type = paramType,
                Optional = paramOptional,
                Decorators = paramDecorators,
            });
            ConsumeOptional(TspTokenKind.Comma);
        }
        Expect(TspTokenKind.CloseParen);

        // Return type
        if (ConsumeOptional(TspTokenKind.Colon))
        {
            op.ReturnType = ParseTypeReference();
        }

        ConsumeOptional(TspTokenKind.Semicolon);
        return op;
    }

    private TspInterface ParseInterface(List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Interface);
        var name = ExpectIdentifier();

        var iface = new TspInterface
        {
            Name = name,
            Namespace = CurrentNamespace,
            Decorators = decorators,
        };

        // Template parameters: interface Foo<T, U>
        if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == "<")
        {
            iface.TemplateParameters = ParseTemplateParameterNames();
        }

        // Extends: interface Foo extends Bar, Baz<T>
        if (Current.Kind == TspTokenKind.Extends)
        {
            Advance();
            iface.Extends.Add(ParseTypeReference());
            while (ConsumeOptional(TspTokenKind.Comma))
            {
                iface.Extends.Add(ParseTypeReference());
            }
        }

        Expect(TspTokenKind.OpenBrace);
        while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
        {
            var opDecorators = ParseDecorators();
            if (Current.Kind == TspTokenKind.Op)
            {
                iface.Operations.Add(ParseOperation(opDecorators, name));
            }
            else if (Current.Kind == TspTokenKind.Identifier || Current.Kind == TspTokenKind.CloseBrace)
            {
                // Shorthand operation (no 'op' keyword inside interface)
                if (Current.Kind == TspTokenKind.CloseBrace) break;
                iface.Operations.Add(ParseShorthandOperation(opDecorators, name));
            }
            else
            {
                Advance(); // skip unknown
            }
        }
        Expect(TspTokenKind.CloseBrace);

        return iface;
    }

    private TspOperation ParseShorthandOperation(List<TspDecorator> decorators, string interfaceName)
    {
        var name = ExpectIdentifier();

        var op = new TspOperation
        {
            Name = name,
            Namespace = CurrentNamespace,
            Interface = interfaceName,
            Decorators = decorators,
        };

        Expect(TspTokenKind.OpenParen);
        while (Current.Kind != TspTokenKind.CloseParen && Current.Kind != TspTokenKind.EndOfFile)
        {
            var paramDecorators = ParseDecorators();

            if (Current.Kind == TspTokenKind.DotDotDot)
            {
                Advance();
                var spreadType = ParseTypeReference();
                op.Parameters.Add(new TspProperty
                {
                    Name = $"...{spreadType}",
                    Type = spreadType,
                    Decorators = paramDecorators,
                });
                ConsumeOptional(TspTokenKind.Comma);
                continue;
            }

            var paramName = ExpectIdentifier();
            bool paramOptional = ConsumeOptional(TspTokenKind.QuestionMark);
            Expect(TspTokenKind.Colon);
            var paramType = ParseTypeReference();

            op.Parameters.Add(new TspProperty
            {
                Name = paramName,
                Type = paramType,
                Optional = paramOptional,
                Decorators = paramDecorators,
            });
            ConsumeOptional(TspTokenKind.Comma);
        }
        Expect(TspTokenKind.CloseParen);

        if (ConsumeOptional(TspTokenKind.Colon))
        {
            op.ReturnType = ParseTypeReference();
        }

        ConsumeOptional(TspTokenKind.Semicolon);
        return op;
    }

    private TspEnum ParseEnum(List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Enum);
        var name = ExpectIdentifier();

        var tspEnum = new TspEnum
        {
            Name = name,
            Namespace = CurrentNamespace,
            Decorators = decorators,
        };

        Expect(TspTokenKind.OpenBrace);
        while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
        {
            var memberDecorators = ParseDecorators();
            if (Current.Kind == TspTokenKind.CloseBrace) break;

            var memberName = Current.Kind == TspTokenKind.StringLiteral
                ? ExpectString()
                : ExpectIdentifier();

            string? value = null;
            if (ConsumeOptional(TspTokenKind.Colon))
            {
                value = ParseValueLiteral();
            }

            tspEnum.Members.Add(new TspEnumMember
            {
                Name = memberName,
                Value = value,
                Decorators = memberDecorators,
            });
            ConsumeOptional(TspTokenKind.Comma);
            ConsumeOptional(TspTokenKind.Semicolon);
        }
        Expect(TspTokenKind.CloseBrace);

        return tspEnum;
    }

    private TspUnion ParseUnion(List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Union);
        var name = ExpectIdentifier();

        var union = new TspUnion
        {
            Name = name,
            Namespace = CurrentNamespace,
            Decorators = decorators,
        };

        Expect(TspTokenKind.OpenBrace);
        while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
        {
            var variantDecorators = ParseDecorators();
            if (Current.Kind == TspTokenKind.CloseBrace) break;

            // Check if this is "name: type" or just "type"
            string variantName;
            string variantType;

            if (Peek(1).Kind == TspTokenKind.Colon)
            {
                variantName = ExpectIdentifier();
                Expect(TspTokenKind.Colon);
                variantType = ParseTypeReference();
            }
            else
            {
                variantType = ParseTypeReference();
                variantName = variantType;
            }

            union.Variants.Add(new TspUnionVariant
            {
                Name = variantName,
                Type = variantType,
            });
            ConsumeOptional(TspTokenKind.Comma);
            ConsumeOptional(TspTokenKind.Semicolon);
        }
        Expect(TspTokenKind.CloseBrace);

        return union;
    }

    private TspScalar ParseScalar(List<TspDecorator> decorators)
    {
        Expect(TspTokenKind.Scalar);
        var name = ExpectIdentifier();

        var scalar = new TspScalar
        {
            Name = name,
            Namespace = CurrentNamespace,
            Decorators = decorators,
        };

        if (Current.Kind == TspTokenKind.Extends)
        {
            Advance();
            scalar.BaseScalar = ParseTypeReference();
        }

        ConsumeOptional(TspTokenKind.Semicolon);
        return scalar;
    }

    // --- Decorators ---

    private List<TspDecorator> ParseDecorators()
    {
        var decorators = new List<TspDecorator>();
        while (Current.Kind == TspTokenKind.At)
        {
            decorators.Add(ParseDecorator());
        }
        return decorators;
    }

    private TspDecorator ParseDecorator()
    {
        Expect(TspTokenKind.At);
        var name = ParseDottedName();

        var decorator = new TspDecorator { Name = name };

        if (Current.Kind == TspTokenKind.OpenParen)
        {
            Advance();
            while (Current.Kind != TspTokenKind.CloseParen && Current.Kind != TspTokenKind.EndOfFile)
            {
                var arg = ParseDecoratorArgument();
                if (arg is not null)
                    decorator.Arguments.Add(arg);
                ConsumeOptional(TspTokenKind.Comma);
            }
            Expect(TspTokenKind.CloseParen);
        }

        return decorator;
    }

    private string? ParseDecoratorArgument()
    {
        if (Current.Kind == TspTokenKind.StringLiteral || Current.Kind == TspTokenKind.TripleQuoteString)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        if (Current.Kind == TspTokenKind.NumericLiteral)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        if (Current.Kind == TspTokenKind.True || Current.Kind == TspTokenKind.False)
        {
            var val = Current.Value;
            Advance();
            return val;
        }

        // Object literal: #{ ... }
        if (Current.Kind == TspTokenKind.Hash)
        {
            return ParseObjectLiteral();
        }

        // Type reference or identifier
        if (Current.Kind == TspTokenKind.Identifier ||
            IsKeywordUsedAsIdentifier(Current.Kind))
        {
            return ParseTypeReference();
        }

        // Skip unknown
        if (Current.Kind != TspTokenKind.CloseParen)
        {
            Advance();
            return null;
        }
        return null;
    }

    private string ParseObjectLiteral()
    {
        // #{ key: value, ... }
        Expect(TspTokenKind.Hash);
        Expect(TspTokenKind.OpenBrace);
        var parts = new List<string>();
        while (Current.Kind != TspTokenKind.CloseBrace && Current.Kind != TspTokenKind.EndOfFile)
        {
            var key = ExpectIdentifier();
            Expect(TspTokenKind.Colon);
            var value = ParseDecoratorArgument() ?? "";
            parts.Add($"{key}: {value}");
            ConsumeOptional(TspTokenKind.Comma);
        }
        Expect(TspTokenKind.CloseBrace);
        return "#{" + string.Join(", ", parts) + "}";
    }

    // --- Type References ---

    private string ParseTypeReference()
    {
        var name = ParseDottedName();

        // Generic: Type<Args>
        // The lexer doesn't tokenize < and > separately for generics,
        // so we handle it via Unknown_Token or special logic.
        // For simplicity, detect < as an unknown token.
        if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == "<")
        {
            name += ParseGenericArgs();
        }

        // Array: Type[]
        if (Current.Kind == TspTokenKind.OpenBracket && Peek(1).Kind == TspTokenKind.CloseBracket)
        {
            Advance(); Advance();
            name += "[]";
        }

        // Optional: Type?
        // (Don't consume ? here - it's consumed in property parsing)

        // Union in type position: A | B
        if (Current.Kind == TspTokenKind.Pipe)
        {
            var parts = new List<string> { name };
            while (ConsumeOptional(TspTokenKind.Pipe))
            {
                parts.Add(ParseTypeReferenceSingle());
            }
            name = string.Join(" | ", parts);
        }

        // Intersection: A & B
        if (Current.Kind == TspTokenKind.Ampersand)
        {
            var parts = new List<string> { name };
            while (ConsumeOptional(TspTokenKind.Ampersand))
            {
                parts.Add(ParseTypeReferenceSingle());
            }
            name = string.Join(" & ", parts);
        }

        return name;
    }

    private string ParseTypeReferenceSingle()
    {
        // Handle parenthesized type expressions
        if (Current.Kind == TspTokenKind.OpenParen)
        {
            Advance();
            var inner = ParseTypeReference();
            Expect(TspTokenKind.CloseParen);
            return $"({inner})";
        }

        var name = ParseDottedName();

        if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == "<")
        {
            name += ParseGenericArgs();
        }

        if (Current.Kind == TspTokenKind.OpenBracket && Peek(1).Kind == TspTokenKind.CloseBracket)
        {
            Advance(); Advance();
            name += "[]";
        }

        return name;
    }

    private string ParseGenericArgs()
    {
        // < already detected as Unknown_Token "<"
        Advance(); // skip <
        var args = new List<string>();
        int depth = 1;
        var current = new System.Text.StringBuilder();

        while (_pos < _tokens.Count && depth > 0)
        {
            if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == "<")
            {
                depth++;
                current.Append('<');
                Advance();
            }
            else if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == ">")
            {
                depth--;
                if (depth > 0)
                {
                    current.Append('>');
                    Advance();
                }
                else
                {
                    if (current.Length > 0)
                        args.Add(current.ToString().Trim());
                    Advance(); // skip final >
                }
            }
            else if (Current.Kind == TspTokenKind.Comma && depth == 1)
            {
                args.Add(current.ToString().Trim());
                current.Clear();
                Advance();
            }
            else
            {
                current.Append(Current.Value);
                if (Current.Kind == TspTokenKind.Dot) current.Append('.');
                Advance();
            }
        }

        return "<" + string.Join(", ", args) + ">";
    }

    /// <summary>
    /// Parses template parameter names from &lt;T, U, V&gt;.
    /// Returns just the bare parameter names (no constraints).
    /// </summary>
    private List<string> ParseTemplateParameterNames()
    {
        // < already detected as Unknown_Token "<"
        Advance(); // skip <
        var names = new List<string>();

        while (_pos < _tokens.Count)
        {
            if (Current.Kind == TspTokenKind.Unknown_Token && Current.Value == ">")
            {
                Advance(); // skip >
                break;
            }

            if (Current.Kind == TspTokenKind.Identifier)
            {
                names.Add(Current.Value!);
                Advance();

                // Skip optional constraint: extends Type or = DefaultType
                if (Current.Kind == TspTokenKind.Extends || Current.Kind == TspTokenKind.Equals)
                {
                    Advance();
                    ParseTypeReference(); // skip the constraint type
                }
            }

            ConsumeOptional(TspTokenKind.Comma);
        }

        return names;
    }

    // --- Helpers ---

    private void ParseImport()
    {
        Expect(TspTokenKind.Import);
        // Skip the import path
        if (Current.Kind == TspTokenKind.StringLiteral)
            Advance();
        ConsumeOptional(TspTokenKind.Semicolon);
    }

    private void ParseUsing()
    {
        Expect(TspTokenKind.Using);
        ParseDottedName();
        ConsumeOptional(TspTokenKind.Semicolon);
    }

    private void SkipAlias()
    {
        Advance(); // skip 'alias'
        // Skip until semicolon or end of statement
        while (Current.Kind != TspTokenKind.Semicolon && Current.Kind != TspTokenKind.EndOfFile)
            Advance();
        ConsumeOptional(TspTokenKind.Semicolon);
    }

    private void SkipDeclaration()
    {
        // Skip extern/dec/fn/const declarations
        while (Current.Kind != TspTokenKind.Semicolon &&
               Current.Kind != TspTokenKind.CloseBrace &&
               Current.Kind != TspTokenKind.EndOfFile)
        {
            if (Current.Kind == TspTokenKind.OpenBrace)
            {
                SkipBraceBlock();
                return;
            }
            Advance();
        }
        ConsumeOptional(TspTokenKind.Semicolon);
    }

    private void SkipBraceBlock()
    {
        Expect(TspTokenKind.OpenBrace);
        int depth = 1;
        while (depth > 0 && Current.Kind != TspTokenKind.EndOfFile)
        {
            if (Current.Kind == TspTokenKind.OpenBrace) depth++;
            if (Current.Kind == TspTokenKind.CloseBrace) depth--;
            Advance();
        }
    }

    private string ParseDottedName()
    {
        var name = ExpectIdentifier();
        while (Current.Kind == TspTokenKind.Dot)
        {
            Advance();
            name += "." + ExpectIdentifier();
        }
        return name;
    }

    private string ParseValueLiteral()
    {
        if (Current.Kind == TspTokenKind.StringLiteral || Current.Kind == TspTokenKind.TripleQuoteString)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        if (Current.Kind == TspTokenKind.NumericLiteral)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        if (Current.Kind == TspTokenKind.True || Current.Kind == TspTokenKind.False)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        // identifier or type ref as default
        return ParseTypeReference();
    }

    private string? CurrentNamespace => _currentNamespace.Count > 0 ? string.Join(".", _currentNamespace) : null;

    private TspToken Current => _pos < _tokens.Count ? _tokens[_pos] : new TspToken(TspTokenKind.EndOfFile, "", 0, 0);

    private TspToken Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : new TspToken(TspTokenKind.EndOfFile, "", 0, 0);
    }

    private void Advance() => _pos++;

    private void Expect(TspTokenKind kind)
    {
        if (Current.Kind == kind)
            Advance();
        // Silently skip on mismatch — we're lenient
    }

    private bool ConsumeOptional(TspTokenKind kind)
    {
        if (Current.Kind == kind)
        {
            Advance();
            return true;
        }
        return false;
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind == TspTokenKind.Identifier || IsKeywordUsedAsIdentifier(Current.Kind))
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        // Recovery: return the token value and advance
        var fallback = Current.Value;
        if (Current.Kind != TspTokenKind.EndOfFile)
            Advance();
        return fallback;
    }

    private string ExpectString()
    {
        if (Current.Kind == TspTokenKind.StringLiteral || Current.Kind == TspTokenKind.TripleQuoteString)
        {
            var val = Current.Value;
            Advance();
            return val;
        }
        return ExpectIdentifier();
    }

    private static bool IsKeywordUsedAsIdentifier(TspTokenKind kind)
    {
        // TypeSpec allows many keywords in identifier positions
        return kind is TspTokenKind.Model or TspTokenKind.Op or TspTokenKind.Interface
            or TspTokenKind.Enum or TspTokenKind.Union or TspTokenKind.Scalar
            or TspTokenKind.Namespace or TspTokenKind.Void or TspTokenKind.Never
            or TspTokenKind.Unknown or TspTokenKind.Is;
    }
}
