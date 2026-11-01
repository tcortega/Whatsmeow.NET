// Command sync-protos keeps third_party/whatsmeow byte identical with the pinned whatsmeow module.
//
// The pin is the single line in third_party/whatsmeow/VERSION, in go.sum form:
//
//	go.mau.fi/whatsmeow v0.0.0-20261030195514-56e5ad0581ce h1:...
//
// The module is fetched with `go mod download -json`, so its content is verified against the Go
// checksum database and against the recorded h1 sum. Every *.proto under the module's proto/
// directory, and the module LICENSE, must match the copy byte for byte, and the copy holds nothing else.
// Once sidecar/go.mod requires the module, the required version must be the pinned one.
//
// By default it only checks, and exits non-zero on any difference. With -write it replaces the copy.
// Run it from the tools module: go -C tools run ./sync-protos [-write].
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"os"
	"os/exec"
	"os/signal"
	"path"
	"path/filepath"
	"slices"
	"strings"

	"golang.org/x/mod/modfile"
)

const (
	versionFile = "VERSION"
	licenseFile = "LICENSE"
	protoDir    = "proto"
)

var (
	errMismatch   = errors.New("third_party copy does not match the pinned module")
	errUnexpected = errors.New("third_party copy holds files that are not upstream protos")
	errPin        = errors.New("the pin does not match")
	errDownload   = errors.New("download failed")
)

func main() {
	write := flag.Bool("write", false, "replace the copy with the pinned module's files instead of checking it")
	dir := flag.String("dir", filepath.Join("..", "third_party", "whatsmeow"), "the third_party copy, holding VERSION")
	goMod := flag.String("gomod", filepath.Join("..", "sidecar", "go.mod"), "the sidecar go.mod; its require of the module, once present, must match VERSION")
	flag.Parse()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	err := run(ctx, config{dir: *dir, goMod: *goMod, write: *write, out: os.Stdout, download: goModDownloader(runGo)})
	stop()
	if err != nil {
		fmt.Fprintln(os.Stderr, "sync-protos:", err)
		os.Exit(1)
	}
}

// module is a downloaded module: what the go command resolved, and its files.
type module struct {
	path    string
	version string
	sum     string
	files   fs.FS
}

// downloader fetches a module version. goModDownload is the real one; tests inject fakes.
type downloader func(ctx context.Context, path, version string) (module, error)

type config struct {
	dir      string
	goMod    string
	write    bool
	out      io.Writer
	download downloader
}

func run(ctx context.Context, cfg config) error {
	raw, err := fs.ReadFile(os.DirFS(cfg.dir), versionFile)
	if err != nil {
		return fmt.Errorf("read the pin: %w", err)
	}

	p, err := parsePin(raw)
	if err != nil {
		return err
	}

	goMod, err := os.ReadFile(cfg.goMod)
	if err != nil {
		return fmt.Errorf("read %s: %w", cfg.goMod, err)
	}

	err = checkRequire(cfg.goMod, goMod, p)
	if err != nil {
		return err
	}

	m, err := cfg.download(ctx, p.path, p.version)
	if err != nil {
		return err
	}

	if m.path != p.path || m.version != p.version || m.sum != p.sum {
		return fmt.Errorf("%w: %s %s %s resolved to %s %s %s", errPin, p.path, p.version, p.sum, m.path, m.version, m.sum)
	}

	want, _, err := collect(ctx, m.files, protoDir)
	if err != nil {
		return fmt.Errorf("read the pinned module: %w", err)
	}

	have, unexpected, err := collect(ctx, os.DirFS(cfg.dir), ".")
	if err != nil {
		return fmt.Errorf("read the third_party copy: %w", err)
	}

	unexpected = slices.DeleteFunc(unexpected, func(name string) bool { return name == versionFile })
	if len(unexpected) > 0 {
		return report(cfg.out, prefixed("unexpected: ", unexpected), fmt.Errorf("%w: remove %d files", errUnexpected, len(unexpected)))
	}

	differences := compare(want, have)
	if len(differences) == 0 {
		return report(cfg.out, []string{fmt.Sprintf("%s matches %s %s (%d files)", cfg.dir, p.path, p.version, len(want))}, nil)
	}

	if !cfg.write {
		return report(cfg.out, differences, fmt.Errorf("%w: %d differences; run go -C tools run ./sync-protos -write", errMismatch, len(differences)))
	}

	err = replace(ctx, cfg.dir, want, have)
	if err != nil {
		return err
	}

	return report(cfg.out, []string{fmt.Sprintf("%s now matches %s %s (%d files)", cfg.dir, p.path, p.version, len(want))}, nil)
}

// report writes each line, then returns result unless the write itself failed.
func report(out io.Writer, lines []string, result error) error {
	for _, line := range lines {
		_, err := fmt.Fprintln(out, line)
		if err != nil {
			return fmt.Errorf("report: %w", err)
		}
	}

	return result
}

func prefixed(prefix string, names []string) []string {
	lines := make([]string, 0, len(names))
	for _, name := range names {
		lines = append(lines, prefix+name)
	}

	return lines
}

// pin is one go.sum line: module path, version and h1 hash of the module zip.
type pin struct {
	path    string
	version string
	sum     string
}

func parsePin(raw []byte) (pin, error) {
	lines := strings.Split(strings.TrimRight(string(raw), "\n"), "\n")
	if len(lines) != 1 {
		return pin{}, fmt.Errorf("%s must hold exactly one line, found %d", versionFile, len(lines))
	}

	fields := strings.Fields(lines[0])
	if len(fields) != 3 || !strings.HasPrefix(fields[2], "h1:") {
		return pin{}, fmt.Errorf("%s must read \"<module> <version> h1:<hash>\", found %q", versionFile, lines[0])
	}

	return pin{path: fields[0], version: fields[1], sum: fields[2]}, nil
}

// checkRequire passes while go.mod does not require the module (Phase 0). Once it does, the required
// version must be the pin, and no replace may point the build at other code.
func checkRequire(name string, goMod []byte, p pin) error {
	file, err := modfile.Parse(name, goMod, nil)
	if err != nil {
		return fmt.Errorf("parse %s: %w", name, err)
	}

	for _, r := range file.Replace {
		if r.Old.Path == p.path {
			return fmt.Errorf("%w: %s replaces %s, so the sidecar would not build the pinned module", errPin, name, p.path)
		}
	}

	for _, r := range file.Require {
		if r.Mod.Path == p.path && r.Mod.Version != p.version {
			return fmt.Errorf("%w: %s requires %s %s, %s pins %s", errPin, name, p.path, r.Mod.Version, versionFile, p.version)
		}
	}

	return nil
}

// runner runs the go command with args in dir and returns its standard output. runGo is the real one;
// tests inject fakes.
type runner func(ctx context.Context, dir string, args ...string) ([]byte, error)

func runGo(ctx context.Context, dir string, args ...string) ([]byte, error) {
	// The arguments come from the committed VERSION file, and exec passes each one without a shell,
	// so there is nothing to inject.
	cmd := exec.CommandContext(ctx, "go", args...) //nolint:gosec // G204: arguments are the committed pin, no shell
	cmd.Dir = dir
	cmd.Stderr = os.Stderr

	stdout, err := cmd.Output()
	if err != nil {
		return stdout, fmt.Errorf("go %s: %w", strings.Join(args, " "), err)
	}

	return stdout, nil
}

// goModDownloader fetches a module into the module cache with `go mod download -json`.
// It runs in an empty temporary directory so it never edits a go.mod or go.sum.
func goModDownloader(run runner) downloader {
	return func(ctx context.Context, modulePath, version string) (m module, err error) {
		scratch, err := os.MkdirTemp("", "sync-protos-")
		if err != nil {
			return module{}, fmt.Errorf("create a scratch directory: %w", err)
		}

		defer func() {
			removeErr := os.RemoveAll(scratch)
			if removeErr != nil {
				err = errors.Join(err, fmt.Errorf("remove %s: %w", scratch, removeErr))
			}
		}()

		// On failure the go command still prints the JSON object, with the reason in Error.
		stdout, runErr := run(ctx, scratch, "mod", "download", "-json", modulePath+"@"+version)

		var info struct {
			Path    string
			Version string
			Dir     string
			Sum     string
			Error   string
		}

		decodeErr := json.Unmarshal(stdout, &info)

		switch {
		case decodeErr == nil && info.Error != "":
			return module{}, fmt.Errorf("%w: go mod download %s@%s: %s", errDownload, modulePath, version, info.Error)
		case runErr != nil:
			return module{}, fmt.Errorf("%w: %w", errDownload, runErr)
		case decodeErr != nil:
			return module{}, fmt.Errorf("%w: decode go mod download output: %w", errDownload, decodeErr)
		}

		return module{path: info.Path, version: info.Version, sum: info.Sum, files: os.DirFS(info.Dir)}, nil
	}
}

// collect reads every *.proto under root, keyed by its slash path relative to root, plus LICENSE
// from the top of fsys when present. It also returns every other file under root, sorted.
func collect(ctx context.Context, fsys fs.FS, root string) (map[string][]byte, []string, error) {
	files := map[string][]byte{}

	var others []string

	err := fs.WalkDir(fsys, root, func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}

		if ctx.Err() != nil {
			return fmt.Errorf("walk: %w", ctx.Err())
		}

		if entry.IsDir() {
			return nil
		}

		relative := name
		if root != "." {
			relative = strings.TrimPrefix(name, root+"/")
		}

		if path.Ext(name) != ".proto" {
			if relative != licenseFile || root != "." {
				others = append(others, relative)
			}

			return nil
		}

		content, err := fs.ReadFile(fsys, name)
		if err != nil {
			return fmt.Errorf("read %s: %w", name, err)
		}

		files[relative] = content

		return nil
	})
	if err != nil {
		return nil, nil, fmt.Errorf("walk %s: %w", root, err)
	}

	slices.Sort(others)

	license, err := fs.ReadFile(fsys, licenseFile)
	if errors.Is(err, fs.ErrNotExist) {
		return files, others, nil
	}

	if err != nil {
		return nil, nil, fmt.Errorf("read %s: %w", licenseFile, err)
	}

	files[licenseFile] = license

	return files, others, nil
}

// compare lists every file that is missing from have, extra in have, or different, sorted by name.
func compare(want, have map[string][]byte) []string {
	var differences []string

	for name, content := range want {
		got, ok := have[name]
		switch {
		case !ok:
			differences = append(differences, "missing: "+name)
		case !bytes.Equal(got, content):
			differences = append(differences, "differs: "+name)
		}
	}

	for name := range have {
		if _, ok := want[name]; !ok {
			differences = append(differences, "extra: "+name)
		}
	}

	slices.SortFunc(differences, func(a, b string) int {
		return strings.Compare(a[strings.Index(a, " ")+1:], b[strings.Index(b, " ")+1:])
	})

	return differences
}

// replace makes dir hold exactly want: it deletes files in have that want lacks, writes the rest,
// and removes directories left empty. VERSION stays.
func replace(ctx context.Context, dir string, want, have map[string][]byte) error {
	for name := range have {
		if _, ok := want[name]; ok {
			continue
		}

		if ctx.Err() != nil {
			return fmt.Errorf("replace: %w", ctx.Err())
		}

		target := filepath.Join(dir, filepath.FromSlash(name))

		err := os.Remove(target)
		if err != nil {
			return fmt.Errorf("remove %s: %w", target, err)
		}

		err = removeEmptyParents(dir, filepath.Dir(target))
		if err != nil {
			return err
		}
	}

	for name, content := range want {
		if ctx.Err() != nil {
			return fmt.Errorf("replace: %w", ctx.Err())
		}

		target := filepath.Join(dir, filepath.FromSlash(name))

		err := os.MkdirAll(filepath.Dir(target), 0o750)
		if err != nil {
			return fmt.Errorf("create %s: %w", filepath.Dir(target), err)
		}

		err = os.WriteFile(target, content, 0o600)
		if err != nil {
			return fmt.Errorf("write %s: %w", target, err)
		}
	}

	return nil
}

func removeEmptyParents(root, dir string) error {
	for dir != root && strings.HasPrefix(dir, root) {
		entries, err := os.ReadDir(dir)
		if err != nil {
			return fmt.Errorf("read %s: %w", dir, err)
		}

		if len(entries) > 0 {
			return nil
		}

		err = os.Remove(dir)
		if err != nil {
			return fmt.Errorf("remove %s: %w", dir, err)
		}

		dir = filepath.Dir(dir)
	}

	return nil
}
