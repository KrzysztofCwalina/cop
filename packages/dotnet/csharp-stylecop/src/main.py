"""
Cop provider that runs StyleCop analyzers via dotnet build and exposes violations.
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
                'name': 'Violation',
                'properties': [
                    {'name': 'File'},
                    {'name': 'Line', 'type': 'int'},
                    {'name': 'Severity'},
                    {'name': 'Message'},
                    {'name': 'Source'},
                ],
            }
        ],
        'collections': [{'name': 'Violations', 'itemType': 'Violation'}],
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
        violations = []

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

            violations.append({
                'File': file_path,
                'Line': int(line),
                'Severity': severity.lower(),
                'Message': f'{rule_id}: {message}',
                'Source': 'stylecop',
            })

        return {'Violations': violations}
    except FileNotFoundError:
        sys.stderr.write('Error: dotnet not found. Install the .NET SDK.\n')
        return {'Violations': []}
    except Exception as error:
        sys.stderr.write(f'Error running dotnet build: {error}\n')
        return {'Violations': []}


define_provider(schema=get_schema, query=query)
