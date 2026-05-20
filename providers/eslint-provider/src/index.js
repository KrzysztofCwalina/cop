'use strict';

const path = require('path');
const { defineProvider } = require('../../sdk/node/cop-provider-sdk');

defineProvider({
  schema: () => ({
    types: [
      {
        name: 'Diagnostic',
        properties: [
          { name: 'FilePath' },
          { name: 'Line', type: 'int' },
          { name: 'Column', type: 'int' },
          { name: 'EndLine', type: 'int', optional: true },
          { name: 'EndColumn', type: 'int', optional: true },
          { name: 'RuleId' },
          { name: 'Message' },
          { name: 'Severity' },
        ],
      },
    ],
    collections: [{ name: 'Diagnostics', itemType: 'Diagnostic' }],
  }),

  query: async (params) => {
    const { rootPath, excludedDirectories } = params || {};
    const projectDir = rootPath || process.cwd();

    try {
      // ESLint v9+ flat config API
      const { ESLint } = require('eslint');

      const eslint = new ESLint({
        cwd: projectDir,
        errorOnUnmatchedPattern: false,
      });

      // Build ignore patterns from excludedDirectories
      const ignorePatterns = (excludedDirectories || []).map(
        (d) => `${d}/`
      );

      // Find JS/TS files to lint
      const patterns = ['**/*.js', '**/*.mjs', '**/*.cjs', '**/*.ts', '**/*.tsx', '**/*.jsx'];

      const results = await eslint.lintFiles(patterns);

      const diagnostics = [];
      for (const result of results) {
        // Skip files in excluded directories
        const relPath = path.relative(projectDir, result.filePath).replace(/\\/g, '/');
        if (ignorePatterns.some((p) => relPath.startsWith(p))) continue;

        for (const msg of result.messages) {
          diagnostics.push({
            FilePath: relPath,
            Line: msg.line || 0,
            Column: msg.column || 0,
            EndLine: msg.endLine || msg.line || 0,
            EndColumn: msg.endColumn || msg.column || 0,
            RuleId: msg.ruleId || 'parse-error',
            Message: msg.message,
            Severity: msg.severity === 2 ? 'error' : 'warning',
          });
        }
      }

      return { Diagnostics: diagnostics };
    } catch (err) {
      // If ESLint is not installed in the project, return empty
      if (err.code === 'MODULE_NOT_FOUND') {
        return { Diagnostics: [] };
      }
      throw err;
    }
  },
});
