"""
Cop provider that runs dotnet format and exposes formatting violations.
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))

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

    relative_path = os.path.relpath(file_path, root_path) if os.path.isabs(file_path) else file_path
    return relative_path.replace('\\', '/')


def is_excluded(file_path, excluded_directories):
    return any(file_path == directory or file_path.startswith(f'{directory}/') for directory in excluded_directories)


def get_report_entries(report_content):
    if isinstance(report_content, list):
        return report_content
    if isinstance(report_content, dict):
        return report_content.get('Files') or report_content.get('files') or []
    return []


def query(params):
    root_path = params.get('rootPath') or os.getcwd()
    excluded_directories = [
        directory.replace('\\', '/').rstrip('/')
        for directory in (params.get('excludedDirectories') or [])
        if directory
    ]
    report_directory = tempfile.mkdtemp(prefix='cop-dotnet-format-', dir=root_path)

    try:
        subprocess.run(
            ['dotnet', 'format', '--verify-no-changes', '--report', report_directory],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
        )

        report_files = []
        for current_directory, _, filenames in os.walk(report_directory):
            for filename in filenames:
                if filename.lower().endswith('.json'):
                    report_files.append(os.path.join(current_directory, filename))

        violations = []
        for report_file in report_files:
            with open(report_file, 'r', encoding='utf-8') as handle:
                report_entries = get_report_entries(json.load(handle))

            for entry in report_entries:
                file_path = normalize_relative_path(entry.get('FilePath') or entry.get('FileName') or '', root_path)
                if not file_path or is_excluded(file_path, excluded_directories):
                    continue

                for change in entry.get('FileChanges') or []:
                    line = int(change.get('LineNumber') or 0)
                    rule_id = change.get('DiagnosticId') or 'IDE0055'
                    message = change.get('FormatDescription') or 'Formatting violation'
                    violations.append({
                        'File': file_path,
                        'Line': line,
                        'Severity': 'warning',
                        'Message': f'{rule_id}: {message}',
                        'Source': 'dotnet-format',
                    })

        return {'Violations': violations}
    except FileNotFoundError:
        sys.stderr.write('Error: dotnet not found. Install the .NET SDK.\n')
        return {'Violations': []}
    except Exception as error:
        sys.stderr.write(f'Error running dotnet format: {error}\n')
        return {'Violations': []}
    finally:
        shutil.rmtree(report_directory, ignore_errors=True)


define_provider(schema=get_schema, query=query)
