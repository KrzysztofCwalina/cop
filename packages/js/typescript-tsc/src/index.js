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
          { command: 'npx.cmd', args: ['--no-install', 'tsc', '--noEmit', '--pretty', 'false'] },
          { command: 'tsc.cmd', args: ['--noEmit', '--pretty', 'false'] },
          { command: 'tsc', args: ['--noEmit', '--pretty', 'false'] },
        ]
      : [
          { command: 'npx', args: ['--no-install', 'tsc', '--noEmit', '--pretty', 'false'] },
          { command: 'tsc', args: ['--noEmit', '--pretty', 'false'] },
        ];

    const result = runCommand(commands, rootPath);
    if (!result) {
      process.stderr.write('TypeScript compiler not found. Install with: npm install typescript\n');
      return { Diagnostics: [] };
    }

    const output = `${result.stdout || ''}${result.stderr || ''}`.trim();
    if (!output) {
      return { Diagnostics: [] };
    }

    const diagnostics = [];
    const diagnosticPattern = /^(.+?)\((\d+),(\d+)\): (error|warning) (TS\d+): (.+)$/;
    const projectErrorPattern = /^(error|warning) (TS\d+): (.+)$/;

    for (const line of output.split(/\r?\n/)) {
      const match = line.match(diagnosticPattern);
      if (match) {
        const filePath = normalizeRelativePath(match[1], rootPath);
        if (!filePath || isExcluded(filePath, excludedDirectories)) {
          continue;
        }

        diagnostics.push({
          FilePath: filePath,
          Line: Number.parseInt(match[2], 10) || 0,
          Column: Number.parseInt(match[3], 10) || 0,
          EndLine: 0,
          EndColumn: 0,
          RuleId: match[5],
          Message: match[6],
          Severity: match[4],
        });
        continue;
      }

      const projectMatch = line.match(projectErrorPattern);
      if (projectMatch) {
        diagnostics.push({
          FilePath: 'tsconfig.json',
          Line: 0,
          Column: 0,
          EndLine: 0,
          EndColumn: 0,
          RuleId: projectMatch[2],
          Message: projectMatch[3],
          Severity: projectMatch[1],
        });
      }
    }

    return { Diagnostics: diagnostics };
  },
});
