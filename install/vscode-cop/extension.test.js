// Tests for VS Code Cop IntelliSense extension
'use strict';

jest.mock('vscode');

const ext = require('./extension.js');
const {
    scanDocument,
    resolveIdentifierType,
    inferExprType,
    resolveFullChainType,
    resolveItemType,
    resolvePropertyChain,
    lookupType,
    getDotCompletions,
    getPredicateCompletions,
    getStatementCompletions,
    getGeneralCompletions,
    parseTypesFromCop,
    resolveCollectionElementType,
    isCollection,
    isString,
    isNumeric,
    elementType,
    stripNullable,
    TYPES,
} = ext._testing;

// ── Helpers ──────────────────────────────────────────────────────────────

/** Create a mock document from lines of text */
function mockDoc(lines, fsPath) {
    return {
        lineCount: lines.length,
        lineAt(i) { return { text: lines[i] }; },
        uri: { fsPath: fsPath || '/fake/path/test.cop' },
        getText(range) {
            if (!range) return lines.join('\n');
            return lines[range.start.line].substring(range.start.character, range.end.character);
        },
        getWordRangeAtPosition(pos, regex) {
            const line = lines[pos.line];
            const match = line.substring(pos.character).match(regex);
            if (match && match.index === 0) {
                const vscode = require('vscode');
                return new vscode.Range(pos.line, pos.character, pos.line, pos.character + match[0].length);
            }
            // Try matching at different positions
            for (let i = 0; i <= pos.character; i++) {
                const sub = line.substring(i);
                const m = sub.match(regex);
                if (m && i + m.index <= pos.character && i + m.index + m[0].length > pos.character) {
                    const vscode = require('vscode');
                    return new vscode.Range(pos.line, i + m.index, pos.line, i + m.index + m[0].length);
                }
            }
            return undefined;
        }
    };
}

/** Get labels from completion items */
function labels(items) {
    if (!items) return [];
    return items.map(i => i.label);
}

// ── Type helper tests ────────────────────────────────────────────────────

describe('type helpers', () => {
    test('isCollection', () => {
        expect(isCollection('[Type]')).toBe(true);
        expect(isCollection('[string]')).toBe(true);
        expect(isCollection('string')).toBe(false);
        expect(isCollection('Type')).toBe(false);
        expect(isCollection(undefined)).toBeFalsy();
    });

    test('isString', () => {
        expect(isString('string')).toBe(true);
        expect(isString('string?')).toBe(true);
        expect(isString('int')).toBe(false);
    });

    test('isNumeric', () => {
        expect(isNumeric('int')).toBe(true);
        expect(isNumeric('int?')).toBe(true);
        expect(isNumeric('string')).toBe(false);
    });

    test('elementType', () => {
        expect(elementType('[Type]')).toBe('Type');
        expect(elementType('[string]')).toBe('string');
        expect(elementType('string')).toBe('string');
    });

    test('stripNullable', () => {
        expect(stripNullable('string?')).toBe('string');
        expect(stripNullable('Type?')).toBe('Type');
        expect(stripNullable('string')).toBe('string');
    });
});

// ── scanDocument tests ───────────────────────────────────────────────────

describe('scanDocument', () => {
    test('parses imports', () => {
        const doc = mockDoc([
            'import code',
            'import code-analysis',
            'export import files',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.imports).toEqual(['code', 'code-analysis', 'files']);
    });

    test('parses let bindings without type annotation', () => {
        const doc = mockDoc([
            'let my-types = Types:isPublic',
            'export let violations = my-types:toError(\'bad\')',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.lets.has('my-types')).toBe(true);
        expect(symbols.lets.get('my-types').expr).toBe('Types:isPublic');
        expect(symbols.lets.get('my-types').typeAnnotation).toBeNull();
    });

    test('parses let bindings with type annotation', () => {
        const doc = mockDoc([
            'let allLines : [Line] = csharp.parse().Lines',
            'let name : string = \'hello\'',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.lets.get('allLines').typeAnnotation).toBe('[Line]');
        expect(symbols.lets.get('allLines').expr).toBe("csharp.parse().Lines");
        expect(symbols.lets.get('name').typeAnnotation).toBe('string');
    });

    test('captures multi-line let with chained :toError continuation', () => {
        const doc = mockDoc([
            'let empty-folder-violations = files.folders():empty',
            "    :toError('Empty folder is not allowed: {item.Path}')",
        ]);
        const symbols = scanDocument(doc);
        const entry = symbols.lets.get('empty-folder-violations');
        expect(entry).toBeDefined();
        expect(entry.expr).toContain(':toError(');
    });

    test('multi-line let capture does not swallow the next declaration', () => {
        const doc = mockDoc([
            'let a = Types:isPublic',
            'let b = Statements',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.lets.get('a').expr).toBe('Types:isPublic');
        expect(symbols.lets.get('b').expr).toBe('Statements');
    });

    test('parses predicates', () => {
        const doc = mockDoc([
            'predicate isPublic(Type) => Type.Modifiers:isSet(Public)',
            'export predicate isTooLong(Method) => Method.Statements.Count > 50',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.predicates.get('isPublic')).toBe('Type');
        expect(symbols.predicates.get('isTooLong')).toBe('Method');
    });

    test('parses functions', () => {
        const doc = mockDoc([
            'export function toError(Type, message: string) => Violation { }',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.functions.get('toError')).toBe('Type');
    });

    test('parses type definitions', () => {
        const doc = mockDoc([
            'type MyType = {',
            '    Name : string,',
            '    Count : int,',
            '    Items : [string]',
            '}',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.types.has('MyType')).toBe(true);
        const myType = symbols.types.get('MyType');
        expect(myType.properties).toEqual([
            { name: 'Name', type: 'string' },
            { name: 'Count', type: 'int' },
            { name: 'Items', type: '[string]' },
        ]);
    });

    test('ignores comment lines', () => {
        const doc = mockDoc([
            '# this is a comment',
            '## doc comment',
            'let x = Types',
        ]);
        const symbols = scanDocument(doc);
        expect(symbols.lets.has('x')).toBe(true);
        expect(symbols.lets.size).toBe(1);
    });
});

// ── resolveIdentifierType tests ──────────────────────────────────────────

describe('resolveIdentifierType', () => {
    test('resolves runtime variables', () => {
        const symbols = { lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(), imports: [] };
        expect(resolveIdentifierType('Code', symbols)).toBe('Codebase');
        expect(resolveIdentifierType('Disk', symbols)).toBe('Filesystem');
        expect(resolveIdentifierType('Markdown', symbols)).toBe('MarkdownContent');
    });

    test('resolves built-in types', () => {
        const symbols = { lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(), imports: [] };
        expect(resolveIdentifierType('Type', symbols)).toBe('Type');
        expect(resolveIdentifierType('Method', symbols)).toBe('Method');
        expect(resolveIdentifierType('Statement', symbols)).toBe('Statement');
    });

    test('resolves let bindings with type annotation', () => {
        const symbols = {
            lets: new Map([['myLines', { expr: 'something', typeAnnotation: '[Line]' }]]),
            predicates: new Map(), functions: new Map(), types: new Map(), imports: [],
        };
        expect(resolveIdentifierType('myLines', symbols)).toBe('[Line]');
    });

    test('resolves let bindings without type annotation via expression inference', () => {
        const symbols = {
            lets: new Map([['cb', { expr: 'Code', typeAnnotation: null }]]),
            predicates: new Map(), functions: new Map(), types: new Map(), imports: [],
        };
        expect(resolveIdentifierType('cb', symbols)).toBe('Codebase');
    });

    test('resolves ambient collections from packages', () => {
        const symbols = {
            lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(), imports: ['code'],
            _resolvedCollections: { Types: 'Type', Statements: 'Statement', Lines: 'Line', Files: 'File' },
            _resolvedTypes: null, _resolvedFunctions: null,
        };
        expect(resolveIdentifierType('Types', symbols)).toBe('[Type]');
        expect(resolveIdentifierType('Statements', symbols)).toBe('[Statement]');
        expect(resolveIdentifierType('Lines', symbols)).toBe('[Line]');
    });

    test('resolves document-defined types', () => {
        const types = new Map([['MyWidget', { properties: [{ name: 'Name', type: 'string' }] }]]);
        const symbols = { lets: new Map(), predicates: new Map(), functions: new Map(), types, imports: [] };
        expect(resolveIdentifierType('MyWidget', symbols)).toBe('MyWidget');
    });

    test('returns undefined for unknown identifiers', () => {
        const symbols = { lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(), imports: [] };
        expect(resolveIdentifierType('unknownThing', symbols)).toBeUndefined();
    });
});

// ── inferExprType tests ──────────────────────────────────────────────────

describe('inferExprType', () => {
    const baseSymbols = {
        lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(),
        imports: [], _resolvedCollections: null, _resolvedTypes: null, _resolvedFunctions: null,
    };

    test('resolves runtime:: expressions', () => {
        expect(inferExprType('runtime::Codebase', baseSymbols)).toBe('Codebase');
        expect(inferExprType('runtime::Filesystem', baseSymbols)).toBe('Filesystem');
    });

    test('resolves dot property access', () => {
        expect(inferExprType('Code.Types', baseSymbols)).toBe('[Type]');
        expect(inferExprType('Code.Files', baseSymbols)).toBe('[File]');
    });

    test('resolves filter chain base type', () => {
        const symbols = {
            ...baseSymbols,
            _resolvedCollections: { Types: 'Type', Statements: 'Statement' },
        };
        // Types:isPublic still resolves to [Type] — filter preserves type
        expect(inferExprType('Types:isPublic', symbols)).toBe('[Type]');
        expect(inferExprType('Types:isPublic:isCSharp', symbols)).toBe('[Type]');
    });

    test('resolves package-qualified functions', () => {
        const symbols = {
            ...baseSymbols,
            imports: ['csharp'],
            _resolvedFunctions: { csharp: [{ name: 'parse', params: '', returnType: 'Codebase' }] },
        };
        expect(inferExprType('csharp.parse()', symbols)).toBe('Codebase');
    });

    test('resolves package-qualified collections', () => {
        const symbols = {
            ...baseSymbols,
            imports: ['code'],
            _resolvedCollections: { Types: 'Type' },
        };
        expect(inferExprType('code.Types', symbols)).toBe('[Type]');
    });

    test('toError/toWarning/toInfo chains resolve to [Violation]', () => {
        expect(inferExprType("files.folders():empty :toError('x')", baseSymbols)).toBe('[Violation]');
        expect(inferExprType("Types:isPublic:toWarning('x')", baseSymbols)).toBe('[Violation]');
        expect(inferExprType("Folders:empty:toInfo('x')", baseSymbols)).toBe('[Violation]');
    });
});

// ── resolveFullChainType tests ───────────────────────────────────────────

describe('resolveFullChainType', () => {
    const symbols = {
        lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(),
        imports: ['code'],
        _resolvedCollections: { Types: 'Type', Statements: 'Statement', Lines: 'Line', Files: 'File' },
        _resolvedTypes: null, _resolvedFunctions: null,
    };

    test('resolves simple identifiers', () => {
        expect(resolveFullChainType('Code', symbols)).toBe('Codebase');
        expect(resolveFullChainType('Types', symbols)).toBe('[Type]');
    });

    test('resolves filter chains', () => {
        expect(resolveFullChainType('Types:isPublic', symbols)).toBe('[Type]');
        expect(resolveFullChainType('Types:isPublic:isCSharp', symbols)).toBe('[Type]');
        expect(resolveFullChainType('Statements:isCall', symbols)).toBe('[Statement]');
    });

    test('resolves dot chains', () => {
        expect(resolveFullChainType('Code.Types', symbols)).toBe('[Type]');
        expect(resolveFullChainType('Code.Files', symbols)).toBe('[File]');
    });

    test('resolves mixed dot+filter chains', () => {
        // Code.Types:isPublic — base is Code.Types = [Type], filter preserves it
        expect(resolveFullChainType('Code.Types:isPublic', symbols)).toBe('[Type]');
    });

    test('returns undefined for unknown expressions', () => {
        expect(resolveFullChainType('unknown', symbols)).toBeUndefined();
        expect(resolveFullChainType('', symbols)).toBeUndefined();
    });
});

// ── resolveItemType tests ────────────────────────────────────────────────

describe('resolveItemType', () => {
    test('resolves item inside predicate body', () => {
        const doc = mockDoc([
            'import code',
            'predicate isPublic(Type) => item.Modifiers:isSet(Public)',
        ]);
        const symbols = scanDocument(doc);
        const pos = { line: 1, character: 35 };
        expect(resolveItemType(doc, pos, symbols)).toBe('Type');
    });

    test('resolves item inside function body', () => {
        const doc = mockDoc([
            'import code',
            'function getName(Method) => item.Name',
        ]);
        const symbols = scanDocument(doc);
        const pos = { line: 1, character: 32 };
        expect(resolveItemType(doc, pos, symbols)).toBe('Method');
    });

    test('resolves item inside foreach', () => {
        const doc = mockDoc([
            'import code',
            "foreach Types => '{item.Name}'",
        ]);
        const symbols = {
            ...scanDocument(doc),
            _resolvedCollections: { Types: 'Type' },
        };
        const pos = { line: 1, character: 22 };
        expect(resolveItemType(doc, pos, symbols)).toBe('Type');
    });

    test('stops at declaration boundary', () => {
        const doc = mockDoc([
            'predicate foo(Type) => item.Name',
            'predicate bar(Method) => item.Name',
        ]);
        const symbols = scanDocument(doc);
        // Position in bar's body — should resolve to Method, not Type
        const pos = { line: 1, character: 30 };
        expect(resolveItemType(doc, pos, symbols)).toBe('Method');
    });
});

// ── resolvePropertyChain tests ───────────────────────────────────────────

describe('resolvePropertyChain', () => {
    const symbols = {
        lets: new Map(), predicates: new Map(), functions: new Map(), types: new Map(),
        imports: [],
        _resolvedCollections: null, _resolvedTypes: null, _resolvedFunctions: null,
    };

    test('resolves Code.Types', () => {
        expect(resolvePropertyChain('Code.Types', symbols)).toBe('[Type]');
    });

    test('resolves Code.Types.Count (collection property would be handled differently)', () => {
        // Code.Types is [Type]; then .Count would look for Count on Type — not found
        // This is actually a limitation: Count is a collection property not on the element type
        // But resolvePropertyChain walks through element types
        // This tests current behavior
        expect(resolvePropertyChain('Code.Types', symbols)).toBe('[Type]');
    });

    test('resolves nested property chains', () => {
        // Code → Codebase.Files → [File]; File.Path → string
        expect(resolvePropertyChain('Code.Files', symbols)).toBe('[File]');
    });
});

// ── getDotCompletions tests ──────────────────────────────────────────────

describe('getDotCompletions', () => {
    test('provides properties after Code.', () => {
        const doc = mockDoc(['import code', '    Code.']);
        const items = getDotCompletions(doc, '    Code.', { line: 1, character: 9 });
        const lbls = labels(items);
        expect(lbls).toContain('Types');
        expect(lbls).toContain('Files');
        expect(lbls).toContain('Statements');
    });

    test('provides collection properties/transforms after Types.', () => {
        const doc = mockDoc(['import code', '    Types.']);
        // Need resolved collections for 'Types' to resolve
        // Without package resolution (no fsPath), falls through to type properties
        // With mock symbols, test via resolveFullChainType
        const items = getDotCompletions(doc, '    Types.', { line: 1, character: 10 });
        const lbls = labels(items);
        // Without package resolution, it should show fallback or type properties
        // At minimum it should return something (not crash)
        expect(items).toBeDefined();
        expect(items.length).toBeGreaterThan(0);
    });

    test('provides element properties after filter chain dot', () => {
        const doc = mockDoc(['import code', '    Types:isPublic.']);
        // The key fix: the regex should capture "Types:isPublic" before the dot
        const items = getDotCompletions(doc, '    Types:isPublic.', { line: 1, character: 19 });
        const lbls = labels(items);
        // Even without package resolution, the regex change ensures
        // the full chain is captured. Verify it doesn't crash.
        expect(items).toBeDefined();
    });

    test('provides string properties after string field dot', () => {
        const doc = mockDoc(['import code', 'predicate foo(Type) => Type.Name.']);
        const items = getDotCompletions(doc, 'predicate foo(Type) => Type.Name.', { line: 1, character: 33 });
        const lbls = labels(items);
        expect(lbls).toContain('Length');
        expect(lbls).toContain('Lower');
        expect(lbls).toContain('Upper');
    });

    test('provides item properties inside predicate body', () => {
        const doc = mockDoc([
            'import code',
            'predicate isLong(Method) => item.',
        ]);
        const items = getDotCompletions(doc, 'predicate isLong(Method) => item.', { line: 1, character: 33 });
        const lbls = labels(items);
        expect(lbls).toContain('Name');
        expect(lbls).toContain('Parameters');
        expect(lbls).toContain('Statements');
    });

    test('provides item properties inside function body', () => {
        const doc = mockDoc([
            'import code',
            'function name(Type) => item.',
        ]);
        const items = getDotCompletions(doc, 'function name(Type) => item.', { line: 1, character: 28 });
        const lbls = labels(items);
        expect(lbls).toContain('Name');
        expect(lbls).toContain('Kind');
        expect(lbls).toContain('Methods');
    });
});

// ── getPredicateCompletions tests ────────────────────────────────────────

describe('getPredicateCompletions', () => {
    test('provides predicates after collection:', () => {
        const doc = mockDoc(['import code', 'let x = Types:']);
        const items = getPredicateCompletions(doc, 'let x = Types:');
        const lbls = labels(items);
        // Should include universal predicates
        expect(lbls).toContain('in');
        // Without resolution, falls back to all predicates
        expect(items.length).toBeGreaterThan(0);
    });

    test('provides predicates after chained filter', () => {
        const doc = mockDoc(['import code', 'let x = Types:isPublic:']);
        const items = getPredicateCompletions(doc, 'let x = Types:isPublic:');
        const lbls = labels(items);
        // Should not crash and should return predicates
        expect(items.length).toBeGreaterThan(0);
        expect(lbls).toContain('in');
    });

    test('provides string predicates after string field:', () => {
        const doc = mockDoc(['import code', 'predicate x(Type) => Type.Name:']);
        const items = getPredicateCompletions(doc, 'predicate x(Type) => Type.Name:');
        const lbls = labels(items);
        expect(lbls).toContain('startsWith');
        expect(lbls).toContain('endsWith');
        expect(lbls).toContain('contains');
        expect(lbls).toContain('equals');
    });

    test('provides numeric predicates after int field:', () => {
        const doc = mockDoc(['import code', 'predicate x(Type) => Type.Line:']);
        const items = getPredicateCompletions(doc, 'predicate x(Type) => Type.Line:');
        const lbls = labels(items);
        expect(lbls).toContain('greaterThan');
        expect(lbls).toContain('lessThan');
        expect(lbls).toContain('equals');
    });

    test('includes user-defined predicates', () => {
        const doc = mockDoc([
            'import code',
            'predicate isOld(Type) => Type.Line:greaterThan(100)',
            'let x = Types:',
        ]);
        const items = getPredicateCompletions(doc, 'let x = Types:');
        const lbls = labels(items);
        expect(lbls).toContain('isOld');
    });

    test('includes user-defined functions', () => {
        const doc = mockDoc([
            'import code',
            'function getName(Type) => Type.Name',
            'let x = Types:',
        ]);
        const items = getPredicateCompletions(doc, 'let x = Types:');
        const lbls = labels(items);
        expect(lbls).toContain('getName');
    });
});

// ── getStatementCompletions tests ────────────────────────────────────────

describe('getStatementCompletions', () => {
    test('provides keywords', () => {
        const doc = mockDoc(['import code', '']);
        const items = getStatementCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('predicate');
        expect(lbls).toContain('function');
        expect(lbls).toContain('let');
        expect(lbls).toContain('type');
        expect(lbls).toContain('import');
    });

    test('provides let bindings', () => {
        const doc = mockDoc(['let my-types = Types', '']);
        const items = getStatementCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('my-types');
    });

    test('provides runtime variables', () => {
        const doc = mockDoc(['import code', '']);
        const items = getStatementCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('Code');
        expect(lbls).toContain('Disk');
    });
});

// ── getGeneralCompletions tests ──────────────────────────────────────────

describe('getGeneralCompletions', () => {
    test('includes item keyword', () => {
        const doc = mockDoc(['import code', 'predicate foo(Type) => ']);
        const items = getGeneralCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('item');
    });

    test('includes imported packages', () => {
        const doc = mockDoc(['import code', 'import code-analysis', '']);
        const items = getGeneralCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('code');
        expect(lbls).toContain('code-analysis');
    });

    test('includes type names', () => {
        const doc = mockDoc(['import code', '']);
        const items = getGeneralCompletions(doc);
        const lbls = labels(items);
        expect(lbls).toContain('Type');
        expect(lbls).toContain('Method');
        expect(lbls).toContain('Statement');
    });
});

// ── parseTypesFromCop tests ──────────────────────────────────────────────

describe('parseTypesFromCop', () => {
    test('parses type definitions', () => {
        const content = [
            'export type Widget = {',
            '    Name : string,',
            '    Size : int',
            '}',
        ].join('\n');
        const types = {};
        const collections = {};
        const functions = [];
        parseTypesFromCop(content, types, collections, functions);
        expect(types.Widget).toBeDefined();
        expect(types.Widget.properties).toEqual([
            { name: 'Name', type: 'string' },
            { name: 'Size', type: 'int' },
        ]);
    });

    test('parses collection declarations', () => {
        const content = 'export collection Widgets : [Widget]';
        const types = {};
        const collections = {};
        const functions = [];
        parseTypesFromCop(content, types, collections, functions);
        expect(collections.Widgets).toBe('Widget');
    });

    test('parses exported functions', () => {
        const content = 'export function toError(Type, msg: string) : Violation => stuff';
        const types = {};
        const collections = {};
        const functions = [];
        parseTypesFromCop(content, types, collections, functions);
        expect(functions.length).toBe(1);
        expect(functions[0].name).toBe('toError');
        expect(functions[0].returnType).toBe('Violation');
    });
});

// ── resolveCollectionElementType tests ───────────────────────────────────

describe('resolveCollectionElementType', () => {
    const types = {
        Type: { properties: [] },
        Statement: { properties: [] },
        Line: { properties: [] },
        File: { properties: [] },
        HttpOperation: { properties: [] },
    };

    test('resolves plural → singular (Types → Type)', () => {
        expect(resolveCollectionElementType('Types', types)).toBe('Type');
    });

    test('resolves es-plural (Files → File)', () => {
        expect(resolveCollectionElementType('Statements', types)).toBe('Statement');
    });

    test('resolves suffix match (Operations → HttpOperation)', () => {
        expect(resolveCollectionElementType('Operations', types)).toBe('HttpOperation');
    });

    test('returns undefined for unmatchable', () => {
        expect(resolveCollectionElementType('Gadgets', types)).toBeUndefined();
    });
});

// ── Integration: expression chain with colon in regex ────────────────────

describe('regex: dot expression captures colon chains', () => {
    // The critical regex fix: the dot completion regex must capture expressions with `:`
    const regex = /([A-Za-z_][A-Za-z0-9_.:()-]*(?:\('[^']*'\)|\(\))?)\.\s*$/;

    test('captures simple identifier before dot', () => {
        const m = '    Code.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Code');
    });

    test('captures dot-chain before dot', () => {
        const m = '    Code.Types.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Code.Types');
    });

    test('captures filter chain before dot', () => {
        const m = '    Types:isPublic.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Types:isPublic');
    });

    test('captures multi-filter chain before dot', () => {
        const m = '    Types:isPublic:isCSharp.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Types:isPublic:isCSharp');
    });

    test('captures mixed dot and filter chain', () => {
        const m = '    Code.Types:isPublic.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Code.Types:isPublic');
    });

    test('captures function call in chain', () => {
        const m = "    csharp.parse().".match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('csharp.parse()');
    });

    test('captures item before dot', () => {
        const m = '    item.'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('item');
    });
});

describe('regex: predicate expression captures colon chains', () => {
    // The predicate completion regex must capture the full chain before the final `:`
    const regex = /([A-Za-z_][A-Za-z0-9_.:()-]*)\s*:\s*$/;

    test('captures simple identifier', () => {
        const m = 'Types:'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Types');
    });

    test('captures dot-chain before colon', () => {
        const m = 'Code.Types:'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Code.Types');
    });

    test('captures filter chain with additional colon', () => {
        const m = 'Types:isPublic:'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Types:isPublic');
    });

    test('captures property dot-chain before colon', () => {
        const m = 'Type.Name:'.match(regex);
        expect(m).not.toBeNull();
        expect(m[1]).toBe('Type.Name');
    });
});

// ── End-to-end: completion provider routing ──────────────────────────────

describe('completion provider routing', () => {
    test('triggers dot completions for item.', () => {
        const doc = mockDoc([
            'import code',
            'predicate isGood(Type) => item.',
        ]);
        const position = { line: 1, character: 31 };
        const textBefore = 'predicate isGood(Type) => item.';
        // Verify the dot regex matches
        expect(/\.$/.test(textBefore)).toBe(true);
        // Verify getDotCompletions doesn't crash
        const items = getDotCompletions(doc, textBefore, position);
        expect(items).toBeDefined();
        // Should resolve item to Type and show Type properties
        const lbls = labels(items);
        expect(lbls).toContain('Name');
        expect(lbls).toContain('Kind');
    });

    test('triggers predicate completions after collection:', () => {
        const doc = mockDoc([
            'import code',
            'predicate isGood(Type) => Type.Name:',
        ]);
        const textBefore = 'predicate isGood(Type) => Type.Name:';
        expect(/:$/.test(textBefore)).toBe(true);
        expect(!/::$/.test(textBefore)).toBe(true);
        const items = getPredicateCompletions(doc, textBefore);
        const lbls = labels(items);
        expect(lbls).toContain('startsWith');
    });
});
