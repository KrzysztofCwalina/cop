// Mock for the 'vscode' module used in extension.js
'use strict';

const CompletionItemKind = {
    Method: 0,
    Function: 1,
    Constructor: 2,
    Field: 3,
    Variable: 4,
    Class: 5,
    Interface: 6,
    Module: 7,
    Property: 8,
    Unit: 9,
    Value: 10,
    Enum: 11,
    Keyword: 12,
    Snippet: 13,
    Color: 14,
    File: 15,
    Reference: 16,
    Folder: 17,
    EnumMember: 18,
    Constant: 19,
};

class CompletionItem {
    constructor(label, kind) {
        this.label = label;
        this.kind = kind;
        this.detail = '';
    }
}

class MarkdownString {
    constructor(value) {
        this.value = value || '';
    }
    appendCodeblock(code, lang) {
        this.value += `\`\`\`${lang || ''}\n${code}\n\`\`\`\n`;
        return this;
    }
    appendMarkdown(md) {
        this.value += md;
        return this;
    }
}

class Hover {
    constructor(contents, range) {
        this.contents = contents;
        this.range = range;
    }
}

class Position {
    constructor(line, character) {
        this.line = line;
        this.character = character;
    }
}

class Range {
    constructor(startOrStartLine, startCharacterOrEnd, endLine, endCharacter) {
        if (typeof startOrStartLine === 'number') {
            this.start = new Position(startOrStartLine, startCharacterOrEnd);
            this.end = new Position(endLine, endCharacter);
        } else {
            this.start = startOrStartLine;
            this.end = startCharacterOrEnd;
        }
    }
}

class Location {
    constructor(uri, range) {
        this.uri = uri;
        this.range = range;
    }
}

const languages = {
    registerCompletionItemProvider: () => ({ dispose: () => {} }),
    registerHoverProvider: () => ({ dispose: () => {} }),
    registerDefinitionProvider: () => ({ dispose: () => {} }),
    createDiagnosticCollection: (name) => {
        const map = new Map();
        return {
            name,
            set: (uri, diags) => map.set(String(uri), diags),
            delete: (uri) => map.delete(String(uri)),
            clear: () => map.clear(),
            dispose: () => map.clear(),
            // test helper
            _get: (uri) => map.get(String(uri)),
        };
    },
};

const DiagnosticSeverity = { Error: 0, Warning: 1, Information: 2, Hint: 3 };

class Diagnostic {
    constructor(range, message, severity) {
        this.range = range;
        this.message = message;
        this.severity = severity;
        this.source = '';
    }
}

const Uri = {
    parse: (s) => ({ _uri: s, toString: () => s }),
    file: (p) => ({ _uri: 'file://' + p, fsPath: p, toString: () => 'file://' + p }),
};

const window = {
    createOutputChannel: (name) => ({ name, appendLine: () => {}, dispose: () => {} }),
};

const workspace = {
    onDidSaveTextDocument: () => ({ dispose: () => {} }),
    onDidOpenTextDocument: () => ({ dispose: () => {} }),
    onDidChangeTextDocument: () => ({ dispose: () => {} }),
    onDidCloseTextDocument: () => ({ dispose: () => {} }),
    getConfiguration: () => ({ get: (_key, def) => def }),
    textDocuments: [],
};

module.exports = {
    CompletionItemKind,
    CompletionItem,
    MarkdownString,
    Hover,
    Position,
    Range,
    Location,
    Diagnostic,
    DiagnosticSeverity,
    Uri,
    languages,
    window,
    workspace,
};
