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

    const stdout = (result.stdout || '').trim();
    if (stdout || result.status === 0 || result.status === 1) {
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
          { command: 'npx.cmd', args: ['--no-install', 'eslint', '-f', 'json', '.'] },
          { command: 'eslint.cmd', args: ['-f', 'json', '.'] },
          { command: 'eslint', args: ['-f', 'json', '.'] },
        ]
      : [
          { command: 'npx', args: ['--no-install', 'eslint', '-f', 'json', '.'] },
          { command: 'eslint', args: ['-f', 'json', '.'] },
        ];

    const result = runCommand(commands, rootPath);
    if (!result) {
      process.stderr.write('ESLint not found. Install with: npm install eslint\n');
      return { Violations: [] };
    }

    const output = (result.stdout || '').trim();
    if (!output) {
      return { Violations: [] };
    }

    let eslintResults;
    try {
      eslintResults = JSON.parse(output);
    } catch (error) {
      process.stderr.write(`Error parsing ESLint output: ${error.message}\n`);
      return { Violations: [] };
    }

    const violations = [];
    for (const fileResult of Array.isArray(eslintResults) ? eslintResults : []) {
      const filePath = normalizeRelativePath(fileResult.filePath || '', rootPath);
      if (!filePath || isExcluded(filePath, excludedDirectories)) {
        continue;
      }

      for (const message of fileResult.messages || []) {
        const ruleId = message.ruleId || 'parse-error';
        violations.push({
          File: filePath,
          Line: message.line || 0,
          Severity: message.severity === 2 ? 'error' : 'warning',
          Message: `${ruleId}: ${message.message || ''}`,
          Source: 'eslint',
        });
      }
    }

    return { Violations: violations };
  },
});
