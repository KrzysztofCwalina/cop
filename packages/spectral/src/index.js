'use strict';

const path = require('path');
const { spawnSync } = require('child_process');
const { defineProvider } = require('../../../sdk/node/cop-provider-sdk');

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

function mapSeverity(severity) {
  if (severity === 0) {
    return 'error';
  }

  if (severity === 1) {
    return 'warning';
  }

  return 'info';
}

function runSpectral(cwd) {
  const fs = require('fs');

  // Create temporary .spectral.json if none exists (provides built-in OAS ruleset)
  const spectralConfigPath = path.join(cwd, '.spectral.json');
  let createdConfig = false;
  if (!fs.existsSync(spectralConfigPath) && !fs.existsSync(path.join(cwd, '.spectral.yaml')) && !fs.existsSync(path.join(cwd, '.spectral.yml'))) {
    fs.writeFileSync(spectralConfigPath, '{"extends":["spectral:oas"]}', 'utf8');
    createdConfig = true;
  }

  const attempts = process.platform === 'win32'
    ? [
        { command: 'spectral.cmd', args: ['lint', '--format', 'json', '**/*.{json,yaml,yml}'] },
        { command: 'npx.cmd', args: ['@stoplight/spectral-cli', 'lint', '--format', 'json', '**/*.{json,yaml,yml}'] },
      ]
    : [
        { command: 'spectral', args: ['lint', '--format', 'json', '**/*.{json,yaml,yml}'] },
        { command: 'npx', args: ['@stoplight/spectral-cli', 'lint', '--format', 'json', '**/*.{json,yaml,yml}'] },
      ];

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
    if (createdConfig) { try { fs.unlinkSync(spectralConfigPath); } catch (_) {} }
    return result;
  }

  if (createdConfig) { try { fs.unlinkSync(spectralConfigPath); } catch (_) {} }
  return fallbackResult;
}

defineProvider({
  schema: getSchema,
  query: (params) => {
    const rootPath = (params && params.rootPath) || process.cwd();
    const excludedDirectories = ((params && params.excludedDirectories) || []).map((directory) =>
      directory.replace(/\\/g, '/').replace(/\/+$/, '')
    );

    const result = runSpectral(rootPath);
    if (!result) {
      process.stderr.write('Spectral not found. Install with: npm install @stoplight/spectral-cli\n');
      return { Diagnostics: [] };
    }

    const output = (result.stdout || '').trim();
    if (!output) {
      return { Diagnostics: [] };
    }

    let spectralResults;
    try {
      spectralResults = JSON.parse(output);
    } catch (error) {
      process.stderr.write(`Error parsing Spectral output: ${error.message}\n`);
      return { Diagnostics: [] };
    }

    const diagnostics = [];
    for (const item of Array.isArray(spectralResults) ? spectralResults : []) {
      const filePath = normalizeRelativePath(item.source || '', rootPath);
      if (!filePath || isExcluded(filePath, excludedDirectories)) {
        continue;
      }

      const start = (((item || {}).range || {}).start) || {};
      const line = Number.isInteger(start.line) ? start.line + 1 : 0;
      const column = Number.isInteger(start.character) ? start.character + 1 : 0;
      diagnostics.push({
        FilePath: filePath,
        Line: line,
        Column: column,
        EndLine: line,
        EndColumn: column,
        RuleId: item.code || '',
        Message: item.message || '',
        Severity: mapSeverity(item.severity),
      });
    }

    return { Diagnostics: diagnostics };
  },
});
