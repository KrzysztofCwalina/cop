// TextMate grammar tokenization tests for the cop language.
//
// These guard the colorizer the same way the other suite guards IntelliSense. The motivating bug:
// a lone `#` line used to start a (non-existent) "block comment" that swallowed every following
// line to EOF, leaving whole files uncolored. We tokenize real snippets through the actual grammar
// and assert code after a lone `#` is still colored as code.
'use strict';

const fs = require('fs');
const path = require('path');
const vsctm = require('vscode-textmate');
const oniguruma = require('vscode-oniguruma');

let _registryPromise;

function getRegistry() {
    if (_registryPromise) return _registryPromise;
    const wasmPath = require.resolve('vscode-oniguruma/release/onig.wasm');
    const wasmBin = fs.readFileSync(wasmPath).buffer;
    const onigLib = oniguruma.loadWASM(wasmBin).then(() => ({
        createOnigScanner: patterns => new oniguruma.OnigScanner(patterns),
        createOnigString: s => new oniguruma.OnigString(s),
    }));
    _registryPromise = Promise.resolve(new vsctm.Registry({
        onigLib,
        loadGrammar: async (scopeName) => {
            if (scopeName !== 'source.cop') return null;
            const grammarPath = path.join(__dirname, 'syntaxes', 'cop.tmLanguage.json');
            const content = fs.readFileSync(grammarPath, 'utf8');
            return vsctm.parseRawGrammar(content, grammarPath);
        },
    }));
    return _registryPromise;
}

/** Tokenize lines, carrying rule state across lines. Returns [[{text, scopes}], ...]. */
async function tokenize(lines) {
    const registry = await getRegistry();
    const grammar = await registry.loadGrammar('source.cop');
    let ruleStack = vsctm.INITIAL;
    const out = [];
    for (const line of lines) {
        const r = grammar.tokenizeLine(line, ruleStack);
        out.push(r.tokens.map(t => ({ text: line.substring(t.startIndex, t.endIndex), scopes: t.scopes })));
        ruleStack = r.ruleStack;
    }
    return out;
}

/** Find the first token whose trimmed text equals `text` across all tokenized lines. */
function findToken(tokenized, text) {
    for (const lineTokens of tokenized) {
        for (const tok of lineTokens) {
            if (tok.text.trim() === text) return tok;
        }
    }
    return undefined;
}

const hasScope = (tok, scope) => !!tok && tok.scopes.some(s => s === scope);
const anyCommentScope = tok => !!tok && tok.scopes.some(s => s.startsWith('comment.'));

describe('grammar: lone # line does not swallow code (colorization regression)', () => {
    test('code between two lone # lines is still colored as code, not a block comment', async () => {
        const tokenized = await tokenize([
            '# header comment',
            '#',
            "let interface-violations = codebase.Types:isPublic",
            '#',
            'let other = codebase.Methods',
        ]);
        const letTok = findToken([tokenized[2]], 'let');
        expect(hasScope(letTok, 'keyword.control.cop')).toBe(true);
        expect(anyCommentScope(letTok)).toBe(false);

        // The other let after the second lone # must also be code.
        const otherLet = findToken([tokenized[4]], 'let');
        expect(hasScope(otherLet, 'keyword.control.cop')).toBe(true);
    });

    test('a single unmatched lone # does not comment out the rest of the file', async () => {
        const tokenized = await tokenize([
            '#',
            'predicate isX(Type) => Type.Name:startsWith(\'I\')',
        ]);
        const predTok = findToken([tokenized[1]], 'predicate');
        expect(hasScope(predTok, 'storage.type.predicate.cop')).toBe(true);
        expect(anyCommentScope(predTok)).toBe(false);
    });

    test('a normal # comment line is still a comment', async () => {
        const tokenized = await tokenize(['# just a comment']);
        const hashTok = tokenized[0][0];
        expect(hashTok.scopes.some(s => s.startsWith('comment.line'))).toBe(true);
    });
});

describe('grammar: keyword lists are metadata-driven', () => {
    test('generated control keywords (test, RUN) are colored', async () => {
        const t = await tokenize(['test has-types = assert(true)']);
        expect(hasScope(findToken(t, 'test'), 'keyword.control.cop')).toBe(true);

        const r = await tokenize(['command MAIN = RUN(OTHER)']);
        expect(hasScope(findToken(r, 'RUN'), 'keyword.control.cop')).toBe(true);
    });

    test('declaration keywords are colored as storage', async () => {
        const t = await tokenize(['enum Color = Red | Green']);
        // enum-definition colors the keyword; assert it is a storage scope.
        expect(findToken(t, 'enum').scopes.some(s => s.startsWith('storage.'))).toBe(true);
    });

    test('constants true/false/nic are colored', async () => {
        const t = await tokenize(['let a = true', 'let b = nic']);
        expect(hasScope(findToken([t[0]], 'true'), 'constant.language.cop')).toBe(true);
        expect(hasScope(findToken([t[1]], 'nic'), 'constant.language.cop')).toBe(true);
    });
});

describe('grammar: role-based action coloring (no hardcoded names)', () => {
    test('an UPPERCASE call like CHECK( is colored as a builtin action', async () => {
        const t = await tokenize(['command MAIN = CHECK(all-violations)']);
        expect(hasScope(findToken(t, 'CHECK'), 'support.function.builtin.cop')).toBe(true);
    });

    test('a PascalCase type is NOT colored as an action', async () => {
        const t = await tokenize(['let x = codebase.Types:isPublic']);
        const typeTok = findToken(t, 'Types');
        expect(hasScope(typeTok, 'support.function.builtin.cop')).toBe(false);
    });
});
