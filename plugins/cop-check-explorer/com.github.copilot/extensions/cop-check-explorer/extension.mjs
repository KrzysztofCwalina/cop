import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { startExplorer } from "./explorer.mjs";

const instances = new Map();
const defaultPath = "cop-checks";
const emptyInput = { type: "object", properties: {}, additionalProperties: false };

function instance(ctx) {
    const entry = instances.get(ctx.instanceId);
    if (!entry) throw new CanvasError("explorer_closed", "Open the Check explorer first.");
    return entry;
}

async function action(ctx, handler) {
    try {
        return await handler(instance(ctx));
    } catch (error) {
        throw new CanvasError("explorer_error", error.message);
    }
}

await joinSession({
    canvases: [
        createCanvas({
            id: "cop-check-explorer",
            displayName: "Check explorer",
            description: "Browse Cop checks from any program file or folder, with refresh, search, and source navigation.",
            inputSchema: {
                type: ["object", "null"],
                properties: {
                    path: {
                        type: "string",
                        minLength: 1,
                        description: "Cop program file or folder, absolute or relative to the session working directory. Use a folder to include sibling check files. Defaults to cop-checks.",
                    },
                },
                additionalProperties: false,
            },
            actions: [
                {
                    name: "refresh",
                    description: "Regenerate the catalogue from source without executing checks.",
                    inputSchema: emptyInput,
                    handler: ctx => action(ctx, entry => entry.refresh()),
                },
                {
                    name: "search",
                    description: "Filter the panel and return matching check IDs, descriptions and locations. An empty query clears search.",
                    inputSchema: {
                        type: "object",
                        properties: { query: { type: "string" } },
                        required: ["query"],
                        additionalProperties: false,
                    },
                    handler: ctx => action(ctx, entry => entry.search(ctx.input.query)),
                },
                {
                    name: "navigate",
                    description: "Show a check or group by ID returned by search, or 'overview'. Clears filters so the selection is visible.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string", minLength: 1 } },
                        required: ["id"],
                        additionalProperties: false,
                    },
                    handler: ctx => action(ctx, entry => entry.navigate(ctx.input.id)),
                },
                {
                    name: "rename_check",
                    description: "Rename a check in this program (not package checks) and update every reference. Edits source files; the change is verified before writing.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string", minLength: 1 }, newName: { type: "string", minLength: 1 } },
                        required: ["id", "newName"],
                        additionalProperties: false,
                    },
                    handler: ctx => action(ctx, entry => entry.renameCheck(ctx.input.id, ctx.input.newName)),
                },
                {
                    name: "rename_file",
                    description: "Rename the local .cop source file containing a check. Does not rename declarations or update paths in scripts or docs. Package files are read-only.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string", minLength: 1 }, newName: { type: "string", minLength: 1 } },
                        required: ["id", "newName"],
                        additionalProperties: false,
                    },
                    handler: ctx => action(ctx, entry => entry.renameFile(ctx.input.id, ctx.input.newName)),
                },
                {
                    name: "rename_group",
                    description: "Rename the folder behind a group in this program (not package groups). Moves the folder on disk.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string", minLength: 1 }, newName: { type: "string", minLength: 1 } },
                        required: ["id", "newName"],
                        additionalProperties: false,
                    },
                    handler: ctx => action(ctx, entry => entry.renameGroup(ctx.input.id, ctx.input.newName)),
                },
            ],
            open: async ctx => {
                try {
                    const inputPath = ctx.input?.path ?? defaultPath;
                    const cwd = ctx.session?.workingDirectory ?? process.cwd();
                    let entry = instances.get(ctx.instanceId);
                    if (entry && (entry.inputPath !== inputPath || entry.cwd !== cwd)) {
                        await entry.close();
                        instances.delete(ctx.instanceId);
                        entry = undefined;
                    }
                    if (!entry) {
                        entry = await startExplorer({ inputPath, cwd, allowMissing: ctx.input?.path == null });
                        instances.set(ctx.instanceId, entry);
                    }
                    return { title: "Check explorer", url: entry.url, status: entry.summary().status };
                } catch (error) {
                    throw new CanvasError("explorer_open_failed", error.message);
                }
            },
            onClose: async ctx => {
                const entry = instances.get(ctx.instanceId);
                instances.delete(ctx.instanceId);
                await entry?.close();
            },
        }),
    ],
});
