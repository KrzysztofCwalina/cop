"""
Cop provider that runs Trivy and exposes vulnerability and misconfiguration findings as violations.
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
            return {'Violations': []}

        trivy_results = json.loads(output)
        violations = []

        for item in trivy_results.get('Results') or []:
            file_path = normalize_relative_path(item.get('Target') or '', root_path)
            if not file_path or is_excluded(file_path, excluded_directories):
                continue

            for vulnerability in item.get('Vulnerabilities') or []:
                rule_id = vulnerability.get('VulnerabilityID') or ''
                message = build_vulnerability_message(vulnerability)
                violations.append({
                    'File': file_path,
                    'Line': 0,
                    'Severity': map_severity(vulnerability.get('Severity')),
                    'Message': f'{rule_id}: {message}' if rule_id else message,
                    'Source': 'trivy',
                })

            for misconfiguration in item.get('Misconfigurations') or []:
                metadata = misconfiguration.get('CauseMetadata') or {}
                line = int(metadata.get('StartLine') or 0)
                rule_id = misconfiguration.get('ID') or misconfiguration.get('AVDID') or ''
                message = misconfiguration.get('Title') or misconfiguration.get('Message') or ''
                violations.append({
                    'File': file_path,
                    'Line': line,
                    'Severity': map_severity(misconfiguration.get('Severity')),
                    'Message': f'{rule_id}: {message}' if rule_id else message,
                    'Source': 'trivy',
                })

        return {'Violations': violations}
    except FileNotFoundError:
        sys.stderr.write('Error: trivy not found. Install Trivy to use this package.\n')
        return {'Violations': []}
    except Exception as error:
        sys.stderr.write(f'Error running trivy: {error}\n')
        return {'Violations': []}


define_provider(schema=get_schema, query=query)
