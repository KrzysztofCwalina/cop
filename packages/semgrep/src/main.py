"""
Cop provider that runs Semgrep and exposes findings as diagnostics.
"""

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', 'sdk', 'python'))

from cop_provider_sdk import define_provider


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


def normalize_severity(severity):
    value = (severity or '').upper()
    if value == 'ERROR':
        return 'error'
    if value == 'WARNING':
        return 'warning'
    return 'info'


def query(params):
    root_path = params.get('rootPath') or os.getcwd()
    excluded_directories = [
        directory.replace('\\', '/').rstrip('/')
        for directory in (params.get('excludedDirectories') or [])
        if directory
    ]

    try:
        result = subprocess.run(
            ['semgrep', 'scan', '--json', '--quiet', '.'],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
        )

        output = (result.stdout or '').strip()
        if not output:
            return {'Diagnostics': []}

        semgrep_results = json.loads(output)
        diagnostics = []

        for item in semgrep_results.get('results') or []:
            file_path = normalize_relative_path(item.get('path') or '', root_path)
            if not file_path or is_excluded(file_path, excluded_directories):
                continue

            start = item.get('start') or {}
            end = item.get('end') or {}
            extra = item.get('extra') or {}
            diagnostics.append({
                'FilePath': file_path,
                'Line': int(start.get('line') or 0),
                'Column': int(start.get('col') or 0),
                'EndLine': int(end.get('line') or start.get('line') or 0),
                'EndColumn': int(end.get('col') or start.get('col') or 0),
                'RuleId': item.get('check_id') or '',
                'Message': extra.get('message') or '',
                'Severity': normalize_severity(extra.get('severity')),
            })

        return {'Diagnostics': diagnostics}
    except FileNotFoundError:
        sys.stderr.write('Error: semgrep not found. Install Semgrep to use this package.\n')
        return {'Diagnostics': []}
    except Exception as error:
        sys.stderr.write(f'Error running semgrep: {error}\n')
        return {'Diagnostics': []}


define_provider(schema=get_schema, query=query)
