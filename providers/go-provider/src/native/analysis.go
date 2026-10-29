package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"go/types"
	"os"
	"strconv"
	"strings"
)

type request struct {
	Path, Source string
	Semantic     bool
	Packages     []packageContext
}
type expression struct {
	Kind        string        `json:"kind"`
	Name        string        `json:"name,omitempty"`
	StringValue *string       `json:"stringValue,omitempty"`
	Target      *expression   `json:"target,omitempty"`
	Children    []*expression `json:"children,omitempty"`
}
type call struct {
	position
	ID                  string        `json:"id"`
	ParentID            string        `json:"parentID"`
	MethodLine          int           `json:"methodLine"`
	MethodColumn        int           `json:"methodColumn"`
	TypeName            string        `json:"typeName"`
	MemberName          string        `json:"memberName"`
	Arguments           []string      `json:"arguments"`
	CallTarget          *expression   `json:"callTarget"`
	ArgumentExpressions []*expression `json:"argumentExpressions"`
}
type response struct {
	Calls       []call        `json:"calls"`
	Errors      []string      `json:"errors"`
	Types       []declaration `json:"types"`
	Methods     []method      `json:"methods"`
	Namespace   string        `json:"namespace"`
	Imports     []string      `json:"imports"`
	Statements  []statement   `json:"statements"`
	ImportFacts []importFact  `json:"importFacts"`
}
type importFact struct {
	position
	Module     string `json:"module"`
	Alias      string `json:"alias,omitempty"`
	IsWildcard bool   `json:"isWildcard"`
}
type typeReference struct {
	Name             string           `json:"name"`
	Namespace        string           `json:"namespace,omitempty"`
	GenericArguments []*typeReference `json:"genericArguments"`
	OriginalText     string           `json:"originalText"`
}

func typeFact(node ast.Expr) *typeReference {
	if node == nil {
		return nil
	}
	text := types.ExprString(node)
	result := &typeReference{Name: text, OriginalText: text, GenericArguments: []*typeReference{}}
	switch t := node.(type) {
	case *ast.IndexExpr:
		result.GenericArguments = append(result.GenericArguments, typeFact(t.Index))
	case *ast.IndexListExpr:
		for _, argument := range t.Indices {
			result.GenericArguments = append(result.GenericArguments, typeFact(argument))
		}
	case *ast.StarExpr:
		result.GenericArguments = typeFact(t.X).GenericArguments
	case *ast.ArrayType:
		result.GenericArguments = typeFact(t.Elt).GenericArguments
	case *ast.Ellipsis:
		return typeFact(t.Elt)
	case *ast.SelectorExpr:
		result.Namespace = types.ExprString(t.X)
	}
	return result
}

type statement struct {
	position
	ID           string `json:"id"`
	ParentID     string `json:"parentID"`
	MethodLine   int    `json:"methodLine"`
	MethodColumn int    `json:"methodColumn"`
	Kind         string `json:"kind"`
	Condition    string `json:"condition"`
	Expression   string `json:"expression"`
	ErrorHandler bool   `json:"errorHandler"`
	MemberName   string `json:"memberName"`
}

func statementKind(node ast.Node) string {
	switch node.(type) {
	case *ast.IfStmt:
		return "if"
	case *ast.ForStmt:
		return "for"
	case *ast.RangeStmt:
		return "range"
	case *ast.SwitchStmt:
		return "switch"
	case *ast.TypeSwitchStmt:
		return "type-switch"
	case *ast.SelectStmt:
		return "select"
	case *ast.ReturnStmt:
		return "return"
	case *ast.DeferStmt:
		return "defer"
	case *ast.GoStmt:
		return "go"
	case *ast.AssignStmt:
		return "assign"
	case *ast.SendStmt:
		return "send"
	case *ast.BranchStmt:
		return "branch"
	}
	return ""
}

type position struct {
	Line      int `json:"line"`
	Column    int `json:"column"`
	EndLine   int `json:"endLine"`
	EndColumn int `json:"endColumn"`
}
type parameter struct {
	position
	Name     string         `json:"name"`
	Type     string         `json:"type"`
	Variadic bool           `json:"variadic"`
	TypeFact *typeReference `json:"typeFact"`
}
type method struct {
	position
	Name            string         `json:"name"`
	Receiver        string         `json:"receiver"`
	ReturnType      string         `json:"returnType"`
	Parameters      []parameter    `json:"parameters"`
	TypeParameters  []string       `json:"typeParameters"`
	PointerReceiver bool           `json:"pointerReceiver"`
	NamedReturns    bool           `json:"namedReturns"`
	Documented      bool           `json:"documented"`
	ReturnTypeFact  *typeReference `json:"returnTypeFact"`
}
type field struct {
	position
	Name     string         `json:"name"`
	Type     string         `json:"type"`
	TypeFact *typeReference `json:"typeFact"`
}
type declaration struct {
	position
	Name            string         `json:"name"`
	Kind            string         `json:"kind"`
	AliasTarget     string         `json:"aliasTarget"`
	BaseTypes       []string       `json:"baseTypes"`
	TypeParameters  []string       `json:"typeParameters"`
	Fields          []field        `json:"fields"`
	Methods         []method       `json:"methods"`
	StructTags      bool           `json:"structTags"`
	Union           bool           `json:"union"`
	Underlying      bool           `json:"underlying"`
	Documented      bool           `json:"documented"`
	AliasTargetFact *typeReference `json:"aliasTargetFact"`
}

func span(files *token.FileSet, node ast.Node) position {
	start, end := files.Position(node.Pos()), files.Position(node.End())
	return position{start.Line, start.Column, end.Line, end.Column}
}
func params(files *token.FileSet, fields *ast.FieldList) []parameter {
	result := []parameter{}
	if fields == nil {
		return result
	}
	for _, f := range fields.List {
		t := types.ExprString(f.Type)
		_, variadic := f.Type.(*ast.Ellipsis)
		if variadic {
			t = types.ExprString(f.Type.(*ast.Ellipsis).Elt)
		}
		if len(f.Names) == 0 {
			result = append(result, parameter{span(files, f), "", t, variadic, typeFact(f.Type)})
		}
		for _, name := range f.Names {
			result = append(result, parameter{span(files, name), name.Name, t, variadic, typeFact(f.Type)})
		}
	}
	return result
}
func generics(fields *ast.FieldList) []string {
	result := []string{}
	if fields != nil {
		for _, f := range fields.List {
			for _, name := range f.Names {
				result = append(result, name.Name+" "+types.ExprString(f.Type))
			}
		}
	}
	return result
}
func signature(files *token.FileSet, name string, function *ast.FuncType) method {
	m := method{position: span(files, function), Name: name, Parameters: params(files, function.Params),
		TypeParameters: generics(function.TypeParams)}
	if function.Results != nil {
		if len(function.Results.List) == 1 && len(function.Results.List[0].Names) == 0 {
			m.ReturnType = types.ExprString(function.Results.List[0].Type)
			m.ReturnTypeFact = typeFact(function.Results.List[0].Type)
		} else {
			returns := []string{}
			for _, f := range function.Results.List {
				t := types.ExprString(f.Type)
				if len(f.Names) == 0 {
					returns = append(returns, t)
				}
				for _, name := range f.Names {
					returns = append(returns, name.Name+" "+t)
				}
			}
			m.ReturnType = "(" + strings.Join(returns, ", ") + ")"
		}
		for _, f := range function.Results.List {
			if len(f.Names) > 0 {
				m.NamedReturns = true
			}
		}
	}
	return m
}

func expr(e ast.Expr) *expression {
	switch n := e.(type) {
	case *ast.Ident:
		return &expression{Kind: "identifier", Name: n.Name}
	case *ast.SelectorExpr:
		return &expression{Kind: "memberAccess", Name: n.Sel.Name, Target: expr(n.X)}
	case *ast.BasicLit:
		if n.Kind == token.STRING {
			if value, err := strconv.Unquote(n.Value); err == nil {
				return &expression{Kind: "stringLiteral", StringValue: &value}
			}
		}
	case *ast.ParenExpr:
		return expr(n.X)
	case *ast.CallExpr:
		children := []*expression{}
		for _, arg := range n.Args {
			children = append(children, expr(arg))
		}
		return &expression{Kind: "invocation", Target: expr(n.Fun), Children: children}
	case *ast.IndexExpr:
		return expr(n.X)
	case *ast.IndexListExpr:
		return expr(n.X)
	}
	return &expression{Kind: "other"}
}

func analyze(req request) response {
	result := response{Calls: []call{}, Errors: []string{}, Types: []declaration{}, Methods: []method{}, Imports: []string{}, Statements: []statement{}, ImportFacts: []importFact{}}
	path, pathErr := base64.StdEncoding.DecodeString(req.Path)
	source, sourceErr := base64.StdEncoding.DecodeString(req.Source)
	if pathErr != nil || sourceErr != nil {
		result.Errors = append(result.Errors, "invalid base64 request")
		return result
	}
	files := token.NewFileSet()
	file, err := parser.ParseFile(files, string(path), source, parser.AllErrors|parser.ParseComments)
	if err != nil {
		result.Errors = append(result.Errors, err.Error())
	}
	if file == nil {
		return result
	}
	result.Namespace = file.Name.Name
	for _, imp := range file.Imports {
		if path, err := strconv.Unquote(imp.Path.Value); err == nil {
			result.Imports = append(result.Imports, path)
			fact := importFact{position: span(files, imp), Module: path}
			if imp.Name != nil {
				fact.Alias = imp.Name.Name
				fact.IsWildcard = fact.Alias == "."
			}
			result.ImportFacts = append(result.ImportFacts, fact)
		}
	}
	for _, decl := range file.Decls {
		switch n := decl.(type) {
		case *ast.FuncDecl:
			m := signature(files, n.Name.Name, n.Type)
			m.position = span(files, n)
			m.Documented = n.Doc != nil
			if n.Recv != nil && len(n.Recv.List) > 0 {
				receiver := n.Recv.List[0].Type
				if pointer, ok := receiver.(*ast.StarExpr); ok {
					m.PointerReceiver = true
					receiver = pointer.X
				}
				if index, ok := receiver.(*ast.IndexExpr); ok {
					receiver = index.X
				}
				if index, ok := receiver.(*ast.IndexListExpr); ok {
					receiver = index.X
				}
				m.Receiver = types.ExprString(receiver)
			}
			result.Methods = append(result.Methods, m)
		case *ast.GenDecl:
			for _, spec := range n.Specs {
				t, ok := spec.(*ast.TypeSpec)
				if !ok {
					continue
				}
				d := declaration{position: span(files, t), Name: t.Name.Name, Kind: "named", BaseTypes: []string{},
					TypeParameters: generics(t.TypeParams), Fields: []field{}, Methods: []method{}, Documented: t.Doc != nil || n.Doc != nil}
				if t.Assign.IsValid() {
					d.Kind = "alias"
					d.AliasTarget = types.ExprString(t.Type)
					d.AliasTargetFact = typeFact(t.Type)
				} else {
					switch body := t.Type.(type) {
					case *ast.StructType:
						d.Kind = "struct"
						for _, f := range body.Fields.List {
							if f.Tag != nil {
								d.StructTags = true
							}
							if len(f.Names) == 0 {
								d.BaseTypes = append(d.BaseTypes, types.ExprString(f.Type))
							}
							for _, name := range f.Names {
								d.Fields = append(d.Fields, field{span(files, name), name.Name, types.ExprString(f.Type), typeFact(f.Type)})
							}
						}
					case *ast.InterfaceType:
						d.Kind = "interface"
						for _, f := range body.Methods.List {
							if function, ok := f.Type.(*ast.FuncType); ok && len(f.Names) > 0 {
								m := signature(files, f.Names[0].Name, function)
								m.position = span(files, f)
								d.Methods = append(d.Methods, m)
							} else {
								d.BaseTypes = append(d.BaseTypes, types.ExprString(f.Type))
							}
							ast.Inspect(f.Type, func(node ast.Node) bool {
								if binary, ok := node.(*ast.BinaryExpr); ok && binary.Op == token.OR {
									d.Union = true
								}
								if unary, ok := node.(*ast.UnaryExpr); ok && unary.Op == token.TILDE {
									d.Underlying = true
								}
								return true
							})
						}
					default:
						d.BaseTypes = append(d.BaseTypes, types.ExprString(t.Type))
					}
				}
				result.Types = append(result.Types, d)
			}
		}
	}
	type ancestor struct {
		id     string
		method int
		column int
	}
	stack := []ancestor{}
	ast.Inspect(file, func(node ast.Node) bool {
		if node == nil {
			stack = stack[:len(stack)-1]
			return true
		}
		parentID := ""
		methodLine := 0
		methodColumn := 0
		if len(stack) > 0 {
			parentID = stack[len(stack)-1].id
			methodLine = stack[len(stack)-1].method
			methodColumn = stack[len(stack)-1].column
		}
		if function, ok := node.(*ast.FuncDecl); ok {
			methodLine = files.Position(function.Pos()).Line
			methodColumn = files.Position(function.Pos()).Column
			parentID = ""
		}
		kind := statementKind(node)
		_, isCall := node.(*ast.CallExpr)
		id := parentID
		if kind != "" || isCall {
			id = fmt.Sprintf("%d:%T", node.Pos(), node)
		}
		stack = append(stack, ancestor{id, methodLine, methodColumn})
		if kind != "" {
			s := statement{position: span(files, node), ID: id, ParentID: parentID, MethodLine: methodLine, MethodColumn: methodColumn, Kind: kind}
			switch control := node.(type) {
			case *ast.IfStmt:
				s.Condition = types.ExprString(control.Cond)
				if comparison, ok := control.Cond.(*ast.BinaryExpr); ok && comparison.Op == token.NEQ {
					left, lok := comparison.X.(*ast.Ident)
					right, rok := comparison.Y.(*ast.Ident)
					s.ErrorHandler = lok && rok && left.Name == "err" && right.Name == "nil"
				}
			case *ast.ForStmt:
				if control.Cond != nil {
					s.Condition = types.ExprString(control.Cond)
				}
			case *ast.ReturnStmt:
				parts := []string{}
				for _, r := range control.Results {
					parts = append(parts, types.ExprString(r))
				}
				s.Expression = strings.Join(parts, ", ")
			}
			result.Statements = append(result.Statements, s)
		}
		n, ok := node.(*ast.CallExpr)
		if !ok {
			return true
		}
		target := expr(n.Fun)
		site := call{position: span(files, n), ID: id, ParentID: parentID, MethodLine: methodLine, MethodColumn: methodColumn, CallTarget: target,
			Arguments: []string{}, ArgumentExpressions: []*expression{}}
		switch target.Kind {
		case "identifier":
			site.MemberName = target.Name
		case "memberAccess":
			site.MemberName = target.Name
			switch fun := n.Fun.(type) {
			case *ast.SelectorExpr:
				site.TypeName = types.ExprString(fun.X)
			}
		default:
			site.MemberName = types.ExprString(n.Fun)
		}
		for _, arg := range n.Args {
			begin, end := files.Position(arg.Pos()).Offset, files.Position(arg.End()).Offset
			site.Arguments = append(site.Arguments, string(source[begin:end]))
			site.ArgumentExpressions = append(site.ArgumentExpressions, expr(arg))
		}
		if target.Kind == "identifier" && (target.Name == "panic" || target.Name == "recover") {
			kind := "throw"
			if target.Name == "recover" {
				kind = "catch"
			}
			result.Statements = append(result.Statements, statement{position: span(files, n), ID: id + ":semantic",
				ParentID: parentID, MethodLine: methodLine, MethodColumn: methodColumn, Kind: kind, MemberName: target.Name, ErrorHandler: target.Name == "recover"})
			site.ParentID = id + ":semantic"
		}
		result.Calls = append(result.Calls, site)
		return true
	})
	return result
}

func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 16*1024*1024)
	writer := bufio.NewWriter(os.Stdout)
	for scanner.Scan() {
		var req request
		if err := json.Unmarshal(scanner.Bytes(), &req); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		var result any
		if req.Semantic {
			result = semantic(req)
		} else {
			result = analyze(req)
		}
		if err := json.NewEncoder(writer).Encode(result); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		writer.Flush()
	}
	if err := scanner.Err(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
}
