// Command parity-report lists every exported method of whatsmeow's Client at the sidecar's pin as covered, excluded
// or missing (#36, ADR 0048):
//
//   - covered: the sidecar's release build calls it, named by each RPC that reaches the call, or, when no RPC does,
//     by the sidecar function that calls it (the event handler, a worker);
//   - excluded: sidecar/parity.excluded gives the reason it is not called;
//   - missing: neither, which is what a whatsmeow bump that adds a method shows.
//
// Coverage is read from the sidecar's code, never from a list: a static call graph (CHA over SSA) from each method
// that implements a whatsmeow.v1 service, through static calls and interface calls only, and into each closure a
// function creates, up to the first call into whatsmeow's Client. A call through a function value is not followed: it
// would credit every closure of that signature to every RPC; the RPC that made the closure gets it instead.
//
// It never fails on a missing method; build/ci.sh shows the report and never fails the build on it. Run it from the
// tools module: go -C tools run ./parity-report.
package main

import (
	"context"
	"errors"
	"fmt"
	"go/constant"
	"go/token"
	"go/types"
	"io"
	"maps"
	"os"
	"os/signal"
	"path/filepath"
	"slices"
	"strings"

	"golang.org/x/tools/go/callgraph"
	"golang.org/x/tools/go/callgraph/cha"
	"golang.org/x/tools/go/packages"
	"golang.org/x/tools/go/ssa"
	"golang.org/x/tools/go/ssa/ssautil"

	"github.com/tcortega/Whatsmeow.NET/tools/internal/whatsmeowapi"
)

const (
	modulePath   = "go.mau.fi/whatsmeow"
	contractDir  = "gen/whatsmeow/v1"
	excludedName = "parity.excluded"
)

var errNoContract = errors.New("the sidecar has no generated whatsmeow.v1 package at " + contractDir)

func main() {
	err := start()
	if err != nil {
		fmt.Fprintln(os.Stderr, "parity-report:", err)
		os.Exit(1)
	}
}

// start reads the sidecar and its exclusions from the tools module.
func start() error {
	dir := filepath.Join("..", "sidecar")

	excluded, err := os.ReadFile(filepath.Join(dir, excludedName)) //nolint:gosec // G304: a fixed repository file
	if err != nil {
		return fmt.Errorf("read sidecar/%s: %w", excludedName, err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	return run(ctx, config{dir: dir, excluded: excluded, out: os.Stdout})
}

type config struct {
	// dir is the sidecar module's directory.
	dir      string
	excluded []byte
	out      io.Writer
}

func run(ctx context.Context, cfg config) error {
	exclusions, err := parseExcluded(cfg.excluded)
	if err != nil {
		return err
	}

	side, err := load(ctx, cfg.dir)
	if err != nil {
		return err
	}

	methods, err := whatsmeowapi.ClientMethods(os.DirFS(side.whatsmeowDir))
	if err != nil {
		return fmt.Errorf("whatsmeow at %s: %w", side.whatsmeowDir, err)
	}

	covered, err := coverage(side)
	if err != nil {
		return err
	}

	_, err = io.WriteString(cfg.out, write(side.whatsmeowVersion, methods, covered, exclusions))
	if err != nil {
		return fmt.Errorf("report: %w", err)
	}

	return nil
}

// parseExcluded reads parity.excluded: one `Name: reason` per line; blank lines and lines starting with # are skipped.
func parseExcluded(raw []byte) (map[string]string, error) {
	exclusions := map[string]string{}

	for i, line := range strings.Split(string(raw), "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}

		at := fmt.Sprintf("%s:%d", excludedName, i+1)

		name, reason, ok := strings.Cut(line, ":")
		name, reason = strings.TrimSpace(name), strings.TrimSpace(reason)

		switch {
		case !ok:
			return nil, fmt.Errorf("%s: want Name: reason", at)
		case !token.IsIdentifier(name) || !token.IsExported(name):
			return nil, fmt.Errorf("%s: %s is not an exported method name", at, name)
		case reason == "":
			return nil, fmt.Errorf("%s: %s has no reason", at, name)
		}

		if _, twice := exclusions[name]; twice {
			return nil, fmt.Errorf("%s: %s is listed twice", at, name)
		}

		exclusions[name] = reason
	}

	return exclusions, nil
}

// sidecar is the sidecar's release build as SSA, and the whatsmeow it links.
type sidecar struct {
	prog             *ssa.Program
	module           string
	whatsmeowDir     string
	whatsmeowVersion string
}

// load type-checks every package of the sidecar module, without build tags: the release build.
func load(ctx context.Context, dir string) (sidecar, error) {
	cfg := &packages.Config{Context: ctx, Dir: dir, Mode: packages.LoadAllSyntax | packages.NeedModule}

	pkgs, err := packages.Load(cfg, "./...")
	if err != nil {
		return sidecar{}, fmt.Errorf("load the sidecar: %w", err)
	}

	var (
		errs     []error
		upstream *packages.Module
	)

	packages.Visit(pkgs, nil, func(pkg *packages.Package) {
		for _, e := range pkg.Errors {
			errs = append(errs, e)
		}

		if pkg.PkgPath == modulePath {
			upstream = pkg.Module
		}
	})

	if len(errs) > 0 {
		return sidecar{}, fmt.Errorf("load the sidecar: %w", errors.Join(errs...))
	}

	if len(pkgs) == 0 || pkgs[0].Module == nil || upstream == nil {
		return sidecar{}, fmt.Errorf("load the sidecar: no module, or no %s in its build", modulePath)
	}

	whatsmeowDir := upstream.Dir
	if upstream.Replace != nil && upstream.Replace.Dir != "" {
		whatsmeowDir = upstream.Replace.Dir
	}

	prog, _ := ssautil.AllPackages(pkgs, ssa.InstantiateGenerics)
	prog.Build()

	return sidecar{prog: prog, module: pkgs[0].Module.Path, whatsmeowDir: whatsmeowDir, whatsmeowVersion: upstream.Version}, nil
}

// covered is what reaches one Client method: the RPCs, else the sidecar functions that call it.
type covered struct {
	rpcs    map[string]bool
	callers map[string]bool
}

func coverage(side sidecar) (map[string]*covered, error) {
	rpcs, err := roots(side)
	if err != nil {
		return nil, err
	}

	graph := cha.CallGraph(side.prog)
	found := map[string]*covered{}
	reached := map[*ssa.Function]bool{}

	at := func(name string) *covered {
		if found[name] == nil {
			found[name] = &covered{rpcs: map[string]bool{}, callers: map[string]bool{}}
		}

		return found[name]
	}

	for _, fn := range slices.SortedFunc(maps.Keys(rpcs), func(a, b *ssa.Function) int { return strings.Compare(a.String(), b.String()) }) {
		rpc := rpcs[fn]
		seen := map[*ssa.Function]bool{}

		var walk func(fn *ssa.Function)

		walk = func(fn *ssa.Function) {
			if seen[fn] || graph.Nodes[fn] == nil {
				return
			}

			seen[fn] = true
			reached[fn] = true

			for _, anon := range fn.AnonFuncs {
				walk(anon)
			}

			for _, edge := range graph.Nodes[fn].Out {
				if !followed(edge) {
					continue
				}

				if name, ok := clientMethod(edge.Callee.Func); ok {
					at(name).rpcs[rpc] = true

					continue
				}

				if side.owns(edge.Callee.Func) {
					walk(edge.Callee.Func)
				}
			}
		}

		walk(fn)
	}

	for fn, node := range graph.Nodes {
		if fn == nil || reached[fn] || !side.owns(fn) {
			continue
		}

		for _, edge := range node.Out {
			if name, ok := clientMethod(edge.Callee.Func); ok && followed(edge) {
				at(name).callers[side.short(fn)] = true
			}
		}
	}

	return found, nil
}

// roots maps each method that implements an RPC of the generated whatsmeow.v1 services to the RPC's full name.
func roots(side sidecar) (map[*ssa.Function]string, error) {
	contract := side.prog.ImportedPackage(side.module + "/" + contractDir)
	if contract == nil {
		return nil, errNoContract
	}

	scope := contract.Pkg.Scope()
	found := map[*ssa.Function]string{}

	for _, name := range scope.Names() {
		service, ok := strings.CutSuffix(name, "Server")
		if !ok || strings.HasPrefix(name, "Unsafe") {
			continue
		}

		iface, ok := scope.Lookup(name).Type().Underlying().(*types.Interface)
		if !ok {
			continue
		}

		for _, impl := range side.prog.AllPackages() {
			if !side.ownsPackage(impl) {
				continue
			}

			for _, member := range impl.Members {
				typ, ok := member.(*ssa.Type)
				if !ok || types.IsInterface(typ.Type()) {
					continue
				}

				ptr := types.NewPointer(typ.Type())
				if !types.Implements(ptr, iface) {
					continue
				}

				for method := range iface.Methods() {
					if !method.Exported() {
						continue
					}

					sel := side.prog.MethodSets.MethodSet(ptr).Lookup(method.Pkg(), method.Name())
					// A method promoted from an embedded field is the Unimplemented one, which reaches nothing.
					if sel == nil || len(sel.Index()) > 1 {
						continue
					}

					found[side.prog.MethodValue(sel)] = fullName(scope, service, method.Name())
				}
			}
		}
	}

	return found, nil
}

// fullName is protoc-gen-go-grpc's `<Service>_<Method>_FullMethodName`, without its leading slash.
func fullName(scope *types.Scope, service, method string) string {
	if c, ok := scope.Lookup(service + "_" + method + "_FullMethodName").(*types.Const); ok && c.Val().Kind() == constant.String {
		return strings.TrimPrefix(constant.StringVal(c.Val()), "/")
	}

	return service + "/" + method
}

// followed is whether an edge is a static call or an interface call: a call through a function value is not.
func followed(edge *callgraph.Edge) bool {
	if edge.Site == nil {
		return false
	}

	common := edge.Site.Common()

	return common.StaticCallee() != nil || common.IsInvoke()
}

// clientMethod is the name of an exported method of whatsmeow's Client.
func clientMethod(fn *ssa.Function) (string, bool) {
	recv := fn.Signature.Recv()
	if recv == nil || !token.IsExported(fn.Name()) {
		return "", false
	}

	typ := recv.Type()
	if ptr, ok := typ.(*types.Pointer); ok {
		typ = ptr.Elem()
	}

	named, ok := typ.(*types.Named)
	if !ok || named.Obj().Pkg() == nil || named.Obj().Pkg().Path() != modulePath || named.Obj().Name() != "Client" {
		return "", false
	}

	return fn.Name(), true
}

func (s sidecar) ownsPackage(pkg *ssa.Package) bool {
	return pkg != nil && (pkg.Pkg.Path() == s.module || strings.HasPrefix(pkg.Pkg.Path(), s.module+"/"))
}

// owns is whether a function is the sidecar's own code; a generic one's instance is its origin's.
func (s sidecar) owns(fn *ssa.Function) bool {
	if fn.Pkg == nil && fn.Origin() != nil {
		fn = fn.Origin()
	}

	return s.ownsPackage(fn.Pkg)
}

// short is a function as `package.(*Type).method`.
func (s sidecar) short(fn *ssa.Function) string {
	if fn.Pkg == nil && fn.Origin() != nil {
		fn = fn.Origin()
	}

	return fn.Pkg.Pkg.Name() + "." + fn.RelString(fn.Pkg.Pkg)
}

// write is the report: a summary line, then a table per whatsmeow file, then the exclusions to update.
func write(version string, methods map[string]whatsmeowapi.Method, found map[string]*covered, exclusions map[string]string) string {
	var (
		b                  strings.Builder
		ncovered, nmissing int
		nexcluded          int
		stale              []string
	)

	byFile := map[string][]string{}
	for name, m := range methods {
		byFile[m.File] = append(byFile[m.File], name)
	}

	var tables strings.Builder

	for _, file := range slices.Sorted(maps.Keys(byFile)) {
		fmt.Fprintf(&tables, "### %s\n\n| Method | Status | By or why |\n| --- | --- | --- |\n", file)

		for _, name := range slices.Sorted(slices.Values(byFile[file])) {
			reason, excluded := exclusions[name]

			switch c := found[name]; {
			case c != nil:
				ncovered++

				fmt.Fprintf(&tables, "| `%s` | covered | %s |\n", name, c.by())

				if excluded {
					stale = append(stale, fmt.Sprintf("- `%s` is covered; drop its exclusion.\n", name))
				}
			case excluded:
				nexcluded++

				fmt.Fprintf(&tables, "| `%s` | excluded | %s |\n", name, strings.ReplaceAll(reason, "|", `\|`))
			default:
				nmissing++

				fmt.Fprintf(&tables, "| `%s` | **missing** | |\n", name)
			}
		}

		tables.WriteString("\n")
	}

	for _, name := range slices.Sorted(maps.Keys(exclusions)) {
		if _, ok := methods[name]; !ok {
			stale = append(stale, fmt.Sprintf("- `%s` is not a `Client` method at this pin; drop its exclusion.\n", name))
		}
	}

	b.WriteString("## whatsmeow Client parity\n\n")
	fmt.Fprintf(&b, "`%s` `%s`: %d exported `Client` methods, %d covered, %d excluded, **%d missing**.\n",
		modulePath, version, len(methods), ncovered, nexcluded, nmissing)
	b.WriteString("Covered: the sidecar's release build calls it, from the RPCs named, or with no RPC from the function named. ")
	b.WriteString("Excluded: `sidecar/parity.excluded` says why not. Missing: neither (ADR 0048).\n\n")
	b.WriteString(tables.String())

	if len(stale) > 0 {
		b.WriteString("### Exclusions to update in sidecar/parity.excluded\n\n")
		b.WriteString(strings.Join(stale, ""))
		b.WriteString("\n")
	}

	return b.String()
}

// by is the RPCs that reach a method, else the functions that call it.
func (c *covered) by() string {
	if len(c.rpcs) > 0 {
		return "`" + strings.Join(slices.Sorted(maps.Keys(c.rpcs)), "`, `") + "`"
	}

	return "no RPC: `" + strings.Join(slices.Sorted(maps.Keys(c.callers)), "`, `") + "`"
}
