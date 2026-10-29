"""Conservative source-only binding. Never import or execute the analyzed modules."""
import ast
import os
import tokenize
from collections import OrderedDict


_binding_trees = OrderedDict()
_binding_tree_bytes = 0


def parse_binding_source(source, path):
    global _binding_tree_bytes
    key = (path, source)
    if key in _binding_trees:
        _binding_trees.move_to_end(key)
        return _binding_trees[key][0]
    tree = ast.parse(source, filename=path, type_comments=True)
    compile(tree, path, "exec", dont_inherit=True)
    size = len(source.encode("utf-8"))
    if size <= 8_388_608:
        while _binding_trees and (len(_binding_trees) >= 32 or _binding_tree_bytes + size > 8_388_608):
            _, (_, removed) = _binding_trees.popitem(last=False)
            _binding_tree_bytes -= removed
        _binding_trees[key] = (tree, size)
        _binding_tree_bytes += size
    return tree


class BindingUnknown(Exception):
    pass


class PythonBinding:
    def __init__(self, request):
        self.path = os.path.abspath(request["path"])
        self.source = request["source"]
        self.modules = {}
        self.scopes = {}
        self.parents = {}
        self.active = set()
        self.member_writes = []
        self.bytes = 0
        self.reasons = set()
        self.root = os.path.dirname(self.path)
        while any(os.path.isfile(os.path.join(self.root, "__init__" + ext)) for ext in (".py", ".pyi")):
            self.root = os.path.dirname(self.root)
        self.package_root = self.root
        directory = self.root
        while True:
            if any(os.path.exists(os.path.join(directory, marker)) for marker in ("pyproject.toml", "setup.cfg", "setup.py", ".git")):
                src = os.path.join(directory, "src")
                self.root = src if os.path.commonpath([src, self.path]) == src else directory
                break
            parent = os.path.dirname(directory)
            if parent == directory:
                break
            directory = parent
        self.caller = self.module(self.path, self.source)

    def unknown(self, reason):
        self.reasons.add(reason)
        return None

    def builtin_decorator(self, name, scope):
        while scope:
            if name in scope["bindings"] or scope.get("wildcard"):
                return False
            scope = scope["parent"]
        return True

    def module(self, path, source=None):
        path = os.path.abspath(path)
        if path in self.modules:
            return self.modules[path]
        if len(self.modules) >= 128:
            raise BindingUnknown("source dependency limit (128 files) exceeded")
        if source is None:
            with tokenize.open(path) as stream:
                source = stream.read(8_388_609)
        self.bytes += len(source.encode("utf-8"))
        if self.bytes > 8_388_608:
            raise BindingUnknown("source dependency size limit (8 MiB) exceeded")
        tree = parse_binding_source(source, path)
        relative = os.path.relpath(path, self.root)
        name = os.path.splitext(relative)[0].replace(os.sep, ".")
        if name.endswith(".__init__"):
            name = name[:-9]
        module = {"kind": "module", "node": tree, "path": path, "source": source, "name": name}
        self.modules[path] = module
        self.scope(tree, None, module)
        return module

    def scope(self, node, parent, module):
        scope = {"node": node, "parent": parent, "module": module, "bindings": {}}
        self.scopes[node] = scope
        bindings = scope["bindings"]

        def add(name, value):
            bindings.setdefault(name, []).append(value)

        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)):
            args = node.args
            for arg in args.posonlyargs + args.args + args.kwonlyargs + ([args.vararg] if args.vararg else []) + ([args.kwarg] if args.kwarg else []):
                add(arg.arg, {"kind": "parameter", "node": arg, "scope": scope})

        def names(target):
            if isinstance(target, ast.Name):
                yield target.id
            elif isinstance(target, (ast.Tuple, ast.List)):
                for child in target.elts:
                    yield from names(child)

        def visit(item, conditional=False):
            self.parents[item] = scope
            if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                kind = "class" if isinstance(item, ast.ClassDef) else "function"
                add(item.name, None if conditional else {"kind": kind, "node": item, "scope": scope})
                self.scope(item, scope, module)
                expressions = list(item.decorator_list)
                if isinstance(item, ast.ClassDef):
                    expressions += item.bases + item.keywords
                else:
                    expressions += [item.args] + ([item.returns] if item.returns else [])
                for expression in expressions:
                    visit(expression, conditional)
                return
            if isinstance(item, ast.Lambda):
                inner = self.scope(item, scope, module)
                for child in ast.walk(item):
                    self.parents[child] = inner
                return
            if isinstance(item, (ast.ListComp, ast.SetComp, ast.DictComp, ast.GeneratorExp)):
                for child in ast.walk(item):
                    self.parents[child] = {"node": item}
                return
            if isinstance(item, (ast.Global, ast.Nonlocal)):
                for name in item.names:
                    add(name, None)
            if isinstance(item, ast.Import):
                for alias in item.names:
                    add(alias.asname or alias.name.split(".")[0], None if conditional else {
                        "kind": "import", "module": alias.name if alias.asname else alias.name.split(".")[0],
                        "name": None, "level": 0, "scope": scope})
            if isinstance(item, ast.ImportFrom):
                for alias in item.names:
                    if alias.name == "*":
                        scope["wildcard"] = True
                    else:
                        add(alias.asname or alias.name, None if conditional else {
                            "kind": "import", "module": item.module or "", "name": alias.name,
                            "level": item.level, "scope": scope})
            if isinstance(item, (ast.Assign, ast.AnnAssign, ast.AugAssign, ast.NamedExpr)):
                targets = item.targets if isinstance(item, ast.Assign) else [item.target]
                for target in targets:
                    if isinstance(target, ast.Attribute) and getattr(item, "value", None) is not None:
                        self.member_writes.append((target, scope))
                    for name in names(target):
                        add(name, None if conditional or not isinstance(target, ast.Name) else {
                            "kind": "variable", "node": item, "scope": scope})
            if isinstance(item, (ast.For, ast.AsyncFor)):
                for name in names(item.target):
                    add(name, None)
            if isinstance(item, (ast.With, ast.AsyncWith)):
                for context in item.items:
                    if context.optional_vars:
                        for name in names(context.optional_vars):
                            add(name, None)
            if isinstance(item, ast.ExceptHandler) and item.name:
                add(item.name, None)
            if isinstance(item, ast.Delete):
                for target in item.targets:
                    if isinstance(target, ast.Attribute):
                        self.member_writes.append((target, scope))
                    for name in names(target):
                        add(name, None)
            if type(item).__name__ in ("MatchAs", "MatchStar") and item.name:
                add(item.name, None)
            if type(item).__name__ == "MatchMapping" and item.rest:
                add(item.rest, None)
            control = isinstance(item, (ast.If, ast.For, ast.AsyncFor, ast.While, ast.Try, ast.With, ast.AsyncWith)) or type(item).__name__ in ("Match", "TryStar")
            for child in ast.iter_child_nodes(item):
                visit(child, conditional or control)

        for item in node.body if hasattr(node, "body") and isinstance(node.body, list) else []:
            visit(item)
        return scope

    def lookup(self, scope, name):
        current = scope
        while current:
            if name in current["bindings"]:
                values = current["bindings"][name]
                if len(values) != 1 or values[0] is None:
                    return self.unknown("ambiguous or dynamically assigned name '" + name + "'")
                return self.materialize(values[0])
            if current.get("wildcard"):
                return self.unknown("wildcard import prevents binding '" + name + "'")
            current = current["parent"]
            # Class bodies are not enclosing lexical scopes for function bodies.
            if isinstance(scope["node"], (ast.FunctionDef, ast.AsyncFunctionDef, ast.Lambda)):
                while current and isinstance(current["node"], ast.ClassDef):
                    current = current["parent"]
        if name in ("str", "int", "float", "bool", "bytes", "complex", "list", "tuple", "dict", "set", "frozenset", "object", "type"):
            return {"kind": "builtin", "name": name}
        return self.unknown("missing declaration for '" + name + "'")

    def materialize(self, symbol):
        if symbol["kind"] != "import":
            return symbol
        scope = symbol["scope"]
        module = scope["module"]
        if symbol["level"]:
            directory = os.path.dirname(module["path"])
            for _ in range(symbol["level"] - 1):
                directory = os.path.dirname(directory)
            base = os.path.join(directory, *symbol["module"].split(".")) if symbol["module"] else directory
        else:
            parts = symbol["module"].split(".")
            local = os.path.join(self.package_root, *parts)
            base = local if os.path.isdir(local) or any(os.path.isfile(local + ext) for ext in (".pyi", ".py")) else os.path.join(self.root, *parts)
        imported = self.load_module(base)
        if imported is None:
            return None
        if symbol["name"] is None:
            return imported
        name = symbol["name"]
        imported_scope = self.scopes[imported["node"]]
        if name in imported_scope["bindings"]:
            return self.lookup(imported_scope, name)
        return self.load_module(os.path.join(os.path.dirname(imported["path"]), name))

    def load_module(self, base):
        if os.path.commonpath([self.root, os.path.abspath(base)]) != self.root:
            return self.unknown("relative import leaves source context")
        for candidate in (base + ".pyi", base + ".py", os.path.join(base, "__init__.pyi"), os.path.join(base, "__init__.py")):
            if os.path.isfile(candidate):
                if os.path.commonpath([os.path.realpath(self.root), os.path.realpath(candidate)]) != os.path.realpath(self.root):
                    return self.unknown("dependency link leaves source context")
                return self.module(candidate)
        if os.path.isdir(base):
            return self.module(os.path.join(base, "__init__.py"), "")
        return self.unknown("source module is unavailable: " + os.path.relpath(base, self.root))

    def resolve(self, node, scope):
        if isinstance(node, ast.Name):
            return self.lookup(scope, node.id)
        if isinstance(node, ast.Attribute):
            target = self.resolve(node.value, scope) if isinstance(node.value, (ast.Name, ast.Attribute)) else None
            if target and target["kind"] == "module":
                member_scope = self.scopes[target["node"]]
                if node.attr in member_scope["bindings"]:
                    return self.lookup(member_scope, node.attr)
                return self.load_module(os.path.join(os.path.dirname(target["path"]), node.attr))
            value = self.value(node.value, scope)
            if value and value.get("symbol"):
                return self.member(value["symbol"], node.attr)
        return self.unknown("dynamic callable expression")

    def intercepts_attributes(self, cls, seen):
        if cls["kind"] != "class" or id(cls["node"]) in seen:
            return False
        seen.add(id(cls["node"]))
        if "__getattribute__" in self.scopes[cls["node"]]["bindings"]:
            return True
        for node in cls["node"].bases:
            base = self.resolve(node, cls["scope"])
            if base and self.intercepts_attributes(base, seen):
                return True
        return False

    def member(self, cls, name, seen=None):
        if cls["kind"] != "class":
            return self.unknown("receiver declaration is unavailable")
        if self.intercepts_attributes(cls, set()):
            return self.unknown("custom __getattribute__ intercepts member lookup")
        seen = set() if seen is None else seen
        if id(cls["node"]) in seen:
            return self.unknown("cyclic inheritance")
        seen.add(id(cls["node"]))
        scope = self.scopes[cls["node"]]
        if cls["node"].decorator_list or cls["node"].keywords:
            return self.unknown("decorated class or custom metaclass can replace members")
        for target, write_scope in list(self.member_writes):
            if target.attr == name:
                receiver = self.value(target.value, write_scope)
                if receiver and receiver.get("symbol", {}).get("node") is cls["node"]:
                    return self.unknown("source-visible member replacement prevents binding")
        if name in scope["bindings"]:
            return self.lookup(scope, name)
        fields = []
        for method in cls["node"].body:
            if isinstance(method, (ast.FunctionDef, ast.AsyncFunctionDef)) and not method.decorator_list:
                args = method.args.posonlyargs + method.args.args
                if args:
                    for child in ast.walk(method):
                        if isinstance(child, ast.AnnAssign) and isinstance(child.target, ast.Attribute) and child.target.attr == name and isinstance(child.target.value, ast.Name) and child.target.value.id == args[0].arg:
                            if self.parents.get(child) is self.scopes[method]:
                                fields.append({"kind": "variable", "node": child, "scope": self.scopes[method]})
        if fields:
            return fields[0] if len(fields) == 1 else self.unknown("ambiguous receiver field annotation")
        bases = cls["node"].bases
        if len(bases) == 1:
            base = self.resolve(bases[0], cls["scope"])
            if base:
                return self.member(base, name, seen)
        return self.unknown("member '" + name + "' has no unique source declaration")

    def type_of_symbol(self, symbol):
        if symbol is None:
            return None
        if symbol["kind"] == "builtin":
            return {"name": symbol["name"], "namespace": "builtins", "text": symbol["name"], "args": [], "symbol": symbol}
        if symbol["kind"] == "class":
            return {"name": symbol["node"].name, "namespace": symbol["scope"]["module"]["name"],
                    "text": symbol["node"].name, "args": [], "symbol": symbol}
        return None

    def annotation(self, node, scope):
        if node is None:
            return None
        if isinstance(node, ast.Constant):
            if node.value is None:
                return {"name": "None", "namespace": "builtins", "text": "None", "args": []}
            if isinstance(node.value, str):
                try:
                    return self.annotation(ast.parse(node.value, mode="eval").body, scope)
                except SyntaxError:
                    return self.unknown("invalid forward annotation")
        if isinstance(node, ast.Subscript):
            base = self.annotation(node.value, scope)
            args = node.slice.elts if isinstance(node.slice, ast.Tuple) else [node.slice]
            arguments = [self.annotation(arg, scope) for arg in args]
            if base and all(arguments):
                return {**base, "args": arguments, "text": base["text"] + "[" + ", ".join(a["text"] for a in arguments) + "]"}
            return self.unknown("unresolved generic annotation")
        if isinstance(node, (ast.Name, ast.Attribute)):
            result = self.type_of_symbol(self.resolve(node, scope))
            if result:
                return result
        return self.unknown("annotation has no unique source type")

    def value(self, node, scope):
        if node is None:
            return None
        key = (id(node), id(scope))
        if key in self.active or len(self.active) >= 64:
            return self.unknown("recursive or excessively deep binding")
        self.active.add(key)
        try:
            if isinstance(node, ast.Name):
                symbol = self.lookup(scope, node.id)
                if symbol:
                    kind = symbol["kind"]
                    if kind in ("class", "builtin"):
                        return self.type_of_symbol(symbol)
                    if kind == "module":
                        return None
                    if kind == "parameter":
                        arg = symbol["node"]
                        parameter_scope = symbol["scope"]
                        parent = parameter_scope["parent"]
                        function = parameter_scope["node"]
                        args = function.args.posonlyargs + function.args.args
                        if parent and isinstance(parent["node"], ast.ClassDef) and args and arg is args[0]:
                            decorators = [d.id for d in function.decorator_list if isinstance(d, ast.Name)]
                            if not function.decorator_list or decorators == ["classmethod"] and self.builtin_decorator("classmethod", parent):
                                return self.type_of_symbol({"kind": "class", "node": parent["node"], "scope": parent["parent"]})
                        return self.annotation(arg.annotation, symbol["scope"])
                    if kind == "variable":
                        assignment = symbol["node"]
                        if symbol["scope"] is self.call_scope and assignment.lineno >= self.call_line:
                            return self.unknown("assignment is not before the call")
                        if getattr(assignment, "annotation", None):
                            return self.annotation(assignment.annotation, symbol["scope"])
                        return self.value(assignment.value, symbol["scope"])
            if isinstance(node, ast.Call):
                symbol = self.resolve(node.func, scope)
                if symbol and symbol["kind"] == "class" and (symbol["node"].decorator_list or symbol["node"].keywords):
                    return self.unknown("decorated class or custom metaclass has an unknown construction result")
                result = self.type_of_symbol(symbol)
                if result:
                    return result
                if symbol and symbol["kind"] == "function":
                    decorators = symbol["node"].decorator_list
                    if isinstance(symbol["node"], ast.AsyncFunctionDef):
                        return self.unknown("async invocation returns an unmodeled coroutine type")
                    if not decorators or len(decorators) == 1 and isinstance(decorators[0], ast.Name) and decorators[0].id in ("staticmethod", "classmethod") and self.builtin_decorator(decorators[0].id, symbol["scope"]):
                        return self.annotation(symbol["node"].returns, symbol["scope"])
            if isinstance(node, ast.Constant):
                name = type(node.value).__name__ if node.value is not None else "None"
                return {"name": name, "namespace": "builtins", "text": name, "args": []}
            if isinstance(node, ast.Attribute):
                target = self.value(node.value, scope)
                if target and target.get("symbol") and target["symbol"]["kind"] == "class":
                    field = self.member(target["symbol"], node.attr)
                    if field and field["kind"] == "variable":
                        return self.annotation(getattr(field["node"], "annotation", None), field["scope"])
                    if field and field["kind"] == "function" and len(field["node"].decorator_list) == 1:
                        decorator = field["node"].decorator_list[0]
                        if isinstance(decorator, ast.Name) and decorator.id == "property" and self.builtin_decorator("property", field["scope"]):
                            return self.annotation(field["node"].returns, field["scope"])
            return self.unknown("expression type is dynamic or unannotated")
        finally:
            self.active.remove(key)

    def callable(self, symbol, call, scope):
        if not symbol:
            return None
        if symbol["kind"] == "class":
            symbol = self.member(symbol, "__init__")
        if not symbol or symbol["kind"] != "function":
            return self.unknown("no source-declared callable")
        function = symbol["node"]
        declaring = symbol["scope"]
        decorators = function.decorator_list
        decorator_names = [d.id for d in decorators if isinstance(d, ast.Name)]
        if decorators and (len(decorators) != 1 or decorator_names not in (["staticmethod"], ["classmethod"])):
            return self.unknown("decorated callable may be replaced at runtime")
        if decorators and not self.builtin_decorator(decorator_names[0], declaring):
            return self.unknown("decorator name is shadowed")
        args = function.args
        positional = args.posonlyargs + args.args
        owner = declaring["node"] if isinstance(declaring["node"], ast.ClassDef) else None
        if owner and decorator_names != ["staticmethod"]:
            positional = positional[1:]
        parameters = positional + ([args.vararg] if args.vararg else []) + args.kwonlyargs + ([args.kwarg] if args.kwarg else [])
        types = [self.annotation(arg.annotation, declaring) for arg in parameters]
        if not all(types):
            return self.unknown("callable has unannotated or unresolved parameter types")
        if any(isinstance(arg, ast.Starred) for arg in call.args) or any(k.arg is None for k in call.keywords):
            return self.unknown("spread arguments prevent static argument binding")
        supplied = {arg.arg for arg in positional[:len(call.args)]}
        if len(call.args) > len(positional) and args.vararg is None:
            return self.unknown("too many positional arguments")
        for keyword in call.keywords:
            if keyword.arg in supplied or any(arg.arg == keyword.arg for arg in args.posonlyargs):
                return self.unknown("invalid keyword argument binding")
            if not any(arg.arg == keyword.arg for arg in positional + args.kwonlyargs) and args.kwarg is None:
                return self.unknown("unknown keyword argument")
            supplied.add(keyword.arg)
        required = positional[:len(positional) - len(args.defaults)] + [arg for arg, default in zip(args.kwonlyargs, args.kw_defaults) if default is None]
        if any(arg.arg not in supplied for arg in required):
            return self.unknown("required arguments are missing")
        return_type = self.annotation(function.returns, declaring)
        module = declaring["module"]
        lines = module["source"].splitlines()
        def column(line, offset):
            return len(lines[line - 1].encode("utf-8")[:offset].decode("utf-8").encode("utf-16-le")) // 2 + 1
        return {"name": function.name, "declaringType": owner.name if owner else None, "namespace": module["name"],
                "parameters": types, "returnType": return_type, "sourceFile": module["path"],
                "line": function.lineno, "column": column(function.lineno, function.col_offset),
                "endLine": function.end_lineno, "endColumn": column(function.end_lineno, function.end_col_offset),
                "signature": module["name"] + "." + (owner.name + "." if owner else "") + function.name
                + "(" + ", ".join(t["text"] for t in types) + "): " + (return_type["text"] if return_type else "?")}

    def bind(self, request):
        call = next((node for node in ast.walk(self.caller["node"]) if isinstance(node, ast.Call)
                     and node.lineno == request["line"] and node.col_offset == request["byteColumn"]
                     and node.end_lineno == request["endLine"] and node.end_col_offset == request["endByteColumn"]), None)
        if call is None:
            return {"diagnostics": ["call is absent from the source snapshot"]}
        scope = self.parents.get(call, self.scopes[self.caller["node"]])
        self.call_scope = scope
        self.call_line = call.lineno
        if isinstance(scope["node"], (ast.Lambda, ast.ListComp, ast.SetComp, ast.DictComp, ast.GeneratorExp)):
            return {"diagnostics": ["dynamic expression scope"]}
        symbol = self.resolve(call.func, scope)
        receiver = self.value(call.func.value, scope) if isinstance(call.func, ast.Attribute) else None
        value = self.value(call, scope)
        selected = self.callable(symbol, call, scope)
        if value is None:
            self.unknown("result type is unresolved or unannotated")
        # Internal symbol graphs are intentionally never included in the wire response.
        def clean(item):
            if isinstance(item, dict):
                return {key: clean(value) for key, value in item.items() if key != "symbol"}
            if isinstance(item, list):
                return [clean(value) for value in item]
            return item
        return clean({"receiver": receiver, "value": value, "callable": selected,
                      "diagnostics": sorted(self.reasons)})


def bind_python(request):
    try:
        return PythonBinding(request).bind(request)
    except (OSError, SyntaxError, UnicodeError, LookupError, BindingUnknown, RecursionError) as error:
        return {"diagnostics": [type(error).__name__ + ": " + str(error)]}
