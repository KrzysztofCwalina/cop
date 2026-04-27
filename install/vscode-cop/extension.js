// @ts-check
/// <reference types="vscode" />
'use strict';

const vscode = require('vscode');

// ── Type and property definitions ──────────────────────────────────────────

/** @type {Record<string, {properties: {name:string, type:string}[]}>} */
const TYPES = {
    Type: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Kind', type: 'string' },
        { name: 'Modifiers', type: 'int' },
        { name: 'BaseTypes', type: '[string]' },
        { name: 'Constructors', type: '[Method]' },
        { name: 'Methods', type: '[Method]' },
        { name: 'MethodNames', type: '[string]' },
        { name: 'NestedTypes', type: '[Type]' },
        { name: 'EnumValues', type: '[string]' },
        { name: 'Decorators', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Documented', type: 'bool' },
        { name: 'Fields', type: '[Field]' },
        { name: 'Properties', type: '[Property]' },
        { name: 'Events', type: '[Event]' },
    ]},
    Method: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Modifiers', type: 'int' },
        { name: 'ReturnType', type: 'TypeReference?' },
        { name: 'Parameters', type: '[Parameter]' },
        { name: 'Statements', type: '[Statement]' },
        { name: 'Decorators', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'Documented', type: 'bool' },
    ]},
    Constructor: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Modifiers', type: 'int' },
        { name: 'ReturnType', type: 'TypeReference?' },
        { name: 'Parameters', type: '[Parameter]' },
        { name: 'Statements', type: '[Statement]' },
        { name: 'Decorators', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'Documented', type: 'bool' },
    ]},
    Field: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'TypeReference?' },
        { name: 'Modifiers', type: 'int' },
        { name: 'Line', type: 'int' },
    ]},
    Property: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'TypeReference?' },
        { name: 'Modifiers', type: 'int' },
        { name: 'HasGetter', type: 'bool' },
        { name: 'HasSetter', type: 'bool' },
        { name: 'Documented', type: 'bool' },
        { name: 'Line', type: 'int' },
    ]},
    Event: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'TypeReference?' },
        { name: 'Modifiers', type: 'int' },
        { name: 'Line', type: 'int' },
    ]},
    Api: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'TypeName', type: 'string' },
        { name: 'MemberName', type: 'string' },
        { name: 'Signature', type: 'string' },
        { name: 'Line', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    Parameter: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'TypeReference?' },
        { name: 'Variadic', type: 'bool' },
        { name: 'Kwargs', type: 'bool' },
        { name: 'Defaulted', type: 'bool' },
        { name: 'DefaultValue', type: 'string?' },
        { name: 'Line', type: 'int' },
    ]},
    TypeReference: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Generic', type: 'bool' },
        { name: 'GenericArguments', type: '[TypeReference]' },
        { name: 'Length', type: 'int' },
    ]},
    Statement: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'Keywords', type: '[string]' },
        { name: 'TypeName', type: 'string?' },
        { name: 'MemberName', type: 'string?' },
        { name: 'Arguments', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'InMethod', type: 'bool' },
        { name: 'Rethrows', type: 'bool' },
        { name: 'Generic', type: 'bool' },
        { name: 'ErrorHandler', type: 'bool' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Method', type: 'Method?' },
        { name: 'Parent', type: 'Statement?' },
        { name: 'Children', type: '[Statement]' },
        { name: 'Ancestors', type: '[Statement]' },
        { name: 'Condition', type: 'string?' },
        { name: 'Expression', type: 'string?' },
    ]},
    Call: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'Keywords', type: '[string]' },
        { name: 'TypeName', type: 'string?' },
        { name: 'MemberName', type: 'string?' },
        { name: 'Arguments', type: '[string]' },
        { name: 'Signature', type: 'string' },
        { name: 'Line', type: 'int' },
        { name: 'InMethod', type: 'bool' },
        { name: 'Rethrows', type: 'bool' },
        { name: 'Generic', type: 'bool' },
        { name: 'ErrorHandler', type: 'bool' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Method', type: 'Method?' },
        { name: 'Parent', type: 'Statement?' },
        { name: 'Children', type: '[Statement]' },
        { name: 'Ancestors', type: '[Statement]' },
        { name: 'Condition', type: 'string?' },
        { name: 'Expression', type: 'string?' },
    ]},
    Declaration: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'Keywords', type: '[string]' },
        { name: 'TypeName', type: 'string?' },
        { name: 'MemberName', type: 'string?' },
        { name: 'Arguments', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'InMethod', type: 'bool' },
        { name: 'Rethrows', type: 'bool' },
        { name: 'Generic', type: 'bool' },
        { name: 'ErrorHandler', type: 'bool' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Method', type: 'Method?' },
        { name: 'Parent', type: 'Statement?' },
        { name: 'Children', type: '[Statement]' },
        { name: 'Ancestors', type: '[Statement]' },
        { name: 'Condition', type: 'string?' },
        { name: 'Expression', type: 'string?' },
    ]},
    ErrorHandler: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'Keywords', type: '[string]' },
        { name: 'TypeName', type: 'string?' },
        { name: 'MemberName', type: 'string?' },
        { name: 'Arguments', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'InMethod', type: 'bool' },
        { name: 'Rethrows', type: 'bool' },
        { name: 'Generic', type: 'bool' },
        { name: 'ErrorHandler', type: 'bool' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Method', type: 'Method?' },
        { name: 'Parent', type: 'Statement?' },
        { name: 'Children', type: '[Statement]' },
        { name: 'Ancestors', type: '[Statement]' },
        { name: 'Condition', type: 'string?' },
        { name: 'Expression', type: 'string?' },
    ]},
    Attribute: { properties: [
        { name: 'Kind', type: 'string' },
        { name: 'Keywords', type: '[string]' },
        { name: 'TypeName', type: 'string?' },
        { name: 'MemberName', type: 'string?' },
        { name: 'Arguments', type: '[string]' },
        { name: 'Line', type: 'int' },
        { name: 'InMethod', type: 'bool' },
        { name: 'Rethrows', type: 'bool' },
        { name: 'Generic', type: 'bool' },
        { name: 'ErrorHandler', type: 'bool' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
        { name: 'Method', type: 'Method?' },
        { name: 'Parent', type: 'Statement?' },
        { name: 'Children', type: '[Statement]' },
        { name: 'Ancestors', type: '[Statement]' },
        { name: 'Condition', type: 'string?' },
        { name: 'Expression', type: 'string?' },
    ]},
    Line: { properties: [
        { name: 'Text', type: 'string' },
        { name: 'Number', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    File: { properties: [
        { name: 'Path', type: 'string' },
        { name: 'Language', type: 'string?' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Usings', type: '[string]' },
        { name: 'Types', type: '[Type]' },
    ]},
    Codebase: { properties: [
        { name: 'Files', type: '[File]' },
        { name: 'Types', type: '[Type]' },
        { name: 'Statements', type: '[Statement]' },
        { name: 'Lines', type: '[Line]' },
    ]},
    Folder: { properties: [
        { name: 'Path', type: 'string' },
        { name: 'Name', type: 'string' },
        { name: 'Empty', type: 'bool' },
        { name: 'FileCount', type: 'int' },
        { name: 'SubfolderCount', type: 'int' },
        { name: 'Depth', type: 'int' },
        { name: 'MinutesSinceModified', type: 'int' },
    ]},
    DiskFile: { properties: [
        { name: 'Path', type: 'string' },
        { name: 'Name', type: 'string' },
        { name: 'Extension', type: 'string' },
        { name: 'Size', type: 'int' },
        { name: 'Folder', type: 'string' },
        { name: 'Depth', type: 'int' },
        { name: 'MinutesSinceModified', type: 'int' },
    ]},
    Filesystem: { properties: [
        { name: 'Folders', type: '[Folder]' },
        { name: 'Files', type: '[DiskFile]' },
    ]},
};

// ── Completion catalogs ────────────────────────────────────────────────────

const Kind = vscode.CompletionItemKind;

const STRING_PREDICATES = [
    { label: 'equals', detail: '(value) — case-insensitive equality', kind: Kind.Method },
    { label: 'notEquals', detail: '(value) — case-insensitive inequality', kind: Kind.Method },
    { label: 'startsWith', detail: '(value) — prefix match', kind: Kind.Method },
    { label: 'endsWith', detail: '(value) — suffix match', kind: Kind.Method },
    { label: 'contains', detail: '(value) — substring match', kind: Kind.Method },
    { label: 'containsAny', detail: '(list) — any list item is a substring', kind: Kind.Method },
    { label: 'matches', detail: '(pattern) — regex match', kind: Kind.Method },
    { label: 'sameAs', detail: '(value) — convention-insensitive comparison', kind: Kind.Method },
    { label: 'empty', detail: '— string is empty', kind: Kind.Method },
];

const NUMERIC_PREDICATES = [
    { label: 'equals', detail: '(value) — equal to', kind: Kind.Method },
    { label: 'notEquals', detail: '(value) — not equal to', kind: Kind.Method },
    { label: 'greaterThan', detail: '(value) — greater than', kind: Kind.Method },
    { label: 'lessThan', detail: '(value) — less than', kind: Kind.Method },
    { label: 'greaterOrEqual', detail: '(value) — greater or equal', kind: Kind.Method },
    { label: 'lessOrEqual', detail: '(value) — less or equal', kind: Kind.Method },
];

const COLLECTION_PREDICATES = [
    { label: 'any', detail: '(predicate) — true if any item matches', kind: Kind.Method },
    { label: 'none', detail: '(predicate) — true if no items match', kind: Kind.Method },
    { label: 'all', detail: '(predicate) — true if all items match', kind: Kind.Method },
    { label: 'contains', detail: '(value) — list contains value', kind: Kind.Method },
    { label: 'empty', detail: '— collection is empty', kind: Kind.Method },
];

const UNIVERSAL_PREDICATES = [
    { label: 'in', detail: '(list) — value is member of list', kind: Kind.Method },
];

// All predicates deduped for fallback
const ALL_PREDICATES = dedup([
    ...STRING_PREDICATES, ...NUMERIC_PREDICATES, ...COLLECTION_PREDICATES, ...UNIVERSAL_PREDICATES
]);

const STRING_PROPERTIES = [
    { label: 'Length', detail: ': int — string length', kind: Kind.Property },
    { label: 'Lower', detail: ': string — lowercase', kind: Kind.Property },
    { label: 'Upper', detail: ': string — uppercase', kind: Kind.Property },
    { label: 'Normalized', detail: ': string — convention-insensitive form', kind: Kind.Property },
    { label: 'Words', detail: ': [string] — split into words', kind: Kind.Property },
];

const STRING_TRANSFORMS = [
    { label: 'Trim', detail: '(suffix) — remove suffix', kind: Kind.Method },
    { label: 'Replace', detail: '(old, new) — replace substring', kind: Kind.Method },
];

const COLLECTION_PROPERTIES = [
    { label: 'Count', detail: ': int — number of items', kind: Kind.Property },
    { label: 'First', detail: '— first item', kind: Kind.Property },
    { label: 'Last', detail: '— last item', kind: Kind.Property },
    { label: 'Single', detail: '— single item (null if not exactly one)', kind: Kind.Property },
];

const COLLECTION_TRANSFORMS = [
    { label: 'Where', detail: '(predicate) — filter items', kind: Kind.Method },
    { label: 'First', detail: '(predicate?) — first matching item', kind: Kind.Method },
    { label: 'Last', detail: '(predicate?) — last matching item', kind: Kind.Method },
    { label: 'Single', detail: '(predicate?) — single matching item', kind: Kind.Method },
    { label: 'ElementAt', detail: '(index) — item at position', kind: Kind.Method },
    { label: 'Select', detail: '(expression) — project each item', kind: Kind.Method },
];

const KEYWORDS = [
    { label: 'predicate', detail: 'Define a boolean test', kind: Kind.Keyword },
    { label: 'function', detail: 'Define a transform function', kind: Kind.Keyword },
    { label: 'type', detail: 'Define a type', kind: Kind.Keyword },
    { label: 'let', detail: 'Bind a named value', kind: Kind.Keyword },
    { label: 'command', detail: 'Define a named command', kind: Kind.Keyword },
    { label: 'import', detail: 'Import a package', kind: Kind.Keyword },
    { label: 'export', detail: 'Export declarations', kind: Kind.Keyword },
    { label: 'collection', detail: 'Declare a collection', kind: Kind.Keyword },
    { label: 'foreach', detail: 'Iterate over a collection', kind: Kind.Keyword },
    { label: 'feed', detail: 'Specify a package feed directory', kind: Kind.Keyword },
    { label: 'flags', detail: 'Define flag constants', kind: Kind.Keyword },
    { label: 'RUN', detail: 'Invoke another command', kind: Kind.Keyword },
    { label: 'true', detail: 'Boolean true', kind: Kind.Constant },
    { label: 'false', detail: 'Boolean false', kind: Kind.Constant },
];

const ACTIONS = [
    { label: 'PRINT', detail: '(message) — output to console', kind: Kind.Function },
    { label: 'SAVE', detail: '(path, template, collection) — write to file', kind: Kind.Function },
    { label: 'RUN', detail: '(command) — invoke another command', kind: Kind.Function },
];

const VIOLATION_TRANSFORMS = [
    { label: 'toError', detail: "(message) — create error violation", kind: Kind.Method },
    { label: 'toWarning', detail: "(message) — create warning violation", kind: Kind.Method },
    { label: 'toInfo', detail: "(message) — create info violation", kind: Kind.Method },
    { label: 'toOutput', detail: "(message) — create output violation", kind: Kind.Method },
    { label: 'toSave', detail: "(path, template) — create save violation", kind: Kind.Method },
];

const BUILTIN_FUNCTIONS = [
    { label: 'Text', detail: '(expr) — convert to string', kind: Kind.Function },
    { label: 'Path', detail: "(pattern) — test file path against glob", kind: Kind.Function },
    { label: 'Matches', detail: '(pattern) — test item text against regex', kind: Kind.Function },
];

const RUNTIME_TYPES = [
    { label: 'Codebase', detail: 'Code analysis collections (Types, Statements, Lines, Files)', kind: Kind.Class },
    { label: 'Filesystem', detail: 'Filesystem collections (Folders, Files)', kind: Kind.Class },
];

const KNOWN_PACKAGES = [
    { label: 'code', detail: 'Source code structural analysis', kind: Kind.Module },
    { label: 'filesystem', detail: 'Filesystem structural analysis', kind: Kind.Module },
    { label: 'code-analysis', detail: 'Code analysis utilities', kind: Kind.Module },
    { label: 'typespec', detail: 'TypeSpec analysis', kind: Kind.Module },
    { label: 'typespec-http', detail: 'TypeSpec HTTP analysis', kind: Kind.Module },
];

const MODIFIER_FLAGS = [
    { label: 'Public', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Private', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Protected', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Internal', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Static', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Sealed', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Abstract', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Virtual', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Async', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Override', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Readonly', detail: 'Modifier flag', kind: Kind.EnumMember },
    { label: 'Const', detail: 'Modifier flag', kind: Kind.EnumMember },
];

// ── Helpers ────────────────────────────────────────────────────────────────

function dedup(defs) {
    const seen = new Set();
    return defs.filter(d => {
        if (seen.has(d.label)) return false;
        seen.add(d.label);
        return true;
    });
}

function toItems(defs) {
    return dedup(defs).map(d => {
        const item = new vscode.CompletionItem(d.label, d.kind);
        item.detail = d.detail;
        return item;
    });
}

function isCollection(t) { return t && t.startsWith('['); }
function isString(t) { return t === 'string' || t === 'string?'; }
function isNumeric(t) { return t === 'int' || t === 'int?'; }
function isBool(t) { return t === 'bool' || t === 'bool?'; }
function stripNullable(t) { return t ? t.replace('?', '') : t; }
function elementType(t) {
    if (t && t.startsWith('[') && t.endsWith(']')) return t.slice(1, -1);
    return t;
}

// ── Document scanner ───────────────────────────────────────────────────────

function scanDocument(doc) {
    const lets = new Map();
    const predicates = new Map();
    const functions = new Map();
    const imports = [];

    for (let i = 0; i < doc.lineCount; i++) {
        const text = doc.lineAt(i).text.trim();
        if (text.startsWith('#')) continue;

        let m;
        if ((m = text.match(/^(?:export\s+)?import\s+([a-zA-Z][a-zA-Z0-9-]*)/))) {
            imports.push(m[1]);
        }
        if ((m = text.match(/^(?:export\s+)?let\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*=\s*(.+)/))) {
            lets.set(m[1], m[2].trim());
        }
        if ((m = text.match(/^(?:export\s+)?predicate\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(([A-Z][a-zA-Z0-9_]*)\)/))) {
            predicates.set(m[1], m[2]);
        }
        if ((m = text.match(/^(?:export\s+)?function\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(([A-Z][a-zA-Z0-9_]*)\)/))) {
            functions.set(m[1], m[2]);
        }
    }

    return { lets, predicates, functions, imports };
}

function resolveIdentifierType(name, symbols) {
    if (name === 'Code') return 'Codebase';
    if (name === 'Disk') return 'Filesystem';
    if (TYPES[name]) return name;

    const letExpr = symbols.lets.get(name);
    if (letExpr) return inferExprType(letExpr, symbols);

    return undefined;
}

function inferExprType(expr, symbols) {
    // runtime::X
    let m = expr.match(/^runtime::(\w+)/);
    if (m) return m[1];

    // X.Prop (possibly chained)
    m = expr.match(/^([A-Za-z_][A-Za-z0-9_-]*)\.(\w+)/);
    if (m) {
        const baseT = resolveIdentifierType(m[1], symbols);
        if (baseT) {
            const bt = stripNullable(baseT);
            const typeDef = TYPES[bt];
            if (typeDef) {
                const prop = typeDef.properties.find(p => p.name === m[2]);
                if (prop) return prop.type;
            }
        }
    }

    // Bare identifier or identifier:filter:filter...
    const baseName = expr.split(':')[0].trim();
    return resolveIdentifierType(baseName, symbols);
}

/** Walk a dot chain like Code.Types to resolve the final property type */
function resolvePropertyChain(chain, symbols) {
    const parts = chain.split('.');
    if (parts.length < 2) return undefined;

    let currentType = resolveIdentifierType(parts[0], symbols);
    if (!currentType) return undefined;

    for (let i = 1; i < parts.length; i++) {
        const bt = stripNullable(isCollection(currentType) ? elementType(currentType) : currentType);
        const typeDef = TYPES[bt];
        if (!typeDef) return undefined;
        const prop = typeDef.properties.find(p => p.name === parts[i]);
        if (!prop) return undefined;
        currentType = prop.type;
    }
    return currentType;
}

// ── Completion provider ────────────────────────────────────────────────────

const provider = {
    provideCompletionItems(document, position, _token, _context) {
        const lineText = document.lineAt(position).text;
        const textBefore = lineText.substring(0, position.character);

        // 1. After `runtime::` → runtime type names
        if (/runtime::$/.test(textBefore)) {
            return toItems(RUNTIME_TYPES);
        }

        // 2. After `import ` → package names
        if (/^\s*(?:export\s+)?import\s+$/.test(textBefore)) {
            return toItems(KNOWN_PACKAGES);
        }

        // 3. After `:` (but not `::`) → predicates
        if (/:$/.test(textBefore) && !(/::$/.test(textBefore))) {
            return getPredicateCompletions(document, textBefore);
        }

        // 4. After `.` → properties and transforms
        if (/\.$/.test(textBefore)) {
            return getDotCompletions(document, textBefore);
        }

        // 5. After `=> ` → actions and violations
        if (/=>\s*$/.test(textBefore)) {
            return toItems([...ACTIONS, ...VIOLATION_TRANSFORMS]);
        }

        // 6. Start of line → keywords and known identifiers
        if (/^\s*$/.test(textBefore)) {
            return getStatementCompletions(document);
        }

        // 7. After `export ` → declaration keywords
        if (/^\s*export\s+$/.test(textBefore)) {
            return toItems(KEYWORDS.filter(k =>
                ['predicate', 'function', 'type', 'let', 'command', 'flags'].includes(k.label)
            ));
        }

        // 8. General fallback
        return getGeneralCompletions(document);
    }
};

function getPredicateCompletions(document, textBefore) {
    const symbols = scanDocument(document);
    const items = [...UNIVERSAL_PREDICATES];

    // Try to infer type from expression before the colon
    const exprMatch = textBefore.match(/([A-Za-z_][A-Za-z0-9_.]*)\s*:\s*$/);
    if (exprMatch) {
        let exprType = inferExprType(exprMatch[1], symbols);

        // For property chains: Code.Types:  → [Type] collection
        if (!exprType && exprMatch[1].includes('.')) {
            exprType = resolvePropertyChain(exprMatch[1], symbols);
        }

        if (exprType) {
            if (isCollection(exprType)) {
                items.push(...COLLECTION_PREDICATES);
                // Also suggest predicates relevant to the element type
                const elT = stripNullable(elementType(exprType));
                if (isString(elT)) items.push(...STRING_PREDICATES);
                else if (isNumeric(elT)) items.push(...NUMERIC_PREDICATES);
            } else if (isString(exprType)) {
                items.push(...STRING_PREDICATES);
            } else if (isNumeric(exprType)) {
                items.push(...NUMERIC_PREDICATES);
            } else if (isBool(exprType)) {
                // No specific bool predicates
            }
        } else {
            items.push(...ALL_PREDICATES);
        }
    } else {
        items.push(...ALL_PREDICATES);
    }

    // User-defined predicates and functions
    for (const [name, paramType] of symbols.predicates) {
        items.push({ label: name, detail: `(${paramType}) — predicate`, kind: Kind.Method });
    }
    for (const [name, paramType] of symbols.functions) {
        items.push({ label: name, detail: `(${paramType}) — function`, kind: Kind.Function });
    }

    items.push(...BUILTIN_FUNCTIONS, ...VIOLATION_TRANSFORMS);

    return toItems(items);
}

function getDotCompletions(document, textBefore) {
    const symbols = scanDocument(document);
    const items = [];

    // Extract the full expression chain before the dot
    const exprMatch = textBefore.match(/([A-Za-z_][A-Za-z0-9_.]*)\.\s*$/);
    if (exprMatch) {
        const fullExpr = exprMatch[1];

        // Resolve through the chain
        let resolvedType;
        if (fullExpr.includes('.')) {
            resolvedType = resolvePropertyChain(fullExpr, symbols);
        } else {
            resolvedType = resolveIdentifierType(fullExpr, symbols);
        }

        if (resolvedType) {
            const bt = stripNullable(isCollection(resolvedType) ? elementType(resolvedType) : resolvedType);
            const typeDef = TYPES[bt];
            if (typeDef) {
                for (const prop of typeDef.properties) {
                    items.push({ label: prop.name, detail: `: ${prop.type}`, kind: Kind.Property });
                }
            }

            if (isString(resolvedType)) {
                items.push(...STRING_PROPERTIES, ...STRING_TRANSFORMS);
            } else if (isCollection(resolvedType)) {
                items.push(...COLLECTION_PROPERTIES, ...COLLECTION_TRANSFORMS);
            }
        }
    }

    // Fallback: if nothing resolved, offer common properties
    if (items.length === 0) {
        const allProps = new Map();
        for (const [typeName, typeDef] of Object.entries(TYPES)) {
            for (const prop of typeDef.properties) {
                if (!allProps.has(prop.name)) {
                    allProps.set(prop.name, { label: prop.name, detail: `: ${prop.type}`, kind: Kind.Property });
                }
            }
        }
        items.push(...allProps.values());
        items.push(...STRING_PROPERTIES, ...STRING_TRANSFORMS);
        items.push(...COLLECTION_PROPERTIES, ...COLLECTION_TRANSFORMS);
    }

    return toItems(items);
}

function getStatementCompletions(document) {
    const symbols = scanDocument(document);
    const items = [...KEYWORDS, ...ACTIONS];

    for (const name of symbols.lets.keys()) {
        items.push({ label: name, detail: 'let binding', kind: Kind.Variable });
    }

    items.push(
        { label: 'Code', detail: 'Codebase runtime variable', kind: Kind.Variable },
        { label: 'Disk', detail: 'Filesystem runtime variable', kind: Kind.Variable },
    );

    return toItems(items);
}

function getGeneralCompletions(document) {
    const symbols = scanDocument(document);
    const items = [...KEYWORDS, ...BUILTIN_FUNCTIONS, ...ACTIONS, ...MODIFIER_FLAGS];

    for (const typeName of Object.keys(TYPES)) {
        items.push({ label: typeName, detail: 'type', kind: Kind.Class });
    }

    for (const name of symbols.lets.keys()) {
        items.push({ label: name, detail: 'let binding', kind: Kind.Variable });
    }

    for (const [name, paramType] of symbols.predicates) {
        items.push({ label: name, detail: `(${paramType}) — predicate`, kind: Kind.Method });
    }

    for (const [name, paramType] of symbols.functions) {
        items.push({ label: name, detail: `(${paramType}) — function`, kind: Kind.Function });
    }

    items.push(
        { label: 'Code', detail: 'Codebase runtime variable', kind: Kind.Variable },
        { label: 'Disk', detail: 'Filesystem runtime variable', kind: Kind.Variable },
        { label: 'runtime', detail: 'Runtime namespace', kind: Kind.Module },
    );

    return toItems(items);
}

// ── Extension entry points ─────────────────────────────────────────────────

function activate(context) {
    context.subscriptions.push(
        vscode.languages.registerCompletionItemProvider(
            { language: 'cop', scheme: 'file' },
            provider,
            '.', ':', ' '
        )
    );
}

function deactivate() {}

module.exports = { activate, deactivate };
