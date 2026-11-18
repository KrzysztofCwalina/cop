import { execFile } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdtemp, readFile, realpath, rename, rm, stat } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";

const client = await readFile(new URL("./client.js", import.meta.url), "utf8");
const styles = await readFile(new URL("./canvas.css", import.meta.url), "utf8");

export async function generateCatalogue(inputPath, outputPath, cwd) {
    const result = await new Promise((resolveResult, reject) => {
        execFile("cop", ["checks", inputPath, "--output", outputPath],
            { cwd, windowsHide: true, timeout: 60_000, maxBuffer: 8 * 1024 * 1024 },
            (error, stdout, stderr) => {
                if (error && (error.code !== 1 || error.killed || error.signal)) {
                    reject(new Error(error.code === "ENOENT"
                        ? "Cop was not found on PATH. Install a version supporting 'cop checks', then reopen the canvas."
                        : `Could not generate the catalogue: ${stderr.trim() || error.message}`));
                } else {
                    resolveResult({ exitCode: error ? 1 : 0, diagnostics: stderr.trim() });
                }
            });
    });
    let html;
    try {
        html = await readFile(outputPath, "utf8");
    } catch (error) {
        throw new Error(`Cop did not produce a catalogue: ${result.diagnostics || error.message}`);
    }
    return { html, ...result };
}

export function renameCheckWithCop(oldName, newName, programRoot, cwd) {
    return new Promise((resolveResult, reject) => {
        execFile("cop", ["checks", "rename", oldName, newName, programRoot],
            { cwd, windowsHide: true, timeout: 120_000, maxBuffer: 8 * 1024 * 1024 },
            (error, stdout, stderr) => {
                if (error) {
                    reject(new Error(error.code === "ENOENT"
                        ? "Cop was not found on PATH. Install a version supporting 'cop checks rename'."
                        : stderr.trim() || stdout.trim() || error.message));
                } else {
                    resolveResult(stdout.trim());
                }
            });
    });
}

const folderNamePattern = /^[A-Za-z0-9_][A-Za-z0-9._-]*$/;

export function readCatalogue(html) {
    const payload = html.match(/<script id="catalogue-data" type="application\/json">([\s\S]*?)<\/script>/);
    if (!payload) throw new Error("Unsupported catalogue format. Update Cop to a version supporting the Check explorer.");
    const data = JSON.parse(payload[1]);
    if (!Array.isArray(data.checks) || !Array.isArray(data.groups) ||
        !Array.isArray(data.commands) || !Array.isArray(data.warnings)) {
        throw new Error("Cop returned an invalid catalogue.");
    }
    return data;
}

export async function startExplorer({
    inputPath, cwd, generate = generateCatalogue, renameCheck: runRename = renameCheckWithCop, allowMissing = false,
}) {
    let program = resolve(cwd, inputPath);
    async function checkPath(path) {
        try {
            const info = await stat(path);
            if (!info.isFile() && !info.isDirectory()) throw new Error(`Not a file or folder: ${path}`);
        } catch (error) {
            if (error.code === "ENOENT") throw new Error(`No Cop program was found at ${path}.`, { cause: error });
            throw error;
        }
    }
    let missing = false;
    try { await checkPath(program); }
    catch (error) {
        if (!allowMissing || error.cause?.code !== "ENOENT") throw error;
        missing = true;
    }
    const directory = await mkdtemp(join(tmpdir(), "cop-check-explorer-"));
    const clients = new Set();
    const base = `/${randomUUID()}/`;
    let current;
    let pending;
    let closed = false;
    let view;
    let revision = 0;
    let origin;

    function broadcast(type, value) {
        for (const response of clients) response.write(`event: ${type}\ndata: ${JSON.stringify(value)}\n\n`);
    }

    function summary() {
        return {
            path: program,
            checks: current?.data.checks.length ?? 0,
            commands: current?.data.commands.length ?? 0,
            warnings: current?.data.warnings ?? [],
            diagnostics: current?.diagnostics ?? `No Cop program was found at ${program}. Enter a file or folder to get started.`,
            generatedAt: current?.generatedAt,
            revision,
            view,
            status: current ? `${current.data.checks.length} checks, ${current.data.warnings.length} warnings` : "Choose a Cop program",
        };
    }

    async function regenerate(target, chooseNode) {
        // A fresh output path prevents an exit-1 failure from reusing an older successful catalogue.
        const outputPath = join(directory, `${randomUUID()}.html`);
        try {
            await checkPath(target);
            const result = await generate(target, outputPath, cwd);
            const data = readCatalogue(result.html);
            if (result.exitCode !== 0 && data.warnings.length === 0) {
                throw new Error(`Cop failed to generate a complete catalogue: ${result.diagnostics}`);
            }
            current = { ...result, data, generatedAt: new Date().toISOString() };
            program = target;
            // The view is set before broadcasting so reloaded panels land on the edited node.
            view = chooseNode ? { query: "", node: chooseNode(data) } : undefined;
            revision++;
            broadcast("reload", { revision });
            return summary();
        } finally {
            await rm(outputPath, { force: true });
        }
    }

    function refresh() {
        if (closed) return Promise.reject(new Error("The Check explorer is closed."));
        if (!pending) pending = regenerate(program).finally(() => { pending = undefined; });
        return pending;
    }

    function select(path) {
        if (closed) return Promise.reject(new Error("The Check explorer is closed."));
        if (pending) return Promise.reject(new Error("A catalogue update is already in progress."));
        if (typeof path !== "string" || !path.trim() || path.includes("\0")) {
            return Promise.reject(new Error("Enter a Cop program file or folder."));
        }
        pending = regenerate(resolve(cwd, path)).finally(() => { pending = undefined; });
        return pending;
    }

    async function programRoot() {
        return (await stat(program)).isDirectory() ? program : dirname(program);
    }

    function findGroup(groups, id) {
        for (const group of groups) {
            if (group.id === id) return group;
            const child = findGroup(group.groups, id);
            if (child) return child;
        }
        return undefined;
    }

    // Edits run one at a time and never overlap a catalogue regeneration.
    function edit(operation) {
        if (closed) return Promise.reject(new Error("The Check explorer is closed."));
        if (pending) return Promise.reject(new Error("A catalogue update is already in progress."));
        if (!current) return Promise.reject(new Error("Select a Cop program first."));
        pending = operation().finally(() => { pending = undefined; });
        return pending;
    }

    function renameCheck(id, newName) {
        return edit(async () => {
            const check = current.data.checks.find(item => item.id === id);
            if (!check) throw new Error(`Unknown check ID: ${id}`);
            if (check.origin !== "workspace") {
                throw new Error(`'${check.name}' comes from the '${check.origin}' package. Package checks are read-only here.`);
            }
            if (typeof newName !== "string" || !newName.trim()) throw new Error("Enter a new check name.");
            const message = await runRename(check.name, newName.trim(), await programRoot(), cwd);
            const state = await regenerate(program, data =>
                data.checks.find(item => item.origin === "workspace" && item.name === newName.trim())?.id ?? "overview");
            return { ...state, message, id: view.node };
        });
    }

    function renameGroup(id, newName) {
        return edit(async () => {
            const group = findGroup(current.data.groups, id);
            if (!group) throw new Error(`Unknown group ID: ${id}`);
            if (group.origin !== "workspace") {
                throw new Error(`'${group.name}' is the '${group.origin}' package, not a folder in this program. Package groups are read-only here.`);
            }
            const name = typeof newName === "string" ? newName.trim() : "";
            if (!folderNamePattern.test(name) || name === "." || name === "..") {
                throw new Error("Folder names may contain letters, digits, '.', '_' and '-'.");
            }
            const root = await programRoot();
            const folder = resolve(root, group.path);
            const relativeFolder = relative(root, folder);
            const holdsTopLevelFiles = group.checkIds.some(checkId =>
                !current.data.checks.find(item => item.id === checkId)?.filePath.includes("/"));
            if (holdsTopLevelFiles || !relativeFolder || relativeFolder.startsWith("..") || isAbsolute(relativeFolder)) {
                throw new Error(`'${group.name}' holds the program's top-level files and cannot be renamed here.`);
            }
            const info = await stat(folder).catch(() => undefined);
            if (!info?.isDirectory()) throw new Error(`Folder not found: ${folder}`);
            const target = join(dirname(folder), name);
            if (await stat(target).then(() => true, () => false)) throw new Error(`A file or folder named '${name}' already exists.`);
            await rename(folder, target);
            const path = [...group.path.split("/").slice(0, -1), name].join("/");
            const state = await regenerate(program, () => `group:workspace:${path}`);
            return { ...state, message: `Renamed folder ${group.path} to ${path}.`, id: view.node };
        });
    }

    function renameFile(id, newName) {
        return edit(async () => {
            const check = current.data.checks.find(item => item.id === id);
            if (!check) throw new Error(`Unknown check ID: ${id}`);
            if (check.origin !== "workspace") throw new Error("Package files are read-only here.");
            const name = typeof newName === "string" ? newName.trim() : "";
            if (!folderNamePattern.test(name) || !name.endsWith(".cop") ||
                /[. ]$/.test(name) || /^(con|prn|aux|nul|com[0-9]|lpt[0-9])\./i.test(name)) {
                throw new Error("Enter a valid filename ending in '.cop', using letters, digits, '.', '_' and '-'.");
            }
            const root = await realpath(await programRoot());
            const file = await realpath(resolve(root, check.filePath));
            const relativeFile = relative(root, file);
            if (!relativeFile || relativeFile.startsWith("..") || isAbsolute(relativeFile)) {
                throw new Error("The source file must be inside this program.");
            }
            if (!(await stat(file)).isFile()) throw new Error("The source is not a file.");
            const target = join(dirname(file), name);
            try {
                await stat(target);
                throw new Error(`A file or folder named '${name}' already exists.`);
            } catch (error) {
                if (error.code !== "ENOENT") throw error;
            }
            const selectedFile = (await stat(program)).isFile() && await realpath(program) === file;
            await rename(file, target);
            const filePath = relative(root, target).replaceAll("\\", "/");
            const state = await regenerate(selectedFile ? target : program, data =>
                data.checks.find(item => item.origin === "workspace" &&
                    item.filePath === filePath && item.name === check.name)?.id ?? "overview");
            return { ...state, message: `Renamed file ${check.filePath} to ${filePath}.`, id: view.node };
        });
    }

    async function readJson(request) {
        let body = "";
        for await (const chunk of request) {
            body += chunk;
            if (body.length > 16384) throw new Error("Request is too large.");
        }
        try { return JSON.parse(body); }
        catch { throw new Error("Invalid request."); }
    }

    const server = createServer((request, response) => {
        void handleRequest(request, response).catch(error => {
            if (!response.headersSent) {
                response.writeHead(500, { "Content-Type": "application/json" });
                response.end(JSON.stringify({ error: error.message }));
            } else {
                response.destroy(error);
            }
        });
    });

    async function handleRequest(request, response) {
        response.setHeader("Cache-Control", "no-store");
        response.setHeader("X-Content-Type-Options", "nosniff");
        response.setHeader("Referrer-Policy", "no-referrer");
        if (request.headers.host !== new URL(origin).host || !request.url.startsWith(base)) {
            response.writeHead(404).end();
            return;
        }
        const route = request.url.slice(base.length).split("?")[0];
        if (request.method === "POST") {
            if (request.headers.origin !== origin || request.headers["x-cop-canvas"] !== "1") {
                response.writeHead(403).end();
                return;
            }
            if (route === "refresh") {
                const state = await refresh();
                response.writeHead(200, { "Content-Type": "application/json" });
                response.end(JSON.stringify(state));
                return;
            }
            if (route === "select" || route === "rename-check" || route === "rename-group" || route === "rename-file") {
                const input = await readJson(request);
                const state = route === "select" ? await select(input?.path)
                    : route === "rename-check" ? await renameCheck(input?.id, input?.newName)
                    : route === "rename-file" ? await renameFile(input?.id, input?.newName)
                    : await renameGroup(input?.id, input?.newName);
                response.writeHead(200, { "Content-Type": "application/json" });
                response.end(JSON.stringify(state));
                return;
            }
        } else if (request.method === "GET") {
            if (route === "") {
                response.setHeader("Content-Security-Policy",
                    "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'");
                response.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
                const html = current?.html ?? '<!doctype html><html><head><meta charset="utf-8"></head><body><main><h1>Check explorer</h1><p>Choose a Cop program file or folder to browse its checks.</p><div class="sidebar-footer"></div></main></body></html>';
                response.end(html.replace("</head>",
                    '<link rel="stylesheet" href="canvas.css"><script defer src="client.js"></script></head>'));
                return;
            }
            if (route === "canvas.css" || route === "client.js") {
                response.writeHead(200, { "Content-Type": route === "canvas.css" ? "text/css" : "text/javascript" });
                response.end(route === "canvas.css" ? styles : client);
                return;
            }
            if (route === "state") {
                response.writeHead(200, { "Content-Type": "application/json" });
                response.end(JSON.stringify(summary()));
                return;
            }
            if (route === "events") {
                response.writeHead(200, { "Content-Type": "text/event-stream", Connection: "keep-alive" });
                response.write(`event: state\ndata: ${JSON.stringify(summary())}\n\n`);
                clients.add(response);
                const heartbeat = setInterval(() => response.write(": keepalive\n\n"), 15_000);
                request.on("close", () => { clearInterval(heartbeat); clients.delete(response); });
                return;
            }
        }
        response.writeHead(404).end();
    }

    async function close() {
        closed = true;
        for (const response of clients) response.end();
        clients.clear();
        if (server.listening) {
            const stopped = new Promise((done, reject) => server.close(error => error ? reject(error) : done()));
            server.closeIdleConnections();
            await stopped;
        }
        try {
            await pending;
        } finally {
            await rm(directory, { recursive: true, force: true });
        }
    }

    try {
        if (!missing) await refresh();
        await new Promise((done, reject) => {
            server.once("error", reject);
            server.listen(0, "127.0.0.1", done);
        });
        origin = `http://127.0.0.1:${server.address().port}`;
    } catch (error) {
        await close();
        throw error;
    }

    return {
        inputPath,
        cwd,
        url: `${origin}${base}`,
        summary,
        refresh,
        close,
        renameCheck,
        renameGroup,
        renameFile,
        search(query) {
            if (!current) throw new Error("Select a Cop program first.");
            const normalized = query.toLocaleLowerCase();
            const matches = current.data.checks.filter(check => {
                const commands = current.data.commands.filter(command => command.checkIds.includes(check.id));
                return [
                    check.name, check.description, check.source, check.filePath, check.origin,
                    ...commands.flatMap(command => [command.name, command.filePath, ...(command.scopes || [])]),
                ].join(" ").toLocaleLowerCase().includes(normalized);
            });
            view = { query, node: "overview" };
            broadcast("view", view);
            return {
                total: matches.length,
                checks: matches.map(({ id, name, description, filePath, line }) => ({ id, name, description, filePath, line })),
            };
        },
        navigate(id) {
            if (!current) throw new Error("Select a Cop program first.");
            function hasGroup(groups) {
                return groups.some(group => group.id === id || hasGroup(group.groups));
            }
            const check = current.data.checks.find(check => check.id === id);
            if (id !== "overview" && !check && !hasGroup(current.data.groups)) {
                throw new Error(`Unknown check or group ID: ${id}`);
            }
            view = { query: "", node: id };
            broadcast("view", view);
            return { id, name: check?.name, path: program };
        },
    };
}
