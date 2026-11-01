package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

const (
	mitText = `MIT License

Copyright (c) 2026 Example Author

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
`
	stdText    = "go licence"
	vendorText = "vendored x/net licence"
	depPath    = "example.com/dep"
)

// fixture is a repository root, a fake GOROOT and fake module directories, plus what go list reports.
type fixture struct {
	root    string
	goroot  string
	modules map[string]string   // module path -> directory
	listed  map[string][]string // target -> module paths go list reports for it
	listErr error
}

func newFixture(t *testing.T, withMain bool) *fixture {
	t.Helper()

	f := &fixture{root: t.TempDir(), goroot: t.TempDir(), modules: map[string]string{}, listed: map[string][]string{}}
	writeFile(t, f.goroot, "LICENSE", stdText)
	writeFile(t, f.goroot, "src/vendor/golang.org/x/net/LICENSE", vendorText)
	writeFile(t, f.goroot, "src/crypto/internal/boring/LICENSE", "boringcrypto needs cgo; never linked here")

	if withMain {
		writeFile(t, f.root, "sidecar/cmd/whatsmeow-net/main.go", "package main")
	}

	return f
}

// module creates a module directory with the given files and makes go list report it on targets.
func (f *fixture) module(t *testing.T, modulePath string, files map[string]string, on ...string) {
	t.Helper()

	dir := t.TempDir()
	for name, content := range files {
		writeFile(t, dir, name, content)
	}

	f.modules[modulePath] = dir

	for _, target := range on {
		f.listed[target] = append(f.listed[target], modulePath)
	}
}

func (f *fixture) run(t *testing.T, allow string) (string, error) {
	t.Helper()

	writeFile(t, f.root, allowFile, allow)

	out := &bytes.Buffer{}
	err := run(context.Background(), config{root: f.root, out: out, goCmd: f.goCmd})

	return out.String(), err
}

func (f *fixture) goCmd(_ context.Context, _ string, env []string, args ...string) ([]byte, error) {
	if args[0] == "env" {
		return []byte(f.goroot + "\n"), nil
	}

	if f.listErr != nil {
		return nil, f.listErr
	}

	var goos, goarch string

	for _, kv := range env {
		if v, ok := strings.CutPrefix(kv, "GOOS="); ok {
			goos = v
		}

		if v, ok := strings.CutPrefix(kv, "GOARCH="); ok {
			goarch = v
		}
	}

	if !slices.Contains(env, "CGO_ENABLED=0") {
		return nil, errors.New("go list must run with CGO_ENABLED=0")
	}

	var out bytes.Buffer

	encode := func(v any) {
		b, _ := json.Marshal(v)
		out.Write(b)
		out.WriteByte('\n')
	}

	encode(map[string]any{"ImportPath": "github.com/tcortega/Whatsmeow.NET/sidecar/cmd/whatsmeow-net", "Module": map[string]any{"Path": "github.com/tcortega/Whatsmeow.NET/sidecar", "Main": true, "Dir": f.root}})
	encode(map[string]any{"ImportPath": "fmt", "Standard": true})

	for _, modulePath := range f.listed[goos+"/"+goarch] {
		encode(map[string]any{"ImportPath": modulePath + "/pkg", "Module": map[string]any{"Path": modulePath, "Version": "v1.0.0", "Dir": f.modules[modulePath]}})
	}

	return out.Bytes(), nil
}

func writeFile(t *testing.T, dir, name, content string) {
	t.Helper()

	target := filepath.Join(dir, filepath.FromSlash(name))

	err := os.MkdirAll(filepath.Dir(target), 0o750)
	if err != nil {
		t.Fatal(err)
	}

	err = os.WriteFile(target, []byte(content), 0o600)
	if err != nil {
		t.Fatal(err)
	}
}

func stdLines(t *testing.T) string {
	t.Helper()

	return "std LICENSE " + sum(t, stdText) + " BSD-3-Clause\n" +
		"std src/vendor/golang.org/x/net/LICENSE " + sum(t, vendorText) + " BSD-3-Clause\n"
}

// sum is the sha256 of a fixture text, computed here rather than through the code under test.
func sum(t *testing.T, content string) string {
	t.Helper()

	h := sha256.Sum256([]byte(content))

	return hex.EncodeToString(h[:])
}

func depLine(t *testing.T) string {
	t.Helper()

	return depPath + " LICENSE " + sum(t, mitText) + " MIT\n"
}

func TestRunPassesWhenTheAllowlistMatches(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.module(t, depPath, map[string]string{"LICENSE": mitText, "README.md": "not a licence"}, targets()...)

	out, err := f.run(t, "# header\n\n"+depLine(t)+stdLines(t))
	if err != nil {
		t.Fatalf("run: %v\n%s", err, out)
	}

	if out != allowFile+" matches the licence files of 1 linked modules and std\n" {
		t.Fatalf("output = %q", out)
	}
}

func TestRunWithoutTheMainPackageComparesOnlyStd(t *testing.T) {
	t.Parallel()

	f := newFixture(t, false)

	out, err := f.run(t, depLine(t)+stdLines(t))
	if err != nil {
		t.Fatalf("run: %v\n%s", err, out)
	}

	if !strings.HasPrefix(out, "notice: sidecar/cmd/whatsmeow-net does not exist yet (#4)") {
		t.Fatalf("output = %q", out)
	}

	writeFile(t, f.goroot, "LICENSE", stdText+" changed")

	out, err = f.run(t, depLine(t)+stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	if !strings.Contains(out, allowFile+":2: [licence-changed] the Go standard library: LICENSE has sha256 ") {
		t.Fatalf("output = %q", out)
	}
}

func TestRunReportsANewModuleAndLeavesTheVerdictToAReviewer(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.module(t, depPath, map[string]string{"LICENSE": mitText}, targets()...)

	out, err := f.run(t, stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	want := allowFile + ":0: [new-module] " + depPath + " v1.0.0 (darwin/amd64, darwin/arm64, linux/amd64, linux/arm64, windows/amd64, windows/arm64) is not in the allowlist"
	if !strings.Contains(out, want) {
		t.Fatalf("output = %q, want it to contain %q", out, want)
	}

	if !strings.Contains(out, "\t"+depPath+" LICENSE "+sum(t, mitText)+" <licence>") {
		t.Fatalf("output = %q, want the line to add, with <licence> left to the reviewer", out)
	}

	out, err = f.run(t, depPath+" LICENSE "+sum(t, mitText)+" <licence>\n"+stdLines(t))
	if !errors.Is(err, errFindings) || !strings.Contains(out, allowFile+":1: [syntax]") {
		t.Fatalf("run = %v, output = %q, want the unedited line rejected", err, out)
	}
}

func TestRunRequiresAModuleLinkedOnOneTargetOnly(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.module(t, depPath, map[string]string{"LICENSE": mitText}, "windows/arm64")

	out, err := f.run(t, stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	if !strings.Contains(out, "[new-module] "+depPath+" v1.0.0 (windows/arm64) is not in the allowlist") {
		t.Fatalf("output = %q", out)
	}
}

func TestRunReportsAChangedLicence(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.module(t, depPath, map[string]string{"LICENSE": mitText + "\nAdditional restriction."}, targets()...)

	out, err := f.run(t, depLine(t)+stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	want := allowFile + ":1: [licence-changed] " + depPath + " v1.0.0 (darwin/amd64, darwin/arm64, linux/amd64, linux/arm64, windows/amd64, windows/arm64): LICENSE has sha256 " +
		sum(t, mitText+"\nAdditional restriction.") + ", the allowlist approved " + sum(t, mitText) + " as MIT"
	if !strings.Contains(out, want) {
		t.Fatalf("output = %q, want it to contain %q", out, want)
	}
}

func TestRunReportsAModuleWithoutALicenceFile(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.module(t, depPath, map[string]string{"README.md": "all rights reserved", "testdata/LICENSE": mitText}, targets()...)

	out, err := f.run(t, depLine(t)+stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	if !strings.Contains(out, "[no-licence] "+depPath+" v1.0.0 (") || !strings.Contains(out, "has no licence file, so it cannot be redistributed") {
		t.Fatalf("output = %q", out)
	}
}

func TestRunReportsAStaleModule(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)

	out, err := f.run(t, depLine(t)+stdLines(t))
	if !errors.Is(err, errFindings) {
		t.Fatalf("run = %v, want errFindings", err)
	}

	if !strings.Contains(out, allowFile+":1: [stale] "+depPath+" no longer links into whatsmeow-net on any target") {
		t.Fatalf("output = %q", out)
	}
}

func TestRunFailsWhenGoListFails(t *testing.T) {
	t.Parallel()

	f := newFixture(t, true)
	f.listErr = errors.New("go list: exit status 1")

	_, err := f.run(t, stdLines(t))
	if err == nil || errors.Is(err, errFindings) || !strings.Contains(err.Error(), "go list: exit status 1") {
		t.Fatalf("run = %v, want the go list error", err)
	}
}

func TestLinkedModulesWithTheRealGoCommand(t *testing.T) {
	t.Parallel()

	root := t.TempDir()
	writeFile(t, root, "sidecar/go.mod", "module example.com/sidecar\n\ngo 1.26.0\n\nrequire "+depPath+" v0.0.0\n\nreplace "+depPath+" => ../dep\n")
	writeFile(t, root, "sidecar/cmd/whatsmeow-net/main.go", "package main\n\nimport _ \""+depPath+"\"\n\nfunc main() {}\n")
	writeFile(t, root, "dep/go.mod", "module "+depPath+"\n\ngo 1.26.0\n")
	writeFile(t, root, "dep/dep.go", "package dep\n")
	writeFile(t, root, "dep/LICENSE", mitText)

	linked, err := linkedModules(context.Background(), config{root: root, goCmd: runGo})
	if err != nil {
		t.Fatalf("linkedModules: %v", err)
	}

	m := linked[depPath]
	if len(linked) != 1 || m == nil {
		t.Fatalf("linked = %v, want only %s", linked, depPath)
	}

	if m.version != "v0.0.0 => ../dep" || !slices.Equal(m.targets, targets()) {
		t.Fatalf("module = %+v", *m)
	}

	files, err := licenseFiles(os.DirFS(m.dir), ".", true)
	if err != nil || string(files["LICENSE"]) != mitText {
		t.Fatalf("licence files in %s = %v, %v", m.dir, files, err)
	}
}
