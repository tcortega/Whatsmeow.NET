// Package whatsmeowapi reads whatsmeow's API from its source: what tools/bump-report and tools/parity-report both
// call a whatsmeow Client method, so the two never disagree on the list.
package whatsmeowapi

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"go/types"
	"io/fs"
	"path"
	"strings"
)

// Method is one exported method of whatsmeow's Client.
type Method struct {
	// Signature is the method on one line, as `func (cli *Client) Name(params) results`.
	Signature string
	// File is the module-root file that declares it, such as group.go.
	File string
}

// ClientMethods is each exported method of package whatsmeow's Client, by name: the methods declared in the
// module root's non-test files of package whatsmeow, so a generator behind `//go:build ignore` (package main) and
// a package deeper in the module are not read.
func ClientMethods(fsys fs.FS) (map[string]Method, error) {
	names, err := fs.Glob(fsys, "*.go")
	if err != nil {
		return nil, fmt.Errorf("list the module root: %w", err)
	}

	fset := token.NewFileSet()
	methods := map[string]Method{}

	for _, name := range names {
		if strings.HasSuffix(name, "_test.go") {
			continue
		}

		src, err := fs.ReadFile(fsys, name)
		if err != nil {
			return nil, fmt.Errorf("read %s: %w", name, err)
		}

		file, err := parser.ParseFile(fset, path.Base(name), src, parser.SkipObjectResolution)
		if err != nil {
			return nil, fmt.Errorf("parse %s: %w", name, err)
		}

		if file.Name.Name != "whatsmeow" {
			continue
		}

		for _, decl := range file.Decls {
			fn, ok := decl.(*ast.FuncDecl)
			if !ok || fn.Recv == nil || !fn.Name.IsExported() || !onClient(fn.Recv) {
				continue
			}

			methods[fn.Name.Name] = Method{Signature: signature(fn), File: name}
		}
	}

	return methods, nil
}

func onClient(recv *ast.FieldList) bool {
	typ := recv.List[0].Type
	if star, ok := typ.(*ast.StarExpr); ok {
		typ = star.X
	}

	ident, ok := typ.(*ast.Ident)

	return ok && ident.Name == "Client"
}

// signature is a method as `func (cli *Client) Name(params) results`, on one line.
func signature(fn *ast.FuncDecl) string {
	recv := fn.Recv.List[0]

	receiver := types.ExprString(recv.Type)
	if len(recv.Names) > 0 {
		receiver = recv.Names[0].Name + " " + receiver
	}

	return "func (" + receiver + ") " + fn.Name.Name + strings.TrimPrefix(types.ExprString(fn.Type), "func")
}
