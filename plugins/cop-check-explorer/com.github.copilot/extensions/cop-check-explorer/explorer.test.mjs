import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { startExplorer } from "./explorer.mjs";

const workspace = await mkdtemp(join(tmpdir(), "cop-check-explorer-test-"));
await mkdir(join(workspace, "analysis rules"));
await writeFile(join(workspace, "rules.cop"), "");
after(() => rm(workspace, { recursive: true, force: true }));

function catalogue({ warnings = [] } = {}) {
    const data = {
        checks: [
            { id: "rule:a", name: "requires-name", description: "Types require names", filePath: "rules.cop", line: 4, source: "let a = []", origin: "workspace" },
            { id: "rule:b", name: "requires-docs", description: "Types require documentation", filePath: "rules.cop", line: 8, source: "let b = []", origin: "workspace" },
        ],
        groups: [{ id: "group:workspace", name: "Workspace", checkIds: ["rule:a", "rule:b"], groups: [] }],
        commands: [{ id: "main", name: "MAIN", checkIds: ["rule:a", "rule:b"] }],
        warnings,
    };
    return {
        html: `<html><head></head><body><script id="catalogue-data" type="application/json">${JSON.stringify(data)}</script></body></html>`,
        exitCode: warnings.length ? 1 : 0,
        diagnostics: warnings.join("\n"),
    };
}

test("serves the shared renderer for an explicitly selected arbitrary program path", async t => {
    let seen;
    const explorer = await startExplorer({
        inputPath: "analysis rules",
        cwd: workspace,
        generate: async (...args) => { seen = args; return catalogue(); },
    });
    t.after(() => explorer.close());
    assert.ok(seen[0].endsWith("analysis rules"));
    assert.equal(seen[2], workspace);
    assert.equal(new URL(explorer.url).hostname, "127.0.0.1");
    const html = await fetch(explorer.url);
    assert.equal(html.status, 200);
    assert.match(await html.text(), /<script defer src="client.js"><\/script>/);
    assert.match(html.headers.get("content-security-policy"), /connect-src 'self'/);
    for (const asset of ["client.js", "canvas.css"]) {
        assert.equal((await fetch(new URL(asset, explorer.url))).status, 200);
    }
    const state = await (await fetch(new URL("state", explorer.url))).json();
    assert.equal(state.checks, 2);
    assert.equal(state.commands, 1);
    assert.deepEqual(state.warnings, []);
});

test("search and navigation update the panel, including actions before its first connection", async t => {
    const explorer = await startExplorer({ inputPath: "rules.cop", cwd: workspace, generate: async () => catalogue() });
    t.after(() => explorer.close());
    assert.deepEqual(explorer.search("DOCUMENTATION").checks.map(check => check.id), ["rule:b"]);
    assert.deepEqual(explorer.search("main").checks.map(check => check.id), ["rule:a", "rule:b"]);
    assert.equal(explorer.navigate("rule:a").name, "requires-name");
    assert.throws(() => explorer.navigate("missing"), /Unknown check or group ID/);
    assert.equal(explorer.navigate("group:workspace").id, "group:workspace");
    const response = await fetch(new URL("events", explorer.url));
    const reader = response.body.getReader();
    t.after(() => reader.cancel());
    const initial = new TextDecoder().decode((await reader.read()).value);
    assert.match(initial, /"view":\{"query":"","node":"group:workspace"\}/);
    explorer.navigate("rule:b");
    const update = new TextDecoder().decode((await reader.read()).value);
    assert.match(update, /event: view/);
    assert.match(update, /"node":"rule:b"/);
    await reader.cancel();
});

test("refresh updates data and exposes analysis warnings rather than hiding them", async t => {
    let count = 0;
    const explorer = await startExplorer({
        inputPath: ".",
        cwd: process.cwd(),
        generate: async () => catalogue({ warnings: count++ ? ["Unresolved import: example"] : [] }),
    });
    t.after(() => explorer.close());
    const response = await fetch(new URL("refresh", explorer.url), {
        method: "POST",
        headers: { Origin: new URL(explorer.url).origin, "X-Cop-Canvas": "1" },
    });
    assert.equal(response.status, 200);
    const state = await response.json();
    assert.equal(state.revision, 2);
    assert.deepEqual(state.warnings, ["Unresolved import: example"]);
    assert.equal(state.diagnostics, "Unresolved import: example");
});

test("a failed refresh reports the error and retains the previous successful catalogue", async t => {
    let count = 0;
    const explorer = await startExplorer({
        inputPath: ".",
        cwd: process.cwd(),
        generate: async () => {
            if (count++) throw new Error("Source contains a parse error");
            return catalogue();
        },
    });
    t.after(() => explorer.close());
    const response = await fetch(new URL("refresh", explorer.url), {
        method: "POST",
        headers: { Origin: new URL(explorer.url).origin, "X-Cop-Canvas": "1" },
    });
    assert.equal(response.status, 500);
    assert.deepEqual(await response.json(), { error: "Source contains a parse error" });
    assert.equal(explorer.summary().revision, 1);
    assert.equal(explorer.summary().checks, 2);
    assert.equal((await fetch(explorer.url)).status, 200);
});

test("an explicitly selected missing program reports an error without running Cop", async () => {
    let ran = false;
    await assert.rejects(
        startExplorer({ inputPath: "cop-checks", cwd: workspace, generate: async () => { ran = true; return catalogue(); } }),
        /No Cop program was found at .*cop-checks\./);
    assert.equal(ran, false);
});

test("an absent default opens a chooser and can load a program without an agent", async t => {
    let runs = 0;
    const explorer = await startExplorer({
        inputPath: "cop-checks", cwd: workspace, allowMissing: true,
        generate: async () => { runs++; return catalogue(); },
    });
    t.after(() => explorer.close());
    assert.equal(runs, 0);
    assert.match(await (await fetch(explorer.url)).text(), /Choose a Cop program file or folder/);
    assert.equal(explorer.summary().status, "Choose a Cop program");
    const headers = { Origin: new URL(explorer.url).origin, "X-Cop-Canvas": "1" };
    const select = path => fetch(new URL("select", explorer.url), {
        method: "POST", headers, body: JSON.stringify({ path }),
    });
    const selected = await select("analysis rules");
    assert.equal(selected.status, 200);
    assert.equal((await selected.json()).checks, 2);
    assert.equal(explorer.summary().path, join(workspace, "analysis rules"));
    assert.equal(runs, 1);
    const failed = await select("missing");
    assert.equal(failed.status, 500);
    assert.match((await failed.json()).error, /No Cop program was found/);
    assert.equal(explorer.summary().checks, 2);
    assert.equal(explorer.summary().path, join(workspace, "analysis rules"));
    assert.equal(runs, 1);
});

test("rejects foreign requests, tokenless reads and unknown routes", async t => {
    const explorer = await startExplorer({ inputPath: ".", cwd: process.cwd(), generate: async () => catalogue() });
    t.after(() => explorer.close());
    for (const headers of [{}, { Origin: "https://example.com", "X-Cop-Canvas": "1" }]) {
        assert.equal((await fetch(new URL("refresh", explorer.url), { method: "POST", headers })).status, 403);
        assert.equal((await fetch(new URL("select", explorer.url), { method: "POST", headers })).status, 403);
    }
    assert.equal((await fetch(new URL("/", explorer.url))).status, 404);
    assert.equal((await fetch(new URL("unknown", explorer.url))).status, 404);
});

test("invalid output and failed generation cannot masquerade as success", async () => {
    for (const result of [
        { html: "<html>Error</html>", exitCode: 0 },
        { ...catalogue(), exitCode: 1, diagnostics: "Generation failed" },
    ]) {
        await assert.rejects(startExplorer({ inputPath: ".", cwd: process.cwd(), generate: async () => result }),
            /Unsupported catalogue format|Generation failed/);
    }
});

test("overlapping refreshes share one generation and closing stops the server", async () => {
    let runs = 0;
    let finish;
    let started;
    const startedPromise = new Promise(done => { started = done; });
    const explorer = await startExplorer({
        inputPath: ".",
        cwd: process.cwd(),
        generate: async () => {
            if (runs++) await new Promise(done => { finish = done; started(); });
            return catalogue();
        },
    });
    try {
        const first = explorer.refresh();
        const second = explorer.refresh();
        assert.equal(first, second);
        await startedPromise;
        finish();
        assert.equal((await first).revision, 2);
        assert.equal(runs, 2);
    } finally {
        await explorer.close();
    }
    await assert.rejects(explorer.refresh(), /closed/);
    await assert.rejects(fetch(explorer.url), /fetch failed/);
});

function folderCatalogue(folder) {
    const data = {
        checks: [
            { id: `workspace:${folder}/a.cop:1:${folder}-check`, name: `${folder}-check`, description: "", filePath: `${folder}/a.cop`, line: 1, source: "", origin: "workspace" },
            { id: "workspace:main.cop:1:root-check", name: "root-check", description: "", filePath: "main.cop", line: 1, source: "", origin: "workspace" },
            { id: "pkg:src/p.cop:1:package-check", name: "package-check", description: "", filePath: "src/p.cop", line: 1, source: "", origin: "pkg" },
        ],
        groups: [
            { id: `group:workspace:${folder}`, name: folder, path: folder, origin: "workspace", checkIds: [`workspace:${folder}/a.cop:1:${folder}-check`], groups: [] },
            { id: "group:workspace:", name: "Repository", path: "", origin: "workspace", checkIds: ["workspace:main.cop:1:root-check"], groups: [] },
            { id: "group:pkg:pkg", name: "pkg", path: "pkg", origin: "pkg", checkIds: ["pkg:src/p.cop:1:package-check"], groups: [] },
        ],
        commands: [],
        warnings: [],
    };
    return { html: `<html><head></head><body><script id="catalogue-data" type="application/json">${JSON.stringify(data)}</script></body></html>`, exitCode: 0, diagnostics: "" };
}

test("rename-check runs the Cop renamer on the program folder and lands on the renamed check", async t => {
    const program = await mkdtemp(join(tmpdir(), "cop-check-explorer-rename-"));
    t.after(() => rm(program, { recursive: true, force: true }));
    let renamed = false;
    let call;
    const explorer = await startExplorer({
        inputPath: program, cwd: workspace,
        generate: async () => {
            const result = folderCatalogue("rules");
            if (renamed) result.html = result.html.replaceAll("root-check", "better-name");
            return result;
        },
        renameCheck: async (...args) => { call = args; renamed = true; return "Renamed."; },
    });
    t.after(() => explorer.close());
    const headers = { Origin: new URL(explorer.url).origin, "X-Cop-Canvas": "1" };
    const response = await fetch(new URL("rename-check", explorer.url), {
        method: "POST", headers, body: JSON.stringify({ id: "workspace:main.cop:1:root-check", newName: " better-name " }),
    });
    assert.equal(response.status, 200);
    const state = await response.json();
    assert.deepEqual(call, ["root-check", "better-name", program, workspace]);
    assert.equal(state.id, "workspace:main.cop:1:better-name");
    assert.deepEqual(explorer.summary().view, { query: "", node: "workspace:main.cop:1:better-name" });
    assert.equal(state.message, "Renamed.");
    await assert.rejects(explorer.renameCheck("pkg:src/p.cop:1:package-check", "x"), /'pkg' package\. Package checks are read-only/);
    await assert.rejects(explorer.renameCheck("missing", "x"), /Unknown check ID/);
    assert.equal((await fetch(new URL("rename-check", explorer.url), { method: "POST", body: "{}" })).status, 403);
});

test("rename-group moves a program folder and refuses package, top-level and conflicting targets", async t => {
    const program = await mkdtemp(join(tmpdir(), "cop-check-explorer-rename-"));
    t.after(() => rm(program, { recursive: true, force: true }));
    await mkdir(join(program, "csharp"));
    await writeFile(join(program, "csharp", "a.cop"), "");
    await mkdir(join(program, "taken"));
    let folder = "csharp";
    const explorer = await startExplorer({ inputPath: program, cwd: workspace, generate: async () => folderCatalogue(folder) });
    t.after(() => explorer.close());
    await assert.rejects(explorer.renameGroup("group:workspace:csharp", "taken"), /already exists/);
    await assert.rejects(explorer.renameGroup("group:workspace:csharp", "../escape"), /Folder names may contain/);
    await assert.rejects(explorer.renameGroup("group:workspace:", "x"), /top-level files/);
    await assert.rejects(explorer.renameGroup("group:pkg:pkg", "x"), /Package groups are read-only/);
    folder = "csharp-source";
    const state = await explorer.renameGroup("group:workspace:csharp", "csharp-source");
    assert.equal(state.id, "group:workspace:csharp-source");
    assert.equal(state.message, "Renamed folder csharp to csharp-source.");
    await assert.rejects(stat(join(program, "csharp")), /ENOENT/);
    assert.ok((await stat(join(program, "csharp-source", "a.cop"))).isFile());
});

test("rename-file moves only the source file, retains sibling declarations and refuses unsafe targets", async t => {
    const program = await mkdtemp(join(tmpdir(), "cop-explorer-files-"));
    t.after(() => rm(program, { recursive: true, force: true }));
    const source = "let a = []\nlet b = []\n";
    await writeFile(join(program, "rules.cop"), source);
    await writeFile(join(program, "taken.cop"), "unchanged");
    const explorer = await startExplorer({
        inputPath: program, cwd: workspace,
        generate: async () => {
            const result = catalogue();
            if (await stat(join(program, "renamed.cop")).then(() => true, error => {
                if (error.code !== "ENOENT") throw error;
                return false;
            })) {
                result.html = result.html.replaceAll("rules.cop", "renamed.cop");
            }
            return result;
        },
    });
    t.after(() => explorer.close());
    for (const name of ["../escape.cop", "rules.txt", "CON.cop", "bad/name.cop"]) {
        await assert.rejects(explorer.renameFile("rule:a", name), /valid filename/);
    }
    await assert.rejects(explorer.renameFile("missing", "new.cop"), /Unknown check ID/);
    await assert.rejects(explorer.renameFile("rule:a", "taken.cop"), /already exists/);
    const response = await fetch(new URL("rename-file", explorer.url), {
        method: "POST",
        headers: { Origin: new URL(explorer.url).origin, "X-Cop-Canvas": "1", "Content-Type": "application/json" },
        body: JSON.stringify({ id: "rule:a", newName: "renamed.cop" }),
    });
    assert.equal(response.status, 200);
    assert.equal((await response.json()).id, "rule:a");
    assert.equal(await readFile(join(program, "renamed.cop"), "utf8"), source);
    assert.equal(await readFile(join(program, "taken.cop"), "utf8"), "unchanged");
    await assert.rejects(stat(join(program, "rules.cop")), /ENOENT/);
    const data = await (await fetch(explorer.url)).text();
    assert.equal((data.match(/"filePath":"renamed.cop"/g) ?? []).length, 2);
});

test("rename-file follows a selected program file and keeps imported files read-only", async t => {
    const program = await mkdtemp(join(tmpdir(), "cop-explorer-selected-file-"));
    t.after(() => rm(program, { recursive: true, force: true }));
    await writeFile(join(program, "rules.cop"), "");
    let selected;
    const explorer = await startExplorer({
        inputPath: join(program, "rules.cop"), cwd: workspace,
        generate: async path => {
            selected = path;
            const result = catalogue();
            result.html = result.html.replace('"origin":"workspace"', '"origin":"package"');
            return result;
        },
    });
    t.after(() => explorer.close());
    await assert.rejects(explorer.renameFile("rule:a", "new.cop"), /Package files are read-only/);
    const state = await explorer.renameFile("rule:b", "new.cop");
    assert.equal(state.path, join(program, "new.cop"));
    assert.equal(selected, join(program, "new.cop"));
});
