"""
Cop provider that runs Ruff (Python linter) and exposes violations.

Requires 'ruff' to be installed (pip install ruff).
Runs 'ruff check --output-format=json' on the project directory.
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


def query(params):
    root_path = params.get("rootPath") or os.getcwd()
    excluded_dirs = set(params.get("excludedDirectories") or [])

    try:
        result = subprocess.run(
            ["ruff", "check", "--output-format=json", "."],
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=root_path,
        )

        # ruff exits with code 1 when there are violations (not an error)
        if result.returncode not in (0, 1):
            return {"Violations": []}

        output = result.stdout.strip()
        if not output:
            return {"Violations": []}

        ruff_results = json.loads(output)
        violations = []

        for item in ruff_results:
            file_path = item.get("filename", "")
            if os.path.isabs(file_path):
                file_path = os.path.relpath(file_path, root_path)
            file_path = file_path.replace("\\", "/")

            if any(file_path.startswith(d + "/") or file_path.startswith(d + "\\") for d in excluded_dirs):
                continue

            location = item.get("location", {})
            rule_code = item.get("code", "")
            message = item.get("message", "")
            severity = "error" if rule_code.startswith("E") else "warning"

            violations.append({
                "File": file_path,
                "Line": location.get("row", 0),
                "Severity": severity,
                "Message": f"{rule_code}: {message}" if rule_code else message,
                "Source": "ruff",
            })

        return {"Violations": violations}

    except FileNotFoundError:
        sys.stderr.write("Error: ruff not found. Install with: pip install ruff\n")
        return {"Violations": []}
    except Exception as e:
        sys.stderr.write(f"Error running ruff: {e}\n")
        return {"Violations": []}


define_provider(schema=get_schema, query=query)
