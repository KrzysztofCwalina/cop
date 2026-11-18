(() => {
    const footer = document.querySelector(".sidebar-footer");
    const label = document.createElement("label");
    label.textContent = "Cop program file or folder";
    label.htmlFor = "canvas-program-path";
    const path = document.createElement("input");
    path.id = "canvas-program-path";
    path.type = "text";
    path.className = "canvas-path";
    const open = document.createElement("button");
    open.type = "button";
    open.className = "small-button";
    open.textContent = "Open";
    const refresh = document.createElement("button");
    refresh.type = "button";
    refresh.className = "small-button";
    refresh.textContent = "Refresh";
    const status = document.createElement("div");
    status.className = "copy-status";
    status.setAttribute("role", "status");
    const diagnostics = document.createElement("div");
    diagnostics.className = "canvas-diagnostics";
    diagnostics.setAttribute("role", "alert");
    footer.replaceChildren(label, path, open, refresh, status, diagnostics);
    document.querySelector(".intro .eyebrow")?.replaceChildren("Program overview");
    let revision;
    let lastView;

    function applyView(view) {
        if (!view || JSON.stringify(view) === lastView) return;
        lastView = JSON.stringify(view);
        const search = document.getElementById("search");
        search.value = view.query;
        document.getElementById("command-filter").value = "";
        location.hash = new URLSearchParams({ node: view.node }).toString();
        search.dispatchEvent(new Event("input", { bubbles: true }));
    }

    function applyState(state) {
        if (revision !== undefined && state.revision !== revision) {
            location.reload();
            return;
        }
        revision = state.revision;
        if (document.activeElement !== path) path.value = state.path;
        path.title = state.path;
        refresh.disabled = state.status === "Choose a Cop program";
        status.textContent = `${state.status}. Static inventory; no checks executed.`;
        diagnostics.textContent = state.diagnostics;
        applyView(state.view);
    }

    async function openPath() {
        if (open.disabled) return;
        open.disabled = true;
        status.textContent = "Opening catalogue...";
        diagnostics.textContent = "";
        try {
            const response = await fetch("select", {
                method: "POST",
                headers: { "Content-Type": "application/json", "X-Cop-Canvas": "1" },
                body: JSON.stringify({ path: path.value }),
            });
            const result = await response.json();
            if (!response.ok) throw new Error(result.error);
            location.reload();
        } catch (error) {
            status.textContent = "Could not open that program.";
            diagnostics.textContent = error.message;
            open.disabled = false;
        }
    }

    open.addEventListener("click", openPath);
    path.addEventListener("keydown", event => {
        if (event.key === "Enter") { event.preventDefault(); void openPath(); }
    });

    refresh.addEventListener("click", async () => {
        refresh.disabled = true;
        status.textContent = "Refreshing catalogue...";
        diagnostics.textContent = "";
        try {
            const response = await fetch("refresh", { method: "POST", headers: { "X-Cop-Canvas": "1" } });
            const result = await response.json();
            if (!response.ok) throw new Error(result.error);
            location.reload();
        } catch (error) {
            status.textContent = "Refresh failed. Showing the previous catalogue.";
            diagnostics.textContent = error.message;
            refresh.disabled = false;
        }
    });

    const catalogue = JSON.parse(document.getElementById("catalogue-data")?.textContent || "null");
    const content = document.getElementById("content");

    function findGroup(groups, id) {
        for (const group of groups) {
            if (group.id === id) return group;
            const child = findGroup(group.groups, id);
            if (child) return child;
        }
        return undefined;
    }

    function addRenameControl() {
        if (!catalogue || content.querySelector(".canvas-rename, .canvas-readonly")) return;
        const id = new URLSearchParams(location.hash.slice(1)).get("node");
        const check = catalogue.checks.find(item => item.id === id);
        const group = check ? undefined : findGroup(catalogue.groups, id);
        const target = check ?? group;
        const header = content.querySelector(check ? ".rule-intro" : ".group-intro");
        if (!target || !header) return;
        const holdsTopLevelFiles = group?.checkIds.some(checkId =>
            !catalogue.checks.find(item => item.id === checkId)?.filePath.includes("/"));
        if (holdsTopLevelFiles && group.origin === "workspace") return;
        if (target.origin !== "workspace") {
            const note = document.createElement("div");
            note.className = "canvas-readonly";
            note.textContent = `Read-only: this ${check ? "check" : "group"} comes from the '${target.origin}' package.`;
            header.append(note);
            return;
        }
        addEditor(check ? header.querySelector(".rule-identity") : header.querySelector("h1"),
            target.name, check ? "check" : "folder", check ? "rename-check" : "rename-group", target.id);
        if (check) {
            addEditor(content.querySelector(".rule-location")?.parentElement,
                check.filePath.split("/").at(-1), "file", "rename-file", target.id);
        }
    }

    function addEditor(host, name, kind, route, id) {
        if (!host) return;
        const container = document.createElement("span");
        container.className = "canvas-rename";
        const icons = {
            pencil: "m11.5 3.5 1-1a1.414 1.414 0 0 1 2 2l-1 1m-2-2L3 12l-1 4 4-1 8.5-8.5m-3-3 3 3",
            save: "m3 9 4 4 8-9",
            cancel: "m4 4 10 10M14 4 4 14",
        };
        function iconButton(label, icon) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "canvas-icon-button";
            button.title = label;
            button.setAttribute("aria-label", label);
            const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
            svg.setAttribute("viewBox", "0 0 18 18");
            svg.setAttribute("aria-hidden", "true");
            const path = document.createElementNS(svg.namespaceURI, "path");
            path.setAttribute("d", icons[icon]);
            svg.append(path);
            button.append(svg);
            return button;
        }
        const start = iconButton(`Rename ${kind}`, "pencil");
        container.append(start);
        host.append(container);

        start.addEventListener("click", () => {
            const input = document.createElement("input");
            input.type = "text";
            input.className = "canvas-path";
            input.value = name;
            input.setAttribute("aria-label", `New ${kind} name`);
            const save = iconButton(`Save ${kind} name`, "save");
            const cancel = iconButton("Cancel rename", "cancel");
            const error = document.createElement("div");
            error.className = "canvas-diagnostics";
            error.setAttribute("role", "alert");
            container.replaceChildren(input, save, cancel, error);
            input.focus();
            input.select();

            async function submit() {
                const newName = input.value.trim();
                if (!newName) {
                    error.textContent = `Enter a new ${kind} name.`;
                    return;
                }
                if (newName === name) { dismiss(); return; }
                save.disabled = cancel.disabled = input.disabled = true;
                error.textContent = "";
                status.textContent = `Renaming ${kind}...`;
                try {
                    const response = await fetch(route, {
                        method: "POST",
                        headers: { "Content-Type": "application/json", "X-Cop-Canvas": "1" },
                        body: JSON.stringify({ id, newName }),
                    });
                    const result = await response.json();
                    if (!response.ok) throw new Error(result.error);
                    location.hash = new URLSearchParams({ node: result.id }).toString();
                    location.reload();
                } catch (failure) {
                    status.textContent = "Rename failed.";
                    error.textContent = failure.message;
                    save.disabled = cancel.disabled = input.disabled = false;
                }
            }

            save.addEventListener("click", submit);
            function dismiss() {
                container.replaceChildren(start);
                start.focus();
            }
            cancel.addEventListener("click", dismiss);
            input.addEventListener("keydown", event => {
                if (event.key === "Enter") { event.preventDefault(); void submit(); }
                if (event.key === "Escape" && !input.disabled) { event.preventDefault(); dismiss(); }
            });
        });
    }

    if (content) {
        new MutationObserver(addRenameControl).observe(content, { childList: true });
        addRenameControl();
    }

    const events = new EventSource("events");
    events.addEventListener("state", event => applyState(JSON.parse(event.data)));
    events.addEventListener("view", event => applyView(JSON.parse(event.data)));
    events.addEventListener("reload", () => location.reload());
    events.onerror = () => {
        status.textContent = "Connection lost. Reconnecting to the Check explorer...";
    };
    window.addEventListener("pagehide", () => events.close());
})();
