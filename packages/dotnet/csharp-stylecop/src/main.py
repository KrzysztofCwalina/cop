"""
Cop provider that runs StyleCop analyzers via dotnet build and exposes diagnostics.
"""

import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))

from cop_provider_sdk import define_provider

DIAGNOSTIC_PATTERN = re.compile(
    r'^(.+?)\((\d+),(\d+)\):\s+(warning|error)\s+(\w+):\s+(.+?)(?:\s+\[.+\])?$'
)


def get_schema():
    return {
        'types': [
            {
                'name': 'Diagnostic',
                'properties': [
                    {'name': 'FilePath'},
                    {'name': 'Line', 'type': 'int'},
                    {'name': 'Column', 'type': 'int'},
                    {'name': 'EndLine', 'type': 'int'},
                    {'name': 'EndColumn', 'type': 'int'},
                    {'name': 'RuleId'},
                    {'name': 'Message'},
                    {'name': 'Severity'},
                ],
            }
        ],
        'collections': [{'name': 'Diagnostics', 'itemType': 'Diagnostic'}],
    }


def normalize_relative_path(file_path, root_path):
    if not file_path:
        return ''

    relative_path = os.path.relpath(file_path, root_path) if os.path.isabs(file_path) else file_path
    return relative_path.replace('\\', '/')


def is_excluded(file_path, excluded_directories):
    return any(file_path == directory or file_path.startswith(f'{directory}/') for directory in excluded_directories)


def query(params):
    root_path = params.get('rootPath') or os.getcwd()
    excluded_directories = [
        directory.replace('\\', '/').rstrip('/')
        for directory in (params.get('excludedDirectories') or [])
        if directory
    ]

    try:
        # Clean first to force analyzer re-run (MSBuild caches skip analyzers on incremental builds)
        subprocess.run(
            ['dotnet', 'clean', '--nologo', '-v', 'q'],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
        )

        result = subprocess.run(
            ['dotnet', 'build', '-consoleloggerparameters:NoSummary'],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
        )

        output = '\n'.join(part for part in [result.stdout, result.stderr] if part)
        diagnostics = []

        for raw_line in output.splitlines():
            match = DIAGNOSTIC_PATTERN.match(raw_line.strip())
            if not match:
                continue

            file_path, line, column, severity, rule_id, message = match.groups()
            if not (rule_id.startswith('SA') or rule_id.startswith('CS')):
                continue

            file_path = normalize_relative_path(file_path, root_path)
            if not file_path or is_excluded(file_path, excluded_directories):
                continue

            diagnostics.append({
                'FilePath': file_path,
                'Line': int(line),
                'Column': int(column),
                'EndLine': int(line),
                'EndColumn': int(column),
                'RuleId': rule_id,
                'Message': message,
                'Severity': severity.lower(),
            })

        return {'Diagnostics': diagnostics}
    except FileNotFoundError:
        sys.stderr.write('Error: dotnet not found. Install the .NET SDK.\n')
        return {'Diagnostics': []}
    except Exception as error:
        sys.stderr.write(f'Error running dotnet build: {error}\n')
        return {'Diagnostics': []}


define_provider(schema=get_schema, query=query)
