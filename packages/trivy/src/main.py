"""
Cop provider that runs Trivy and exposes vulnerability and misconfiguration findings.
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


def map_severity(severity):
    value = (severity or '').upper()
    if value in {'CRITICAL', 'HIGH'}:
        return 'error'
    if value == 'MEDIUM':
        return 'warning'
    if value == 'LOW':
        return 'info'
    return 'warning'


def build_vulnerability_message(item):
    title = item.get('Title') or item.get('Description') or item.get('VulnerabilityID') or 'Vulnerability'
    package_name = item.get('PkgName') or 'unknown-package'
    installed_version = item.get('InstalledVersion') or 'unknown-version'
    return f'{title} in {package_name}@{installed_version}'


def query(params):
    root_path = params.get('rootPath') or os.getcwd()
    excluded_directories = [
        directory.replace('\\', '/').rstrip('/')
        for directory in (params.get('excludedDirectories') or [])
        if directory
    ]

    try:
        result = subprocess.run(
            ['trivy', 'fs', '--format', 'json', '--scanners', 'vuln,misconfig', '.'],
            capture_output=True,
            text=True,
            encoding='utf-8',
            cwd=root_path,
        )

        output = (result.stdout or '').strip()
        if not output:
            return {'Diagnostics': []}

        trivy_results = json.loads(output)
        diagnostics = []

        for item in trivy_results.get('Results') or []:
            file_path = normalize_relative_path(item.get('Target') or '', root_path)
            if not file_path or is_excluded(file_path, excluded_directories):
                continue

            for vulnerability in item.get('Vulnerabilities') or []:
                diagnostics.append({
                    'FilePath': file_path,
                    'Line': 0,
                    'Column': 0,
                    'EndLine': 0,
                    'EndColumn': 0,
                    'RuleId': vulnerability.get('VulnerabilityID') or '',
                    'Message': build_vulnerability_message(vulnerability),
                    'Severity': map_severity(vulnerability.get('Severity')),
                })

            for misconfiguration in item.get('Misconfigurations') or []:
                metadata = misconfiguration.get('CauseMetadata') or {}
                line = int(metadata.get('StartLine') or 0)
                end_line = int(metadata.get('EndLine') or line)
                diagnostics.append({
                    'FilePath': file_path,
                    'Line': line,
                    'Column': 0,
                    'EndLine': end_line,
                    'EndColumn': 0,
                    'RuleId': misconfiguration.get('ID') or misconfiguration.get('AVDID') or '',
                    'Message': misconfiguration.get('Title') or misconfiguration.get('Message') or '',
                    'Severity': map_severity(misconfiguration.get('Severity')),
                })

        return {'Diagnostics': diagnostics}
    except FileNotFoundError:
        sys.stderr.write('Error: trivy not found. Install Trivy to use this package.\n')
        return {'Diagnostics': []}
    except Exception as error:
        sys.stderr.write(f'Error running trivy: {error}\n')
        return {'Diagnostics': []}


define_provider(schema=get_schema, query=query)
