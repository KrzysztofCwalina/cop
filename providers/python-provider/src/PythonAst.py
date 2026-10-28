import ast
import io
import json
import math
import sys
import tokenize


def main(request):
    source = request["source"]
    path = request["path"]
    try:
        if request.get("mode") == "toml":
            try:
                import tomllib
            except ModuleNotFoundError:
                return {"unsupported": "TOML metadata requires Python 3.11 or newer"}
            data = tomllib.loads(source)
            project = data.get("project", {})
            poetry = data.get("tool", {}).get("poetry", {})
            dependencies = list(project.get("dependencies", []))
            for group in project.get("optional-dependencies", {}).values():
                dependencies.extend(group)
            dependencies.extend(k for k in poetry.get("dependencies", {}) if k != "python")
            dependencies.extend(poetry.get("dev-dependencies", {}))
            for group in poetry.get("group", {}).values():
                dependencies.extend(group.get("dependencies", {}))
            return {"name": project.get("name", poetry.get("name")), "dependencies": dependencies,
                    "unresolved": project.get("dynamic", [])}

        tree = ast.parse(source, filename=path, type_comments=True)
        compile(tree, path, "exec", dont_inherit=True)
        if request.get("mode") == "setup":
            for node in ast.walk(tree):
                if isinstance(node, ast.Call) and (
                    isinstance(node.func, ast.Name) and node.func.id == "setup"
                    or isinstance(node.func, ast.Attribute) and node.func.attr == "setup"
                ):
                    values = {}
                    unresolved = []
                    for keyword in node.keywords:
                        if keyword.arg in ("name", "install_requires", "extras_require"):
                            try:
                                values[keyword.arg] = ast.literal_eval(keyword.value)
                            except (ValueError, TypeError):
                                unresolved.append(keyword.arg)
                    dependencies = list(values.get("install_requires", []))
                    for group in values.get("extras_require", {}).values():
                        dependencies.extend(group)
                    return {"name": values.get("name"), "dependencies": dependencies, "unresolved": unresolved}
            return {"name": None, "dependencies": []}

        lines = source.splitlines(keepends=True)

        def text(node):
            if not hasattr(node, "lineno") or not hasattr(node, "end_lineno"):
                return ""
            first = node.lineno - 1
            last = node.end_lineno - 1
            if first == last:
                return lines[first].encode("utf-8")[node.col_offset:node.end_col_offset].decode("utf-8")
            return (lines[first].encode("utf-8")[node.col_offset:].decode("utf-8")
                    + "".join(lines[first + 1:last])
                    + lines[last].encode("utf-8")[:node.end_col_offset].decode("utf-8"))

        def encode(node):
            if isinstance(node, ast.AST):
                result = {"kind": type(node).__name__}
                if isinstance(node, ast.Constant):
                    result["constant_type"] = type(node.value).__name__
                if hasattr(node, "lineno"):
                    result["line"] = node.lineno
                    result["column"] = len(lines[node.lineno - 1].encode("utf-8")[:node.col_offset].decode("utf-8").encode("utf-16-le")) // 2 + 1
                if getattr(node, "end_lineno", None) is not None:
                    result["end_line"] = node.end_lineno
                    result["end_column"] = len(lines[node.end_lineno - 1].encode("utf-8")[:node.end_col_offset].decode("utf-8").encode("utf-16-le")) // 2 + 1
                if isinstance(node, (ast.expr, ast.arg)):
                    result["text"] = text(node)
                for key, value in ast.iter_fields(node):
                    if key not in ("ctx", "kind"):
                        result[key] = encode(value)
                return result
            if isinstance(node, list):
                return [encode(item) for item in node]
            if isinstance(node, float) and not math.isfinite(node):
                return repr(node)
            if node is None or isinstance(node, (str, bool, int, float)):
                return node
            return repr(node)

        comments = [t.start[0] for t in tokenize.generate_tokens(io.StringIO(source).readline)
                    if t.type == tokenize.COMMENT]
        return {"tree": encode(tree), "comments": comments}
    except SyntaxError as error:
        return {"error": error.msg, "line": error.lineno or 1, "column": error.offset or 1}
    except (ValueError, TypeError) as error:
        return {"error": str(error), "line": 1, "column": 1}


for request_line in sys.stdin:
    try:
        print(json.dumps(main(json.loads(request_line)), ensure_ascii=True, allow_nan=False), flush=True)
    except Exception as error:
        print(type(error).__name__ + ": " + str(error), file=sys.stderr, flush=True)
        sys.exit(1)
