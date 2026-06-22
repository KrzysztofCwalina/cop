// @ts-check
/// <reference types="vscode" />
'use strict';

const vscode = require('vscode');
const fs = require('fs');
const path = require('path');

// ── Dynamic package resolution ─────────────────────────────────────────────

/** Cache: packageDir → { types, collections, functions } */
const _packageCache = new Map();

/**
 * Find a package directory by name, searching up from docDir for `packages/` dirs
 * and also checking `.cop/packages/`. Falls back to ~/.cop/packages/ (global).
 */
function findPackageDir(docDir, packageName) {
    let dir = docDir;
    while (dir) {
        // Check packages/{name} directly or recursively through group folders
        const packagesDir = path.join(dir, 'packages');
        if (fs.existsSync(packagesDir)) {
            const found = findPackageInFeed(packagesDir, packageName);
            if (found) return found;
        }
        // Check .cop/packages/{name} (restored packages)
        const copPackages = path.join(dir, '.cop', 'packages', packageName);
        if (fs.existsSync(copPackages)) return copPackages;

        const parent = path.dirname(dir);
        if (parent === dir) break;
        dir = parent;
    }
    // Global fallback: ~/.cop/packages/{name}
    const homeDir = process.env.USERPROFILE || process.env.HOME;
    if (homeDir) {
        const globalPkg = path.join(homeDir, '.cop', 'packages', packageName);
        if (fs.existsSync(globalPkg)) return globalPkg;
    }
    return undefined;
}

/**
 * Recursively find a package in a feed directory (mirrors ImportResolver.FindPackageDir)
 */
function findPackageInFeed(feedPath, packageName) {
    const direct = path.join(feedPath, packageName);
    if (fs.existsSync(direct) && isPackageDir(direct, packageName)) return direct;

    // Recurse into group folders (non-package subdirectories)
    try {
        for (const entry of fs.readdirSync(feedPath, { withFileTypes: true })) {
            if (!entry.isDirectory() || entry.name.startsWith('.')) continue;
            const subDir = path.join(feedPath, entry.name);
            if (isPackageDir(subDir, entry.name)) continue; // skip actual packages
            const result = findPackageInFeed(subDir, packageName);
            if (result) return result;
        }
    } catch { /* ignore read errors */ }
    return undefined;
}

function isPackageDir(dirPath, dirName) {
    if (fs.existsSync(path.join(dirPath, `${dirName}.md`))) return true;
    if (fs.existsSync(path.join(dirPath, 'src'))) return true;
    if (fs.existsSync(path.join(dirPath, 'types'))) return true;
    return false;
}

/**
 * Parse a package directory: extract types from .cop files and collections from .md
 */
function parsePackageInfo(packageDir) {
    if (_packageCache.has(packageDir)) return _packageCache.get(packageDir);

    const types = {};      // typeName → { properties: [{name, type}], base? }
    const collections = {}; // collectionName → elementType
    const functions = [];  // [{name, params, returnType}]
    const predicates = []; // [{name, paramType, returnType}]

    // Find .cop source files in src/ or types/
    let copDir = null;
    for (const sub of ['src', 'types']) {
        const candidate = path.join(packageDir, sub);
        if (fs.existsSync(candidate)) { copDir = candidate; break; }
    }

    if (copDir) {
        try {
            const files = fs.readdirSync(copDir).filter(f => f.endsWith('.cop'));
            for (const file of files) {
                const content = fs.readFileSync(path.join(copDir, file), 'utf8');
                parseTypesFromCop(content, types, collections, functions, predicates);
            }
        } catch { /* ignore read errors */ }
    }

    // Parse .md for "Provides collections for:" and match element types
    const dirName = path.basename(packageDir);
    const mdPath = path.join(packageDir, `${dirName}.md`);
    if (fs.existsSync(mdPath)) {
        try {
            const md = fs.readFileSync(mdPath, 'utf8');
            const m = md.match(/Provides collections for:\s*(.+)/);
            if (m) {
                const names = m[1].replace(/\.$/, '').split(',').map(s => s.trim());
                for (const collName of names) {
                    const elType = resolveCollectionElementType(collName, types);
                    if (elType) collections[collName] = elType;
                }
            }
        } catch { /* ignore */ }
    }

    const result = { types, collections, functions, predicates };
    _packageCache.set(packageDir, result);
    return result;
}

/**
 * Parse export type definitions, collection declarations, and exported functions from .cop content
 */
function parseTypesFromCop(content, types, collections, functions, predicates) {
    const lines = content.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        if (line.startsWith('#')) continue;

        // export type Name = { ... }   OR   export type Name = Base & { ... }
        let m = line.match(/^(?:export\s+)?type\s+([A-Z][a-zA-Z0-9_]*)\s*=\s*(?:([A-Z][a-zA-Z0-9_]*)\s*&\s*)?\{(.*)$/);
        if (m) {
            const typeName = m[1];
            const base = m[2] || undefined;
            const properties = [];
            if (!m[3].includes('}')) {
                for (let j = i + 1; j < lines.length; j++) {
                    const fieldLine = lines[j].trim();
                    if (fieldLine === '}' || fieldLine.startsWith('}')) break;
                    if (fieldLine.startsWith('#')) continue;
                    const fm = fieldLine.match(/^([A-Z][a-zA-Z0-9_]*)\s*:\s*(.+?),?\s*$/);
                    if (fm) {
                        properties.push({ name: fm[1], type: fm[2].replace(/,$/, '').trim() });
                    }
                }
            }
            types[typeName] = { properties, base };
            continue;
        }

        // export collection Name : [ElementType]
        m = line.match(/^(?:export\s+)?collection\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*\[([A-Z][a-zA-Z0-9_]*)\]/);
        if (m) {
            collections[m[1]] = m[2];
            continue;
        }

        // export function name(...) : ReturnType => ...
        m = line.match(/^export\s+function\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(([^)]*)\)\s*(?::\s*([^\s=]+))?\s*=>/);
        if (m && functions) {
            const funcName = m[1];
            const params = m[2].trim();
            const returnType = m[3] || undefined;
            functions.push({ name: funcName, params, returnType });
            continue;
        }

        // export predicate name(ParamType[:guard]) [: NarrowedType] => ...
        // Captures the first parameter's type and the optional narrowing return type so the
        // editor can follow `:as<Language>` narrowing (e.g. asCSharp(Type) : CSharpType).
        m = line.match(/^(?:export\s+)?predicate\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(\s*([A-Z][a-zA-Z0-9_]*)[^)]*\)\s*(?::\s*([A-Z][a-zA-Z0-9_]*))?/);
        if (m && predicates) {
            predicates.push({ name: m[1], paramType: m[2], returnType: m[3] || undefined });
        }
    }
}

/**
 * Resolve a collection name to its element type by matching against known type names
 */
function resolveCollectionElementType(collectionName, types) {
    const typeNames = Object.keys(types);

    // Direct singular: Types → Type, Statements → Statement
    const singular = collectionName.replace(/s$/, '');
    if (types[singular]) return singular;

    // Strip 'es' ending: Files → File
    const singularEs = collectionName.replace(/es$/, '');
    if (types[singularEs]) return singularEs;

    // Find type whose name ends with singular: Operations → HttpOperation, TspOperation
    const match = typeNames.find(t => t.endsWith(singular) && t !== singular);
    if (match) return match;

    // Try singularEs match
    const matchEs = typeNames.find(t => t.endsWith(singularEs) && t !== singularEs);
    if (matchEs) return matchEs;

    return undefined;
}

/**
 * Resolve all imports for a document: returns merged { types, collections } from all packages
 */
function resolveImports(docPath, imports) {
    const docDir = path.dirname(docPath);
    const mergedTypes = {};
    const mergedCollections = {};
    const mergedFunctions = {}; // packageName → [{name, params, returnType}]
    const mergedPredicates = []; // [{name, paramType, returnType}] across all packages

    for (const pkg of imports) {
        const pkgDir = findPackageDir(docDir, pkg);
        if (pkgDir) {
            const info = parsePackageInfo(pkgDir);
            Object.assign(mergedTypes, info.types);
            Object.assign(mergedCollections, info.collections);
            if (info.functions && info.functions.length > 0) {
                mergedFunctions[pkg] = info.functions;
            }
            if (info.predicates && info.predicates.length > 0) {
                mergedPredicates.push(...info.predicates);
            }
        }
    }

    return { types: mergedTypes, collections: mergedCollections, functions: mergedFunctions, predicates: mergedPredicates };
}

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
        { name: 'Documentation', type: 'string?' },
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
        { name: 'Documentation', type: 'string?' },
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
        { name: 'Documentation', type: 'string?' },
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
        { name: 'ApiAsText', type: 'string' },
        { name: 'Line', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    Member: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'DeclaringType', type: 'string' },
        { name: 'Line', type: 'int' },
    ]},
    Region: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'StartLine', type: 'int' },
        { name: 'EndLine', type: 'int' },
        { name: 'Content', type: 'string' },
        { name: 'ContentHash', type: 'string' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    Project: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Path', type: 'string' },
        { name: 'Language', type: 'string?' },
        { name: 'References', type: '[string]' },
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
        { name: 'Calls', type: '[Statement]' },
        { name: 'Lines', type: '[Line]' },
        { name: 'Members', type: '[Member]' },
        { name: 'Api', type: '[Api]' },
        { name: 'Regions', type: '[Region]' },
        { name: 'Projects', type: '[Project]' },
    ]},
    Folder: { properties: [
        { name: 'Path', type: 'string' },
        { name: 'Name', type: 'string' },
        { name: 'Empty', type: 'bool' },
        { name: 'FileCount', type: 'int' },
        { name: 'SubfolderCount', type: 'int' },
        { name: 'Depth', type: 'int' },
        { name: 'MinutesSinceModified', type: 'int' },
        { name: 'Source', type: 'string' },
    ]},
    File: { properties: [
        { name: 'Path', type: 'string' },
        { name: 'Name', type: 'string' },
        { name: 'Extension', type: 'string' },
        { name: 'Size', type: 'int' },
        { name: 'Folder', type: 'string' },
        { name: 'Depth', type: 'int' },
        { name: 'MinutesSinceModified', type: 'int' },
        { name: 'Source', type: 'string' },
    ]},
    Filesystem: { properties: [
        { name: 'Folders', type: '[Folder]' },
        { name: 'Files', type: '[File]' },
    ]},
    // TypeSpec raw types
    TspDecorator: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Arguments', type: '[string]' },
    ]},
    TspProperty: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'string' },
        { name: 'Optional', type: 'bool' },
        { name: 'Default', type: 'string?' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspModel: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Properties', type: '[TspProperty]' },
        { name: 'BaseModel', type: 'string?' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspOperation: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Interface', type: 'string?' },
        { name: 'Parameters', type: '[TspProperty]' },
        { name: 'ReturnType', type: 'string' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspInterface: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Operations', type: '[TspOperation]' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspEnumMember: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Value', type: 'string?' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspEnum: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Members', type: '[TspEnumMember]' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspUnionVariant: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'string' },
    ]},
    TspUnion: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'Variants', type: '[TspUnionVariant]' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspScalar: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string?' },
        { name: 'BaseScalar', type: 'string?' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    TspNamespace: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'FullName', type: 'string' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    // TypeSpec HTTP types
    HttpParameter: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'string' },
        { name: 'In', type: 'string' },
        { name: 'Optional', type: 'bool' },
        { name: 'Style', type: 'string?' },
    ]},
    HttpHeader: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Type', type: 'string' },
    ]},
    HttpResponse: { properties: [
        { name: 'StatusCode', type: 'string' },
        { name: 'Description', type: 'string?' },
        { name: 'Body', type: 'string?' },
        { name: 'Headers', type: '[HttpHeader]' },
    ]},
    HttpOperation: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Verb', type: 'string' },
        { name: 'Path', type: 'string' },
        { name: 'UriTemplate', type: 'string' },
        { name: 'Parameters', type: '[HttpParameter]' },
        { name: 'Responses', type: '[HttpResponse]' },
        { name: 'Interface', type: 'string?' },
        { name: 'Decorators', type: '[TspDecorator]' },
    ]},
    HttpService: { properties: [
        { name: 'Name', type: 'string' },
        { name: 'Namespace', type: 'string' },
        { name: 'Operations', type: '[HttpOperation]' },
        { name: 'Auth', type: 'string?' },
    ]},
    // Markdown types
    Heading: { properties: [
        { name: 'Text', type: 'string' },
        { name: 'Level', type: 'int' },
        { name: 'Line', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    Link: { properties: [
        { name: 'Url', type: 'string' },
        { name: 'Text', type: 'string?' },
        { name: 'Line', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    Section: { properties: [
        { name: 'Heading', type: 'string' },
        { name: 'Level', type: 'int' },
        { name: 'Content', type: 'string' },
        { name: 'StartLine', type: 'int' },
        { name: 'EndLine', type: 'int' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    FenceBlock: { properties: [
        { name: 'Language', type: 'string?' },
        { name: 'Tag', type: 'string?' },
        { name: 'StartLine', type: 'int' },
        { name: 'EndLine', type: 'int' },
        { name: 'Content', type: 'string' },
        { name: 'ContentHash', type: 'string' },
        { name: 'File', type: 'File?' },
        { name: 'Source', type: 'string' },
    ]},
    MarkdownContent: { properties: [
        { name: 'Headings', type: '[Heading]' },
        { name: 'Links', type: '[Link]' },
        { name: 'Sections', type: '[Section]' },
        { name: 'FenceBlocks', type: '[FenceBlock]' },
    ]},
    // Code-analysis Violation type
    Violation: { properties: [
        { name: 'Severity', type: 'string' },
        { name: 'Message', type: 'string' },
        { name: 'File', type: 'string' },
        { name: 'Line', type: 'int' },
        { name: 'Source', type: 'string' },
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
    { label: 'isSet', detail: '(flag) — flags bit is set', kind: Kind.Method },
    { label: 'isClear', detail: '(flag) — flags bit is clear', kind: Kind.Method },
];

const COLLECTION_PREDICATES = [
    { label: 'any', detail: '((object) => bool) — true if any item matches', kind: Kind.Method },
    { label: 'none', detail: '((object) => bool) — true if no items match', kind: Kind.Method },
    { label: 'all', detail: '((object) => bool) — true if all items match', kind: Kind.Method },
    { label: 'count', detail: '((object) => bool) — count items matching predicate', kind: Kind.Method },
    { label: 'contains', detail: '(value) — list contains value', kind: Kind.Method },
    { label: 'containsAny', detail: '(values) — list contains any value from list', kind: Kind.Method },
    { label: 'empty', detail: '— collection is empty', kind: Kind.Method },
];

const UNIVERSAL_PREDICATES = [
    { label: 'in', detail: '(list) — value is member of list', kind: Kind.Method },
    { label: 'isError', detail: '— value is an error', kind: Kind.Method },
];

const OBJECT_PREDICATES = [
    { label: 'containsKey', detail: '(name) — object has field with given name', kind: Kind.Method },
];

// All predicates deduped for fallback
const ALL_PREDICATES = dedup([
    ...STRING_PREDICATES, ...NUMERIC_PREDICATES, ...COLLECTION_PREDICATES, ...UNIVERSAL_PREDICATES, ...OBJECT_PREDICATES
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
    { label: 'Tail', detail: '— all elements except the first', kind: Kind.Property },
];

const COLLECTION_TRANSFORMS = [
    { label: 'Where', detail: '((object) => bool) — filter items', kind: Kind.Method },
    { label: 'First', detail: '((object) => bool?) — first matching item', kind: Kind.Method },
    { label: 'Last', detail: '((object) => bool?) — last matching item', kind: Kind.Method },
    { label: 'Single', detail: '((object) => bool?) — single matching item', kind: Kind.Method },
    { label: 'ElementAt', detail: '(index: int) — item at position', kind: Kind.Method },
    { label: 'Select', detail: '((object) => object) — project each item', kind: Kind.Method },
    { label: 'OrderBy', detail: '((object) => object) — sort ascending', kind: Kind.Method },
    { label: 'OrderByDescending', detail: '((object) => object) — sort descending', kind: Kind.Method },
    { label: 'Distinct', detail: '— remove duplicates', kind: Kind.Method },
    { label: 'GroupBy', detail: '((object) => object) — group by key → Key, Items', kind: Kind.Method },
    { label: 'Sum', detail: '((object) => float) — sum numeric field', kind: Kind.Method },
    { label: 'Min', detail: '((object) => float) — minimum value', kind: Kind.Method },
    { label: 'Max', detail: '((object) => float) — maximum value', kind: Kind.Method },
    { label: 'Average', detail: '((object) => float) — average value', kind: Kind.Method },
    { label: 'Reduce', detail: '((object, object) => object, initial) — reduce collection', kind: Kind.Method },
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
    { label: 'enum', detail: 'Define an enumeration', kind: Kind.Keyword },
    { label: 'async', detail: 'Run foreach asynchronously', kind: Kind.Keyword },
    { label: 'RUN', detail: 'Invoke another command', kind: Kind.Keyword },
    { label: 'true', detail: 'Boolean true', kind: Kind.Constant },
    { label: 'false', detail: 'Boolean false', kind: Kind.Constant },
    { label: 'nic', detail: 'Null value', kind: Kind.Constant },
];

const ACTIONS = [
    { label: 'PRINT', detail: '(message) — output to console', kind: Kind.Function },
    { label: 'SAVE', detail: '(path, template) — write to file', kind: Kind.Function },
    { label: 'RUN', detail: '(command) — invoke another command', kind: Kind.Function },
    { label: 'DEBUG', detail: '(collection) — print internal structure', kind: Kind.Function },
    { label: 'ASSERT', detail: '(collection) — fail if zero items', kind: Kind.Function },
    { label: 'ASSERT_EMPTY', detail: '(collection) — fail if any items', kind: Kind.Function },
    { label: 'FAIL', detail: '(message) — terminate with error', kind: Kind.Function },
];

// Note: toError/toWarning/toInfo are package-level functions from code-analysis,
// not language built-ins. They are surfaced via dynamic package import resolution.

const BUILTIN_FUNCTIONS = [
    { label: 'Text', detail: '(expr) — convert to string', kind: Kind.Function },
    { label: 'Path', detail: "(pattern) — test file path against glob", kind: Kind.Function },
    { label: 'Matches', detail: '(pattern) — test item text against regex', kind: Kind.Function },
    { label: 'File', detail: '(path) — read file contents as string', kind: Kind.Function },
    { label: 'Get', detail: '(obj, name) — get property by name dynamically', kind: Kind.Function },
    { label: 'error', detail: '(message?) — create an error value', kind: Kind.Function },
];

const RUNTIME_TYPES = [
    { label: 'Codebase', detail: 'Code analysis collections (Types, Statements, Lines, Files)', kind: Kind.Class },
    { label: 'Filesystem', detail: 'Filesystem collections (Folders, Files)', kind: Kind.Class },
];

const KNOWN_PACKAGES = [
    { label: 'code', detail: 'Source code structural analysis', kind: Kind.Module },
    { label: 'code-analysis', detail: 'Violation reporting framework', kind: Kind.Module },
    { label: 'code-layering', detail: 'Project dependency analysis', kind: Kind.Module },
    { label: 'csharp', detail: 'C# source language provider', kind: Kind.Module },
    { label: 'python', detail: 'Python source language provider', kind: Kind.Module },
    { label: 'javascript', detail: 'JavaScript/TypeScript source language provider', kind: Kind.Module },
    { label: 'files', detail: 'Filesystem structural analysis', kind: Kind.Module },
    { label: 'json',detail: 'JSON document analysis', kind: Kind.Module },
    { label: 'markdown', detail: 'Markdown document analysis', kind: Kind.Module },
    { label: 'typespec', detail: 'TypeSpec analysis', kind: Kind.Module },
    { label: 'typespec-http', detail: 'TypeSpec HTTP analysis', kind: Kind.Module },
    { label: 'http', detail: 'HTTP request/response provider', kind: Kind.Module },
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
    const lets = new Map();       // name → { expr, typeAnnotation? }
    const predicates = new Map();
    const functions = new Map();
    const types = new Map();
    const imports = [];
    const docNarrowing = [];      // [{name, paramType, returnType}] for document-local predicates

    for (let i = 0; i < doc.lineCount; i++) {
        const text = doc.lineAt(i).text.trim();
        if (text.startsWith('#')) continue;

        let m;
        if ((m = text.match(/^(?:export\s+)?import\s+([a-zA-Z][a-zA-Z0-9-]*)/))) {
            imports.push(m[1]);
        }
        // let name : [Type] = expr  OR  let name = expr
        if ((m = text.match(/^(?:export\s+)?let\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*(?::\s*(\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_?]*))?\s*=\s*(.+)/))) {
            // Gather continuation lines so multi-line expressions are captured in full,
            // e.g. a chained `:toError(...)` on the next line, or a union spread across
            // lines (`a +` / `b`). Without this the hover would miss the transform and the
            // inferred type would be wrong (or "unknown").
            let letExpr = m[3].trim();
            let j = i + 1;
            while (j < doc.lineCount) {
                const cont = doc.lineAt(j).text.trim();
                if (cont === '' || cont.startsWith('#')) break;
                // Don't swallow a new top-level declaration.
                if (/^(?:export\s+)?(?:let|predicate|function|command|type|enum|flags|import|feed|test|foreach)\b/.test(cont)) break;
                if (/^[:.+]/.test(cont) || /[+:(,]$/.test(letExpr)) {
                    letExpr += ' ' + cont;
                    j++;
                } else break;
            }
            lets.set(m[1], { expr: letExpr.trim(), typeAnnotation: m[2] || null });
            i = j - 1;
        }
        if ((m = text.match(/^(?:export\s+)?predicate\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(\s*([A-Z][a-zA-Z0-9_]*)[^)]*\)\s*(?::\s*([A-Z][a-zA-Z0-9_]*))?/))) {
            predicates.set(m[1], m[2]);
            docNarrowing.push({ name: m[1], paramType: m[2], returnType: m[3] || undefined });
        }
        if ((m = text.match(/^(?:export\s+)?function\s+([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(([A-Z][a-zA-Z0-9_]*)/))) {
            functions.set(m[1], m[2]);
        }
        if ((m = text.match(/^(?:export\s+)?type\s+([A-Z][a-zA-Z0-9_]*)\s*=\s*(?:([A-Z][a-zA-Z0-9_]*)\s*&\s*)?\{(.*)$/))) {
            const typeName = m[1];
            const base = m[2] || undefined;
            const properties = [];
            if (!m[3].includes('}')) {
                for (let j = i + 1; j < doc.lineCount; j++) {
                    const fieldLine = doc.lineAt(j).text.trim();
                    if (fieldLine === '}' || fieldLine.startsWith('}')) break;
                    const fm = fieldLine.match(/^([A-Z][a-zA-Z0-9_]*)\s*:\s*(.+?),?\s*$/);
                    if (fm) {
                        properties.push({ name: fm[1], type: fm[2].replace(/,$/, '').trim() });
                    }
                }
            }
            types.set(typeName, { properties, base });
        }
    }

    // Resolve imported packages dynamically from disk
    const symbols = { lets, predicates, functions, types, imports, docNarrowing, _resolvedTypes: null, _resolvedCollections: null, _resolvedFunctions: null, _resolvedPredicates: null };
    if (imports.length > 0 && doc.uri && doc.uri.fsPath) {
        const resolved = resolveImports(doc.uri.fsPath, imports);
        symbols._resolvedTypes = resolved.types;
        symbols._resolvedCollections = resolved.collections;
        symbols._resolvedFunctions = resolved.functions;
        symbols._resolvedPredicates = resolved.predicates;
    }

    return symbols;
}

function resolveIdentifierType(name, symbols) {
    if (name === 'Code') return 'Codebase';
    if (name === 'Disk') return 'Filesystem';
    if (name === 'Markdown') return 'MarkdownContent';
    if (TYPES[name]) return name;

    const letEntry = symbols.lets.get(name);
    if (letEntry) {
        // If type annotation is present, use it directly
        if (letEntry.typeAnnotation) return letEntry.typeAnnotation;
        return inferExprType(letEntry.expr, symbols);
    }

    // Check collections from imported packages (dynamic + static fallback)
    const colls = symbols._resolvedCollections;
    if (colls && colls[name]) {
        return `[${colls[name]}]`;
    }

    // Check document-defined types and dynamically loaded package types
    if (symbols.types && symbols.types.has(name)) {
        return name;
    }
    if (symbols._resolvedTypes && symbols._resolvedTypes[name]) {
        return name;
    }

    return undefined;
}

function inferExprType(expr, symbols) {
    // toError / toWarning / toInfo / toViolation transform a filtered collection into a
    // list of Violations, regardless of the source collection type. This must run before
    // the generic filter-chain handling below (which assumes `:` preserves the type).
    if (/:to(?:Error|Warning|Info|Violation)\s*\(/.test(expr)) return '[Violation]';

    // runtime::X
    let m = expr.match(/^runtime::(\w+)/);
    if (m) return m[1];

    // Package-qualified function call: package.func() or package.func('...')
    m = expr.match(/^([A-Za-z_][A-Za-z0-9_-]*)\.([a-zA-Z_][a-zA-Z0-9_-]*)\s*\(/);
    if (m) {
        const pkgName = m[1];
        const funcName = m[2];
        if (symbols.imports.includes(pkgName)) {
            const pkgFuncs = symbols._resolvedFunctions && symbols._resolvedFunctions[pkgName];
            if (pkgFuncs) {
                const fn = pkgFuncs.find(f => f.name === funcName);
                if (fn && fn.returnType) return fn.returnType;
            }
        }
    }

    // X.Prop (possibly chained)
    m = expr.match(/^([A-Za-z_][A-Za-z0-9_-]*)\.(\w+)/);
    if (m) {
        const baseT = resolveIdentifierType(m[1], symbols);
        if (baseT) {
            const bt = stripNullable(baseT);
            const typeDef = lookupType(bt, symbols);
            if (typeDef) {
                const prop = typeDef.properties.find(p => p.name === m[2]);
                if (prop) return prop.type;
            }
        }
        // Namespace-qualified function (no parens): package.func → return type
        if (symbols.imports.includes(m[1])) {
            const pkgFuncs = symbols._resolvedFunctions && symbols._resolvedFunctions[m[1]];
            if (pkgFuncs) {
                const fn = pkgFuncs.find(f => f.name === m[2]);
                if (fn && fn.returnType) return fn.returnType;
            }
        }
        // Namespace-qualified collection: package.Collection
        const colls = symbols._resolvedCollections;
        if (colls && colls[m[2]]) {
            if (symbols.imports.includes(m[1])) {
                return `[${colls[m[2]]}]`;
            }
        }
    }

    // Handle filter chains: split on `:` and resolve the base
    // In cop, filtering doesn't change the type: Types:isPublic is still [Type]
    const baseName = expr.split(':')[0].trim();
    // If the base contains a dot, resolve it as a property chain
    if (baseName.includes('.')) {
        return resolvePropertyChain(baseName, symbols);
    }
    return resolveIdentifierType(baseName, symbols);
}

/**
 * Resolve the type of a full expression chain that may include `.` and `:` operators.
 * In cop, `:` is a filter that preserves the collection type, and `.` accesses properties.
 * Examples:
 *   "Types:isPublic" → [Type]
 *   "Types:isPublic:isCSharp" → [Type]
 *   "Type.Methods" → [Method]
 *   "Type.Name" → string
 */
function resolveFullChainType(fullExpr, symbols) {
    if (!fullExpr) return undefined;

    // Split by `:` first — resolve the base, then apply narrowing predicates segment by segment.
    const colonParts = fullExpr.split(':');
    const baseExpr = colonParts[0].trim();

    // Resolve the base (may include dots)
    let currentType;
    if (baseExpr.includes('.')) {
        currentType = resolvePropertyChain(baseExpr, symbols);
    } else {
        currentType = resolveIdentifierType(baseExpr, symbols);
    }
    if (!currentType) return currentType;

    // Apply narrowing predicates. Most `:pred` filters preserve the type, but a narrowing
    // predicate (e.g. `asCSharp(Type) : CSharpType`) switches the element type to its return
    // type, so editor completions and hovers follow the narrowing.
    const narrowing = getNarrowingPredicates(symbols);
    if (narrowing.length > 0) {
        for (let i = 1; i < colonParts.length; i++) {
            const seg = colonParts[i].trim();
            if (!seg) continue;
            // strip a leading `!` and any argument list to get the bare predicate name
            const predName = seg.replace(/^!+/, '').replace(/\(.*$/, '').trim();
            if (!predName) continue;
            const coll = isCollection(currentType);
            const elem = stripNullable(coll ? elementType(currentType) : currentType);
            const narrowed = findNarrowingReturn(narrowing, predName, elem);
            if (narrowed) {
                currentType = coll ? `[${narrowed}]` : narrowed;
            }
        }
    }
    return currentType;
}

/** Collect narrowing predicates (with return types) from imported packages and the document. */
function getNarrowingPredicates(symbols) {
    const list = [];
    if (symbols._resolvedPredicates) list.push(...symbols._resolvedPredicates);
    if (symbols.docNarrowing) list.push(...symbols.docNarrowing);
    return list;
}

/** Find a narrowing predicate's return type for `name` applied to element type `elemType`. */
function findNarrowingReturn(list, name, elemType) {
    // Prefer an exact parameter-type match: narrowing predicates like `asCSharp` are overloaded
    // across Type/Method/Statement, so the parameter type selects the right return type.
    const hit = list.find(p => p.name === name && p.returnType && p.paramType === elemType);
    return hit ? hit.returnType : undefined;
}

/**
 * Determine what type `item` refers to at a given document position.
 * In cop, `item` is the implicit iteration variable:
 * - Inside a predicate body: the predicate's parameter type
 * - Inside a function body: the function's parameter type
 * - Inside foreach: the element type of the iterated collection
 * - After :toError/.../filter functions: the element type of the filtered collection
 */
function resolveItemType(doc, position, symbols) {
    // Walk backwards from current line looking for enclosing context
    for (let i = position.line; i >= 0; i--) {
        const text = doc.lineAt(i).text.trim();
        if (text.startsWith('#')) continue;

        // predicate name(Type) or predicate name(Type:constraint)
        let m = text.match(/^(?:export\s+)?predicate\s+\w+\s*\(([A-Z][a-zA-Z0-9_]*)(?:\s*:\s*\w+)?\)/);
        if (m) return m[1];

        // function name(Type, ...) or function name(Type)
        m = text.match(/^(?:export\s+)?function\s+\w+\s*\(([A-Z][a-zA-Z0-9_]*)/);
        if (m) return m[1];

        // foreach collection => '...' (old syntax)
        m = text.match(/^(?:async\s+)?foreach\s+(.+?)\s*=>/);
        if (m) {
            const collExpr = m[1].trim();
            const collType = resolveFullChainType(collExpr, symbols);
            if (collType && isCollection(collType)) return elementType(collType);
            return collType;
        }

        // Stop at another top-level declaration (don't cross boundaries)
        if (i < position.line && /^(?:export\s+)?(?:predicate|function|command|let|type|flags|enum)\b/.test(text)) break;
    }

    // If we're in a let binding with a filter chain (e.g., let x = Types:isPublic:toError('...'))
    // look at the current line for context
    const currentLine = doc.lineAt(position.line).text;
    const beforeCursor = currentLine.substring(0, position.character);

    // Inside a function call argument like :toError('{item.Name}')
    const funcCallMatch = beforeCursor.match(/([A-Za-z_][A-Za-z0-9_:.-]*):(?:toError|toWarning|toInfo|toViolation)\s*\(\s*'/);
    if (funcCallMatch) {
        const chainExpr = funcCallMatch[1];
        const chainType = resolveFullChainType(chainExpr, symbols);
        if (chainType && isCollection(chainType)) return elementType(chainType);
        return chainType;
    }

    return undefined;
}

/** Look up a type definition from built-in TYPES, document types, or resolved package types */
function lookupType(typeName, symbols) {
    let def;
    if (TYPES[typeName]) def = TYPES[typeName];
    else if (symbols.types && symbols.types.has(typeName)) def = symbols.types.get(typeName);
    else if (symbols._resolvedTypes && symbols._resolvedTypes[typeName]) def = symbols._resolvedTypes[typeName];
    if (!def) return undefined;
    // Narrowing subtypes (e.g. `CSharpType = Type & {...}`) declare a base — merge the base's
    // properties so a narrowed value exposes both the common fields and the language-specific ones.
    if (def.base) {
        const baseDef = lookupType(def.base, symbols);
        if (baseDef && baseDef.properties) {
            const own = def.properties || [];
            const ownNames = new Set(own.map(p => p.name));
            return { properties: [...baseDef.properties.filter(p => !ownNames.has(p.name)), ...own] };
        }
    }
    return def;
}

/** Returns the type and its base ancestors (e.g. CSharpType → [CSharpType, Type]). */
function typeAncestors(typeName, symbols) {
    const chain = [];
    let t = typeName;
    const guard = new Set();
    while (t && !guard.has(t)) {
        guard.add(t);
        chain.push(t);
        const def = TYPES[t]
            || (symbols.types && symbols.types.get && symbols.types.get(t))
            || (symbols._resolvedTypes && symbols._resolvedTypes[t]);
        t = def && def.base;
    }
    return chain;
}

/** Walk a dot chain like Code.Types to resolve the final property type */
function resolvePropertyChain(chain, symbols) {
    const parts = chain.split('.');
    if (parts.length < 2) return undefined;

    let currentType = resolveIdentifierType(parts[0], symbols);
    if (!currentType) {
        // Check namespace-qualified function: package.func → return type
        if (symbols.imports.includes(parts[0])) {
            const pkgFuncs = symbols._resolvedFunctions && symbols._resolvedFunctions[parts[0]];
            if (pkgFuncs) {
                const fn = pkgFuncs.find(f => f.name === parts[1]);
                if (fn && fn.returnType) {
                    currentType = fn.returnType;
                    if (parts.length === 2) return currentType;
                    for (let i = 2; i < parts.length; i++) {
                        const bt = stripNullable(isCollection(currentType) ? elementType(currentType) : currentType);
                        const typeDef = lookupType(bt, symbols);
                        if (!typeDef) return undefined;
                        const prop = typeDef.properties.find(p => p.name === parts[i]);
                        if (!prop) return undefined;
                        currentType = prop.type;
                    }
                    return currentType;
                }
            }
        }
        // Check namespace-qualified collection: package.Collection
        const colls = symbols._resolvedCollections;
        if (colls && colls[parts[1]]) {
            if (symbols.imports.includes(parts[0])) {
                currentType = `[${colls[parts[1]]}]`;
                if (parts.length === 2) return currentType;
                for (let i = 2; i < parts.length; i++) {
                    const bt = stripNullable(isCollection(currentType) ? elementType(currentType) : currentType);
                    const typeDef = lookupType(bt, symbols);
                    if (!typeDef) return undefined;
                    const prop = typeDef.properties.find(p => p.name === parts[i]);
                    if (!prop) return undefined;
                    currentType = prop.type;
                }
                return currentType;
            }
        }
        return undefined;
    }

    for (let i = 1; i < parts.length; i++) {
        const bt = stripNullable(isCollection(currentType) ? elementType(currentType) : currentType);
        const typeDef = lookupType(bt, symbols);
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
            return getDotCompletions(document, textBefore, position);
        }

        // 5. After `=> ` → actions and violations
        if (/=>\s*$/.test(textBefore)) {
            return toItems([...ACTIONS]);
        }

        // 6. Start of line → keywords and known identifiers
        if (/^\s*$/.test(textBefore)) {
            return getStatementCompletions(document);
        }

        // 7. After `export ` → declaration keywords
        if (/^\s*export\s+$/.test(textBefore)) {
            return toItems(KEYWORDS.filter(k =>
                ['predicate', 'function', 'type', 'let', 'command', 'flags', 'enum', 'collection'].includes(k.label)
            ));
        }

        // 8. General fallback
        return getGeneralCompletions(document);
    }
};

function getPredicateCompletions(document, textBefore) {
    const symbols = scanDocument(document);
    const items = [...UNIVERSAL_PREDICATES];

    // Try to infer type from the full expression chain before the colon
    // Match the chain before the final `:`, including previous `:` filter operators
    const exprMatch = textBefore.match(/([A-Za-z_][A-Za-z0-9_.:()-]*)\s*:\s*$/);
    if (exprMatch) {
        const fullChain = exprMatch[1];
        let exprType = resolveFullChainType(fullChain, symbols);

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

    // Imported package predicates — scope by the (possibly narrowed) element type so a narrow
    // like `:asCSharp` surfaces CSharpType / CSharpMethod / CSharpStatement predicates, while
    // base predicates (isPublic, ...) stay available on the narrowed subtype.
    if (symbols._resolvedPredicates && symbols._resolvedPredicates.length > 0) {
        let applicable = null;
        if (exprMatch) {
            const t = resolveFullChainType(exprMatch[1], symbols);
            if (t) {
                const elemType = stripNullable(isCollection(t) ? elementType(t) : t);
                applicable = new Set(typeAncestors(elemType, symbols));
            }
        }
        const seen = new Set();
        for (const p of symbols._resolvedPredicates) {
            if (applicable && !applicable.has(p.paramType)) continue;
            const key = p.name + '/' + p.paramType;
            if (seen.has(key)) continue;
            seen.add(key);
            const detail = p.returnType ? `(${p.paramType}) → ${p.returnType}` : `(${p.paramType}) — predicate`;
            items.push({ label: p.name, detail, kind: Kind.Method });
        }
    }

    // Also add functions from imported packages
    if (symbols._resolvedFunctions) {
        for (const [pkgName, funcs] of Object.entries(symbols._resolvedFunctions)) {
            for (const fn of funcs) {
                items.push({ label: fn.name, detail: `(${fn.params || '...'}) — ${pkgName}`, kind: Kind.Function });
            }
        }
    }

    items.push(...BUILTIN_FUNCTIONS);

    return toItems(items);
}

function getDotCompletions(document, textBefore, position) {
    const symbols = scanDocument(document);
    const items = [];

    // Extract the full expression before the dot, including filter chains with `:`
    // Match: identifier chains with `.` and `:` operators, optional function calls
    const exprMatch = textBefore.match(/([A-Za-z_][A-Za-z0-9_.:()-]*(?:\('[^']*'\)|\(\))?)\.\s*$/);
    if (exprMatch) {
        let rawExpr = exprMatch[1];
        // Strip trailing function call syntax for resolution
        const cleanExpr = rawExpr.replace(/\('[^']*'\)$/, '').replace(/\(\)$/, '');

        // Handle `item.` — resolve to the element type in current context
        if (cleanExpr === 'item') {
            const pos = position || { line: getLineNumber(document, textBefore), character: textBefore.length };
            const itemType = resolveItemType(document, pos, symbols);
            if (itemType) {
                const typeDef = lookupType(stripNullable(itemType), symbols);
                if (typeDef) {
                    for (const prop of typeDef.properties) {
                        items.push({ label: prop.name, detail: `: ${prop.type}`, kind: Kind.Property });
                    }
                }
                if (isString(itemType)) {
                    items.push(...STRING_PROPERTIES, ...STRING_TRANSFORMS);
                } else if (isCollection(itemType)) {
                    items.push(...COLLECTION_PROPERTIES, ...COLLECTION_TRANSFORMS);
                }
                if (items.length > 0) return toItems(items);
            }
        }

        // Check if this is an imported package namespace (e.g., "csharp." → show its exported functions/collections)
        if (symbols.imports.includes(cleanExpr)) {
            // Show exported functions for this package (resolved from disk)
            const pkgFuncs = symbols._resolvedFunctions && symbols._resolvedFunctions[cleanExpr];
            if (pkgFuncs) {
                const seen = new Set();
                for (const fn of pkgFuncs) {
                    if (seen.has(fn.name)) continue;
                    seen.add(fn.name);
                    const detail = fn.returnType ? `→ ${fn.returnType}` : 'function';
                    items.push({ label: fn.name, detail, kind: Kind.Function });
                }
            }
            // Show collections for this package (if any, resolved from disk)
            const colls = symbols._resolvedCollections || {};
            for (const [collName, itemType] of Object.entries(colls)) {
                items.push({ label: collName, detail: `→ [${itemType}]`, kind: Kind.Field });
            }
            // Return whatever we found (even empty) — don't fall through to generic fallback
            return toItems(items);
        }

        // Resolve the full chain type (handles both `.` and `:` operators)
        let resolvedType = resolveFullChainType(cleanExpr, symbols);

        if (resolvedType) {
            const bt = stripNullable(isCollection(resolvedType) ? elementType(resolvedType) : resolvedType);
            const typeDef = lookupType(bt, symbols);
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

/** Get the 0-based line number for the cursor position from textBefore */
function getLineNumber(document, textBefore) {
    // We're called with the text of the current line up to cursor.
    // The document position is the line we're currently on.
    // Since we don't have the position directly, scan for matching line.
    for (let i = 0; i < document.lineCount; i++) {
        const lineText = document.lineAt(i).text;
        if (lineText.includes(textBefore) || lineText.startsWith(textBefore)) {
            return i;
        }
    }
    return 0;
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

    // Add ambient collections from imported packages (Types, Statements, Lines, Files, etc.)
    const colls = symbols._resolvedCollections;
    if (colls) {
        for (const [collName, elType] of Object.entries(colls)) {
            items.push({ label: collName, detail: `[${elType}] — collection`, kind: Kind.Variable });
        }
    }

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
        { label: 'item', detail: 'Current iteration variable', kind: Kind.Variable },
    );

    // Add ambient collections from imported packages
    const colls = symbols._resolvedCollections;
    if (colls) {
        for (const [collName, elType] of Object.entries(colls)) {
            items.push({ label: collName, detail: `[${elType}] — collection`, kind: Kind.Variable });
        }
    }

    // Add imported package names for namespace-qualified access
    for (const pkg of symbols.imports) {
        items.push({ label: pkg, detail: 'imported package', kind: Kind.Module });
    }

    return toItems(items);
}

// ── Hover provider ─────────────────────────────────────────────────────────

const hoverProvider = {
    provideHover(document, position, _token) {
        const symbols = scanDocument(document);
        const lineText = document.lineAt(position).text;
        const wordRange = document.getWordRangeAtPosition(position, /[A-Za-z_][A-Za-z0-9_-]*/);
        if (!wordRange) return undefined;

        const word = document.getText(wordRange);

        // Expand to full dot-chain (e.g. Code.Types.Name)
        const chain = expandDotChain(lineText, wordRange.start.character, wordRange.end.character);

        // 1. Predicate definition: `predicate name(Type)`
        if (symbols.predicates.has(word)) {
            const paramType = symbols.predicates.get(word);
            const constraint = getPredicateConstraint(document, word);
            const sig = constraint
                ? `predicate ${word}(${paramType}:${constraint}) => bool`
                : `predicate ${word}(${paramType}) => bool`;
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock(sig, 'cop')
            );
        }

        // 2. Function definition: `function name(Type) => ReturnType`
        if (symbols.functions.has(word)) {
            const paramType = symbols.functions.get(word);
            const retType = getFunctionReturnType(document, word);
            const sig = retType
                ? `function ${word}(${paramType}) => ${retType}`
                : `function ${word}(${paramType})`;
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock(sig, 'cop')
            );
        }

        // 3. Let bindings
        if (symbols.lets.has(word)) {
            const letEntry = symbols.lets.get(word);
            let resolvedType;
            if (letEntry.typeAnnotation) {
                resolvedType = letEntry.typeAnnotation;
            } else {
                resolvedType = inferExprType(letEntry.expr, symbols);
            }
            const typeStr = resolvedType || 'unknown';
            const md = new vscode.MarkdownString();
            md.appendCodeblock(`let ${word}: ${typeStr}`, 'cop');
            if (resolvedType && resolvedType !== letEntry.expr) {
                md.appendMarkdown(`\n\n= \`${letEntry.expr}\``);
            }
            return new vscode.Hover(md);
        }

        // 4. Dot-chain property resolution (e.g. hovering over "Name" in Code.Types.Name)
        if (chain && chain.includes('.')) {
            const dotParts = chain.split('.');
            const propName = dotParts[dotParts.length - 1];
            const parentChain = dotParts.slice(0, -1).join('.');

            let parentType;
            if (dotParts.length === 2) {
                parentType = resolveIdentifierType(dotParts[0], symbols);
            } else {
                parentType = resolvePropertyChain(parentChain, symbols);
            }

            if (parentType) {
                const bt = stripNullable(isCollection(parentType) ? elementType(parentType) : parentType);
                const typeDef = lookupType(bt, symbols);
                if (typeDef) {
                    const prop = typeDef.properties.find(p => p.name === propName);
                    if (prop) {
                        const md = new vscode.MarkdownString();
                        md.appendCodeblock(`(property) ${bt}.${prop.name}: ${prop.type}`, 'cop');
                        return new vscode.Hover(md);
                    }
                }
                // String/collection built-in properties
                if (isString(parentType)) {
                    const sp = STRING_PROPERTIES.find(p => p.label === propName);
                    if (sp) return new vscode.Hover(new vscode.MarkdownString().appendCodeblock(`(property) string.${propName}${sp.detail}`, 'cop'));
                }
                if (isCollection(parentType)) {
                    const cp = COLLECTION_PROPERTIES.find(p => p.label === propName);
                    if (cp) return new vscode.Hover(new vscode.MarkdownString().appendCodeblock(`(property) [${elementType(parentType)}].${propName}${cp.detail}`, 'cop'));
                }
            }
        }

        // 5. Runtime variables and `item`
        if (word === 'Code') {
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock('(runtime) Code: Codebase', 'cop')
            );
        }
        if (word === 'Disk') {
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock('(runtime) Disk: Filesystem', 'cop')
            );
        }
        if (word === 'item') {
            const itemType = resolveItemType(document, position, symbols);
            if (itemType) {
                return new vscode.Hover(
                    new vscode.MarkdownString().appendCodeblock(`(variable) item: ${itemType}`, 'cop')
                );
            }
        }

        // 6. Type names (built-in and document-defined)
        const typeDef = TYPES[word] || (symbols.types && symbols.types.get(word));
        if (typeDef) {
            const md = new vscode.MarkdownString();
            md.appendCodeblock(`type ${word}`, 'cop');
            const propLines = typeDef.properties.map(p => `  ${p.name}: ${p.type}`);
            if (propLines.length > 0) {
                md.appendMarkdown('\n\n**Properties:**\n');
                md.appendCodeblock(propLines.join('\n'), 'cop');
            }
            return new vscode.Hover(md);
        }

        // 7. Predicate parameter variable (the iteration variable in a predicate body)
        const paramInfo = getEnclosingPredicateParam(document, position);
        if (paramInfo && word === paramInfo.varName) {
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock(`(parameter) ${paramInfo.varName}: ${paramInfo.typeName}`, 'cop')
            );
        }

        // 8. Collection names from foreach/let that reference collections
        const foreachType = resolveForeachVariable(document, position, word, symbols);
        if (foreachType) {
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock(`(variable) ${word}: ${foreachType}`, 'cop')
            );
        }

        // 9. Keywords
        const kw = KEYWORDS.find(k => k.label === word);
        if (kw) {
            return new vscode.Hover(
                new vscode.MarkdownString().appendCodeblock(`(keyword) ${word}`, 'cop').appendMarkdown(`\n\n${kw.detail}`)
            );
        }

        return undefined;
    }
};

/** Expand the word at cursor to include surrounding dot-chain */
function expandDotChain(lineText, startChar, endChar) {
    // Walk left across dots and identifiers
    let left = startChar;
    while (left > 0) {
        if (lineText[left - 1] === '.') {
            let j = left - 2;
            while (j >= 0 && /[A-Za-z0-9_-]/.test(lineText[j])) j--;
            if (j < left - 2) { left = j + 1; } else break;
        } else break;
    }
    // Walk right across dots and identifiers
    let right = endChar;
    while (right < lineText.length) {
        if (lineText[right] === '.') {
            let j = right + 1;
            while (j < lineText.length && /[A-Za-z0-9_-]/.test(lineText[j])) j++;
            if (j > right + 1) { right = j; } else break;
        } else break;
    }
    const chain = lineText.substring(left, right);
    return /^[A-Za-z_]/.test(chain) ? chain : undefined;
}

/** Get the constraint (language) for a predicate definition */
function getPredicateConstraint(doc, predName) {
    for (let i = 0; i < doc.lineCount; i++) {
        const text = doc.lineAt(i).text.trim();
        const m = text.match(new RegExp(`^(?:export\\s+)?predicate\\s+${escapeRegex(predName)}\\s*\\([A-Z][a-zA-Z0-9_]*\\s*:\\s*([a-z][a-zA-Z0-9_]*)\\)`));
        if (m) return m[1];
    }
    return undefined;
}

/** Get the return type for a function definition */
function getFunctionReturnType(doc, funcName) {
    for (let i = 0; i < doc.lineCount; i++) {
        const text = doc.lineAt(i).text.trim();
        const m = text.match(new RegExp(`^(?:export\\s+)?function\\s+${escapeRegex(funcName)}\\s*\\([^)]*\\)\\s*=>\\s*([A-Z][a-zA-Z0-9_]*)`));
        if (m) return m[1];
    }
    return undefined;
}

/** Find the predicate/function enclosing a position and return param info */
function getEnclosingPredicateParam(doc, position) {
    for (let i = position.line; i >= 0; i--) {
        const text = doc.lineAt(i).text.trim();
        let m = text.match(/^(?:export\s+)?predicate\s+\w+\s*\(([A-Z][a-zA-Z0-9_]*)\)/);
        if (m) {
            // Parameter variable is lowercase of the type name by convention: Type -> type, or use the predicate's implicit 'it' variable
            return { varName: m[1][0].toLowerCase() + m[1].slice(1), typeName: m[1] };
        }
        m = text.match(/^(?:export\s+)?predicate\s+\w+\s*\(([A-Z][a-zA-Z0-9_]*)\s*:\s*\w+\)/);
        if (m) {
            return { varName: m[1][0].toLowerCase() + m[1].slice(1), typeName: m[1] };
        }
        // Stop at another top-level declaration
        if (i < position.line && /^(?:export\s+)?(?:predicate|function|command|let|type|flags)\b/.test(text)) break;
    }
    return undefined;
}

/** Resolve a foreach iteration variable to its element type */
function resolveForeachVariable(doc, position, word, symbols) {
    for (let i = position.line; i >= 0; i--) {
        const text = doc.lineAt(i).text.trim();
        const m = text.match(/^foreach\s+([A-Za-z_][A-Za-z0-9_-]*)\s+in\s+(.+)/);
        if (m && m[1] === word) {
            const collectionExpr = m[2].trim();
            const collType = inferExprType(collectionExpr, symbols);
            if (collType && isCollection(collType)) {
                return elementType(collType);
            }
            return collType || 'unknown';
        }
    }
    return undefined;
}

function escapeRegex(str) {
    return str.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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
    context.subscriptions.push(
        vscode.languages.registerHoverProvider(
            { language: 'cop', scheme: 'file' },
            hoverProvider
        )
    );

    // Clear package cache when .cop files are saved (types may have changed)
    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument(doc => {
            if (doc.fileName.endsWith('.cop') || doc.fileName.endsWith('.md')) {
                _packageCache.clear();
            }
        })
    );
}

function deactivate() {}

module.exports = {
    activate,
    deactivate,
    // Exported for testing
    _testing: {
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
        parsePackageInfo,
        resolveCollectionElementType,
        isCollection,
        isString,
        isNumeric,
        elementType,
        stripNullable,
        TYPES,
        STRING_PREDICATES,
        NUMERIC_PREDICATES,
        COLLECTION_PREDICATES,
        COLLECTION_PROPERTIES,
        COLLECTION_TRANSFORMS,
        STRING_PROPERTIES,
        STRING_TRANSFORMS,
    }
};
