package main

import (
	"io/fs"
	"os"
	"path/filepath"
	"testing"
	"testing/fstest"
)

func TestParsePin(t *testing.T) {
	t.Parallel()

	got, err := parsePin([]byte("go.mau.fi/whatsmeow v0.0.0-20261030195514-56e5ad0581ce h1:abc=\n"))
	if err != nil {
		t.Fatalf("parsePin: %v", err)
	}

	want := pin{path: "go.mau.fi/whatsmeow", version: "v0.0.0-20261030195514-56e5ad0581ce", sum: "h1:abc="}
	if got != want {
		t.Fatalf("parsePin = %+v, want %+v", got, want)
	}
}

func TestParsePinRejectsMalformed(t *testing.T) {
	t.Parallel()

	for name, raw := range map[string]string{
		"empty":        "",
		"two lines":    "go.mau.fi/whatsmeow v1 h1:a=\ngo.mau.fi/whatsmeow v2 h1:b=\n",
		"no sum":       "go.mau.fi/whatsmeow v1\n",
		"not h1":       "go.mau.fi/whatsmeow v1 sha256:a\n",
		"extra fields": "go.mau.fi/whatsmeow v1 h1:a= extra\n",
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

	module := fstest.MapFS{
		"LICENSE":                      {Data: []byte("mpl")},
		"go.mod":                       {Data: []byte("module x")},
		"proto/extra.go":               {Data: []byte("package proto")},
		"proto/waE2E/E2E.proto":        {Data: []byte("e2e")},
		"proto/waE2E/E2E.pb.go":        {Data: []byte("generated")},
		"proto/waCommon/Common.proto":  {Data: []byte("common")},
		"client.go":                    {Data: []byte("package whatsmeow")},
		"notproto/Elsewhere.proto":     {Data: []byte("outside proto/")},
		"proto/waCommon/Common.proto~": {Data: []byte("backup")},
	}

	got, err := collect(module, protoDir)
	if err != nil {
		t.Fatalf("collect: %v", err)
	}

	want := map[string][]byte{
		"LICENSE":               []byte("mpl"),
		"waE2E/E2E.proto":       []byte("e2e"),
		"waCommon/Common.proto": []byte("common"),
	}
	assertFiles(t, got, want)
}

func TestCompare(t *testing.T) {
	t.Parallel()

	want := map[string][]byte{
		"LICENSE":   []byte("mpl"),
		"a/A.proto": []byte("a"),
		"b/B.proto": []byte("b"),
		"c/C.proto": []byte("c"),
	}
	have := map[string][]byte{
		"LICENSE":   []byte("mpl"),
		"a/A.proto": []byte("a"),
		"b/B.proto": []byte("b\r"),
		"d/D.proto": []byte("d"),
	}

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

func TestCompareIdenticalIsEmpty(t *testing.T) {
	t.Parallel()

	files := map[string][]byte{"LICENSE": []byte("mpl"), "a/A.proto": []byte("a")}

	got := compare(files, map[string][]byte{"LICENSE": []byte("mpl"), "a/A.proto": []byte("a")})
	if len(got) != 0 {
		t.Fatalf("compare = %q, want no differences", got)
	}
}

func TestReplaceMirrorsWantAndKeepsVersion(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	writeFile(t, dir, "VERSION", "go.mau.fi/whatsmeow v1 h1:a=\n")
	writeFile(t, dir, "a/A.proto", "stale")
	writeFile(t, dir, "gone/Gone.proto", "removed upstream")

	want := map[string][]byte{
		"LICENSE":   []byte("mpl"),
		"a/A.proto": []byte("fresh"),
		"n/N.proto": []byte("new"),
	}

	have, err := collect(os.DirFS(dir), ".")
	if err != nil {
		t.Fatalf("collect before: %v", err)
	}

	err = replace(dir, want, have)
	if err != nil {
		t.Fatalf("replace: %v", err)
	}

	after, err := collect(os.DirFS(dir), ".")
	if err != nil {
		t.Fatalf("collect after: %v", err)
	}

	assertFiles(t, after, want)

	_, err = os.Stat(filepath.Join(dir, "gone"))
	if !os.IsNotExist(err) {
		t.Fatalf("directory emptied by replace still exists: %v", err)
	}

	version, err := fs.ReadFile(os.DirFS(dir), "VERSION")
	if err != nil || string(version) != "go.mau.fi/whatsmeow v1 h1:a=\n" {
		t.Fatalf("VERSION after replace = %q, %v", version, err)
	}
}

func assertFiles(t *testing.T, got, want map[string][]byte) {
	t.Helper()

	if len(got) != len(want) {
		t.Fatalf("got %d files %v, want %d", len(got), keys(got), len(want))
	}

	for name, content := range want {
		if string(got[name]) != string(content) {
			t.Fatalf("%s = %q, want %q", name, got[name], content)
		}
	}
}

func keys(files map[string][]byte) []string {
	names := make([]string, 0, len(files))
	for name := range files {
		names = append(names, name)
	}

	return names
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
