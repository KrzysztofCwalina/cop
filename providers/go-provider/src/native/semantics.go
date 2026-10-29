package main

import (
	"encoding/base64"
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"go/types"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

type packageError struct{ Err string }
type packageContext struct {
	Dir, ImportPath, Name, GoVersion             string
	GoFiles, CgoFiles, TestGoFiles, XTestGoFiles []string
	ImportMap                                    map[string]string
	Module                                       *struct{ GoVersion string }
	Error                                        *packageError
	DepsErrors                                   []packageError
}
type callableReference struct {
	Name          string           `json:"name"`
	Signature     string           `json:"signature"`
	DeclaringType string           `json:"declaringType,omitempty"`
	Namespace     string           `json:"namespace,omitempty"`
	ReturnType    *typeReference   `json:"returnType"`
	Parameters    []*typeReference `json:"parameters"`
	SourceFile    string           `json:"sourceFile,omitempty"`
	Line          int              `json:"line,omitempty"`
	Column        int              `json:"column,omitempty"`
}
type semanticFact struct {
	position
	ReceiverType *typeReference     `json:"receiverType"`
	ValueType    *typeReference     `json:"valueType"`
	Callable     *callableReference `json:"callable"`
}
type semanticResponse struct {
	Facts  []semanticFact `json:"facts"`
	Errors []string       `json:"errors"`
}

func semanticType(t types.Type) *typeReference {
	if t == nil {
		return nil
	}
	if basic, ok := t.(*types.Basic); ok && basic.Kind() == types.Invalid {
		return nil
	}
	text := types.TypeString(t, func(p *types.Package) string { return p.Path() })
	result := &typeReference{Name: text, OriginalText: text, GenericArguments: []*typeReference{}}
	base := t
	for {
		switch b := base.(type) {
		case *types.Pointer:
			base = b.Elem()
		case *types.Slice:
			base = b.Elem()
		case *types.Array:
			base = b.Elem()
		default:
			goto arguments
		}
	}
arguments:
	switch named := base.(type) {
	case interface {
		Obj() *types.TypeName
		TypeArgs() *types.TypeList
	}:
		if named.Obj().Pkg() != nil {
			result.Namespace = named.Obj().Pkg().Path()
		}
		for i := 0; i < named.TypeArgs().Len(); i++ {
			argument := semanticType(named.TypeArgs().At(i))
			if argument == nil {
				return nil
			}
			result.GenericArguments = append(result.GenericArguments, argument)
		}
	}
	return result
}
func resultsType(signature *types.Signature) *typeReference {
	if signature.Results().Len() == 0 {
		return nil
	}
	if signature.Results().Len() == 1 {
		return semanticType(signature.Results().At(0).Type())
	}
	return semanticType(signature.Results())
}

type semanticLoader struct {
	files                                   *token.FileSet
	context                                 map[string]packageContext
	packages                                map[string]*types.Package
	loading                                 map[string]bool
	errors                                  *[]string
	targetPath, targetSource, targetPackage string
	target                                  *ast.File
	info                                    *types.Info
	count                                   int
	bytes                                   int64
}
type packageImporter struct {
	loader  *semanticLoader
	context packageContext
}

func (i packageImporter) Import(path string) (*types.Package, error) {
	if mapped := i.context.ImportMap[path]; mapped != "" {
		path = mapped
	}
	return i.loader.load(path)
}
func (l *semanticLoader) load(path string) (*types.Package, error) {
	if path == "unsafe" {
		return types.Unsafe, nil
	}
	if p := l.packages[path]; p != nil {
		return p, nil
	}
	if l.loading[path] {
		return nil, fmt.Errorf("import cycle involving %s", path)
	}
	context, ok := l.context[path]
	if !ok || context.Dir == "" {
		return nil, fmt.Errorf("dependency %s has no locally available source context", path)
	}
	l.loading[path] = true
	defer delete(l.loading, path)
	sources := []*ast.File{}
	names := append([]string{}, context.GoFiles...)
	if path == l.targetPackage && strings.HasSuffix(l.targetPath, "_test.go") {
		names = append(names, context.TestGoFiles...)
	}
	visited := map[string]bool{}
	for _, name := range names {
		full := filepath.Join(context.Dir, name)
		if visited[full] {
			continue
		}
		visited[full] = true
		var source any
		if samePath(full, l.targetPath) {
			source = l.targetSource
		}
		l.count++
		if source != nil {
			l.bytes += int64(len(l.targetSource))
		} else {
			info, err := os.Stat(full)
			if err != nil {
				return nil, fmt.Errorf("Go semantic source %s: %w", full, err)
			}
			l.bytes += info.Size()
		}
		if l.count > 10000 || l.bytes > 64*1024*1024 {
			return nil, fmt.Errorf("Go semantic context exceeds 10000 files or 64 MiB")
		}
		parsed, err := parser.ParseFile(l.files, full, source, parser.AllErrors)
		if err != nil {
			*l.errors = append(*l.errors, err.Error())
		}
		if parsed != nil {
			sources = append(sources, parsed)
			if samePath(full, l.targetPath) {
				l.target = parsed
			}
		}
	}
	if len(context.CgoFiles) > 0 {
		*l.errors = append(*l.errors, "package "+path+" contains cgo files; C compilation is disabled and its semantic context is incomplete")
	}
	config := types.Config{
		Importer:         packageImporter{l, context},
		Sizes:            types.SizesFor("gc", runtime.GOARCH),
		IgnoreFuncBodies: path != l.targetPackage,
		Error:            func(err error) { *l.errors = append(*l.errors, err.Error()) },
	}
	if context.Module != nil && context.Module.GoVersion != "" {
		config.GoVersion = "go" + context.Module.GoVersion
	}
	info := &types.Info{Types: map[ast.Expr]types.TypeAndValue{}, Uses: map[*ast.Ident]types.Object{},
		Defs: map[*ast.Ident]types.Object{}, Selections: map[*ast.SelectorExpr]*types.Selection{},
		Instances: map[*ast.Ident]types.Instance{}}
	pkg, err := config.Check(path, l.files, sources, info)
	if pkg != nil {
		l.packages[path] = pkg
	}
	if path == l.targetPackage {
		l.info = info
	}
	return pkg, err
}
func samePath(left, right string) bool {
	a, firstError := filepath.Abs(left)
	b, secondError := filepath.Abs(right)
	if firstError != nil || secondError != nil {
		return false
	}
	return a == b || runtime.GOOS == "windows" && strings.EqualFold(a, b)
}
func semantic(req request) semanticResponse {
	result := semanticResponse{Facts: []semanticFact{}, Errors: []string{}}
	path, err := base64.StdEncoding.DecodeString(req.Path)
	if err != nil {
		result.Errors = append(result.Errors, err.Error())
		return result
	}
	source, err := base64.StdEncoding.DecodeString(req.Source)
	if err != nil {
		result.Errors = append(result.Errors, err.Error())
		return result
	}
	l := semanticLoader{files: token.NewFileSet(), context: map[string]packageContext{}, packages: map[string]*types.Package{},
		loading: map[string]bool{}, errors: &result.Errors, targetPath: string(path), targetSource: string(source)}
	seen := map[string]bool{}
	for _, p := range req.Packages {
		l.context[p.ImportPath] = p
		if samePath(p.Dir, filepath.Dir(string(path))) {
			for _, name := range p.GoFiles {
				if samePath(filepath.Join(p.Dir, name), string(path)) {
					l.targetPackage = p.ImportPath
				}
			}
		}
		for _, problem := range append(p.DepsErrors, func() []packageError {
			if p.Error != nil {
				return []packageError{*p.Error}
			}
			return nil
		}()...) {
			if !seen[problem.Err] {
				result.Errors = append(result.Errors, problem.Err)
				seen[problem.Err] = true
			}
		}
	}
	if l.targetPackage == "" {
		result.Errors = append(result.Errors, "target package was not discovered")
		return result
	}
	before := len(result.Errors)
	if _, err := l.load(l.targetPackage); err != nil && len(result.Errors) == before {
		result.Errors = append(result.Errors, err.Error())
	}
	if l.target == nil || l.info == nil {
		result.Errors = append(result.Errors, "target source is excluded by the current Go build context or could not be checked")
		return result
	}
	ast.Inspect(l.target, func(node ast.Node) bool {
		call, ok := node.(*ast.CallExpr)
		if !ok {
			return true
		}
		fact := semanticFact{position: span(l.files, call), ValueType: semanticType(l.info.TypeOf(call))}
		fun := call.Fun
		switch indexed := fun.(type) {
		case *ast.IndexExpr:
			fun = indexed.X
		case *ast.IndexListExpr:
			fun = indexed.X
		}
		var object types.Object
		switch target := fun.(type) {
		case *ast.Ident:
			object = l.info.Uses[target]
		case *ast.SelectorExpr:
			if selection := l.info.Selections[target]; selection != nil {
				object = selection.Obj()
				fact.ReceiverType = semanticType(selection.Recv())
			} else {
				object = l.info.Uses[target.Sel]
			}
		}
		if function, ok := object.(*types.Func); ok {
			signature, _ := l.info.TypeOf(call.Fun).(*types.Signature)
			if signature == nil {
				signature, _ = function.Type().(*types.Signature)
			}
			if signature != nil {
				binding := &callableReference{Name: function.Name(), Signature: function.FullName() + types.TypeString(signature, func(p *types.Package) string { return p.Path() }),
					ReturnType: resultsType(signature), Parameters: []*typeReference{}}
				if function.Pkg() != nil {
					binding.Namespace = function.Pkg().Path()
				}
				if signature.Recv() != nil {
					binding.DeclaringType = types.TypeString(signature.Recv().Type(), func(p *types.Package) string { return p.Path() })
				}
				complete := signature.Results().Len() == 0 || binding.ReturnType != nil
				for i := 0; i < signature.Params().Len(); i++ {
					parameter := semanticType(signature.Params().At(i).Type())
					if parameter == nil {
						complete = false
						break
					}
					binding.Parameters = append(binding.Parameters, parameter)
				}
				if function.Pos().IsValid() {
					at := l.files.Position(function.Pos())
					binding.SourceFile = at.Filename
					binding.Line = at.Line
					binding.Column = at.Column
				}
				if complete {
					fact.Callable = binding
				}
			}
		}
		result.Facts = append(result.Facts, fact)
		return true
	})
	return result
}
