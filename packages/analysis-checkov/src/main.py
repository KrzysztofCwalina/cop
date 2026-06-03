"""
Cop provider that runs Checkov and exposes IaC security findings.
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

    cleaned_path = file_path.lstrip('/\\')
    relative_path = os.path.relpath(cleaned_path, root_path) if os.path.isabs(cleaned_path) else cleaned_path
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
        import platform
        use_shell = platform.system() == 'Windows'
        result = subprocess.run(
            ['checkov', '-d', '.', '-o', 'json', '--quiet', '--compact'],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
            shell=use_shell,
        )

        output = (result.stdout or '').strip()
        if not output:
            return {'Diagnostics': []}

        checkov_results = json.loads(output)
        diagnostics = []

        # checkov returns a dict for single check type, or a list for multiple
        result_groups = checkov_results if isinstance(checkov_results, list) else [checkov_results]

        for group in result_groups:
            for item in (group.get('results') or {}).get('failed_checks') or []:
                file_path = normalize_relative_path(item.get('file_path') or '', root_path)
                if not file_path or is_excluded(file_path, excluded_directories):
                    continue

                line_range = item.get('file_line_range') or []
                line = int(line_range[0]) if line_range else 0
                end_line = int(line_range[-1]) if line_range else line
                diagnostics.append({
                    'FilePath': file_path,
                    'Line': line,
                    'Column': 0,
                    'EndLine': end_line,
                    'EndColumn': 0,
                    'RuleId': item.get('check_id') or '',
                    'Message': item.get('check_name') or '',
                    'Severity': 'warning',
                })

        return {'Diagnostics': diagnostics}
    except FileNotFoundError:
        sys.stderr.write('Error: checkov not found. Install Checkov to use this package.\n')
        return {'Diagnostics': []}
    except Exception as error:
        sys.stderr.write(f'Error running checkov: {error}\n')
        return {'Diagnostics': []}


define_provider(schema=get_schema, query=query)
