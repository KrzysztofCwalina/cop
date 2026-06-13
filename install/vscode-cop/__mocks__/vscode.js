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

const languages = {
    registerCompletionItemProvider: () => ({ dispose: () => {} }),
    registerHoverProvider: () => ({ dispose: () => {} }),
};

const workspace = {
    onDidSaveTextDocument: () => ({ dispose: () => {} }),
};

module.exports = {
    CompletionItemKind,
    CompletionItem,
    MarkdownString,
    Hover,
    Position,
    Range,
    languages,
    workspace,
};
