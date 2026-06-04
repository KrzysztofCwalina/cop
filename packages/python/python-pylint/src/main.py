"""
Cop provider that runs pylint and exposes violations.

Requires 'pylint' to be installed.
Runs 'pylint --output-format=json --recursive=y .' on the project directory.
"""

import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'sdk', 'python'))

from cop_provider_sdk import define_provider


def get_schema():
    return {
        "types": [
            {
                "name": "Violation",
                "properties": [
                    {"name": "File"},
                    {"name": "Line", "type": "int"},
                    {"name": "Severity"},
                    {"name": "Message"},
                    {"name": "Source"},
                ],
            }
        ],
        "collections": [{"name": "Violations", "itemType": "Violation"}],
    }


def normalize_path(file_path, root_path):
    if not file_path:
        return ""
    if os.path.isabs(file_path):
        file_path = os.path.relpath(file_path, root_path)
    file_path = os.path.normpath(file_path)
    return file_path.replace("\\", "/")


def is_excluded(file_path, excluded_dirs):
    return any(file_path == d or file_path.startswith(d + "/") for d in excluded_dirs)


def query(params):
    root_path = params.get("rootPath") or os.getcwd()
    excluded_dirs = {
        os.path.normpath(d).replace("\\", "/").rstrip("/")
        for d in (params.get("excludedDirectories") or [])
        if d
    }

    try:
        result = subprocess.run(
            ["pylint", "--output-format=json", "--recursive=y", "."],
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root_path,
        )

        output = result.stdout.strip()
        if not output:
            return {"Violations": []}

        pylint_results = json.loads(output)
        violations = []
        severity_map = {
            "fatal": "error",
            "error": "error",
            "warning": "warning",
            "convention": "info",
            "refactor": "info",
            "info": "info",
        }

        for item in pylint_results:
            file_path = normalize_path(item.get("path", ""), root_path)
            if is_excluded(file_path, excluded_dirs):
                continue

            rule_id = item.get("message-id", "")
            message = item.get("message", "")
            violations.append({
                "File": file_path,
                "Line": item.get("line", 0),
                "Severity": severity_map.get(item.get("type", "").lower(), "info"),
                "Message": f"{rule_id}: {message}" if rule_id else message,
                "Source": "pylint",
            })

        return {"Violations": violations}

    except FileNotFoundError:
        sys.stderr.write("Error: pylint not found. Install with: pip install pylint\n")
        return {"Violations": []}
    except Exception as e:
        sys.stderr.write(f"Error running pylint: {e}\n")
        return {"Violations": []}


define_provider(schema=get_schema, query=query)
