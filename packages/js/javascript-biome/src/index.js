'use strict';

const path = require('path');
const { spawnSync } = require('child_process');
const { defineProvider } = require('../../../../sdk/node/cop-provider-sdk');

function getSchema() {
  return {
    types: [
      {
        name: 'Diagnostic',
        properties: [
          { name: 'FilePath' },
          { name: 'Line', type: 'int' },
          { name: 'Column', type: 'int' },
          { name: 'EndLine', type: 'int' },
          { name: 'EndColumn', type: 'int' },
          { name: 'RuleId' },
          { name: 'Message' },
          { name: 'Severity' },
        ],
      },
    ],
    collections: [{ name: 'Diagnostics', itemType: 'Diagnostic' }],
  };
}

function normalizeRelativePath(filePath, rootPath) {
  if (!filePath) {
    return '';
  }

  const relativePath = path.isAbsolute(filePath)
    ? path.relative(rootPath, filePath)
    : filePath;

  return relativePath.replace(/\\/g, '/');
}

function isExcluded(filePath, excludedDirectories) {
  return excludedDirectories.some((directory) =>
    filePath === directory || filePath.startsWith(`${directory}/`)
  );
}

function decodeGitHubCommandValue(value) {
  return (value || '')
    .replace(/%0D/g, '\r')
    .replace(/%0A/g, '\n')
    .replace(/%25/g, '%');
}

function parseProperties(text) {
  const properties = {};
  for (const segment of text.split(',')) {
    const separatorIndex = segment.indexOf('=');
    if (separatorIndex === -1) {
      continue;
    }

    const key = segment.slice(0, separatorIndex).trim();
    const value = segment.slice(separatorIndex + 1).trim();
    properties[key] = decodeGitHubCommandValue(value);
  }

  return properties;
}

function runCommand(attempts, cwd) {
  let fallbackResult = null;
  const isWindows = process.platform === 'win32';

  for (const attempt of attempts) {
    const result = spawnSync(attempt.command, attempt.args, {
      cwd,
      encoding: 'utf-8',
      windowsHide: true,
      shell: isWindows,
    });

    if (result.error && result.error.code === 'ENOENT') {
      continue;
    }

    fallbackResult = fallbackResult || result;

    const output = `${result.stdout || ''}${result.stderr || ''}`.trim();
    if (output || result.status === 0 || result.status === 1) {
      return result;
    }
  }

  return fallbackResult;
}

defineProvider({
  schema: getSchema,
  query: (params) => {
    const rootPath = (params && params.rootPath) || process.cwd();
    const excludedDirectories = ((params && params.excludedDirectories) || []).map((directory) =>
      directory.replace(/\\/g, '/').replace(/\/+$/, '')
    );

    const commands = process.platform === 'win32'
      ? [
          { command: 'npx.cmd', args: ['--no-install', '@biomejs/biome', 'lint', '--reporter=github', '.'] },
          { command: 'biome.cmd', args: ['lint', '--reporter=github', '.'] },
          { command: 'biome', args: ['lint', '--reporter=github', '.'] },
        ]
      : [
          { command: 'npx', args: ['--no-install', '@biomejs/biome', 'lint', '--reporter=github', '.'] },
          { command: 'biome', args: ['lint', '--reporter=github', '.'] },
        ];

    const result = runCommand(commands, rootPath);
    if (!result) {
      process.stderr.write('Biome not found. Install with: npm install @biomejs/biome\n');
      return { Diagnostics: [] };
    }

    const output = `${result.stdout || ''}${result.stderr || ''}`.trim();
    if (!output) {
      return { Diagnostics: [] };
    }

    const diagnostics = [];
    const lines = output.split(/\r?\n/);
    const annotationPattern = /^::(error|warning)\s+(.+?)::(.*)$/;

    for (const line of lines) {
      const match = line.match(annotationPattern);
      if (!match) {
        continue;
      }

      const severity = match[1];
      const properties = parseProperties(match[2]);
      const filePath = normalizeRelativePath(properties.file || properties.path || '', rootPath);
      if (!filePath || isExcluded(filePath, excludedDirectories)) {
        continue;
      }

      diagnostics.push({
        FilePath: filePath,
        Line: Number.parseInt(properties.line || '0', 10) || 0,
        Column: Number.parseInt(properties.col || properties.column || '0', 10) || 0,
        EndLine: Number.parseInt(properties.endLine || properties.line || '0', 10) || 0,
        EndColumn: Number.parseInt(properties.endColumn || properties.col || properties.column || '0', 10) || 0,
        RuleId: properties.title || properties.category || 'biome',
        Message: decodeGitHubCommandValue(match[3]),
        Severity: severity,
      });
    }

    return { Diagnostics: diagnostics };
  },
});
