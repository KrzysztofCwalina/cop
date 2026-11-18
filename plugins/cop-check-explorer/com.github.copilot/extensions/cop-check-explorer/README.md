# Check explorer for the GitHub Copilot app

Browse Cop checks beside the conversation: grouped tree, description-first summaries,
expanded implementation, search, command filters, and a **Refresh** button. This is a
static inventory, not check execution.

The extension uses the same HTML explorer as `cop checks`; it does not maintain a
second renderer. It accepts any Cop program file or folder. No particular directory
name or "self-check" convention is required.

## Install

This extension ships as the `cop-check-explorer` plugin in the `cop` marketplace
(`KrzysztofCwalina/cop`, catalogue at `.github/plugin/marketplace.json`).

1. Install Cop on PATH with support for `cop checks`.
2. In the GitHub Copilot app, open **Customize**, add the marketplace
   `KrzysztofCwalina/cop`, and install **cop-check-explorer**. From a terminal you can
   run `copilot plugin marketplace add KrzysztofCwalina/cop` and then
   `copilot plugin install cop-check-explorer@cop` instead.
3. Start a new session.

The Copilot runtime supplies the SDK, so no npm install is needed. A repository can
enable the plugin for all its contributors in `.github/copilot/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "cop": { "source": { "source": "github", "repo": "KrzysztofCwalina/cop" } }
  },
  "enabledPlugins": { "cop-check-explorer@cop": true }
}
```

## Open it

The canvas needs no input. Ask the agent to open **Check explorer**, or use an app
canvas menu if your version offers one. Without input it shows the `cop-checks` folder
in the session's working directory. If that folder doesn't exist, it opens a path
field instead. Enter any Cop program file or folder and select **Open**; the same
field lets you switch programs later without asking the agent.

To open another program, ask the agent:

> Open the Check explorer for `analysis/rules`.

The canvas input is `{ "path": "analysis/rules" }`. Paths can be absolute or relative
to the session working directory. Select a folder to catalogue all of its `.cop`
files recursively, including rules composed from sibling files. Selecting a file
includes only that file and its imports, just like `cop checks <file>`.

**Refresh** regenerates from the same path without opening an external browser.
Analysis warnings remain visible. A generation failure shows an error and retains the
previous catalogue. Missing packages are not automatically restored.

The agent can use `search` (`query`), `navigate` (`id`), `refresh`, `rename_check`
(`id`, `newName`), `rename_file` (`id`, `newName`), and `rename_group` (`id`, `newName`) actions.
Search returns check IDs, descriptions, and locations; navigation accepts a check ID,
group ID, or `overview`. Search and navigation also update the visible panel.

## Rename checks, files, and folders

Click the small pencil beside a check's identifier or folder heading to edit its name
inline. A pencil beside a check's source location renames its `.cop` file independently
of the check name. Press Enter or click the tick to save; Escape or the cross cancels.
Each icon has a tooltip and accessible label and is keyboard-focusable.

- **Rename check** runs `cop checks rename`, which updates the declaration and every
  reference across the program folder, refuses names already in use (locally or by
  imported packages), and verifies the result before writing anything. A file named
  after the check is renamed as well.
- **Rename folder** renames the folder on disk. The folder holding the program's
  top-level files cannot be renamed here. References outside `.cop` files (scripts,
  docs) are not updated.
- **Rename file** changes the source filename, not the declarations inside it. All
  checks from that file show the new path. Paths in scripts and docs are not updated.
- Checks and groups from imported packages are read-only; rename those in the package.

## Scope and fallback

Canvas APIs are experimental and require a compatible GitHub Copilot app/runtime;
this is not a VS Code extension.

Outside the app, the standalone command still works:

```shell
cop checks analysis/rules
cop checks analysis/rules --output checks.html
```

The canvas runs a loopback-only server with an unguessable URL, invokes Cop without a
shell, and keeps generated HTML in a temporary directory removed when the panel closes.
It changes source files only when you use a rename control.
Reopening/restoring a panel uses its original input path; view filters are transient.

## Development

```shell
node --test plugins/cop-check-explorer/com.github.copilot/extensions/cop-check-explorer/explorer.test.mjs
```

To try local edits, run `copilot plugin marketplace add <repo-root>` and
`copilot plugin install cop-check-explorer@cop`. The plugin is loaded live from that
checkout; start a new session to pick up edits. Use the extension manager's
Inspect operation for startup errors.
