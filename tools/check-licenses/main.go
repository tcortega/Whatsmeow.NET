// Command check-licenses keeps sidecar/licenses.allow equal to what the whatsmeow-net binary links (ADR 0016).
//
// The allowlist holds one line per licence file of every linked module, and of the Go standard library:
//
//	<module or std> <file> <sha256 of the file> <licence: SPDX expression, LicenseRef-..., or NONE>
//
// For each release target (ADR 0007) it lists the packages of sidecar/cmd/whatsmeow-net with
// CGO_ENABLED=0 go list -deps, which names the same modules as go version -m on the built binary
// (ADR 0016). Every licence-named file of each linked module is hashed and compared with the allowlist.
// It fails on a module that is not allowlisted, a licence file that was added, removed or changed, a
// module without any licence file, and an allowlisted module that no longer links. The licence column
// is a human verdict: for a new or changed file it prints the line to add with <licence> left for the
// reviewer, and the parser rejects that placeholder.
//
// Findings print as path:line: [rule] message. Run it from the tools module: go -C tools run ./check-licenses.
// With -targets it only prints the release targets, one GOOS/GOARCH per line: build/ci.sh builds exactly those
// for the native packages, so the binaries and this check cover the same six (ADR 0023).
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"maps"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
)

const (
	allowFile = "sidecar/licenses.allow"
	moduleDir = "sidecar"
	mainPkg   = "./cmd/whatsmeow-net"
	stdModule = "std"
	review    = "review the licence text, then edit " + allowFile + " in a reviewed change (ADR 0016)"
)

// targets are the GOOS/GOARCH pairs the native packages ship (ADR 0007). build/ci.sh builds the binaries from this
// list (-targets) and fails when the packaged binaries are not exactly these (ADR 0023).
func targets() []string {
	return []string{"darwin/amd64", "darwin/arm64", "linux/amd64", "linux/arm64", "windows/amd64", "windows/arm64"}
}

var (
	licenseName = regexp.MustCompile(`(?i)^(?:licen[cs]e|copying|notice|patents|unlicense)(?:[._-].*)?$`)
	sha256Hex   = regexp.MustCompile(`^[0-9a-f]{64}$`)
	licenseExpr = regexp.MustCompile(`^[A-Za-z0-9.+-]+(?: (?:AND|OR|WITH) [A-Za-z0-9.+-]+)*$`)
)

var errFindings = errors.New("licence check failed")

func main() {
	root := flag.String("root", "..", "the repository root")
	printTargets := flag.Bool("targets", false, "print the release targets, one GOOS/GOARCH per line, and exit")
	flag.Parse()

	if *printTargets {
		for _, target := range targets() {
			fmt.Println(target)
		}

		return
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	err := run(ctx, config{root: *root, out: os.Stdout, goCmd: runGo})
	stop()
	if err != nil {
		fmt.Fprintln(os.Stderr, "check-licenses:", err)
		os.Exit(1)
	}
}

// runner runs the go command with args in dir, with env added to the process environment, and returns
// its standard output. runGo is the real one; tests inject fakes.
type runner func(ctx context.Context, dir string, env []string, args ...string) ([]byte, error)

func runGo(ctx context.Context, dir string, env []string, args ...string) ([]byte, error) {
	// The arguments are this tool's constants, and exec passes each one without a shell.
	cmd := exec.CommandContext(ctx, "go", args...) //nolint:gosec // G204: constant arguments, no shell
	cmd.Dir = dir
	cmd.Env = append(os.Environ(), env...)
	cmd.Stderr = os.Stderr

	stdout, err := cmd.Output()
	if err != nil {
		return stdout, fmt.Errorf("go %s: %w", strings.Join(args, " "), err)
	}

	return stdout, nil
}

type config struct {
	root  string
	out   io.Writer
	goCmd runner
}

// finding is one rule violation at an allowlist line (0 when no line fits).
type finding struct {
	line    int
	rule    string
	message string
}

func (f finding) String() string {
	return fmt.Sprintf("%s:%d: [%s] %s", allowFile, f.line, f.rule, f.message)
}

func run(ctx context.Context, cfg config) error {
	raw, err := os.ReadFile(filepath.Join(cfg.root, filepath.FromSlash(allowFile)))
	if err != nil {
		return fmt.Errorf("read the allowlist: %w", err)
	}

	allow, findings := parseAllow(raw)

	linked, err := linkedModules(ctx, cfg)
	if err != nil {
		return err
	}

	goroot, std, err := stdModuleFiles(ctx, cfg)
	if err != nil {
		return err
	}

	compared, err := compare(allow, goroot, std, linked)
	if err != nil {
		return err
	}

	findings = append(findings, compared...)
	slices.SortStableFunc(findings, func(a, b finding) int { return a.line - b.line })

	for _, f := range findings {
		_, err = fmt.Fprintln(cfg.out, f)
		if err != nil {
			return fmt.Errorf("report: %w", err)
		}
	}

	if len(findings) > 0 {
		return fmt.Errorf("%w: %d findings (ADR 0016)", errFindings, len(findings))
	}

	_, err = fmt.Fprintf(cfg.out, "%s matches the licence files of %d linked modules and %s\n", allowFile, len(linked), stdModule)
	if err != nil {
		return fmt.Errorf("report: %w", err)
	}

	return nil
}

// entry is one allowlist line.
type entry struct {
	line    int
	module  string
	file    string
	sum     string
	license string
}

// parseAllow reads the allowlist. Blank lines and lines starting with # are skipped.
func parseAllow(raw []byte) (map[string]map[string]entry, []finding) {
	allow := map[string]map[string]entry{}

	var findings []finding

	scanner := bufio.NewScanner(bytes.NewReader(raw))
	for number := 1; scanner.Scan(); number++ {
		text := scanner.Text()
		if strings.TrimSpace(text) == "" || strings.HasPrefix(text, "#") {
			continue
		}

		fields := strings.Fields(text)
		if len(fields) < 4 {
			findings = append(findings, finding{number, "syntax", "want \"<module> <file> <sha256> <licence>\""})

			continue
		}

		e := entry{line: number, module: fields[0], file: fields[1], sum: fields[2], license: strings.Join(fields[3:], " ")}

		if !sha256Hex.MatchString(e.sum) {
			findings = append(findings, finding{number, "syntax", "the third field must be a lowercase hex sha256"})
		}

		if !licenseExpr.MatchString(e.license) {
			findings = append(findings, finding{number, "syntax", fmt.Sprintf("licence %q is not an SPDX expression, a LicenseRef- or NONE; %s", e.license, review)})
		}

		if allow[e.module] == nil {
			allow[e.module] = map[string]entry{}
		}

		allow[e.module][e.file] = e
	}

	return allow, findings
}

// module is a linked module: where its files are, and which targets link it.
type module struct {
	path    string
	version string
	dir     string
	targets []string
}

// listedPackage is the part of go list -json output this tool reads.
type listedPackage struct {
	ImportPath string
	Standard   bool
	Module     *struct {
		Path    string
		Version string
		Main    bool
		Dir     string
	}
}

// linkedModules returns every non-main module linked into the main package on any target, keyed by
// module path.
func linkedModules(ctx context.Context, cfg config) (map[string]*module, error) {
	dir := filepath.Join(cfg.root, moduleDir)

	_, err := os.Stat(filepath.Join(dir, filepath.FromSlash(mainPkg)))
	if err != nil {
		return nil, fmt.Errorf("stat the main package: %w", err)
	}

	linked := map[string]*module{}

	for _, target := range targets() {
		goos, goarch, _ := strings.Cut(target, "/")
		env := []string{"CGO_ENABLED=0", "GOOS=" + goos, "GOARCH=" + goarch, "GOWORK=off"}

		stdout, err := cfg.goCmd(ctx, dir, env, "list", "-mod=readonly", "-deps", "-json=ImportPath,Standard,Module", mainPkg)
		if err != nil {
			return nil, fmt.Errorf("list the packages of %s for %s: %w", mainPkg, target, err)
		}

		err = addPackages(linked, stdout, target)
		if err != nil {
			return nil, fmt.Errorf("list the packages of %s for %s: %w", mainPkg, target, err)
		}
	}

	return linked, nil
}

func addPackages(linked map[string]*module, stdout []byte, target string) error {
	decoder := json.NewDecoder(bytes.NewReader(stdout))

	for {
		var pkg listedPackage

		err := decoder.Decode(&pkg)
		if errors.Is(err, io.EOF) {
			return nil
		}

		if err != nil {
			return fmt.Errorf("decode go list output: %w", err)
		}

		if pkg.Standard || pkg.Module == nil || pkg.Module.Main {
			continue
		}

		if pkg.Module.Dir == "" {
			return fmt.Errorf("go list gave no directory for %s (package %s)", pkg.Module.Path, pkg.ImportPath)
		}

		m := linked[pkg.Module.Path]
		if m == nil {
			m = &module{path: pkg.Module.Path, version: pkg.Module.Version, dir: pkg.Module.Dir}
			linked[pkg.Module.Path] = m
		}

		if !slices.Contains(m.targets, target) {
			m.targets = append(m.targets, target)
		}
	}
}

// stdModuleFiles returns GOROOT, as the sidecar module's go command sees it, and the licence files of
// the standard library: the ones at the top of GOROOT and those of the modules it vendors under
// src/vendor, which link into std packages.
func stdModuleFiles(ctx context.Context, cfg config) (string, map[string][]byte, error) {
	stdout, err := cfg.goCmd(ctx, filepath.Join(cfg.root, moduleDir), nil, "env", "GOROOT")
	if err != nil {
		return "", nil, fmt.Errorf("find GOROOT: %w", err)
	}

	dir := strings.TrimSpace(string(stdout))
	goroot := os.DirFS(dir)

	files, err := licenseFiles(goroot, ".", false)
	if err != nil {
		return "", nil, fmt.Errorf("read the GOROOT licence files: %w", err)
	}

	vendored, err := licenseFiles(goroot, "src/vendor", true)
	if err != nil {
		return "", nil, fmt.Errorf("read the GOROOT licence files: %w", err)
	}

	maps.Copy(files, vendored)

	return dir, files, nil
}

// licenseFiles reads every licence-named file under root in fsys, keyed by its slash path relative to
// fsys. Without recurse it reads only root itself. Directories the go command ignores (testdata, vendor,
// names starting with . or _) are skipped below root.
func licenseFiles(fsys fs.FS, root string, recurse bool) (map[string][]byte, error) {
	files := map[string][]byte{}

	err := fs.WalkDir(fsys, root, func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}

		if entry.IsDir() {
			if name == root {
				return nil
			}

			base := entry.Name()
			if !recurse || base == "testdata" || base == "vendor" || strings.HasPrefix(base, ".") || strings.HasPrefix(base, "_") {
				return fs.SkipDir
			}

			return nil
		}

		if !licenseName.MatchString(entry.Name()) {
			return nil
		}

		content, err := fs.ReadFile(fsys, name)
		if err != nil {
			return fmt.Errorf("read %s: %w", name, err)
		}

		files[name] = content

		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("walk %s: %w", root, err)
	}

	return files, nil
}

// compare reports every difference between the allowlist and what links.
func compare(allow map[string]map[string]entry, goroot string, std map[string][]byte, linked map[string]*module) ([]finding, error) {
	findings := compareFiles(stdModule, goroot, "the Go standard library", allow[stdModule], std)

	for _, name := range slices.Sorted(maps.Keys(linked)) {
		m := linked[name]
		slices.Sort(m.targets)
		what := fmt.Sprintf("%s %s (%s)", m.path, m.version, strings.Join(m.targets, ", "))

		files, err := licenseFiles(os.DirFS(m.dir), ".", true)
		if err != nil {
			return nil, fmt.Errorf("read the licence files of %s: %w", m.path, err)
		}

		if len(files) == 0 {
			findings = append(findings, finding{0, "no-licence", what + " has no licence file, so it cannot be redistributed; drop the dependency or get it licensed, never guess"})

			continue
		}

		if allow[name] == nil {
			findings = append(findings, finding{0, "new-module", fmt.Sprintf("%s is not in the allowlist; %s:\n%s", what, review, suggest(name, m.dir, files))})

			continue
		}

		findings = append(findings, compareFiles(name, m.dir, what, allow[name], files)...)
	}

	for _, name := range slices.Sorted(maps.Keys(allow)) {
		if name == stdModule || linked[name] != nil {
			continue
		}

		first := slices.MinFunc(slices.Collect(maps.Values(allow[name])), func(a, b entry) int { return a.line - b.line })
		findings = append(findings, finding{first.line, "stale", name + " no longer links into whatsmeow-net on any target; remove its lines"})
	}

	return findings, nil
}

func compareFiles(name, dir, what string, allowed map[string]entry, files map[string][]byte) []finding {
	var findings []finding

	for _, file := range slices.Sorted(maps.Keys(files)) {
		sum := digest(files[file])

		e, ok := allowed[file]
		switch {
		case !ok:
			findings = append(findings, finding{0, "licence-added", fmt.Sprintf("%s: %s is not in the allowlist; %s:\n%s", what, file, review, suggestLine(name, dir, file, files[file]))})
		case e.sum != sum:
			findings = append(findings, finding{e.line, "licence-changed", fmt.Sprintf("%s: %s has sha256 %s, the allowlist approved %s as %s; %s:\n%s", what, file, sum, e.sum, e.license, review, suggestLine(name, dir, file, files[file]))})
		}
	}

	for _, file := range slices.Sorted(maps.Keys(allowed)) {
		if _, ok := files[file]; !ok {
			findings = append(findings, finding{allowed[file].line, "licence-removed", fmt.Sprintf("%s: %s is gone; %s", what, file, review)})
		}
	}

	return findings
}

func suggest(name, dir string, files map[string][]byte) string {
	lines := make([]string, 0, len(files))
	for _, file := range slices.Sorted(maps.Keys(files)) {
		lines = append(lines, suggestLine(name, dir, file, files[file]))
	}

	return strings.Join(lines, "\n")
}

// suggestLine is the path of a licence file to read, as an allowlist comment, and its allowlist line,
// with <licence> for the reviewer to replace.
func suggestLine(name, dir, file string, content []byte) string {
	return fmt.Sprintf("\t# read %s\n\t%s %s %s <licence>", filepath.Join(dir, filepath.FromSlash(file)), name, file, digest(content))
}

func digest(content []byte) string {
	sum := sha256.Sum256(content)

	return hex.EncodeToString(sum[:])
}
