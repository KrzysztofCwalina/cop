"""
Cop provider that runs bandit and exposes violations.

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
            ["bandit", "-r", ".", "-f", "json", "--quiet"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root_path,
        )

        if result.returncode not in (0, 1):
            return {"Violations": []}

        output = result.stdout.strip()
        if not output:
            return {"Violations": []}

        bandit_results = json.loads(output)
        violations = []
        severity_map = {
            "HIGH": "error",
            "MEDIUM": "warning",
            "LOW": "info",
        }

        for item in bandit_results.get("results", []):
            file_path = normalize_path(item.get("filename", ""), root_path)
            if is_excluded(file_path, excluded_dirs):
                continue

            rule_id = item.get("test_id", "")
            message = item.get("issue_text", "")
            violations.append({
                "File": file_path,
                "Line": item.get("line_number", 0),
                "Severity": severity_map.get(item.get("issue_severity", "").upper(), "info"),
                "Message": f"{rule_id}: {message}" if rule_id else message,
                "Source": "bandit",
            })

        return {"Violations": violations}

    except FileNotFoundError:
        sys.stderr.write("Error: bandit not found. Install with: pip install bandit\n")
        return {"Violations": []}
    except Exception as e:
        sys.stderr.write(f"Error running bandit: {e}\n")
        return {"Violations": []}


define_provider(schema=get_schema, query=query)
