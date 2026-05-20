"""
Cop provider that runs Ruff (Python linter) and exposes diagnostics.

Requires 'ruff' to be installed (pip install ruff).
Runs 'ruff check --output-format=json' on the project directory.
"""

import json
import os
import subprocess
import sys

# Add the SDK to the path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', 'sdk', 'python'))

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
                    {"name": "EndLine", "type": "int", "optional": True},
                    {"name": "EndColumn", "type": "int", "optional": True},
                    {"name": "RuleId"},
                    {"name": "Message"},
                    {"name": "Severity"},
                ],
            }
        ],
        "collections": [{"name": "Diagnostics", "itemType": "Diagnostic"}],
    }


def query(params):
    root_path = params.get("rootPath") or os.getcwd()
    excluded_dirs = set(params.get("excludedDirectories") or [])

    try:
        # Run ruff check with JSON output
        result = subprocess.run(
            ["ruff", "check", "--output-format=json", "."],
            capture_output=True,
            text=True,
            cwd=root_path,
        )

        # ruff exits with code 1 when there are violations (not an error)
        if result.returncode not in (0, 1):
            # Real error (ruff not found, config error, etc.)
            return {"Diagnostics": []}

        output = result.stdout.strip()
        if not output:
            return {"Diagnostics": []}

        ruff_results = json.loads(output)
        diagnostics = []

        for item in ruff_results:
            file_path = item.get("filename", "")
            # Make path relative to root
            if os.path.isabs(file_path):
                file_path = os.path.relpath(file_path, root_path)
            file_path = file_path.replace("\\", "/")

            # Skip excluded directories
            if any(file_path.startswith(d + "/") or file_path.startswith(d + "\\") for d in excluded_dirs):
                continue

            location = item.get("location", {})
            end_location = item.get("end_location", {})

            # Ruff uses "fix" availability to suggest severity
            # E/W prefix in rule code indicates error vs warning
            rule_code = item.get("code", "")
            severity = "error" if rule_code.startswith("E") else "warning"

            diagnostics.append({
                "FilePath": file_path,
                "Line": location.get("row", 0),
                "Column": location.get("column", 0),
                "EndLine": end_location.get("row", 0),
                "EndColumn": end_location.get("column", 0),
                "RuleId": rule_code,
                "Message": item.get("message", ""),
                "Severity": severity,
            })

        return {"Diagnostics": diagnostics}

    except FileNotFoundError:
        # ruff not installed
        return {"Diagnostics": []}
    except Exception:
        return {"Diagnostics": []}


define_provider(schema=get_schema, query=query)
