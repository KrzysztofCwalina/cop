'use strict';

const path = require('path');
const { spawnSync } = require('child_process');
const { defineProvider } = require('../../../../sdk/node/cop-provider-sdk');

function getSchema() {
  return {
    types: [
      {
        name: 'Violation',
        properties: [
          { name: 'File' },
          { name: 'Line', type: 'int' },
          { name: 'Severity' },
          { name: 'Message' },
          { name: 'Source' },
        ],
      },
    ],
    collections: [{ name: 'Violations', itemType: 'Violation' }],
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
      return { Violations: [] };
    }

    const output = `${result.stdout || ''}${result.stderr || ''}`.trim();
    if (!output) {
      return { Violations: [] };
    }

    const violations = [];
    const diagnosticPattern = /^(.+?)\((\d+),(\d+)\): (error|warning) (TS\d+): (.+)$/;
    const projectErrorPattern = /^(error|warning) (TS\d+): (.+)$/;

    for (const line of output.split(/\r?\n/)) {
      const match = line.match(diagnosticPattern);
      if (match) {
        const filePath = normalizeRelativePath(match[1], rootPath);
        if (!filePath || isExcluded(filePath, excludedDirectories)) {
          continue;
        }

        const ruleId = match[5];
        violations.push({
          File: filePath,
          Line: Number.parseInt(match[2], 10) || 0,
          Severity: match[4],
          Message: `${ruleId}: ${match[6]}`,
          Source: 'tsc',
        });
        continue;
      }

      const projectMatch = line.match(projectErrorPattern);
      if (projectMatch) {
        const ruleId = projectMatch[2];
        violations.push({
          File: 'tsconfig.json',
          Line: 0,
          Severity: projectMatch[1],
          Message: `${ruleId}: ${projectMatch[3]}`,
          Source: 'tsc',
        });
      }
    }

    return { Violations: violations };
  },
});
