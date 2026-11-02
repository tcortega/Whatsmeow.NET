// Command parity-report lists every exported method of whatsmeow's Client at the sidecar's pin as covered, excluded
// or missing (#36, ADR 0048):
//
//   - covered: the sidecar's release build calls it, named by each RPC that reaches the call, or, when no RPC does,
//     by the sidecar function that calls it (the event handler, a worker);
//   - excluded: sidecar/parity.excluded gives the reason it is not called;
//   - missing: neither, which is what a whatsmeow bump that adds a method shows.
//
// Coverage is read from the sidecar's code, never from a list: a static call graph (CHA over SSA) of the release build
// (no build tags, no tests), walked from each method that implements a whatsmeow.v1 service, then from each main
// function, through static calls and interface calls only, and into each function a reached function takes as a value
// (a closure it makes, a method value it hands on), up to the first call into whatsmeow's Client. A call through a
// function value is not followed: it would credit every closure of that signature to every RPC; the function that took
// the closure gets it instead. Code main never reaches, such as a package the build does not link, credits nothing.
//
// It never fails on a missing method; build/ci.sh shows the report and never fails the build on it. Run it from the
// tools module: go -C tools run ./parity-report.
package main

import (
	"context"
	"errors"
	"fmt"
	"go/token"
	"go/types"
	"io"
	"maps"
	"os"
	"os/signal"
	"path/filepath"
	"slices"
	"strings"

	"golang.org/x/tools/go/packages"
	"golang.org/x/tools/go/ssa"
	"golang.org/x/tools/go/ssa/ssautil"

	"github.com/tcortega/Whatsmeow.NET/tools/internal/whatsmeowapi"
)

const (
	modulePath   = "go.mau.fi/whatsmeow"
	contractDir  = "gen/whatsmeow/v1"
	protoPackage = "whatsmeow.v1"
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
	prog   *ssa.Program
	module string
	client *types.Pointer
	// types is every type the sidecar declares, and its pointer: what an interface call may reach.
	types            []types.Type
	whatsmeowDir     string
	whatsmeowVersion string
}

// load type-checks the sidecar module's packages as the release build links them: no build tags and no tests.
func load(ctx context.Context, dir string) (sidecar, error) {
	cfg := &packages.Config{Context: ctx, Dir: dir, Mode: packages.LoadAllSyntax | packages.NeedModule}

	pkgs, err := packages.Load(cfg, "./...")
	if err != nil {
		return sidecar{}, fmt.Errorf("load the sidecar: %w", err)
	}

	var (
		errs     []error
		upstream *packages.Package
	)

	packages.Visit(pkgs, nil, func(pkg *packages.Package) {
		for _, e := range pkg.Errors {
			errs = append(errs, e)
		}

		if pkg.PkgPath == modulePath {
			upstream = pkg
		}
	})

	if len(errs) > 0 {
		return sidecar{}, fmt.Errorf("load the sidecar: %w", errors.Join(errs...))
	}

	if len(pkgs) == 0 || pkgs[0].Module == nil || upstream == nil || upstream.Types.Scope().Lookup("Client") == nil {
		return sidecar{}, fmt.Errorf("load the sidecar: no module, or no %s.Client in its build", modulePath)
	}

	prog, _ := ssautil.AllPackages(pkgs, ssa.InstantiateGenerics)
	prog.Build()

	var declared []types.Type

	module := pkgs[0].Module.Path
	for _, pkg := range prog.AllPackages() {
		for _, member := range pkg.Members {
			typ, ok := member.(*ssa.Type)
			// An interface type implements an interface too, with no method of its own to reach; whatsmeow's own types
			// are not the sidecar's code.
			if ok && !types.IsInterface(typ.Type()) && (pkg.Pkg.Path() == module || strings.HasPrefix(pkg.Pkg.Path(), module+"/")) {
				declared = append(declared, typ.Type(), types.NewPointer(typ.Type()))
			}
		}
	}

	return sidecar{
		prog:             prog,
		module:           module,
		client:           types.NewPointer(upstream.Types.Scope().Lookup("Client").Type()),
		types:            declared,
		whatsmeowDir:     upstream.Module.Dir,
		whatsmeowVersion: upstream.Module.Version,
	}, nil
}

// covered is what reaches one Client method: the RPCs, else the sidecar functions that call it.
type covered struct {
	rpcs    map[string]bool
	callers map[string]bool
}

// coverage walks from each RPC, then from each main function for what no RPC reaches.
func coverage(side sidecar) (map[string]*covered, error) {
	rpcs, err := roots(side)
	if err != nil {
		return nil, err
	}

	found := map[string]*covered{}

	at := func(name string) *covered {
		if found[name] == nil {
			found[name] = &covered{rpcs: map[string]bool{}, callers: map[string]bool{}}
		}

		return found[name]
	}

	for fn, rpc := range rpcs {
		side.walk(fn, func(_ *ssa.Function, name string) { at(name).rpcs[rpc] = true })
	}

	for _, pkg := range side.prog.AllPackages() {
		if side.ownsPath(pkg.Pkg.Path()) && pkg.Pkg.Name() == "main" {
			side.walk(pkg.Func("main"), func(caller *ssa.Function, name string) { at(name).callers[side.short(caller)] = true })
		}
	}

	return found, nil
}

// walk visits each Client method the sidecar's code reachable from a function calls or takes as a value. It follows
// each function a reached function calls statically or takes as a value (a closure it makes, a method value it hands
// on, since what it hands over is called later), and each interface call to every type of the sidecar that
// implements the interface, as CHA does, and to whatsmeow's Client when it does. A call through a function value is
// not followed: it would reach every function of that signature, crediting every closure to every RPC.
func (s sidecar) walk(root *ssa.Function, visit func(caller *ssa.Function, method string)) {
	seen := map[*ssa.Function]bool{}
	next := []*ssa.Function{root}

	for len(next) > 0 {
		fn := next[len(next)-1]
		next = next[:len(next)-1]

		if seen[fn] {
			continue
		}

		seen[fn] = true

		for _, block := range fn.Blocks {
			for _, instr := range block.Instrs {
				for _, op := range instr.Operands(nil) {
					value, ok := (*op).(*ssa.Function)

					switch {
					case !ok:
					case s.clientMethod(value):
						visit(fn, top(value).Object().Name())
					case s.owns(value):
						next = append(next, value)
					}
				}

				call, ok := instr.(ssa.CallInstruction)
				if !ok || !call.Common().IsInvoke() {
					continue
				}

				method := call.Common().Method
				// An invoke's value is an interface.
				iface := call.Common().Value.Type().Underlying().(*types.Interface)

				if types.Implements(s.client, iface) {
					visit(fn, method.Name())
				}

				for _, typ := range s.types {
					if types.Implements(typ, iface) {
						next = append(next, s.prog.MethodValue(s.prog.MethodSets.MethodSet(typ).Lookup(method.Pkg(), method.Name())))
					}
				}
			}
		}
	}
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
		// A service's server interface; its client interface is implemented by the generated client only.
		iface, ok := scope.Lookup(name).Type().Underlying().(*types.Interface)
		if !ok {
			continue
		}

		service := strings.TrimSuffix(name, "Server")

		for _, impl := range side.prog.AllPackages() {
			for _, member := range impl.Members {
				typ, ok := member.(*ssa.Type)
				if !ok || !types.Implements(types.NewPointer(typ.Type()), iface) {
					continue
				}

				ptr := types.NewPointer(typ.Type())

				for method := range iface.Methods() {
					sel := side.prog.MethodSets.MethodSet(ptr).Lookup(method.Pkg(), method.Name())
					found[side.prog.MethodValue(sel)] = protoPackage + "." + service + "/" + method.Name()
				}
			}
		}
	}

	return found, nil
}

func (s sidecar) ownsPath(path string) bool {
	return path == s.module || strings.HasPrefix(path, s.module+"/")
}

// top is the named function a closure is in, or the function itself.
func top(fn *ssa.Function) *ssa.Function {
	for fn.Parent() != nil {
		fn = fn.Parent()
	}

	return fn
}

// clientMethod is whether a function is a method of whatsmeow's Client, or a method value of one.
func (s sidecar) clientMethod(fn *ssa.Function) bool {
	obj, ok := top(fn).Object().(*types.Func)

	return ok && obj.Signature().Recv() != nil && types.Identical(obj.Signature().Recv().Type(), s.client)
}

// owns is whether a function is the sidecar's own code: by the declaration it comes from, so a generic function's
// instance and a method value's wrapper are their declaration's.
func (s sidecar) owns(fn *ssa.Function) bool {
	obj := top(fn).Object()

	// A method value of a universe method, such as error's, has no package.
	return obj.Pkg() != nil && s.ownsPath(obj.Pkg().Path())
}

// short is a function as `package.(*Type).method`.
func (s sidecar) short(fn *ssa.Function) string {
	pkg := top(fn).Object().Pkg()

	return pkg.Name() + "." + fn.RelString(pkg)
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
