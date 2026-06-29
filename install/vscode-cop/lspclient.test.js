// Tests for the dependency-free LSP client embedded in the extension.
// These lock in the JSON-RPC transport plumbing (framing, document sync, diagnostic
// rendering). The diagnostics themselves come from the real `cop langserver`, tested in C#.
'use strict';

jest.mock('vscode');

const vscode = require('vscode');
const { EventEmitter } = require('events');
const ext = require('./extension.js');
const { createMessageBuffer, lspToVscodeDiagnostic, CopLanguageClient } = ext._testing;

function frame(obj) {
    const body = Buffer.from(JSON.stringify(obj), 'utf8');
    return Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]);
}

function fakeProc() {
    const proc = new EventEmitter();
    proc.stdin = {
        writes: [],
        write(s) { this.writes.push(Buffer.isBuffer(s) ? s : Buffer.from(s)); },
    };
    proc.stdout = new EventEmitter();
    proc.stderr = new EventEmitter();
    proc.killed = false;
    proc.kill = () => { proc.killed = true; };
    return proc;
}

function decodeSent(proc) {
    const buf = createMessageBuffer();
    const sent = [];
    buf.push(Buffer.concat(proc.stdin.writes), m => sent.push(m));
    return sent;
}

describe('LSP message framing', () => {
    test('parses a single complete message', () => {
        const buf = createMessageBuffer();
        const got = [];
        buf.push(frame({ a: 1 }), m => got.push(m));
        expect(got).toEqual([{ a: 1 }]);
    });

    test('parses two messages delivered in one chunk', () => {
        const buf = createMessageBuffer();
        const got = [];
        buf.push(Buffer.concat([frame({ a: 1 }), frame({ b: 2 })]), m => got.push(m));
        expect(got).toEqual([{ a: 1 }, { b: 2 }]);
    });

    test('parses a message split across chunks (partial header, then body)', () => {
        const buf = createMessageBuffer();
        const f = frame({ hello: 'world' });
        const got = [];
        buf.push(f.slice(0, 8), m => got.push(m));
        expect(got).toEqual([]);
        buf.push(f.slice(8), m => got.push(m));
        expect(got).toEqual([{ hello: 'world' }]);
    });
});

describe('lspToVscodeDiagnostic', () => {
    test('maps error severity, range, message and source', () => {
        const d = lspToVscodeDiagnostic({
            range: { start: { line: 2, character: 1 }, end: { line: 2, character: 6 } },
            severity: 1, source: 'cop', message: 'boom',
        });
        expect(d.message).toBe('boom');
        expect(d.severity).toBe(vscode.DiagnosticSeverity.Error);
        expect(d.source).toBe('cop');
        expect(d.range.start.line).toBe(2);
        expect(d.range.start.character).toBe(1);
        expect(d.range.end.character).toBe(6);
    });

    test('maps the LSP severity scale to vscode severities', () => {
        expect(lspToVscodeDiagnostic({ severity: 2, message: '' }).severity).toBe(vscode.DiagnosticSeverity.Warning);
        expect(lspToVscodeDiagnostic({ severity: 3, message: '' }).severity).toBe(vscode.DiagnosticSeverity.Information);
        expect(lspToVscodeDiagnostic({ severity: 9, message: '' }).severity).toBe(vscode.DiagnosticSeverity.Hint);
    });
});

describe('CopLanguageClient', () => {
    test('start sends initialize then initialized', () => {
        const collection = vscode.languages.createDiagnosticCollection('cop');
        const client = new CopLanguageClient(collection, { appendLine() {} });
        const proc = fakeProc();
        const ok = client.start('cop', () => proc);
        expect(ok).toBe(true);
        const sent = decodeSent(proc);
        expect(sent[0].method).toBe('initialize');
        expect(sent[1].method).toBe('initialized');
    });

    test('renders a publishDiagnostics notification into the diagnostic collection', () => {
        const collection = vscode.languages.createDiagnosticCollection('cop');
        const client = new CopLanguageClient(collection, { appendLine() {} });
        const proc = fakeProc();
        client.start('cop', () => proc);

        proc.stdout.emit('data', frame({
            jsonrpc: '2.0',
            method: 'textDocument/publishDiagnostics',
            params: {
                uri: 'file:///x/bad.cop',
                diagnostics: [{
                    range: { start: { line: 0, character: 0 }, end: { line: 0, character: 3 } },
                    severity: 1, source: 'cop', message: "Undefined variable 'foo'",
                }],
            },
        }));

        const diags = collection._get('file:///x/bad.cop');
        expect(diags).toBeDefined();
        expect(diags.length).toBe(1);
        expect(diags[0].message).toBe("Undefined variable 'foo'");
        expect(diags[0].severity).toBe(vscode.DiagnosticSeverity.Error);
    });

    test('didOpen sends a didOpen notification carrying the document text', () => {
        const collection = vscode.languages.createDiagnosticCollection('cop');
        const client = new CopLanguageClient(collection, { appendLine() {} });
        const proc = fakeProc();
        client.start('cop', () => proc);
        proc.stdin.writes.length = 0; // discard the handshake

        const doc = { uri: { toString: () => 'file:///x/a.cop' }, version: 1, getText: () => 'let x = 1\n' };
        client.didOpen(doc);

        const sent = decodeSent(proc);
        expect(sent[0].method).toBe('textDocument/didOpen');
        expect(sent[0].params.textDocument.uri).toBe('file:///x/a.cop');
        expect(sent[0].params.textDocument.text).toBe('let x = 1\n');
    });

    test('didChange debounces rapid edits into a single notification', () => {
        jest.useFakeTimers();
        try {
            const collection = vscode.languages.createDiagnosticCollection('cop');
            const client = new CopLanguageClient(collection, { appendLine() {} });
            const proc = fakeProc();
            client.start('cop', () => proc);
            proc.stdin.writes.length = 0;

            const doc = { uri: { toString: () => 'file:///x/a.cop' }, version: 2, getText: () => 'let x = 2\n' };
            client.didChange(doc);
            client.didChange(doc);
            client.didChange(doc);
            // Nothing sent before the debounce window elapses.
            expect(decodeSent(proc)).toEqual([]);

            jest.advanceTimersByTime(300);
            const sent = decodeSent(proc);
            expect(sent.length).toBe(1);
            expect(sent[0].method).toBe('textDocument/didChange');
            expect(sent[0].params.contentChanges[0].text).toBe('let x = 2\n');
        } finally {
            jest.useRealTimers();
        }
    });

    test('start returns false and logs when the executable cannot be spawned', () => {
        const collection = vscode.languages.createDiagnosticCollection('cop');
        const logs = [];
        const client = new CopLanguageClient(collection, { appendLine: (s) => logs.push(s) });
        const ok = client.start('cop', () => { throw new Error('ENOENT'); });
        expect(ok).toBe(false);
        expect(logs.join('\n')).toContain('ENOENT');
    });

    test('stop tries to terminate the process', () => {
        const collection = vscode.languages.createDiagnosticCollection('cop');
        const client = new CopLanguageClient(collection, { appendLine() {} });
        const proc = fakeProc();
        client.start('cop', () => proc);
        client.stop();
        expect(proc.killed).toBe(true);
    });
});
