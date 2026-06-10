"""
Cop provider that runs Checkov and exposes IaC security findings as violations.
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
            return {'Violations': []}

        checkov_results = json.loads(output)
        violations = []

        # checkov returns a dict for single check type, or a list for multiple
        result_groups = checkov_results if isinstance(checkov_results, list) else [checkov_results]

        for group in result_groups:
            for item in (group.get('results') or {}).get('failed_checks') or []:
                file_path = normalize_relative_path(item.get('file_path') or '', root_path)
                if not file_path or is_excluded(file_path, excluded_directories):
                    continue

                line_range = item.get('file_line_range') or []
                line = int(line_range[0]) if line_range else 0
                rule_id = item.get('check_id') or ''
                message = item.get('check_name') or ''
                violations.append({
                    'File': file_path,
                    'Line': line,
                    'Severity': 'warning',
                    'Message': f'{rule_id}: {message}' if rule_id else message,
                    'Source': 'checkov',
                })

        return {'Violations': violations}
    except FileNotFoundError:
        sys.stderr.write('Error: checkov not found. Install Checkov to use this package.\n')
        return {'Violations': []}
    except Exception as error:
        sys.stderr.write(f'Error running checkov: {error}\n')
        return {'Violations': []}


define_provider(schema=get_schema, query=query)
