// Renders the standalone HTML check catalogue.

using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cop.Cli.Commands;

internal static class CheckCatalogueHtml
{
    /// <summary>Renders the analysis as a self-contained HTML page.</summary>
    public static string Render(string title, CheckCatalogueAnalysis analysis)
    {
        string payload = JsonSerializer.Serialize(new
        {
            title,
            checks = analysis.Checks,
            groups = CheckCatalogueGroup.Build(analysis.Checks),
            commands = analysis.Commands,
            warnings = analysis.Warnings
        }, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Cop check explorer</title>
              <style>
                :root { color-scheme: light; font: 13px/1.5 "Segoe UI", system-ui, sans-serif; color: #263248; background: #f5f7fb; --line: #e1e6ef; --muted: #65738a; --accent: #315ddd; }
                * { box-sizing: border-box; }
                body { margin: 0; height: 100vh; display: grid; grid-template: 62px minmax(0, 1fr) / 280px minmax(0, 1fr); overflow: hidden; }
                button, input, select { font: inherit; }
                button, summary, select { cursor: pointer; }
                button { color: inherit; border: 0; background: none; text-align: left; }
                button:focus-visible, summary:focus-visible, input:focus-visible, select:focus-visible { outline: 2px solid #6389ff; outline-offset: 2px; }
                .topbar { grid-column: 1 / -1; display: flex; align-items: center; gap: 12px; padding: 0 22px; background: #14213b; color: #fff; }
                .logo { background: #6889ff; border-radius: 8px; width: 30px; height: 30px; display: grid; place-items: center; font-weight: 800; font-size: 19px; }
                .brand { font-size: 15px; font-weight: 600; white-space: nowrap; }
                .brand span { color: #a7b6d1; font-weight: 400; margin-left: 8px; }
                #search { margin-left: auto; width: min(440px, 42vw); border: 1px solid #455371; border-radius: 7px; background: #263550; color: white; padding: 8px 12px; }
                #search::placeholder { color: #bdc9df; }
                .sidebar { display: flex; flex-direction: column; min-height: 0; background: #fff; border-right: 1px solid var(--line); }
                .sidebar-top { padding: 18px 14px 12px; border-bottom: 1px solid var(--line); }
                .eyebrow { color: var(--muted); font-size: 10px; font-weight: 700; letter-spacing: 1.3px; text-transform: uppercase; }
                .sidebar-top label { display: block; margin: 14px 3px 5px; font-size: 11px; color: var(--muted); }
                select { width: 100%; padding: 6px 8px; border: 1px solid var(--line); border-radius: 5px; background: #fafbfe; color: #263248; }
                .overview-link { display: flex; width: 100%; align-items: center; gap: 9px; padding: 8px 10px; border-radius: 6px; margin-top: 8px; font-weight: 600; }
                .selected { color: #264fc4; background: #eaf0ff; }
                .count { margin-left: auto; font-size: 10px; font-weight: 600; color: var(--muted); background: #edf0f6; padding: 1px 6px; border-radius: 4px; white-space: nowrap; }
                #tree { flex: 1; overflow: auto; padding: 12px 9px; }
                #tree ul { list-style: none; padding: 0; margin: 0; }
                #tree ul ul { padding-left: 13px; margin-left: 10px; border-left: 1px solid #e8ecf4; }
                #tree summary, .tree-check { display: flex; align-items: center; gap: 7px; padding: 6px 8px; border-radius: 5px; min-height: 31px; width: 100%; list-style: none; }
                #tree summary::-webkit-details-marker { display: none; }
                #tree summary:hover, .tree-check:hover, .overview-link:hover { background: #f0f3fa; }
                #tree details.selected { background: none; }
                #tree details.selected > summary, .tree-check.selected { background: #eaf0ff; color: #264fc4; }
                .caret { font-size: 10px; width: 9px; color: #8c98ab; flex-shrink: 0; }
                details[open] > summary > .caret { transform: rotate(90deg); }
                .folder-icon { position: relative; width: 12px; height: 9px; border: 1px solid #89a0d4; border-radius: 2px; background: #eef3ff; flex-shrink: 0; }
                .folder-icon::before { content: ""; position: absolute; width: 5px; height: 2px; top: -3px; left: 0; border: 1px solid #89a0d4; border-bottom: 0; border-radius: 2px 2px 0 0; }
                .tree-name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-size: 12px; }
                .tree-check { padding-left: 17px; color: #526179; }
                .check-dot { width: 5px; height: 5px; background: #8fa5dc; border-radius: 50%; flex-shrink: 0; }
                .sidebar-footer { padding: 12px 17px; font-size: 10px; color: var(--muted); border-top: 1px solid var(--line); }
                .footer-command { display: flex; align-items: center; gap: 6px; margin: 5px 0; }
                .footer-command code { flex: 1; padding: 3px 5px; border: 1px solid var(--line); border-radius: 4px; background: #f8f9fc; color: #263248; }
                .copy-command { border: 1px solid #cbd4e3; border-radius: 4px; padding: 2px 6px; background: #fff; font-size: 10px; }
                .copy-command:hover { background: #f0f3fa; }
                .copy-status { min-height: 15px; }
                main { overflow: auto; padding: 20px 30px 30px; }
                #breadcrumbs { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; min-height: 22px; margin-bottom: 18px; color: #8591a5; font-size: 11px; }
                #breadcrumbs button { color: var(--muted); padding: 0; }
                #breadcrumbs button:hover { color: var(--accent); }
                h1 { font-size: 27px; letter-spacing: -.8px; line-height: 1.2; margin: 5px 0 8px; color: #17243b; overflow-wrap: anywhere; }
                h2 { font-size: 14px; margin: 0; color: #263248; }
                p { margin: 6px 0; }
                .muted { color: var(--muted); }
                .intro { margin-bottom: 20px; }
                .intro p { max-width: 850px; color: var(--muted); }
                .stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 22px; }
                .stat { border: 1px solid var(--line); background: white; border-radius: 8px; padding: 12px 16px; }
                .stat strong { font-size: 27px; font-weight: 650; color: #234fc3; display: block; line-height: 1.2; }
                .stat span { font-size: 11px; color: var(--muted); }
                .section { margin-top: 20px; }
                .section-heading { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-bottom: 9px; }
                .section-heading span { color: var(--muted); font-size: 11px; }
                .panel { border: 1px solid var(--line); border-radius: 8px; background: #fff; overflow: hidden; }
                .table-scroll { overflow-x: auto; }
                table { border-collapse: collapse; width: 100%; font-size: 12px; }
                th { text-align: left; font-size: 10px; text-transform: uppercase; letter-spacing: .6px; color: #758198; padding: 9px 14px; background: #fafbfe; font-weight: 600; }
                td { padding: 8px 14px; border-top: 1px solid #edf0f6; vertical-align: top; }
                tbody tr:hover { background: #f8faff; }
                td.numeric { text-align: right; width: 65px; font-variant-numeric: tabular-nums; }
                .row-link { color: #3159c7; font-weight: 600; padding: 0; overflow-wrap: anywhere; }
                .row-link:hover { text-decoration: underline; }
                .row-meta { color: var(--muted); font-size: 10px; margin-top: 2px; }
                .purpose-link { color: #233c68; font-weight: 500; line-height: 1.5; white-space: pre-line; }
                .check-commands { width: 110px; }
                .badge { display: inline-block; border: 1px solid #dce5fc; border-radius: 4px; padding: 1px 5px; color: #4761a3; background: #f2f5fe; font-size: 10px; white-space: nowrap; margin: 0 4px 3px 0; }
                .badge.optional { color: #86641e; background: #fff8e9; border-color: #efdfb9; }
                .subgroups { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 18px; }
                .subgroup { display: flex; align-items: center; gap: 12px; border: 1px solid #dbe3f4; background: #fff; border-radius: 6px; padding: 8px 12px; font-size: 12px; color: #3159c7; }
                .subgroup:hover { border-color: #849fe8; background: #f5f8ff; }
                .pagination { display: flex; align-items: center; gap: 8px; padding: 9px 14px; border-top: 1px solid var(--line); color: var(--muted); font-size: 11px; }
                .pagination span { margin-right: auto; }
                .small-button { border: 1px solid var(--line); border-radius: 4px; padding: 3px 9px; background: #fff; font-size: 11px; }
                .small-button:disabled { opacity: .4; cursor: default; }
                .group-intro { margin-bottom: 6px; }
                .group-intro h1 { font-size: 22px; }
                .group-counts { display: flex; flex-wrap: wrap; gap: 6px 16px; margin-bottom: 12px; color: var(--muted); font-size: 11px; }
                .group-counts strong { color: #263248; }
                .rule-purpose { font-size: 20px; font-weight: 600; line-height: 1.5; letter-spacing: -.2px; margin: 6px 0 8px; white-space: pre-line; }
                .rule-identity { color: var(--muted); overflow-wrap: anywhere; margin: 0 0 10px; }
                .rule-context { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 8px; color: var(--muted); font-size: 11px; margin-bottom: 12px; }
                .rule-location { color: var(--muted); overflow-wrap: anywhere; padding: 0; }
                .rule-location:hover { color: var(--accent); text-decoration: underline; }
                .technical-details { margin-top: 8px; border-top: 1px solid var(--line); padding-top: 8px; }
                .technical-details > summary { color: var(--muted); font-size: 11px; }
                .technical-details .panel, .technical-details .row-meta { margin-top: 8px; }
                code, pre { font-family: "Cascadia Code", Consolas, monospace; font-size: 11px; }
                pre { margin: 0; padding: 18px; color: #dce6ff; background: #1b2942; white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.8; }
                pre.scope { margin: 0; padding: 0; color: #53617b; background: none; }
                .warning { border-left: 3px solid #d6a245; background: #fff9ed; color: #735923; padding: 9px 12px; font-size: 12px; margin: 6px 0; overflow-wrap: anywhere; }
                .note { color: var(--muted); font-size: 11px; margin-top: 15px; }
                .empty { color: var(--muted); padding: 20px; }
                noscript { position: fixed; inset: 62px 0 0; background: white; padding: 30px; z-index: 1; }
                @media (max-width: 900px) { body { grid-template-columns: 220px minmax(0, 1fr); } main { padding: 18px; } .stats { gap: 6px; } .stat { padding: 10px; } .brand span { display: none; } }
                @media (max-width: 600px) {
                  :root { font-size: 11px; }
                  body { grid-template: 46px minmax(0, 1fr) / 145px minmax(0, 1fr); }
                  .topbar { padding: 0 12px; gap: 8px; }
                  .logo { width: 24px; height: 24px; font-size: 16px; }
                  .brand { font-size: 12px; }
                  #search { width: 52vw; padding: 5px 8px; }
                  main { padding: 12px; }
                  #breadcrumbs { margin-bottom: 10px; }
                  h1 { font-size: 21px; }
                  .intro { margin-bottom: 12px; }
                  .stats { gap: 4px; margin-bottom: 14px; }
                  .stat { padding: 7px; }
                  .stat strong { font-size: 20px; }
                  .stat span { font-size: 9px; }
                  .section { margin-top: 14px; }
                  .section-heading > span { display: none; }
                  .group-contents { display: none; }
                  td, th { padding: 6px 8px; }
                  table { font-size: 11px; }
                  .sidebar-top { padding: 12px 6px; }
                  .sidebar-footer { padding: 8px; font-size: 9px; }
                  .tree-name { font-size: 11px; }
                  #tree { padding: 9px 5px; }
                  #tree summary { padding: 6px 5px; gap: 5px; }
                  .rule-purpose { font-size: 17px; }
                  .check-commands { width: 60px; }
                }
              </style>
            </head>
            <body>
              <header class="topbar">
                <div class="logo" aria-hidden="true">C</div>
                <div class="brand">Cop <span>/ Check explorer</span></div>
                <input id="search" type="search" placeholder="Search checks, descriptions, or source..." aria-label="Search checks">
              </header>
              <aside class="sidebar">
                <div class="sidebar-top">
                  <div class="eyebrow">Check catalogue</div>
                  <button id="overview" class="overview-link">Overview <span id="total-count" class="count"></span></button>
                  <label for="command-filter">Command membership</label>
                  <select id="command-filter"><option value="">All discovered checks</option></select>
                </div>
                <nav id="tree" aria-label="Check groups"></nav>
                <footer class="sidebar-footer">
                  <div>To regenerate this catalogue, run in a terminal from the repository root:</div>
                  <div class="footer-command"><code>cop checks</code><button id="copy-command" class="copy-command" type="button">Copy</button></div>
                  <div id="copy-status" class="copy-status" aria-live="polite">Static inventory. No checks executed.</div>
                </footer>
              </aside>
              <main id="main">
                <nav id="breadcrumbs" aria-label="Breadcrumb"></nav>
                <div id="content"></div>
              </main>
              <noscript>Enable JavaScript to browse this standalone check catalogue.</noscript>
              <script id="catalogue-data" type="application/json">__DATA__</script>
              <script>
                (() => {
                  const data = JSON.parse(document.getElementById('catalogue-data').textContent);
                  const byId = new Map(data.checks.map(check => [check.id, check]));
                  const nodes = new Map();
                  const parent = new Map();
                  const membership = new Map(data.checks.map(check => [check.id, []]));
                  for (const command of data.commands) {
                    for (const id of command.checkIds) membership.get(id)?.push(command);
                  }
                  function indexGroup(group, parentId) {
                    nodes.set(group.id, group);
                    parent.set(group.id, parentId);
                    group.allIds = [...group.checkIds];
                    for (const child of group.groups) group.allIds.push(...indexGroup(child, group.id));
                    for (const id of group.checkIds) parent.set(id, group.id);
                    return group.allIds;
                  }
                  for (const group of data.groups) indexGroup(group, 'overview');
                  for (const check of data.checks) nodes.set(check.id, check);
                  const content = document.getElementById('content');
                  const tree = document.getElementById('tree');
                  const breadcrumbs = document.getElementById('breadcrumbs');
                  const search = document.getElementById('search');
                  const commandFilter = document.getElementById('command-filter');
                  const overview = document.getElementById('overview');
                  const expanded = new Set();
                  const pageSize = 25;
                  let page = 0;
                  let visible = new Set();
                  let selected = 'overview';
                  const make = (tag, className, text) => {
                    const node = document.createElement(tag);
                    if (className) node.className = className;
                    if (text !== undefined) node.textContent = text;
                    return node;
                  };
                  const button = (text, className, onClick) => {
                    const node = make('button', className, text);
                    node.type = 'button';
                    node.addEventListener('click', onClick);
                    return node;
                  };
                  const normalized = value => (value || '').toLocaleLowerCase();
                  const sortChecks = ids => ids.map(id => byId.get(id)).filter(Boolean)
                    .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
                  const matchingIds = group => group.allIds.filter(id => visible.has(id));
                  const commandLabel = command => `${command.name} - ${command.filePath}`;
                  const searchText = new Map(data.checks.map(check => [check.id, normalized([
                    check.name, check.description, check.source, check.filePath, check.origin,
                    ...(membership.get(check.id) || []).flatMap(command =>
                      [command.name, command.filePath, ...command.scopes])
                  ].join(' '))]));
                  document.title = data.title;
                  for (const command of data.commands) {
                    const option = make('option', '', commandLabel(command));
                    option.value = command.id;
                    commandFilter.append(option);
                  }
                  const unreferenced = make('option', '', 'Not referenced by a command');
                  unreferenced.value = 'unreferenced';
                  commandFilter.append(unreferenced);
                  function navigate(id) {
                    page = 0;
                    const hash = new URLSearchParams({ node: id }).toString();
                    if (location.hash.slice(1) === hash) render();
                    else location.hash = hash;
                    document.getElementById('main').scrollTop = 0;
                  }
                  function updateFilter() {
                    page = 0;
                    render();
                    document.getElementById('main').scrollTop = 0;
                  }
                  function render() {
                    const focusedNode = tree.contains(document.activeElement)
                      ? document.activeElement.dataset.nodeId : null;
                    const query = normalized(search.value);
                    visible = new Set(data.checks.filter(check => {
                      const commands = membership.get(check.id) || [];
                      const matchesCommand = !commandFilter.value ||
                        (commandFilter.value === 'unreferenced'
                          ? commands.length === 0
                          : commands.some(command => command.id === commandFilter.value));
                      return matchesCommand && (!query || searchText.get(check.id).includes(query));
                    }).map(check => check.id));
                    selected = new URLSearchParams(location.hash.slice(1)).get('node') || 'overview';
                    const node = nodes.get(selected);
                    if (!node || (node.allIds ? !matchingIds(node).length : !visible.has(node.id))) {
                      selected = 'overview';
                    }
                    for (let id = parent.get(selected); id && id !== 'overview'; id = parent.get(id)) {
                      expanded.add(id);
                    }
                    overview.classList.toggle('selected', selected === 'overview');
                    overview.setAttribute('aria-current', selected === 'overview' ? 'page' : 'false');
                    document.getElementById('total-count').textContent = `${visible.size}/${data.checks.length}`;
                    tree.replaceChildren(renderTree(data.groups, !!query));
                    if (!visible.size) tree.append(make('p', 'empty', 'No matching checks.'));
                    renderBreadcrumbs();
                    content.replaceChildren();
                    if (selected === 'overview') renderOverview();
                    else if (nodes.get(selected).allIds) renderGroup(nodes.get(selected));
                    else renderCheck(nodes.get(selected));
                    if (focusedNode) {
                      [...tree.querySelectorAll('[data-node-id]')]
                        .find(element => element.dataset.nodeId === focusedNode)?.focus({ preventScroll: true });
                    }
                  }
                  function renderTree(groups, searching) {
                    const list = make('ul');
                    for (const group of groups) {
                      const ids = matchingIds(group);
                      if (!ids.length) continue;
                      const item = make('li');
                      const details = make('details', group.id === selected ? 'selected' : '');
                      details.open = searching || expanded.has(group.id);
                      const summary = make('summary');
                      summary.dataset.nodeId = group.id;
                      summary.title = `${group.origin} / ${group.path}`;
                      summary.setAttribute('aria-current', group.id === selected ? 'page' : 'false');
                      summary.append(make('span', 'caret', '>'), make('span', 'folder-icon'),
                        make('span', 'tree-name', group.name), make('span', 'count', ids.length));
                      summary.addEventListener('click', event => {
                        event.preventDefault();
                        if (details.open) expanded.delete(group.id);
                        else expanded.add(group.id);
                        navigate(group.id);
                      });
                      details.append(summary);
                      const children = renderTree(group.groups, searching);
                      for (const check of sortChecks(group.checkIds.filter(id => visible.has(id)))) {
                        const leaf = button('', `tree-check${selected === check.id ? ' selected' : ''}`,
                          () => navigate(check.id));
                        leaf.title = check.description || check.name;
                        leaf.dataset.nodeId = check.id;
                        leaf.setAttribute('aria-current', selected === check.id ? 'page' : 'false');
                        leaf.append(make('span', 'check-dot'), make('span', 'tree-name', check.name));
                        const row = make('li');
                        row.append(leaf);
                        children.append(row);
                      }
                      details.append(children);
                      item.append(details);
                      list.append(item);
                    }
                    return list;
                  }
                  function renderBreadcrumbs() {
                    breadcrumbs.replaceChildren(button('Overview', '', () => navigate('overview')));
                    const trail = [];
                    for (let id = selected; id && id !== 'overview'; id = parent.get(id)) trail.unshift(nodes.get(id));
                    for (const node of trail) {
                      breadcrumbs.append(make('span', '', '/'),
                        button(node.allIds ? node.name : 'Rule', '', () => navigate(node.id)));
                    }
                  }
                  function intro(label, title, description) {
                    const section = make('header', 'intro');
                    section.append(make('div', 'eyebrow', label), make('h1', '', title));
                    if (description) section.append(make('p', '', description));
                    content.append(section);
                    return section;
                  }
                  function stats(values) {
                    const row = make('div', 'stats');
                    for (const [value, label] of values) {
                      const stat = make('div', 'stat');
                      stat.append(make('strong', '', value), make('span', '', label));
                      row.append(stat);
                    }
                    content.append(row);
                  }
                  function section(title, note) {
                    const block = make('section', 'section');
                    const heading = make('div', 'section-heading');
                    heading.append(make('h2', '', title));
                    if (note) heading.append(make('span', '', note));
                    block.append(heading);
                    content.append(block);
                    return block;
                  }
                  function table(headers) {
                    const panel = make('div', 'panel');
                    const scroll = make('div', 'table-scroll');
                    const grid = make('table');
                    const head = make('thead');
                    const row = make('tr');
                    for (const text of headers) row.append(make('th', text === 'Contents' ? 'group-contents' : '', text));
                    head.append(row);
                    const body = make('tbody');
                    grid.append(head, body);
                    scroll.append(grid);
                    panel.append(scroll);
                    return { panel, body };
                  }
                  function cell(row, child, className) {
                    const td = make('td', className);
                    td.append(typeof child === 'string' ? document.createTextNode(child) : child);
                    row.append(td);
                    return td;
                  }
                  function badges(commands) {
                    const result = make('span');
                    for (const command of commands) {
                      const badge = make('span', 'badge', command.name);
                      badge.title = commandLabel(command);
                      result.append(badge);
                    }
                    if (!commands.length) result.append(make('span', 'badge optional', 'Unreferenced'));
                    return result;
                  }
                  function renderOverview() {
                    intro('Repository overview', 'Your checks, at a glance.',
                      'Browse source groups, then select a check to inspect it.');
                    const groups = data.groups.filter(group => matchingIds(group).length);
                    stats([[visible.size, 'checks in view'], [groups.length, 'source groups'],
                      [data.commands.length, 'discovered commands'], [data.warnings.length, 'analysis warnings']]);
                    const block = section('Source groups', 'Folders and imported packages');
                    const { panel, body } = table(['Group', 'Contents', 'Commands', 'Checks']);
                    for (const group of groups) {
                      const ids = matchingIds(group);
                      const row = make('tr');
                      cell(row, button(group.name, 'row-link', () => navigate(group.id)));
                      cell(row, `${group.origin === 'workspace' ? 'Workspace folder' : 'Imported package'} / ${group.groups.filter(child => matchingIds(child).length).length} subgroups`, 'muted group-contents');
                      cell(row, badges(data.commands.filter(command => command.checkIds.some(id => ids.includes(id)))));
                      cell(row, `${ids.length}`, 'numeric');
                      body.append(row);
                    }
                    if (!groups.length) {
                      const row = make('tr');
                      const td = cell(row, 'No checks match. Clear search or choose a different command.', 'empty');
                      td.colSpan = 4;
                      body.append(row);
                    }
                    block.append(panel);
                    renderCommands(section('Command composition', 'Select a command to narrow the explorer'),
                      data.commands);
                    if (data.warnings.length) {
                      const warnings = section('Analysis warnings', 'Membership may be incomplete');
                      for (const warning of data.warnings) warnings.append(make('p', 'warning', warning));
                    }
                    content.append(make('p', 'note',
                      'Static inventory only. Command membership does not prove a check runs in CI or predict runtime-dependent branches. Unreferenced checks are included.'));
                  }
                  function renderGroup(group) {
                    const ids = matchingIds(group);
                    const commands = data.commands.filter(command => command.checkIds.some(id => ids.includes(id)));
                    intro(group.origin === 'workspace' ? 'Workspace group' : `Package / ${group.origin}`,
                      group.path).classList.add('group-intro');
                    const counts = make('div', 'group-counts');
                    for (const [value, label] of [[ids.length, 'checks'],
                      [group.groups.filter(child => matchingIds(child).length).length, 'subgroups'],
                      [commands.length, 'including commands']]) {
                      const count = make('span');
                      count.append(make('strong', '', value), document.createTextNode(` ${label}`));
                      counts.append(count);
                    }
                    content.append(counts);
                    const subgroups = make('div', 'subgroups');
                    for (const child of group.groups) {
                      const count = matchingIds(child).length;
                      if (!count) continue;
                      const link = button(child.name, 'subgroup', () => navigate(child.id));
                      link.append(make('span', 'count', count));
                      subgroups.append(link);
                    }
                    content.append(subgroups);
                    renderChecks(section('Checks', 'Includes checks in subgroups'), sortChecks(ids));
                  }
                  function renderChecks(block, checks) {
                    const { panel, body } = table(['What the rule does', 'Included by']);
                    const pageCount = Math.max(1, Math.ceil(checks.length / pageSize));
                    page = Math.min(page, pageCount - 1);
                    for (const check of checks.slice(page * pageSize, (page + 1) * pageSize)) {
                      const row = make('tr');
                      const label = make('div');
                      const purpose = button(check.description || check.name, 'row-link purpose-link',
                        () => navigate(check.id));
                      purpose.title = `${check.filePath}:${check.line}`;
                      label.append(purpose, make('div', 'row-meta', check.description
                        ? check.name : `${check.name} / No description provided.`));
                      cell(row, label);
                      cell(row, badges(membership.get(check.id) || []), 'check-commands');
                      body.append(row);
                    }
                    const pagination = make('div', 'pagination');
                    pagination.append(make('span', '', `${page * pageSize + 1}-${Math.min(checks.length, (page + 1) * pageSize)} of ${checks.length} checks`));
                    const previous = button('Previous', 'small-button', () => { page--; render(); });
                    previous.disabled = page === 0;
                    const next = button('Next', 'small-button', () => { page++; render(); });
                    next.disabled = page + 1 >= pageCount;
                    pagination.append(previous, make('div', '', `${page + 1} / ${pageCount}`), next);
                    panel.append(pagination);
                    block.append(panel);
                  }
                  function renderCommands(block, commands) {
                    const { panel, body } = table(['Command', 'Checks in view']);
                    for (const command of commands) {
                      const row = make('tr');
                      const label = make('div');
                      label.append(button(command.name, 'row-link', () => {
                        commandFilter.value = command.id;
                        navigate('overview');
                      }));
                      label.append(make('div', 'row-meta', `${command.filePath}:${command.line}`));
                      for (const issue of command.unresolved) label.append(make('p', 'warning', issue));
                      cell(row, label);
                      cell(row, `${command.checkIds.filter(id => visible.has(id)).length} / ${command.checkIds.length}`, 'numeric');
                      body.append(row);
                    }
                    block.append(panel);
                  }
                  function renderCheck(check) {
                    const header = make('header', 'rule-intro');
                    header.append(make('div', 'eyebrow', 'What this rule checks'),
                      make('h1', 'rule-purpose', check.description || check.name));
                    if (!check.description) {
                      header.append(make('p', 'warning',
                        'No description provided. Inspect the implementation to understand this rule.'));
                    }
                    const identity = make('p', 'rule-identity');
                    identity.append(make('code', '', check.name));
                    header.append(identity);
                    content.append(header);

                    const commands = membership.get(check.id) || [];
                    const context = make('div', 'rule-context');
                    context.append(make('span', '', 'Included by'), badges(commands));
                    const implementation = make('details', 'technical-details');
                    implementation.open = true;
                    implementation.append(make('summary', '', 'Implementation'));
                    context.append(button(`${check.filePath}:${check.line}`, 'rule-location', () => {
                      implementation.open = !implementation.open;
                    }));
                    content.append(context);
                    for (const command of commands) {
                      for (const issue of command.unresolved) {
                        content.append(make('p', 'warning', `${commandLabel(command)}: ${issue}`));
                      }
                    }
                    if (commands.length) {
                      const scopedCommands = commands.filter(command => command.scopes.length);
                      if (scopedCommands.length) {
                        const scopes = make('details', 'technical-details');
                        scopes.append(make('summary', '', 'Scope arguments (command-level)'));
                        for (const command of scopedCommands) {
                          scopes.append(make('p', 'row-meta', `${commandLabel(command)}:${command.line}`));
                          for (const scope of command.scopes) scopes.append(make('pre', 'scope', scope));
                        }
                        content.append(scopes);
                      }
                    } else {
                      content.append(make('p', 'warning',
                        'Not referenced by a discovered command. This does not prove the check is unused elsewhere.'));
                    }
                    const source = make('div', 'panel');
                    source.append(make('pre', '', check.source));
                    implementation.append(make('p', 'row-meta',
                      `${check.kind} / ${check.origin} / ${check.filePath}:${check.line}`), source);
                    content.append(implementation);
                  }
                  overview.addEventListener('click', () => navigate('overview'));
                  search.addEventListener('input', updateFilter);
                  commandFilter.addEventListener('change', updateFilter);
                  document.getElementById('copy-command').addEventListener('click', async () => {
                    const status = document.getElementById('copy-status');
                    try {
                      await navigator.clipboard.writeText('cop checks');
                      status.textContent = 'Copied. Run it from the repository root.';
                    } catch {
                      const input = document.createElement('textarea');
                      input.value = 'cop checks';
                      input.style.position = 'fixed';
                      input.style.opacity = '0';
                      document.body.append(input);
                      input.select();
                      const copied = document.execCommand('copy');
                      input.remove();
                      if (copied) {
                        status.textContent = 'Copied. Run it from the repository root.';
                      } else {
                        const command = document.querySelector('.footer-command code');
                        const selection = window.getSelection();
                        const range = document.createRange();
                        range.selectNodeContents(command);
                        selection.removeAllRanges();
                        selection.addRange(range);
                        status.textContent = 'Clipboard access blocked. Command selected; press Ctrl+C to copy.';
                      }
                    }
                  });
                  window.addEventListener('hashchange', () => { page = 0; render(); });
                  render();
                })();
              </script>
            </body>
            </html>
            """.Replace("__DATA__", payload, StringComparison.Ordinal);
    }
}
