"""
Cop provider that runs mypy and exposes violations.

Requires 'mypy' to be installed.
Runs 'mypy . --output json' on the project directory.
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
            ["mypy", ".", "--output", "json", "--ignore-missing-imports"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root_path,
        )

        if result.returncode == 2 and not result.stdout.strip():
            sys.stderr.write(result.stderr or '')
            return {"Violations": []}

        if result.returncode not in (0, 1, 2):
            return {"Violations": []}

        output = result.stdout.strip()
        if not output:
            return {"Violations": []}

        violations = []

        for line in output.splitlines():
            line = line.strip()
            if not line:
                continue

            try:
                item = json.loads(line)
            except json.JSONDecodeError:
                continue

            file_path = normalize_path(item.get("file", ""), root_path)
            if is_excluded(file_path, excluded_dirs):
                continue

            severity = item.get("severity", "error")
            rule_id = item.get("code", "")
            message = item.get("message", "")
            violations.append({
                "File": file_path,
                "Line": item.get("line", 0),
                "Severity": "info" if severity == "note" else "error",
                "Message": f"{rule_id}: {message}" if rule_id else message,
                "Source": "mypy",
            })

        return {"Violations": violations}

    except FileNotFoundError:
        sys.stderr.write("Error: mypy not found. Install with: pip install mypy\n")
        return {"Violations": []}
    except Exception as e:
        sys.stderr.write(f"Error running mypy: {e}\n")
        return {"Violations": []}


define_provider(schema=get_schema, query=query)
