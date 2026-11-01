package main

import (
	"bytes"
	"context"
	"errors"
	"io/fs"
	"os"
	"path/filepath"
	"testing"
	"testing/fstest"
)

const (
	testPath    = "go.mau.fi/whatsmeow"
	testVersion = "v0.0.0-20261030195514-56e5ad0581ce"
	testSum     = "h1:abc="
	testPin     = testPath + " " + testVersion + " " + testSum + "\n"
	noRequire   = "module github.com/tcortega/Whatsmeow.NET/sidecar\n\ngo 1.26.0\n"
)

// upstream is the pinned module as the fake downloader serves it.
func upstream() fstest.MapFS {
	return fstest.MapFS{
		"LICENSE":                     {Data: []byte("mpl")},
		"go.mod":                      {Data: []byte("module go.mau.fi/whatsmeow")},
		"client.go":                   {Data: []byte("package whatsmeow")},
		"proto/extra.go":              {Data: []byte("package proto")},
		"proto/waE2E/E2E.proto":       {Data: []byte("e2e")},
		"proto/waE2E/E2E.pb.go":       {Data: []byte("generated")},
		"proto/waCommon/Common.proto": {Data: []byte("common")},
	}
}

func fakeDownload(m module) downloader {
	return func(_ context.Context, _, _ string) (module, error) {
		return m, nil
	}
}

func pinnedModule() module {
	return module{path: testPath, version: testVersion, sum: testSum, files: upstream()}
}

// setup writes a third_party copy holding VERSION and the given files, and a go.mod.
func setup(t *testing.T, goMod string, files map[string]string) config {
	t.Helper()

	root := t.TempDir()
	dir := filepath.Join(root, "third_party")
	writeFile(t, dir, "VERSION", testPin)

	for name, content := range files {
		writeFile(t, dir, name, content)
	}

	writeFile(t, root, "go.mod", goMod)

	return config{dir: dir, goMod: filepath.Join(root, "go.mod"), out: &bytes.Buffer{}, download: fakeDownload(pinnedModule())}
}

func matchingCopy() map[string]string {
	return map[string]string{"LICENSE": "mpl", "waE2E/E2E.proto": "e2e", "waCommon/Common.proto": "common"}
}

func TestRunMatchingCopyPasses(t *testing.T) {
	t.Parallel()

	cfg := setup(t, noRequire, matchingCopy())

	err := run(context.Background(), cfg)
	if err != nil {
		t.Fatalf("run: %v", err)
	}
}

func TestRunCheckModeReportsMismatchAndWritesNothing(t *testing.T) {
	t.Parallel()

	files := matchingCopy()
	files["waE2E/E2E.proto"] = "e2e\r"
	cfg := setup(t, noRequire, files)

	err := run(context.Background(), cfg)
	if !errors.Is(err, errMismatch) {
		t.Fatalf("run = %v, want errMismatch", err)
	}

	if got := cfg.out.(*bytes.Buffer).String(); got != "differs: waE2E/E2E.proto\n" {
		t.Fatalf("output = %q", got)
	}

	content, err := fs.ReadFile(os.DirFS(cfg.dir), "waE2E/E2E.proto")
	if err != nil || string(content) != "e2e\r" {
		t.Fatalf("check mode changed the copy: %q, %v", content, err)
	}
}

func TestRunRejectsAModuleThatIsNotThePin(t *testing.T) {
	t.Parallel()

	for name, m := range map[string]module{
		"sum":     {path: testPath, version: testVersion, sum: "h1:other=", files: upstream()},
		"version": {path: testPath, version: "v0.0.0-20261101000000-000000000000", sum: testSum, files: upstream()},
		"path":    {path: "example.com/other", version: testVersion, sum: testSum, files: upstream()},
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			cfg := setup(t, noRequire, matchingCopy())
			cfg.download = fakeDownload(m)

			err := run(context.Background(), cfg)
			if !errors.Is(err, errPin) {
				t.Fatalf("run = %v, want errPin", err)
			}
		})
	}
}

func TestRunRejectsUnexpectedFiles(t *testing.T) {
	t.Parallel()

	files := matchingCopy()
	files[".editorconfig"] = "root = true"
	files["waE2E/notes.md"] = "notes"

	for _, write := range []bool{false, true} {
		cfg := setup(t, noRequire, files)
		cfg.write = write

		err := run(context.Background(), cfg)
		if !errors.Is(err, errUnexpected) {
			t.Fatalf("run (write=%v) = %v, want errUnexpected", write, err)
		}

		if got := cfg.out.(*bytes.Buffer).String(); got != "unexpected: .editorconfig\nunexpected: waE2E/notes.md\n" {
			t.Fatalf("output = %q", got)
		}
	}
}

func TestRunWriteModeMirrorsThePin(t *testing.T) {
	t.Parallel()

	cfg := setup(t, noRequire, map[string]string{"waE2E/E2E.proto": "stale", "gone/Gone.proto": "removed upstream"})
	cfg.write = true

	err := run(context.Background(), cfg)
	if err != nil {
		t.Fatalf("run -write: %v", err)
	}

	cfg.write = false

	err = run(context.Background(), cfg)
	if err != nil {
		t.Fatalf("run after -write: %v", err)
	}

	_, err = os.Stat(filepath.Join(cfg.dir, "gone"))
	if !os.IsNotExist(err) {
		t.Fatalf("directory emptied by -write still exists: %v", err)
	}

	version, err := fs.ReadFile(os.DirFS(cfg.dir), "VERSION")
	if err != nil || string(version) != testPin {
		t.Fatalf("VERSION after -write = %q, %v", version, err)
	}
}

func TestRunChecksTheGoModRequire(t *testing.T) {
	t.Parallel()

	for name, tc := range map[string]struct {
		goMod string
		want  error
	}{
		"no require":       {noRequire, nil},
		"matching require": {noRequire + "\nrequire " + testPath + " " + testVersion + "\n", nil},
		"block require":    {noRequire + "\nrequire (\n\t" + testPath + " " + testVersion + "\n)\n", nil},
		"other version":    {noRequire + "\nrequire " + testPath + " v0.0.0-20261101000000-000000000000\n", errPin},
		"replaced":         {noRequire + "\nrequire " + testPath + " " + testVersion + "\n\nreplace " + testPath + " => ../whatsmeow\n", errPin},
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			cfg := setup(t, tc.goMod, matchingCopy())

			err := run(context.Background(), cfg)
			if !errors.Is(err, tc.want) {
				t.Fatalf("run = %v, want %v", err, tc.want)
			}
		})
	}
}

func TestParsePinRejectsMalformed(t *testing.T) {
	t.Parallel()

	for name, raw := range map[string]string{
		"empty":        "",
		"two lines":    testPin + testPin,
		"no sum":       testPath + " " + testVersion + "\n",
		"not h1":       testPath + " " + testVersion + " sha256:a\n",
		"extra fields": testPath + " " + testVersion + " " + testSum + " extra\n",
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			_, err := parsePin([]byte(raw))
			if err == nil {
				t.Fatalf("parsePin(%q) succeeded, want an error", raw)
			}
		})
	}
}

func TestCollectKeepsProtosAndLicenseOnly(t *testing.T) {
	t.Parallel()

	got, _, err := collect(context.Background(), upstream(), protoDir)
	if err != nil {
		t.Fatalf("collect: %v", err)
	}

	want := map[string][]byte{"LICENSE": []byte("mpl"), "waE2E/E2E.proto": []byte("e2e"), "waCommon/Common.proto": []byte("common")}
	if len(got) != len(want) {
		t.Fatalf("collect = %d files, want %d", len(got), len(want))
	}

	for name, content := range want {
		if !bytes.Equal(got[name], content) {
			t.Fatalf("%s = %q, want %q", name, got[name], content)
		}
	}
}

func TestCompare(t *testing.T) {
	t.Parallel()

	want := map[string][]byte{"LICENSE": []byte("mpl"), "a/A.proto": []byte("a"), "b/B.proto": []byte("b"), "c/C.proto": []byte("c")}
	have := map[string][]byte{"LICENSE": []byte("mpl"), "a/A.proto": []byte("a"), "b/B.proto": []byte("b\r"), "d/D.proto": []byte("d")}

	got := compare(want, have)

	expected := []string{"differs: b/B.proto", "missing: c/C.proto", "extra: d/D.proto"}
	if len(got) != len(expected) {
		t.Fatalf("compare = %q, want %q", got, expected)
	}

	for i := range expected {
		if got[i] != expected[i] {
			t.Fatalf("compare = %q, want %q", got, expected)
		}
	}
}

func writeFile(t *testing.T, dir, name, content string) {
	t.Helper()

	target := filepath.Join(dir, filepath.FromSlash(name))

	err := os.MkdirAll(filepath.Dir(target), 0o750)
	if err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	err = os.WriteFile(target, []byte(content), 0o600)
	if err != nil {
		t.Fatalf("write: %v", err)
	}
}
