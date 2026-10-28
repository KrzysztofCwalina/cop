'use strict';

// Only the installed compiler is loaded. Target modules and tsconfig plugins are never executed.
const path = require('path');
const readline = require('readline');
let ts, unavailable;
let previousProgram;
const sourceCache = new Map();
const configCache = new Map();
try {
    ts = require(process.argv[2]);
    for (const api of ['createProgram', 'createSourceFile', 'canHaveDecorators', 'isSatisfiesExpression',
        'getParsedCommandLineOfConfigFile', 'isExpressionNode'])
        if (typeof ts[api] !== 'function') throw new Error(`Installed TypeScript lacks required API ${api}.`);
} catch (error) {
    unavailable = `Installed TypeScript compiler cannot be loaded: ${error.message}`;
}

function projectConfiguration(request) {
    const configPath = path.resolve(request.filePath);
    if (configCache.has(configPath)) return configCache.get(configPath);
    const errors = [];
    const config = ts.getParsedCommandLineOfConfigFile(configPath, {}, {
        ...ts.sys, onUnRecoverableConfigFileDiagnostic: error => errors.push(error)
    });
    if (config) errors.push(...config.errors.filter(e => e.code !== 18003));
    const result = {
        SourceFiles: config ? config.fileNames : [],
        References: config ? (config.projectReferences || []).map(r => ts.resolveProjectReferencePath(r)) : [],
        PathAliases: config ? config.options.paths || {} : {},
        BaseUrl: config ? config.options.baseUrl || null : null,
        Errors: errors.map(e => ts.flattenDiagnosticMessageText(e.messageText, '\n'))
    };
    Object.defineProperty(result, 'Options', { value: config ? config.options : {} });
    if (configCache.size >= 16) configCache.delete(configCache.keys().next().value);
    configCache.set(configPath, result);
    return result;
}

function findConfig(directory) {
    for (let current = directory; ; current = path.dirname(current)) {
        for (const name of ['tsconfig.json', 'jsconfig.json']) {
            const candidate = path.join(current, name);
            if (ts.sys.fileExists(candidate)) return candidate;
        }
        if (path.dirname(current) === current) return null;
    }
}

function project(request) {
    const fileName = path.resolve(request.filePath);
    const source = ts.createSourceFile(fileName, request.sourceText, ts.ScriptTarget.Latest, true);
    let options = {
        target: ts.ScriptTarget.Latest, module: ts.ModuleKind.NodeNext,
        moduleResolution: ts.ModuleResolutionKind.NodeNext, jsx: ts.JsxEmit.Preserve,
        allowJs: true, checkJs: true, noEmit: true, skipLibCheck: true
    };
    const configErrors = [];
    const configPath = findConfig(path.dirname(fileName));
    if (configPath) {
        const config = projectConfiguration({ filePath: configPath });
        configErrors.push(...config.Errors.map(messageText => ({ code: 0, messageText })));
        options = { ...options, ...config.Options, allowJs: true, checkJs: true, noEmit: true, skipLibCheck: true };
    }
    const host = ts.createCompilerHost(options);
    const originalGetSourceFile = host.getSourceFile.bind(host);
    host.getSourceFile = (name, languageVersion, ...args) => {
        if (path.resolve(name) === fileName) return source;
        const stamp = ts.sys.getModifiedTime(name)?.getTime();
        const cached = sourceCache.get(name);
        const version = JSON.stringify(languageVersion);
        if (cached && cached.stamp === stamp && cached.version === version) return cached.source;
        const parsed = originalGetSourceFile(name, languageVersion, ...args);
        if (parsed) {
            if (sourceCache.size >= 256) sourceCache.delete(sourceCache.keys().next().value);
            sourceCache.set(name, { stamp, version, source: parsed });
        }
        return parsed;
    };
    const program = ts.createProgram([fileName], options, host, previousProgram);
    previousProgram = program;
    const checker = program.getTypeChecker();
    const result = { Types: [], Methods: [], Statements: [], Usings: [], Imports: [], CommentLines: [], ParseErrors: [] };
    const text = node => node ? node.getText(source) : null;
    const line = node => source.getLineAndCharacterOfPosition(node.getStart(source)).line + 1;
    function span(node) {
        const start = source.getLineAndCharacterOfPosition(node.getStart(source));
        const end = source.getLineAndCharacterOfPosition(node.end);
        return { Line: start.line + 1, Column: start.character + 1, EndLine: end.line + 1, EndColumn: end.character + 1 };
    }
    const name = node => node.name ? (ts.isStringLiteralLike(node.name) ? node.name.text : node.name.getText()) : null;
    function namespace(node) {
        const parts = [];
        for (let parent = node.parent; parent; parent = parent.parent)
            if (ts.isModuleDeclaration(parent)) parts.unshift(name(parent));
        return parts.length ? parts.join('.') : null;
    }
    const has = (node, kind) => (ts.getModifiers(node) || []).some(m => m.kind === kind);
    const exported = node => has(node, ts.SyntaxKind.ExportKeyword) || has(node, ts.SyntaxKind.DefaultKeyword) ||
        ts.isVariableDeclaration(node) && ts.isVariableDeclarationList(node.parent) && exported(node.parent.parent) ||
        ts.isClassExpression(node) && ts.isVariableDeclaration(node.parent) && exported(node.parent);
    function modifiers(node, isMember = false) {
        let value = 0;
        if (has(node, ts.SyntaxKind.PrivateKeyword) || (node.name && ts.isPrivateIdentifier(node.name))) value |= 2;
        else if (has(node, ts.SyntaxKind.ProtectedKeyword)) value |= 4;
        else if (isMember || exported(node)) value |= 1;
        for (const [kind, flag] of [
            [ts.SyntaxKind.StaticKeyword, 16], [ts.SyntaxKind.AbstractKeyword, 64],
            [ts.SyntaxKind.AsyncKeyword, 256], [ts.SyntaxKind.OverrideKeyword, 512],
            [ts.SyntaxKind.ReadonlyKeyword, 1024]
        ]) if (has(node, kind)) value |= flag;
        return value;
    }
    const decorators = node => (ts.canHaveDecorators(node) ? ts.getDecorators(node) || [] : [])
        .map(d => text(ts.isCallExpression(d.expression) ? d.expression.expression : d.expression));
    const docs = node => node.jsDoc && node.jsDoc.length ? node.jsDoc.map(d => text(d)).join('\n') :
        ts.isVariableDeclaration(node) && ts.isVariableDeclarationList(node.parent) ? docs(node.parent.parent) : null;
    function typeReference(node) {
        if (!node) return null;
        const original = text(node);
        let typeName = original, namespace = null, args = [];
        if (ts.isTypeReferenceNode(node) || ts.isExpressionWithTypeArguments(node)) {
            typeName = text(node.typeName || node.expression);
            args = (node.typeArguments || []).map(typeReference);
            const dot = typeName.lastIndexOf('.');
            if (dot >= 0) { namespace = typeName.slice(0, dot); typeName = typeName.slice(dot + 1); }
        } else if (ts.isArrayTypeNode(node)) {
            typeName = 'Array'; args = [typeReference(node.elementType)];
        } else if (ts.isUnionTypeNode(node) || ts.isIntersectionTypeNode(node)) {
            typeName = ts.isUnionTypeNode(node) ? 'union' : 'intersection';
            args = node.types.map(typeReference);
        } else if (ts.isTupleTypeNode(node)) {
            typeName = 'tuple'; args = node.elements.map(typeReference);
        }
        return { Name: typeName, Namespace: namespace, GenericArguments: args, OriginalText: original };
    }
    function inferredType(type, dynamic = false) {
        if (!type || !dynamic && (type.flags & (ts.TypeFlags.Any | ts.TypeFlags.Unknown))) return null;
        const original = checker.typeToString(type, undefined, ts.TypeFormatFlags.NoTruncation);
        // Avoid source-less synthetic type nodes: preserve a structured annotation using a tiny parsed type.
        const temp = ts.createSourceFile('type.ts', 'type T = ' + original, ts.ScriptTarget.Latest, true);
        function convert(node) {
            if (!node) return null;
            const originalText = node.getText(temp);
            if (ts.isTypeReferenceNode(node)) {
                const qualified = node.typeName.getText(temp), dot = qualified.lastIndexOf('.');
                return {
                    Name: dot < 0 ? qualified : qualified.slice(dot + 1),
                    Namespace: dot < 0 ? null : qualified.slice(0, dot),
                    GenericArguments: (node.typeArguments || []).map(convert), OriginalText: originalText
                };
            }
            return { Name: originalText, Namespace: null, GenericArguments: [], OriginalText: originalText };
        }
        return convert(temp.statements[0] && temp.statements[0].type);
    }
    function expression(node, depth = 0) {
        if (!node || depth > 64) return { Kind: 'other' };
        const nested = n => expression(n, depth + 1);
        if (ts.isParenthesizedExpression(node) || ts.isAsExpression(node) ||
            ts.isTypeAssertionExpression(node) || ts.isNonNullExpression(node) ||
            ts.isSatisfiesExpression(node)) return nested(node.expression);
        if (ts.isIdentifier(node) || node.kind === ts.SyntaxKind.ThisKeyword || node.kind === ts.SyntaxKind.SuperKeyword)
            return { Kind: 'identifier', Name: text(node) };
        if (ts.isStringLiteralLike(node)) return { Kind: 'stringLiteral', StringValue: node.text };
        if (ts.isPropertyAccessExpression(node))
            return { Kind: 'memberAccess', Name: node.name.text, Target: nested(node.expression) };
        if (ts.isElementAccessExpression(node))
            return { Kind: 'other', Target: nested(node.expression), Children: [nested(node.argumentExpression)] };
        if (ts.isCallExpression(node) || ts.isNewExpression(node))
            return { Kind: 'invocation', Target: nested(node.expression),
                Children: (node.arguments || []).map(nested) };
        if (ts.isNumericLiteral(node)) return { Kind: 'numericLiteral', Name: node.text };
        if (node.kind === ts.SyntaxKind.TrueKeyword || node.kind === ts.SyntaxKind.FalseKeyword)
            return { Kind: 'booleanLiteral', Name: text(node) };
        if (node.kind === ts.SyntaxKind.NullKeyword) return { Kind: 'nullLiteral' };
        if (ts.isBinaryExpression(node)) return {
            Kind: 'other', Name: text(node.operatorToken), Children: [nested(node.left), nested(node.right)]
        };
        if (ts.isArrayLiteralExpression(node)) return { Kind: 'arrayLiteral', Children: node.elements.map(nested) };
        if (ts.isObjectLiteralExpression(node)) return {
            Kind: 'objectLiteral', Children: node.properties.map(p => p.initializer ? nested(p.initializer) : { Kind: 'other' })
        };
        if (ts.isAwaitExpression(node)) return { Kind: 'other', Target: nested(node.expression) };
        if (ts.isConditionalExpression(node)) return {
            Kind: 'other', Children: [nested(node.condition), nested(node.whenTrue), nested(node.whenFalse)]
        };
        const children = [];
        ts.forEachChild(node, child => { if (ts.isExpressionNode(child)) children.push(nested(child)); });
        return { Kind: 'other', Children: children };
    }
    function receiverType(call) {
        if (!ts.isNewExpression(call) && !ts.isPropertyAccessExpression(call.expression)) return null;
        const receiver = ts.isNewExpression(call) ? call : call.expression.expression;
        const type = checker.getTypeAtLocation(receiver);
        if (!(type.flags & (ts.TypeFlags.Any | ts.TypeFlags.Unknown))) {
            const symbol = type.aliasSymbol || type.getSymbol();
            if (symbol && !symbol.name.startsWith('__')) return symbol.name;
            if (type.flags & ts.TypeFlags.StringLike) return 'string';
            if (type.flags & ts.TypeFlags.NumberLike) return 'number';
        }
        // A syntactic receiver is not a resolved type. CallTarget retains its exact identity.
        return null;
    }
    function binding(node) {
        const call = ts.isCallExpression(node) || ts.isNewExpression(node);
        const value = call || ts.isAwaitExpression(node) ? node : node.initializer || node.expression;
        const target = call && (ts.isPropertyAccessExpression(node.expression) ||
            ts.isElementAccessExpression(node.expression)) ? node.expression.expression : null;
        const signature = call ? checker.getResolvedSignature(node) : null;
        const declaration = signature?.declaration;
        let callable = null;
        if (declaration && !source.parseDiagnostics.length) {
            const symbol = declaration.symbol;
            const parent = declaration.parent;
            const file = declaration.getSourceFile();
            const start = file.getLineAndCharacterOfPosition(declaration.getStart());
            const end = file.getLineAndCharacterOfPosition(declaration.end);
            callable = {
                Name: name(declaration) || (ts.isVariableDeclaration(parent) ? name(parent) : null) ||
                    symbol?.name || (ts.isConstructorDeclaration(declaration) ? 'constructor' : null),
                Signature: checker.signatureToString(signature, node, ts.TypeFormatFlags.NoTruncation),
                DeclaringType: parent && (ts.isClassLike(parent) || ts.isInterfaceDeclaration(parent)) ? name(parent) : null,
                Namespace: namespace(declaration),
                ReturnType: inferredType(checker.getReturnTypeOfSignature(signature)),
                Parameters: signature.getParameters().map(p => inferredType(checker.getTypeOfSymbolAtLocation(p, node), true)),
                SourceFile: file.fileName, Line: start.line + 1, Column: start.character + 1,
                EndLine: end.line + 1, EndColumn: end.character + 1
            };
            if (!callable.Name || callable.Parameters.some(p => !p)) callable = null;
        }
        return { ReceiverType: target ? inferredType(checker.getTypeAtLocation(target)) : null,
            ValueType: value ? inferredType(checker.getTypeAtLocation(value)) : null, ReferencedCallable: callable };
    }
    function implementedInterfaces(node, seen = new Set()) {
        const names = new Set();
        if (!node || seen.has(node)) return [];
        seen.add(node);
        for (const heritage of node.heritageClauses || []) for (const reference of heritage.types) {
            const type = checker.getTypeAtLocation(reference);
            const symbol = type.aliasSymbol || type.getSymbol();
            if (!symbol) continue;
            if (heritage.token === ts.SyntaxKind.ImplementsKeyword ||
                symbol.declarations?.some(ts.isInterfaceDeclaration)) names.add(symbol.name);
            for (const declaration of symbol.declarations || [])
                for (const inherited of implementedInterfaces(declaration, seen)) names.add(inherited);
        }
        return [...names];
    }
    let methodId = 0;
    function method(node, declaration = node, methodName = name(declaration), member = false) {
        const signature = checker.getSignatureFromDeclaration(node);
        const docComment = docs(declaration);
        const value = {
            Id: methodId++, Name: methodName || (has(node, ts.SyntaxKind.DefaultKeyword) ? 'default' : '<anonymous>'),
            Modifiers: modifiers(declaration, member) | modifiers(node),
            Decorators: decorators(declaration), ReturnType: typeReference(node.type) ||
                (signature ? inferredType(checker.getReturnTypeOfSignature(signature)) : null),
            Parameters: node.parameters.map(p => ({
                Name: text(p.name), Type: typeReference(p.type) || inferredType(checker.getTypeAtLocation(p)), IsVariadic: !!p.dotDotDotToken,
                IsKwargs: false, HasDefaultValue: !!p.initializer, DefaultValueText: text(p.initializer),
                IsOptional: !!p.questionToken || !!p.initializer, ...span(p)
            })),
            ...span(declaration), TypeParameters: (node.typeParameters || []).map(p => text(p.name)),
            HasDocComment: !!docComment, DocComment: docComment,
            IsGenerator: !!node.asteriskToken, IsArrow: ts.isArrowFunction(node),
            IsGetter: ts.isGetAccessorDeclaration(node), IsSetter: ts.isSetAccessorDeclaration(node)
        };
        return value;
    }
    function statement(node, kind, owner, parent, extra = {}) {
        const value = {
            Id: result.Statements.length, Kind: kind, Keywords: [], Arguments: [], ArgumentExpressions: [],
            ...span(node), MethodId: owner ? owner.Id : null, ParentId: parent ? parent.Id : null,
            IsBraced: !!node.statement && ts.isBlock(node.statement), ...binding(node), ...extra
        };
        result.Statements.push(value);
        return value;
    }
    const importModule = value => { if (value && !result.Usings.includes(value)) result.Usings.push(value); };
    function containsThrow(node) {
        if (ts.isFunctionLike(node)) return false;
        if (ts.isThrowStatement(node)) return true;
        return !!ts.forEachChild(node, containsThrow);
    }
    function visit(node, owner = null, parent = null, container = null) {
        if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) {
            if (node.moduleSpecifier && ts.isStringLiteralLike(node.moduleSpecifier)) {
                const module = node.moduleSpecifier.text;
                importModule(module);
                if (ts.isImportDeclaration(node)) {
                    const add = fields => result.Imports.push({ Module: module, ...span(node), ...fields });
                    if (!node.importClause) add({});
                    else {
                        if (node.importClause.name) add({ Name: 'default', Alias: node.importClause.name.text });
                        const bindings = node.importClause.namedBindings;
                        if (bindings && ts.isNamespaceImport(bindings))
                            add({ Alias: bindings.name.text, IsWildcard: true });
                        else if (bindings) for (const binding of bindings.elements)
                            add({ Name: (binding.propertyName || binding.name).text, Alias: binding.name.text });
                    }
                }
            }
        }
        if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference) &&
            node.moduleReference.expression && ts.isStringLiteralLike(node.moduleReference.expression))
            importModule(node.moduleReference.expression.text);
        if (ts.isClassDeclaration(node) || ts.isClassExpression(node) || ts.isInterfaceDeclaration(node) ||
            ts.isEnumDeclaration(node) || ts.isTypeAliasDeclaration(node)) {
            const docComment = docs(node);
            const type = {
                Name: name(node) || (node.parent && ts.isVariableDeclaration(node.parent) ? name(node.parent) : 'default'),
                Kind: ts.isTypeAliasDeclaration(node) ? 'Alias' :
                    ts.isEnumDeclaration(node) ? 'Enum' : ts.isInterfaceDeclaration(node) ? 'Interface' : 'Class',
                Namespace: namespace(node),
                Modifiers: modifiers(node), BaseTypes: (node.heritageClauses || []).flatMap(h => h.types.map(text)),
                Decorators: decorators(node), Constructors: [], Methods: [], NestedTypes: [], Fields: [], Properties: [],
                EnumValues: ts.isEnumDeclaration(node) ? node.members.map(name) : [], ...span(node),
                TypeParameters: (node.typeParameters || []).map(p => text(p.name)),
                AliasTarget: ts.isTypeAliasDeclaration(node) ? typeReference(node.type) : null,
                Interfaces: implementedInterfaces(node),
                HasDocComment: !!docComment, DocComment: docComment,
                IsExported: exported(node), IsAbstract: has(node, ts.SyntaxKind.AbstractKeyword),
                IsGeneric: !!node.typeParameters && node.typeParameters.length > 0,
                HasBaseClass: !ts.isInterfaceDeclaration(node) && (node.heritageClauses || [])
                    .some(h => h.token === ts.SyntaxKind.ExtendsKeyword),
                HasImplements: (node.heritageClauses || []).some(h => h.token === ts.SyntaxKind.ImplementsKeyword)
            };
            (container ? container.NestedTypes : result.Types).push(type);
            for (const decorator of ts.canHaveDecorators(node) ? ts.getDecorators(node) || [] : [])
                visit(decorator.expression, owner, parent, null);
            if (ts.isTypeAliasDeclaration(node)) {
                if (ts.isTypeLiteralNode(node.type)) for (const child of node.type.members) visit(child, null, null, type);
            } else if (!ts.isEnumDeclaration(node)) for (const child of node.members) visit(child, null, null, type);
            else for (const child of node.members) if (child.initializer) visit(child.initializer, owner, parent, container);
            return;
        }
        if (ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node) || ts.isArrowFunction(node) ||
            ts.isMethodDeclaration(node) || ts.isMethodSignature(node) || ts.isConstructorDeclaration(node) ||
            ts.isGetAccessorDeclaration(node) || ts.isSetAccessorDeclaration(node)) {
            let declaration = node;
            if ((ts.isArrowFunction(node) || ts.isFunctionExpression(node)) &&
                (ts.isVariableDeclaration(node.parent) || ts.isPropertyDeclaration(node.parent) ||
                    ts.isPropertyAssignment(node.parent))) declaration = node.parent;
            const isMember = !!container && (declaration.parent === node.parent ||
                ts.isPropertyDeclaration(declaration)) && !owner;
            const m = method(node, declaration, ts.isConstructorDeclaration(node) ? 'constructor' : name(declaration), isMember);
            if (isMember) {
                (ts.isConstructorDeclaration(node) ? container.Constructors : container.Methods).push(m);
                if (ts.isConstructorDeclaration(node)) for (const p of node.parameters) {
                    if (!(ts.getModifiers(p) || []).some(modifier =>
                        [ts.SyntaxKind.PublicKeyword, ts.SyntaxKind.PrivateKeyword,
                            ts.SyntaxKind.ProtectedKeyword, ts.SyntaxKind.ReadonlyKeyword].includes(modifier.kind))) continue;
                    container.Fields.push({
                        Name: text(p.name), Type: typeReference(p.type) || inferredType(checker.getTypeAtLocation(p)),
                        Modifiers: modifiers(p, true), Line: line(p)
                    });
                }
                if (m.IsGetter || m.IsSetter) {
                    let property = container.Properties.find(p => p.Name === m.Name);
                    if (!property) {
                        property = { Name: m.Name, Type: m.IsGetter ? m.ReturnType : m.Parameters[0]?.Type,
                            Modifiers: m.Modifiers, Line: m.Line, HasDocComment: m.HasDocComment, DocComment: m.DocComment };
                        container.Properties.push(property);
                    }
                    if (m.IsGetter) { property.HasGetter = true; property.Type = m.ReturnType; }
                    if (m.IsSetter) property.HasSetter = true;
                }
            } else result.Methods.push(m);
            // Parameters can contain calls in defaults and decorators.
            for (const decorator of ts.canHaveDecorators(declaration) ? ts.getDecorators(declaration) || [] : [])
                visit(decorator.expression, owner, parent, null);
            for (const p of node.parameters) {
                if (p.initializer) visit(p.initializer, m, null, null);
                for (const decorator of ts.canHaveDecorators(p) ? ts.getDecorators(p) || [] : [])
                    visit(decorator.expression, m, null, null);
            }
            if (node.body) visit(node.body, m, null, null);
            return;
        }
        if (container && (ts.isPropertyDeclaration(node) || ts.isPropertySignature(node))) {
            if (node.initializer && (ts.isArrowFunction(node.initializer) || ts.isFunctionExpression(node.initializer))) {
                visit(node.initializer, null, parent, container);
                return;
            }
            const value = {
                Name: name(node), Type: typeReference(node.type) || inferredType(checker.getTypeAtLocation(node)),
                Modifiers: modifiers(node, true), Line: line(node)
            };
            if (ts.isPropertySignature(node)) {
                const docComment = docs(node);
                container.Properties.push({ ...value, HasGetter: true, HasSetter: !has(node, ts.SyntaxKind.ReadonlyKeyword),
                    HasDocComment: !!docComment, DocComment: docComment });
            } else container.Fields.push(value);
            if (node.initializer) visit(node.initializer, owner, parent, null);
            return;
        }
        let current = parent;
        if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
            const callee = node.expression;
            let memberName = ts.isPropertyAccessExpression(callee) ? callee.name.text :
                ts.isIdentifier(callee) ? callee.text : ts.isElementAccessExpression(callee) &&
                    ts.isStringLiteralLike(callee.argumentExpression) ? callee.argumentExpression.text : null;
            current = statement(node, 'call', owner, parent, {
                TypeName: receiverType(node), MemberName: memberName, CallTarget: expression(callee),
                Arguments: (node.arguments || []).map(text), ArgumentExpressions: (node.arguments || []).map(n => expression(n)),
                ConstructedTypeInterfaces: ts.isNewExpression(node) ? (() => {
                    const symbol = checker.getTypeAtLocation(node).getSymbol();
                    return [...new Set((symbol?.declarations || []).flatMap(d => implementedInterfaces(d)))];
                })() : []
            });
            if ((ts.isIdentifier(callee) && callee.text === 'require' || callee.kind === ts.SyntaxKind.ImportKeyword) &&
                node.arguments && node.arguments[0] && ts.isStringLiteralLike(node.arguments[0]))
                importModule(node.arguments[0].text);
        } else if (ts.isVariableDeclaration(node)) {
            const declarationList = node.parent;
            const keyword = declarationList.flags & ts.NodeFlags.Const ? 'const' :
                declarationList.flags & ts.NodeFlags.Let ? 'let' : 'var';
            current = statement(node, 'declaration', owner, parent, {
                MemberName: name(node), Keywords: [keyword], Expression: text(node.initializer),
                TypeName: node.type ? text(node.type) : null, ValueExpression: node.initializer ? expression(node.initializer) : null
            });
        } else if (ts.isAwaitExpression(node)) {
            const type = checker.getTypeAtLocation(node.expression);
            const primitive = type.flags & ts.TypeFlags.NumberLike ? 'number' :
                type.flags & ts.TypeFlags.StringLike ? 'string' : type.flags & ts.TypeFlags.BooleanLike ? 'boolean' :
                    type.flags & ts.TypeFlags.Null ? 'null' : type.flags & ts.TypeFlags.Undefined ? 'undefined' : null;
            current = statement(node, 'await', owner, parent, {
                Expression: text(node.expression), ValueExpression: expression(node.expression), TypeName: primitive
            });
        } else if (ts.isIfStatement(node) || ts.isWhileStatement(node) || ts.isDoStatement(node) ||
            ts.isForStatement(node) || ts.isForOfStatement(node) || ts.isForInStatement(node) ||
            ts.isSwitchStatement(node)) {
            const kind = ts.isIfStatement(node) ? 'if' : ts.isWhileStatement(node) ? 'while' :
                ts.isDoStatement(node) ? 'do' : ts.isForOfStatement(node) ? 'for-of' :
                    ts.isForInStatement(node) ? 'for-in' : ts.isSwitchStatement(node) ? 'switch' : 'for';
            current = statement(node, kind, owner, parent, {
                Keywords: [kind.startsWith('for') ? 'for' : kind],
                Condition: text(node.condition || node.expression), IsBraced: !!node.statement &&
                    ts.isBlock(node.statement) || ts.isIfStatement(node) && ts.isBlock(node.thenStatement)
            });
        } else if (ts.isTryStatement(node)) {
            current = statement(node, 'try', owner, parent, { IsBraced: true });
        } else if (ts.isCatchClause(node)) {
            current = statement(node, 'catch', owner, parent, {
                IsErrorHandler: true, IsGenericErrorHandler: true, HasRethrow: containsThrow(node.block), IsBraced: true
            });
        } else if (ts.isReturnStatement(node) || ts.isThrowStatement(node)) {
            current = statement(node, ts.isReturnStatement(node) ? 'return' : 'throw', owner, parent, {
                Expression: text(node.expression),
                ValueExpression: node.expression ? expression(node.expression) : null,
                TypeName: node.expression && ts.isNewExpression(node.expression) ? receiverType(node.expression) : null
            });
        } else if (node.kind === ts.SyntaxKind.DebuggerStatement) {
            current = statement(node, 'call', owner, parent, { MemberName: 'debugger', Keywords: ['debugger'] });
        }
        ts.forEachChild(node, child => visit(child, owner, current, container));
    }
    visit(source);
    // Comment ranges come from the compiler scanner, not regexes that mistake strings/JSX for comments.
    const comments = new Set();
    function commentRanges(node) {
        for (const range of [
            ...(ts.getLeadingCommentRanges(request.sourceText, node.pos) || []),
            ...(ts.getTrailingCommentRanges(request.sourceText, node.end) || [])
        ]) {
            const start = source.getLineAndCharacterOfPosition(range.pos).line + 1;
            const end = source.getLineAndCharacterOfPosition(range.end).line + 1;
            for (let i = start; i <= end; i++) comments.add(i);
        }
        ts.forEachChild(node, commentRanges);
    }
    commentRanges(source);
    result.CommentLines = [...comments].sort((a, b) => a - b);
    for (const diagnostic of [...source.parseDiagnostics, ...configErrors]) {
        const location = diagnostic.file && diagnostic.start !== undefined ?
            diagnostic.file.getLineAndCharacterOfPosition(diagnostic.start) : { line: 0, character: 0 };
        result.ParseErrors.push(`${diagnostic.file ? diagnostic.file.fileName : configPath || fileName}` +
            `(${location.line + 1},${location.character + 1}): error TS${diagnostic.code}: ` +
            ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'));
    }
    return result;
}

readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', input => {
    try {
        const request = JSON.parse(input);
        process.stdout.write(JSON.stringify(unavailable ? { Unavailable: unavailable } :
            request.kind === 'project' ? projectConfiguration(request) : project(request)) + '\n');
    }
    catch (error) {
        process.stderr.write(String(error.stack || error) + '\n');
        process.exitCode = 1;
        process.stdin.destroy();
    }
});
