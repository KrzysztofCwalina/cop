"""
Cop provider that runs bandit and exposes diagnostics.

Requires 'bandit' to be installed.
Runs 'bandit -r . -f json --quiet' on the project directory.
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
            ["bandit", "-r", ".", "-f", "json", "--quiet"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root_path,
        )

        if result.returncode not in (0, 1):
            return {"Diagnostics": []}

        output = result.stdout.strip()
        if not output:
            return {"Diagnostics": []}

        bandit_results = json.loads(output)
        diagnostics = []
        severity_map = {
            "HIGH": "error",
            "MEDIUM": "warning",
            "LOW": "info",
        }

        for item in bandit_results.get("results", []):
            file_path = normalize_path(item.get("filename", ""), root_path)
            if is_excluded(file_path, excluded_dirs):
                continue

            diagnostics.append({
                "FilePath": file_path,
                "Line": item.get("line_number", 0),
                "Column": item.get("col_offset", 0),
                "EndLine": 0,
                "EndColumn": 0,
                "RuleId": item.get("test_id", ""),
                "Message": item.get("issue_text", ""),
                "Severity": severity_map.get(item.get("issue_severity", "").upper(), "info"),
            })

        return {"Diagnostics": diagnostics}

    except FileNotFoundError:
        sys.stderr.write("Error: bandit not found. Install with: pip install bandit\n")
        return {"Diagnostics": []}
    except Exception as e:
        sys.stderr.write(f"Error running bandit: {e}\n")
        return {"Diagnostics": []}


define_provider(schema=get_schema, query=query)
