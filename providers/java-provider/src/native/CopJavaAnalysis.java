import com.sun.source.tree.*;
import com.sun.source.util.*;
import javax.tools.*;
import javax.lang.model.element.Modifier;
import javax.lang.model.element.*;
import javax.lang.model.type.*;
import java.io.*;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.*;

public class CopJavaAnalysis {
    static String array(List<String> values) { return "[" + String.join(",", values) + "]"; }
    static String strings(Collection<?> values) {
        List<String> result = new ArrayList<>();
        for (Object value : values) result.add(quote(value.toString()));
        return array(result);
    }
    static String typeFact(Tree tree) {
        if (tree == null) return "null";
        List<String> arguments = new ArrayList<>();
        Tree base = tree;
        while (base instanceof ArrayTypeTree || base instanceof AnnotatedTypeTree)
            base = base instanceof ArrayTypeTree ? ((ArrayTypeTree)base).getType() : ((AnnotatedTypeTree)base).getUnderlyingType();
        if (base instanceof ParameterizedTypeTree) {
            for (Tree argument : ((ParameterizedTypeTree)base).getTypeArguments()) arguments.add(typeFact(argument));
            base = ((ParameterizedTypeTree)base).getType();
        }
        String namespace = base instanceof MemberSelectTree ? ((MemberSelectTree)base).getExpression().toString() : "";
        return "{\"name\":" + quote(tree.toString()) + ",\"originalText\":" + quote(tree.toString())
            + ",\"namespace\":" + quote(namespace) + ",\"genericArguments\":" + array(arguments) + "}";
    }
    static class Declarations {
        final CompilationUnitTree unit;
        final SourcePositions positions;
        Declarations(CompilationUnitTree unit, Trees trees) { this.unit = unit; this.positions = trees.getSourcePositions(); }
        String span(Tree tree) {
            long start = positions.getStartPosition(unit, tree), end = positions.getEndPosition(unit, tree);
            if (start < 0 || end < 0) return "\"line\":1,\"column\":1,\"endLine\":1,\"endColumn\":1";
            return "\"line\":" + unit.getLineMap().getLineNumber(start) + ",\"column\":" + unit.getLineMap().getColumnNumber(start)
                + ",\"endLine\":" + unit.getLineMap().getLineNumber(end) + ",\"endColumn\":" + unit.getLineMap().getColumnNumber(end);
        }
        String decorations(ModifiersTree modifiers) {
            List<String> result = new ArrayList<>();
            for (AnnotationTree annotation : modifiers.getAnnotations()) result.add(quote(annotation.getAnnotationType().toString()));
            return array(result);
        }
        String field(VariableTree field) {
            return "{\"name\":" + quote(field.getName().toString()) + ",\"type\":" + quote(field.getType().toString())
                + ",\"typeFact\":" + typeFact(field.getType())
                + ",\"modifiers\":" + strings(field.getModifiers().getFlags()) + "," + span(field) + "}";
        }
        String method(MethodTree method) {
            List<String> parameters = new ArrayList<>();
            for (VariableTree parameter : method.getParameters()) {
                boolean variadic = parameter.toString().contains("...");
                parameters.add("{\"name\":" + quote(parameter.getName().toString()) + ",\"type\":"
                    + quote(parameter.getType().toString()) + ",\"typeFact\":" + typeFact(parameter.getType())
                    + ",\"variadic\":" + variadic + "," + span(parameter) + "}");
            }
            return "{\"name\":" + quote(method.getName().toString()) + ",\"returnType\":"
                + quote(method.getReturnType() == null ? "" : method.getReturnType().toString())
                + ",\"returnTypeFact\":" + typeFact(method.getReturnType())
                + ",\"modifiers\":" + strings(method.getModifiers().getFlags())
                + ",\"decorators\":" + decorations(method.getModifiers()) + ",\"parameters\":" + array(parameters)
                + ",\"typeParameters\":" + strings(method.getTypeParameters()) + "," + span(method) + "}";
        }
        String type(ClassTree type) {
            List<String> fields = new ArrayList<>(), methods = new ArrayList<>(), nested = new ArrayList<>(), bases = new ArrayList<>(), values = new ArrayList<>();
            if (type.getExtendsClause() != null) bases.add(quote(type.getExtendsClause().toString()));
            for (Tree implemented : type.getImplementsClause()) bases.add(quote(implemented.toString()));
            for (Tree member : type.getMembers()) {
                if (member instanceof VariableTree) {
                    VariableTree variable = (VariableTree)member;
                    if (variable.getType() != null) fields.add(field(variable));
                    if (type.getKind() == Tree.Kind.ENUM && variable.getInitializer() instanceof NewClassTree
                        && variable.getModifiers().getFlags().contains(Modifier.PUBLIC)
                        && variable.getModifiers().getFlags().contains(Modifier.STATIC)
                        && variable.getModifiers().getFlags().contains(Modifier.FINAL))
                        values.add(quote(variable.getName().toString()));
                } else if (member instanceof MethodTree) methods.add(method((MethodTree)member));
                else if (member instanceof ClassTree) nested.add(type((ClassTree)member));
            }
            return "{\"name\":" + quote(type.getSimpleName().toString()) + ",\"kind\":" + quote(type.getKind().name().toLowerCase(Locale.ROOT))
                + ",\"modifiers\":" + strings(type.getModifiers().getFlags()) + ",\"decorators\":" + decorations(type.getModifiers())
                + ",\"baseTypes\":" + array(bases) + ",\"typeParameters\":" + strings(type.getTypeParameters())
                + ",\"fields\":" + array(fields) + ",\"methods\":" + array(methods)
                + ",\"nestedTypes\":" + array(nested) + ",\"enumValues\":" + array(values) + "," + span(type) + "}";
        }
    }
    static String quote(String value) {
        StringBuilder out = new StringBuilder("\"");
        for (char c : value.toCharArray()) {
            switch (c) {
                case '"': out.append("\\\""); break;
                case '\\': out.append("\\\\"); break;
                case '\n': out.append("\\n"); break;
                case '\r': out.append("\\r"); break;
                case '\t': out.append("\\t"); break;
                default:
                    if (c < 32) out.append(String.format("\\u%04x", (int)c));
                    else out.append(c);
            }
        }
        return out.append('"').toString();
    }

    static String field(String json, String name) {
        String key = "\"" + name + "\":\"";
        int start = json.indexOf(key);
        if (start < 0) throw new IllegalArgumentException("missing request field " + name);
        start += key.length();
        int end = json.indexOf('"', start);
        if (end < 0) throw new IllegalArgumentException("unterminated request field " + name);
        return new String(Base64.getDecoder().decode(json.substring(start, end)), StandardCharsets.UTF_8);
    }

    static String expression(ExpressionTree tree) {
        if (tree instanceof IdentifierTree)
            return "{\"kind\":\"identifier\",\"name\":" + quote(((IdentifierTree)tree).getName().toString()) + "}";
        if (tree instanceof MemberSelectTree) {
            MemberSelectTree member = (MemberSelectTree)tree;
            return "{\"kind\":\"memberAccess\",\"name\":" + quote(member.getIdentifier().toString())
                + ",\"target\":" + expression(member.getExpression()) + "}";
        }
        if (tree instanceof LiteralTree && ((LiteralTree)tree).getValue() instanceof String)
            return "{\"kind\":\"stringLiteral\",\"stringValue\":" + quote((String)((LiteralTree)tree).getValue()) + "}";
        if (tree instanceof ParenthesizedTree)
            return expression(((ParenthesizedTree)tree).getExpression());
        if (tree instanceof MethodInvocationTree) {
            MethodInvocationTree call = (MethodInvocationTree)tree;
            List<String> children = new ArrayList<>();
            for (ExpressionTree argument : call.getArguments()) children.add(expression(argument));
            return "{\"kind\":\"invocation\",\"target\":" + expression(call.getMethodSelect())
                + ",\"children\":[" + String.join(",", children) + "]}";
        }
        return "{\"kind\":\"other\"}";
    }

    static String analyze(JavaCompiler compiler, String path, String source) throws IOException {
        DiagnosticCollector<JavaFileObject> diagnostics = new DiagnosticCollector<>();
        JavaFileObject input = new SimpleJavaFileObject(URI.create("string:///Input.java"), JavaFileObject.Kind.SOURCE) {
            public CharSequence getCharContent(boolean ignoreEncodingErrors) { return source; }
        };
        List<String> calls = new ArrayList<>(), declarations = new ArrayList<>(), imports = new ArrayList<>(), statements = new ArrayList<>(), importFacts = new ArrayList<>();
        String namespace = "";
        try (StandardJavaFileManager manager = compiler.getStandardFileManager(diagnostics, null, StandardCharsets.UTF_8)) {
            JavacTask task = (JavacTask)compiler.getTask(null, manager, diagnostics,
                Arrays.asList("-proc:none"), null, Arrays.asList(input));
            Iterable<? extends CompilationUnitTree> units = task.parse();
            Trees trees = Trees.instance(task);
            for (CompilationUnitTree unit : units) {
                namespace = unit.getPackageName() == null ? "" : unit.getPackageName().toString();
                Declarations model = new Declarations(unit, trees);
                for (ImportTree imported : unit.getImports()) {
                    String module = imported.getQualifiedIdentifier().toString();
                    imports.add(quote(module));
                    importFacts.add("{\"module\":" + quote(module) + ",\"isStatic\":" + imported.isStatic()
                        + ",\"isWildcard\":" + module.endsWith(".*") + "," + model.span(imported) + "}");
                }

                for (Tree declaration : unit.getTypeDecls())
                    if (declaration instanceof ClassTree) declarations.add(model.type((ClassTree)declaration));
                new TreeScanner<Void, Void>() {
                    String parent = "";
                    long methodLine = 0;
                    long methodColumn = 0;
                    String id(Tree node) { return positions(node) + ":" + node.getKind().name(); }
                    long positions(Tree node) { return trees.getSourcePositions().getStartPosition(unit, node); }
                    String kind(Tree node) {
                        switch (node.getKind()) {
                            case IF: return "if";
                            case WHILE_LOOP: return "while";
                            case DO_WHILE_LOOP: return "do";
                            case FOR_LOOP: case ENHANCED_FOR_LOOP: return "for";
                            case SWITCH: return "switch";
                            case TRY: return "try";
                            case CATCH: return "catch";
                            case SYNCHRONIZED: return "synchronized";
                            case RETURN: return "return";
                            case THROW: return "throw";
                            case ASSERT: return "assert";
                            case BREAK: return "break";
                            case CONTINUE: return "continue";
                            default: return "";
                        }
                    }
                    public Void scan(Tree node, Void unused) {
                        if (node == null) return null;
                        String previousParent = parent;
                        long previousMethod = methodLine;
                        long previousColumn = methodColumn;
                        if (node instanceof MethodTree) { methodLine = unit.getLineMap().getLineNumber(positions(node)); methodColumn = unit.getLineMap().getColumnNumber(positions(node)); parent = ""; }
                        String kind = kind(node);
                        if (!kind.isEmpty()) {
                            String type = "", condition = "", expression = "";
                            boolean generic = false, rethrow = false;
                            if (node instanceof IfTree) condition = ((IfTree)node).getCondition().toString();
                            if (node instanceof WhileLoopTree) condition = ((WhileLoopTree)node).getCondition().toString();
                            if (node instanceof ReturnTree && ((ReturnTree)node).getExpression() != null) expression = ((ReturnTree)node).getExpression().toString();
                            if (node instanceof ThrowTree) {
                                ExpressionTree thrown = ((ThrowTree)node).getExpression();
                                if (thrown instanceof NewClassTree) type = ((NewClassTree)thrown).getIdentifier().toString();
                                expression = thrown.toString();
                            }
                            if (node instanceof CatchTree) {
                                CatchTree caught = (CatchTree)node;
                                type = caught.getParameter().getType().toString();
                                for (String part : type.split("\\|")) {
                                    String simple = part.trim();
                                    generic |= simple.equals("Exception") || simple.equals("Throwable") || simple.equals("Error")
                                        || simple.endsWith(".Exception") || simple.endsWith(".Throwable") || simple.endsWith(".Error");
                                }
                                type = type.split("\\|")[0].trim();
                                String variable = caught.getParameter().getName().toString();
                                for (StatementTree body : caught.getBlock().getStatements())
                                    if (body instanceof ThrowTree && ((ThrowTree)body).getExpression() instanceof IdentifierTree
                                        && ((IdentifierTree)((ThrowTree)body).getExpression()).getName().contentEquals(variable)) rethrow = true;
                            }
                            statements.add("{\"id\":" + quote(id(node)) + ",\"parentID\":" + quote(parent) + ",\"methodLine\":" + methodLine
                                + ",\"methodColumn\":" + methodColumn
                                + ",\"kind\":" + quote(kind) + ",\"typeName\":" + quote(type)
                                + ",\"condition\":" + quote(condition) + ",\"expression\":" + quote(expression)
                                + ",\"genericError\":" + generic + ",\"rethrow\":" + rethrow
                                + ",\"enhancedFor\":" + (node instanceof EnhancedForLoopTree)
                                + ",\"tryWithResources\":" + (node instanceof TryTree && !((TryTree)node).getResources().isEmpty())
                                + "," + model.span(node) + "}");
                        }
                        if (node instanceof MethodInvocationTree || node instanceof NewClassTree) enclosingCalls.put(id(node), parent);
                        if (!kind.isEmpty() || node instanceof MethodInvocationTree || node instanceof NewClassTree) parent = id(node);
                        Void result = super.scan(node, unused);
                        parent = previousParent;
                        methodLine = previousMethod;
                        methodColumn = previousColumn;
                        return result;
                    }
                    void call(Tree node, String type, String member, String target,
                              List<? extends ExpressionTree> arguments) {
                        List<String> text = new ArrayList<>(), expressions = new ArrayList<>();
                        for (ExpressionTree argument : arguments) {
                            long start = trees.getSourcePositions().getStartPosition(unit, argument);
                            long end = trees.getSourcePositions().getEndPosition(unit, argument);
                            text.add(quote(start >= 0 && end >= start && end <= source.length()
                                ? source.substring((int)start, (int)end) : argument.toString()));
                            expressions.add(expression(argument));
                        }
                        long position = trees.getSourcePositions().getStartPosition(unit, node);
                        long line = position < 0 ? 1 : unit.getLineMap().getLineNumber(position);
                        String callParent = enclosingCalls.getOrDefault(id(node), "");
                        calls.add("{\"id\":" + quote(id(node)) + ",\"parentID\":" + quote(callParent)
                            + ",\"methodLine\":" + methodLine + ",\"methodColumn\":" + methodColumn + "," + model.span(node) + ",\"typeName\":" + quote(type)
                            + ",\"memberName\":" + quote(member) + ",\"callTarget\":" + target
                            + ",\"arguments\":[" + String.join(",", text)
                            + "],\"argumentExpressions\":[" + String.join(",", expressions) + "]}");
                    }
                    final Map<String, String> enclosingCalls = new HashMap<>();
                    public Void visitMethodInvocation(MethodInvocationTree node, Void unused) {
                        ExpressionTree select = node.getMethodSelect();
                        String member = select.toString(), type = "";
                        if (select instanceof MemberSelectTree) {
                            MemberSelectTree selected = (MemberSelectTree)select;
                            member = selected.getIdentifier().toString();
                            type = selected.getExpression().toString();
                        } else if (select instanceof IdentifierTree) {
                            member = ((IdentifierTree)select).getName().toString();
                        }
                        call(node, type, member, expression(select), node.getArguments());
                        return super.visitMethodInvocation(node, unused);
                    }
                    public Void visitNewClass(NewClassTree node, Void unused) {
                        String type = node.getIdentifier().toString();
                        call(node, type, "<init>", "{\"kind\":\"memberAccess\",\"name\":\"<init>\",\"target\":{\"kind\":\"identifier\",\"name\":"
                            + quote(type) + "}}", node.getArguments());
                        return super.visitNewClass(node, unused);
                    }
                }.scan(unit, null);
            }
        }
        List<String> errors = new ArrayList<>();
        for (Diagnostic<? extends JavaFileObject> diagnostic : diagnostics.getDiagnostics())
            if (diagnostic.getKind() == Diagnostic.Kind.ERROR)
                errors.add(quote(path + "(" + diagnostic.getLineNumber() + "," + diagnostic.getColumnNumber()
                    + "): error: " + diagnostic.getMessage(Locale.ROOT)));
        return "{\"calls\":" + array(calls) + ",\"errors\":" + array(errors)
            + ",\"types\":" + array(declarations) + ",\"imports\":" + array(imports) + ",\"namespace\":" + quote(namespace)
            + ",\"statements\":" + array(statements) + ",\"importFacts\":" + array(importFacts) + "}";
    }

    static String mirror(TypeMirror type) {
        if (type == null || type.getKind() == TypeKind.ERROR || type.getKind() == TypeKind.NONE) return "null";
        List<String> arguments = new ArrayList<>();
        String namespace = "";
        TypeMirror base = type;
        while (base instanceof ArrayType) base = ((ArrayType)base).getComponentType();
        if (base instanceof DeclaredType) {
            Element owner = ((DeclaredType)base).asElement();
            while (owner != null && !(owner instanceof PackageElement)) owner = owner.getEnclosingElement();
            if (owner instanceof PackageElement) namespace = ((PackageElement)owner).getQualifiedName().toString();
            for (TypeMirror argument : ((DeclaredType)base).getTypeArguments()) {
                String fact = mirror(argument);
                if (fact.equals("null")) return "null";
                arguments.add(fact);
            }
        }
        return "{\"name\":" + quote(type.toString()) + ",\"originalText\":" + quote(type.toString())
            + ",\"namespace\":" + quote(namespace) + ",\"genericArguments\":" + array(arguments) + "}";
    }

    static String semantic(JavaCompiler compiler, String path, String source, String sources,
                           String classpath, String sourcepath) throws IOException {
        DiagnosticCollector<JavaFileObject> diagnostics = new DiagnosticCollector<>();
        List<String> facts = new ArrayList<>(), errors = new ArrayList<>();
        try (StandardJavaFileManager manager = compiler.getStandardFileManager(diagnostics, null, StandardCharsets.UTF_8)) {
            JavaFileObject original = manager.getJavaFileObjects(new File(path)).iterator().next();
            JavaFileObject target = new ForwardingJavaFileObject<JavaFileObject>(original) {
                public CharSequence getCharContent(boolean ignoreEncodingErrors) { return source; }
            };
            // Module attribution needs the manager's real path object, not the source overlay.
            JavaFileManager overlay = (JavaFileManager)java.lang.reflect.Proxy.newProxyInstance(
                JavaFileManager.class.getClassLoader(), new Class<?>[] { JavaFileManager.class }, (proxy, method, arguments) -> {
                    if (arguments != null)
                        for (int i = 0; i < arguments.length; i++) if (arguments[i] == target) arguments[i] = original;
                    try { return method.invoke(manager, arguments); }
                    catch (java.lang.reflect.InvocationTargetException failure) { throw failure.getCause(); }
                });
            List<JavaFileObject> inputs = new ArrayList<>();
            inputs.add(target);
            if (!sources.isEmpty())
                for (JavaFileObject input : manager.getJavaFileObjectsFromStrings(Arrays.asList(sources.split("\n")))) inputs.add(input);
            List<File> classFiles = new ArrayList<>();
            if (!classpath.isEmpty())
                for (String entry : classpath.split(java.util.regex.Pattern.quote(File.pathSeparator))) classFiles.add(new File(entry));
            manager.setLocation(StandardLocation.CLASS_PATH, classFiles);
            List<String> options = new ArrayList<>(Arrays.asList("-proc:none", "-implicit:none", "-encoding", "UTF-8",
                "-sourcepath", sourcepath));
            if (!classpath.isEmpty() && !System.getProperty("java.specification.version", "1.8").startsWith("1.")) {
                options.add("--module-path"); options.add(classpath);
            }
            JavacTask task = (JavacTask)compiler.getTask(null, overlay, diagnostics, options, null, inputs);
            List<CompilationUnitTree> units = new ArrayList<>();
            for (CompilationUnitTree unit : task.parse()) units.add(unit);
            task.analyze();
            Trees trees = Trees.instance(task);
            for (CompilationUnitTree unit : units) {
                if (!unit.getSourceFile().toUri().equals(target.toUri())) continue;
                Declarations model = new Declarations(unit, trees);
                new TreePathScanner<Void, Void>() {
                    void bind(Tree node, ExpressionTree select, ExpressionTree receiver) {
                        if (trees.getSourcePositions().getStartPosition(unit, node) < 0
                            || trees.getSourcePositions().getEndPosition(unit, node) < 0) return;
                        TreePath current = getCurrentPath();
                        Element element = trees.getElement(current);
                        TypeMirror value = trees.getTypeMirror(current);
                        String callable = "null";
                        if (element instanceof ExecutableElement) {
                            ExecutableElement method = (ExecutableElement)element;
                            Element owner = method.getEnclosingElement();
                            TypeMirror selected = select == null ? method.asType()
                                : trees.getTypeMirror(new TreePath(current, select));
                            ExecutableType signature = selected instanceof ExecutableType ? (ExecutableType)selected : (ExecutableType)method.asType();
                            List<String> parameters = new ArrayList<>();
                            boolean complete = signature.getReturnType().getKind() != TypeKind.ERROR;
                            for (TypeMirror parameter : signature.getParameterTypes()) {
                                String fact = mirror(parameter);
                                complete &= !fact.equals("null");
                                parameters.add(fact);
                            }
                            String namespace = task.getElements().getPackageOf(method).getQualifiedName().toString();
                            TreePath declaration = trees.getPath(method);
                            String location = "";
                            if (declaration != null) {
                                CompilationUnitTree declaredUnit = declaration.getCompilationUnit();
                                long start = trees.getSourcePositions().getStartPosition(declaredUnit, declaration.getLeaf());
                                long end = trees.getSourcePositions().getEndPosition(declaredUnit, declaration.getLeaf());
                                location = ",\"sourceFile\":" + quote(new File(declaredUnit.getSourceFile().toUri()).getAbsolutePath())
                                    + ",\"line\":" + declaredUnit.getLineMap().getLineNumber(start)
                                    + ",\"column\":" + declaredUnit.getLineMap().getColumnNumber(start);
                                if (end >= 0) location += ",\"endLine\":" + declaredUnit.getLineMap().getLineNumber(end)
                                    + ",\"endColumn\":" + declaredUnit.getLineMap().getColumnNumber(end);
                            }
                            callable = "{\"name\":" + quote(method.getSimpleName().toString())
                                + ",\"signature\":" + quote(owner.toString() + "." + method.getSimpleName() + signature.toString())
                                + ",\"declaringType\":" + quote(owner.toString()) + ",\"namespace\":" + quote(namespace)
                                + ",\"returnType\":" + mirror(signature.getReturnType()) + ",\"parameters\":" + array(parameters)
                                + location + "}";
                            if (!complete) callable = "null";
                        }
                        TypeMirror receiverType = receiver == null ? null
                            : trees.getTypeMirror(TreePath.getPath(unit, receiver));
                        facts.add("{" + model.span(node) + ",\"receiverType\":" + mirror(receiverType)
                            + ",\"valueType\":" + mirror(value) + ",\"callable\":" + callable + "}");
                    }
                    public Void visitMethodInvocation(MethodInvocationTree node, Void unused) {
                        ExpressionTree select = node.getMethodSelect();
                        bind(node, select, select instanceof MemberSelectTree ? ((MemberSelectTree)select).getExpression() : null);
                        return super.visitMethodInvocation(node, unused);
                    }
                    public Void visitNewClass(NewClassTree node, Void unused) {
                        bind(node, null, null);
                        return super.visitNewClass(node, unused);
                    }
                }.scan(unit, null);
            }
        }
        for (Diagnostic<? extends JavaFileObject> diagnostic : diagnostics.getDiagnostics())
            if (diagnostic.getKind() == Diagnostic.Kind.ERROR)
                errors.add(quote("Java semantic context: " + (diagnostic.getSource() == null ? path : diagnostic.getSource().getName())
                    + "(" + diagnostic.getLineNumber() + "): " + diagnostic.getMessage(Locale.ROOT)));
        return "{\"facts\":" + array(facts) + ",\"errors\":" + array(errors) + "}";
    }

    public static void main(String[] args) throws Exception {
        JavaCompiler compiler = ToolProvider.getSystemJavaCompiler();
        if (compiler == null) throw new IllegalStateException("JDK compiler-tree API unavailable");
        BufferedReader input = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        PrintWriter output = new PrintWriter(new OutputStreamWriter(System.out, StandardCharsets.UTF_8), true);
        String line;
        while ((line = input.readLine()) != null) {
            boolean semantic = line.contains("\"Semantic\":");
            output.println(semantic ? semantic(compiler, field(line, "Path"), field(line, "Source"),
                field(line, "Sources"), field(line, "Classpath"), field(line, "Sourcepath"))
                : analyze(compiler, field(line, "Path"), field(line, "Source")));
        }
    }
}
