// @ts-check
/// <reference types="vscode" />
'use strict';

const vscode = require('vscode');

// ── Language server client (live diagnostics from the real cop compiler) ────
//
// The extension does NOT reimplement the compiler for diagnostics. It launches `cop
// langserver` — an LSP server backed by the EXACT same parse/bind/type-check pipeline as
// `cop verify` — and renders the diagnostics it publishes. Everything below is generic
// JSON-RPC plumbing only; no language semantics live here, so the squiggles/Problems can
// never drift from the compiler. (Hover/completion remain provided above for now.)

const { spawn: _defaultSpawn } = require('child_process');

/** Splits a growing byte Buffer into complete LSP messages (Content-Length framed). */
function createMessageBuffer() {
    let buffer = Buffer.alloc(0);
    return {
        push(chunk, onMessage) {
            buffer = Buffer.concat([buffer, chunk]);
            while (true) {
                const headerEnd = buffer.indexOf('\r\n\r\n');
                if (headerEnd < 0) return;
                const header = buffer.slice(0, headerEnd).toString('ascii');
                const m = /Content-Length:\s*(\d+)/i.exec(header);
                if (!m) { buffer = buffer.slice(headerEnd + 4); continue; }
                const len = parseInt(m[1], 10);
                const start = headerEnd + 4;
                if (buffer.length < start + len) return;
                const body = buffer.slice(start, start + len).toString('utf8');
                buffer = buffer.slice(start + len);
                let msg;
                try { msg = JSON.parse(body); } catch (e) { continue; }
                onMessage(msg);
            }
        }
    };
}

/** Maps a server LSP diagnostic to a vscode.Diagnostic (severity: 1=Error,2=Warn,3=Info,4=Hint). */
function lspToVscodeDiagnostic(d) {
    const r = d.range || { start: { line: 0, character: 0 }, end: { line: 0, character: 1 } };
    const range = new vscode.Range(r.start.line, r.start.character, r.end.line, r.end.character);
    const severity =
        d.severity === 1 ? vscode.DiagnosticSeverity.Error :
        d.severity === 2 ? vscode.DiagnosticSeverity.Warning :
        d.severity === 3 ? vscode.DiagnosticSeverity.Information :
        vscode.DiagnosticSeverity.Hint;
    const diag = new vscode.Diagnostic(range, d.message || '', severity);
    diag.source = d.source || 'cop';
    return diag;
}

/** A thin LSP client: spawns `cop langserver`, syncs documents, renders publishDiagnostics. */
class CopLanguageClient {
    constructor(diagnostics, output) {
        this.diagnostics = diagnostics;
        this.output = output;
        this.proc = null;
        this.messages = createMessageBuffer();
        this.seq = 0;
        this._debounce = new Map();
        this._pending = new Map();
    }

    start(serverPath, spawnFn) {
        const spawnImpl = spawnFn || _defaultSpawn;
        try {
            this.proc = spawnImpl(serverPath, ['langserver'], { stdio: ['pipe', 'pipe', 'pipe'] });
        } catch (e) {
            this._log(`failed to start '${serverPath} langserver': ${e.message}`);
            this.proc = null;
            return false;
        }
        this.proc.on('error', e => this._log(`process error: ${e.message}`));
        if (this.proc.stderr) this.proc.stderr.on('data', d => this._log(`[server] ${d.toString().trimEnd()}`));
        this.proc.stdout.on('data', d => this.messages.push(d, m => this._onMessage(m)));
        this.proc.on('exit', code => { this._log(`server exited (code ${code})`); this.proc = null; });
        this._send({ jsonrpc: '2.0', id: ++this.seq, method: 'initialize', params: { processId: process.pid, capabilities: {} } });
        this._notify('initialized', {});
        return true;
    }

    _onMessage(msg) {
        // Response to a request we sent (hover, etc.) — resolve the pending promise.
        if (msg && msg.id !== undefined && msg.id !== null && this._pending.has(msg.id)) {
            const resolve = this._pending.get(msg.id);
            this._pending.delete(msg.id);
            resolve(msg.error ? null : (msg.result ?? null));
            return;
        }
        if (msg && msg.method === 'textDocument/publishDiagnostics' && msg.params) {
            const uri = vscode.Uri.parse(msg.params.uri);
            const diags = (msg.params.diagnostics || []).map(lspToVscodeDiagnostic);
            this.diagnostics.set(uri, diags);
        }
    }

    /** Sends a request and resolves with its result (or null on error/timeout/no server). */
    sendRequest(method, params) {
        if (!this.proc) return Promise.resolve(null);
        const id = ++this.seq;
        return new Promise((resolve) => {
            const timer = setTimeout(() => {
                if (this._pending.has(id)) { this._pending.delete(id); resolve(null); }
            }, 3000);
            if (timer && typeof timer.unref === 'function') timer.unref();
            this._pending.set(id, (value) => { clearTimeout(timer); resolve(value); });
            this._send({ jsonrpc: '2.0', id, method, params });
        });
    }

    _send(msg) {
        if (!this.proc || !this.proc.stdin) return;
        const body = Buffer.from(JSON.stringify(msg), 'utf8');
        try {
            this.proc.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
            this.proc.stdin.write(body);
        } catch (e) { this._log(`write failed: ${e.message}`); }
    }

    _notify(method, params) { this._send({ jsonrpc: '2.0', method, params }); }

    didOpen(doc) {
        this._notify('textDocument/didOpen', {
            textDocument: { uri: doc.uri.toString(), languageId: 'cop', version: doc.version || 1, text: doc.getText() }
        });
    }

    didChange(doc) {
        // Re-analysis parses the whole directory; debounce so it doesn't run on every keystroke.
        const key = doc.uri.toString();
        const prev = this._debounce.get(key);
        if (prev) clearTimeout(prev);
        this._debounce.set(key, setTimeout(() => {
            this._debounce.delete(key);
            this._notify('textDocument/didChange', {
                textDocument: { uri: key, version: doc.version || 0 },
                contentChanges: [{ text: doc.getText() }]
            });
        }, 250));
    }

    didClose(doc) {
        const key = doc.uri.toString();
        const prev = this._debounce.get(key);
        if (prev) { clearTimeout(prev); this._debounce.delete(key); }
        this._notify('textDocument/didClose', { textDocument: { uri: key } });
        this.diagnostics.delete(doc.uri);
    }

    stop() {
        for (const t of this._debounce.values()) clearTimeout(t);
        this._debounce.clear();
        if (this.proc) {
            try { this._send({ jsonrpc: '2.0', id: ++this.seq, method: 'shutdown' }); this._notify('exit', {}); } catch (e) {}
            try { this.proc.kill(); } catch (e) {}
            this.proc = null;
        }
    }

    _log(message) { if (this.output && this.output.appendLine) this.output.appendLine(`[cop] ${message}`); }
}

/** Starts the language server and wires document-sync events. Returns the client (or null). */
function startLanguageServer(context) {
    const config = vscode.workspace.getConfiguration ? vscode.workspace.getConfiguration('cop') : null;
    const enabled = config && config.get ? config.get('languageServer.enabled', true) : true;
    if (enabled === false) return null;

    const diagnostics = vscode.languages.createDiagnosticCollection('cop');
    context.subscriptions.push(diagnostics);
    const output = vscode.window.createOutputChannel('cop');
    context.subscriptions.push(output);

    const serverPath = (config && config.get ? config.get('languageServer.path', 'cop') : 'cop') || 'cop';
    const client = new CopLanguageClient(diagnostics, output);
    if (!client.start(serverPath)) {
        output.appendLine(`[cop] language server disabled: could not start '${serverPath} langserver'. ` +
            `Set "cop.languageServer.path" to your cop executable, or "cop.languageServer.enabled" to false.`);
        return null;
    }

    for (const doc of vscode.workspace.textDocuments || []) {
        if (doc.languageId === 'cop') client.didOpen(doc);
    }
    context.subscriptions.push(
        vscode.workspace.onDidOpenTextDocument(doc => { if (doc.languageId === 'cop') client.didOpen(doc); }),
        vscode.workspace.onDidChangeTextDocument(e => { if (e.document.languageId === 'cop') client.didChange(e.document); }),
        vscode.workspace.onDidCloseTextDocument(doc => { if (doc.languageId === 'cop') client.didClose(doc); }),
        { dispose: () => client.stop() }
    );

    // Hover + completion come from the compiler via the server (replaces the JS reimplementations).
    context.subscriptions.push(
        vscode.languages.registerHoverProvider({ language: 'cop', scheme: 'file' }, makeServerHoverProvider(client)),
        vscode.languages.registerCompletionItemProvider(
            { language: 'cop', scheme: 'file' }, makeServerCompletionProvider(client), '.', ':', ' ')
    );
    return client;
}

/** A vscode HoverProvider that asks the language server (the real compiler) for hover content. */
function makeServerHoverProvider(client) {
    return {
        async provideHover(document, position) {
            const res = await client.sendRequest('textDocument/hover', {
                textDocument: { uri: document.uri.toString() },
                position: { line: position.line, character: position.character },
            });
            if (res && res.contents && res.contents.value) {
                return new vscode.Hover(new vscode.MarkdownString(res.contents.value));
            }
            return null;
        }
    };
}

/** A vscode CompletionItemProvider backed by the language server (the real compiler). */
function makeServerCompletionProvider(client) {
    return {
        async provideCompletionItems(document, position) {
            const res = await client.sendRequest('textDocument/completion', {
                textDocument: { uri: document.uri.toString() },
                position: { line: position.line, character: position.character },
            });
            const items = Array.isArray(res) ? res : (res && res.items) || [];
            return items.map(it => {
                // The server sends LSP CompletionItemKind (1-based); vscode's enum is 0-based.
                const kind = typeof it.kind === 'number' ? it.kind - 1 : undefined;
                const item = new vscode.CompletionItem(it.label, kind);
                if (it.detail) item.detail = it.detail;
                return item;
            });
        }
    };
}


function activate(context) {
    // All language intelligence (diagnostics, hover, completion) comes from the cop compiler
    // via `cop langserver` (LSP over stdio). Syntax highlighting is the TextMate grammar.
    // The editor no longer reimplements the compiler in JavaScript.
    startLanguageServer(context);
}

function deactivate() {}

module.exports = {
    activate,
    deactivate,
    // Exported for testing (LSP transport plumbing only).
    _testing: {
        CopLanguageClient,
        createMessageBuffer,
        lspToVscodeDiagnostic,
        startLanguageServer,
        makeServerHoverProvider,
        makeServerCompletionProvider,
    }
};
