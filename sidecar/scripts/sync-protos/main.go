// Command sync-protos keeps third_party/whatsmeow byte identical with the pinned whatsmeow module.
//
// The pin is the single line in third_party/whatsmeow/VERSION, in go.sum form:
//
//	go.mau.fi/whatsmeow v0.0.0-20261030195514-56e5ad0581ce h1:...
//
// The module is fetched with `go mod download -json`, so its content is verified against the Go
// checksum database and against the recorded h1 sum. Every *.proto under the module's proto/
// directory, and the module LICENSE, must match the copy byte for byte.
//
// By default it only checks, and exits non-zero on any difference. With -write it replaces the copy.
// Run it from the sidecar module: go -C sidecar run ./scripts/sync-protos [-write].
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
)

const (
	versionFile = "VERSION"
	licenseFile = "LICENSE"
	protoDir    = "proto"
)

var errMismatch = errors.New("third_party copy does not match the pinned module")

func main() {
	write := flag.Bool("write", false, "replace the copy with the pinned module's files instead of checking it")
	dir := flag.String("dir", filepath.Join("..", "third_party", "whatsmeow"), "the third_party copy, holding VERSION")
	flag.Parse()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	err := run(ctx, *dir, *write, os.Stdout)
	stop()
	if err != nil {
		fmt.Fprintln(os.Stderr, "sync-protos:", err)
		os.Exit(1)
	}
}

func run(ctx context.Context, dir string, write bool, out io.Writer) error {
	raw, err := fs.ReadFile(os.DirFS(dir), versionFile)
	if err != nil {
		return fmt.Errorf("read the pin: %w", err)
	}

	p, err := parsePin(raw)
	if err != nil {
		return err
	}

	moduleDir, err := download(ctx, p)
	if err != nil {
		return err
	}

	want, err := collect(os.DirFS(moduleDir), protoDir)
	if err != nil {
		return fmt.Errorf("read the pinned module: %w", err)
	}

	have, err := collect(os.DirFS(dir), ".")
	if err != nil {
		return fmt.Errorf("read the third_party copy: %w", err)
	}

	differences := compare(want, have)
	if len(differences) == 0 {
		_, err = fmt.Fprintf(out, "%s matches %s %s (%d files)\n", dir, p.path, p.version, len(want))
		if err != nil {
			return fmt.Errorf("report: %w", err)
		}

		return nil
	}

	if !write {
		for _, d := range differences {
			_, err = fmt.Fprintln(out, d)
			if err != nil {
				return fmt.Errorf("report: %w", err)
			}
		}

		return fmt.Errorf("%w: %d differences; run go -C sidecar run ./scripts/sync-protos -write", errMismatch, len(differences))
	}

	err = replace(dir, want, have)
	if err != nil {
		return err
	}

	_, err = fmt.Fprintf(out, "%s now matches %s %s (%d files)\n", dir, p.path, p.version, len(want))
	if err != nil {
		return fmt.Errorf("report: %w", err)
	}

	return nil
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

// download fetches the pinned module into the module cache and returns its directory.
// It runs in an empty temporary directory so it never edits a go.mod or go.sum.
func download(ctx context.Context, p pin) (moduleDir string, err error) {
	scratch, err := os.MkdirTemp("", "sync-protos-")
	if err != nil {
		return "", fmt.Errorf("create a scratch directory: %w", err)
	}

	defer func() {
		removeErr := os.RemoveAll(scratch)
		if removeErr != nil {
			err = errors.Join(err, fmt.Errorf("remove %s: %w", scratch, removeErr))
		}
	}()

	// The module path and version come from the committed VERSION file, and exec passes them as one
	// argument without a shell, so there is nothing to inject.
	cmd := exec.CommandContext(ctx, "go", "mod", "download", "-json", p.path+"@"+p.version) //nolint:gosec // G204: argument is the committed pin, no shell
	cmd.Dir = scratch
	cmd.Stderr = os.Stderr

	stdout, err := cmd.Output()
	if err != nil {
		return "", fmt.Errorf("go mod download %s@%s: %w", p.path, p.version, err)
	}

	var info struct {
		Path    string
		Version string
		Dir     string
		Sum     string
		Error   string
	}

	err = json.Unmarshal(stdout, &info)
	if err != nil {
		return "", fmt.Errorf("decode go mod download output: %w", err)
	}

	if info.Error != "" {
		return "", fmt.Errorf("go mod download %s@%s: %s", p.path, p.version, info.Error)
	}

	if info.Path != p.path || info.Version != p.version || info.Sum != p.sum {
		return "", fmt.Errorf("pin %s %s %s resolved to %s %s %s", p.path, p.version, p.sum, info.Path, info.Version, info.Sum)
	}

	return info.Dir, nil
}

// collect reads every *.proto under root, keyed by its slash path relative to root, plus LICENSE
// from the top of fsys when present.
func collect(fsys fs.FS, root string) (map[string][]byte, error) {
	files := map[string][]byte{}

	err := fs.WalkDir(fsys, root, func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}

		if entry.IsDir() || path.Ext(name) != ".proto" {
			return nil
		}

		content, err := fs.ReadFile(fsys, name)
		if err != nil {
			return fmt.Errorf("read %s: %w", name, err)
		}

		relative := name
		if root != "." {
			relative = strings.TrimPrefix(name, root+"/")
		}

		files[relative] = content

		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("walk %s: %w", root, err)
	}

	license, err := fs.ReadFile(fsys, licenseFile)
	if errors.Is(err, fs.ErrNotExist) {
		return files, nil
	}

	if err != nil {
		return nil, fmt.Errorf("read %s: %w", licenseFile, err)
	}

	files[licenseFile] = license

	return files, nil
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
// and removes directories left empty. VERSION and anything that is not a proto or LICENSE stay.
func replace(dir string, want, have map[string][]byte) error {
	for name := range have {
		if _, ok := want[name]; ok {
			continue
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
