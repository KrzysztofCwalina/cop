"""
Cop provider that runs pylint and exposes diagnostics.

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
                "name": "Diagnostic",
                "properties": [
                    {"name": "FilePath"},
                    {"name": "Line", "type": "int"},
                    {"name": "Column", "type": "int"},
                    {"name": "EndLine", "type": "int"},
                    {"name": "EndColumn", "type": "int"},
                    {"name": "RuleId"},
                    {"name": "Message"},
                    {"name": "Severity"},
                ],
            }
        ],
        "collections": [{"name": "Diagnostics", "itemType": "Diagnostic"}],
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
            return {"Diagnostics": []}

        pylint_results = json.loads(output)
        diagnostics = []
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

            diagnostics.append({
                "FilePath": file_path,
                "Line": item.get("line", 0),
                "Column": item.get("column", 0),
                "EndLine": 0,
                "EndColumn": 0,
                "RuleId": item.get("message-id", ""),
                "Message": item.get("message", ""),
                "Severity": severity_map.get(item.get("type", "").lower(), "info"),
            })

        return {"Diagnostics": diagnostics}

    except FileNotFoundError:
        sys.stderr.write("Error: pylint not found. Install with: pip install pylint\n")
        return {"Diagnostics": []}
    except Exception as e:
        sys.stderr.write(f"Error running pylint: {e}\n")
        return {"Diagnostics": []}


define_provider(schema=get_schema, query=query)
