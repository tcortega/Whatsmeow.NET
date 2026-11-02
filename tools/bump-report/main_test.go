package main

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"testing/fstest"
)

const (
	recorded   = "56e5ad0581cee858000f856b1a98dbd03cc5b14f"
	fromPin    = "v0.0.0-20261101000000-aaaaaaaaaaaa"
	toPin      = "v0.0.0-20261102000000-bbbbbbbbbbbb"
	sidecarMod = "module github.com/tcortega/Whatsmeow.NET/sidecar\n\ngo 1.26.0\n\ntoolchain go1.27.3\n"
	modGo126   = "module go.mau.fi/whatsmeow\n\ngo 1.26.0\n"
)

// doc is an ARCHITECTURE.md with the §14 pin sentence and the given lines after it.
func doc(lines ...string) string {
	return "# Architecture\n\n**The whatsmeow pin** is `" + recorded + "`, the merge commit.\n" + strings.Join(lines, "\n") + "\n"
}

// tree is a whatsmeow module: a go.mod with go 1.26.0 and the given files.
func tree(files map[string]string) fstest.MapFS {
	fsys := fstest.MapFS{"go.mod": {Data: []byte(modGo126)}}
	for name, content := range files {
		fsys[name] = &fstest.MapFile{Data: []byte(content)}
	}

	return fsys
}

// report runs the tool over the recorded pin, the pin on main and the new pin, and returns what it printed.
func report(t *testing.T, architecture, goMod string, atRecorded, atFrom, atTo fstest.MapFS) string {
	t.Helper()

	modules := map[string]module{
		recorded: {version: "v0.0.0-20261030195514-56e5ad0581ce", files: atRecorded},
		fromPin:  {version: fromPin, files: atFrom},
		toPin:    {version: toPin, files: atTo},
	}
	out := &bytes.Buffer{}
	cfg := config{
		doc:   []byte(architecture),
		goMod: []byte(goMod),
		from:  fromPin,
		to:    toPin,
		out:   out,
		download: func(_ context.Context, _, version string) (module, error) {
			m, ok := modules[version]
			if !ok {
				t.Fatalf("downloaded an unexpected version %q", version)
			}

			return m, nil
		},
	}

	err := run(context.Background(), cfg)
	if err != nil {
		t.Fatalf("run: %v", err)
	}

	return out.String()
}

func same(files map[string]string) (fstest.MapFS, fstest.MapFS, fstest.MapFS) {
	return tree(files), tree(files), tree(files)
}

func TestAFactCitingAChangedRootFileIsListedWithItsLine(t *testing.T) {
	t.Parallel()

	architecture := doc("- Gating: a handler returning false withholds the receipt (message.go:459-462).", "- Unrelated (send.go:41).")
	old := tree(map[string]string{"message.go": "package whatsmeow // old", "send.go": "package whatsmeow"})
	updated := tree(map[string]string{"message.go": "package whatsmeow // new", "send.go": "package whatsmeow"})

	got := report(t, architecture, sidecarMod, old, old, updated)

	if !strings.Contains(got, "docs/ARCHITECTURE.md:4 `message.go`") {
		t.Errorf("the fact on line 4 citing the changed message.go is not listed:\n%s", got)
	}

	if strings.Contains(got, "`send.go`") {
		t.Errorf("a fact citing the unchanged send.go is listed:\n%s", got)
	}
}

func TestABareNameOutsideTheRootIsMatchedByItsPathSuffix(t *testing.T) {
	t.Parallel()

	architecture := doc("- An unknown pair is the empty JID (lidmap.go:96-98).")
	old := tree(map[string]string{"store/sqlstore/lidmap.go": "package sqlstore // old"})
	updated := tree(map[string]string{"store/sqlstore/lidmap.go": "package sqlstore // new"})

	got := report(t, architecture, sidecarMod, old, old, updated)

	if !strings.Contains(got, "docs/ARCHITECTURE.md:4 `lidmap.go`") {
		t.Errorf("the fact citing lidmap.go, which changed as store/sqlstore/lidmap.go, is not listed:\n%s", got)
	}
}

func TestAPathOfAnotherModuleIsNotTakenForAWhatsmeowFile(t *testing.T) {
	t.Parallel()

	architecture := doc("- mautrix sets the flags (mautrix-whatsapp pkg/connector/client.go:83).")
	old := tree(map[string]string{"client.go": "package whatsmeow // old"})
	updated := tree(map[string]string{"client.go": "package whatsmeow // new"})

	got := report(t, architecture, sidecarMod, old, old, updated)

	if strings.Contains(got, "ARCHITECTURE.md:4") {
		t.Errorf("a mautrix path is listed because whatsmeow's client.go changed:\n%s", got)
	}
}

func TestTheChangesAreCountedFromThePinArchitectureRecords(t *testing.T) {
	t.Parallel()

	// receipt.go changed after the pin ARCHITECTURE records and before main's pin; a merged bump that nobody
	// re-checked must not drop it.
	architecture := doc("- A receipt is acked only when no handler failed (receipt.go:115-128).")
	atRecorded := tree(map[string]string{"receipt.go": "package whatsmeow // old"})
	later := tree(map[string]string{"receipt.go": "package whatsmeow // new"})

	got := report(t, architecture, sidecarMod, atRecorded, later, later)

	if !strings.Contains(got, "docs/ARCHITECTURE.md:4 `receipt.go`") {
		t.Errorf("a file changed since the recorded pin is not listed:\n%s", got)
	}
}

func TestNothingChangedSaysSo(t *testing.T) {
	t.Parallel()

	atRecorded, atFrom, atTo := same(map[string]string{"message.go": "package whatsmeow"})

	got := report(t, doc("- (message.go:1)."), sidecarMod, atRecorded, atFrom, atTo)

	if !strings.Contains(got, "No file a fact cites changed") || !strings.Contains(got, "No exported `Client` method was added, removed or changed") {
		t.Errorf("an unchanged module does not say so:\n%s", got)
	}
}

func TestClientMethodsAddedRemovedAndChangedAreListed(t *testing.T) {
	t.Parallel()

	old := tree(map[string]string{
		"send.go": "package whatsmeow\n\ntype Client struct{}\n\n" +
			"func (cli *Client) SendMessage(to string) error { return nil }\n" +
			"func (cli *Client) Kept() {}\n" +
			"func (cli *Client) Gone() {}\n",
	})
	updated := tree(map[string]string{
		"send.go": "package whatsmeow\n\ntype Client struct{}\n\n" +
			"func (cli *Client) SendMessage(to string, extra ...int) error { return nil }\n" +
			"func (cli *Client) Kept() {}\n" +
			"func (cli *Client) New(ctx int) (bool, error) { return false, nil }\n" +
			"func (cli *Client) unexported() {}\n" +
			"func (o *Other) Elsewhere() {}\n",
		"send_test.go":          "package whatsmeow\n\nfunc (cli *Client) FromATest() {}\n",
		"internals_generate.go": "//go:build ignore\n\npackage main\n\nfunc (cli *Client) Generator() {}\n",
		"socket/socket.go":      "package socket\n\nfunc (cli *Client) Nested() {}\n",
	})

	got := report(t, doc(), sidecarMod, old, old, updated)

	for _, want := range []string{
		"- added `func (cli *Client) New(ctx int) (bool, error)`",
		"- removed `func (cli *Client) Gone()`",
		"- changed `func (cli *Client) SendMessage(to string) error` to `func (cli *Client) SendMessage(to string, extra ...int) error`",
	} {
		if !strings.Contains(got, want) {
			t.Errorf("missing %q:\n%s", want, got)
		}
	}

	for _, unwanted := range []string{"Kept", "unexported", "Elsewhere", "FromATest", "Generator", "Nested"} {
		if strings.Contains(got, unwanted) {
			t.Errorf("%s is listed:\n%s", unwanted, got)
		}
	}
}

func TestAGoFloorAboveThePinnedToolchainIsSaid(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name   string
		floor  string
		raised bool
	}{
		{"below the toolchain's patch release", "1.27.0", false},
		{"the toolchain itself", "1.27.3", false},
		{"a later patch release", "1.27.4", true},
		{"a later minor release", "1.28", true},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			atRecorded, atFrom, _ := same(nil)
			atTo := fstest.MapFS{"go.mod": {Data: []byte("module go.mau.fi/whatsmeow\n\ngo " + c.floor + "\n")}}

			got := report(t, doc(), sidecarMod, atRecorded, atFrom, atTo)

			said := strings.Contains(got, "**whatsmeow now needs go "+c.floor+", above the pinned toolchain go1.27.3**")
			if said != c.raised {
				t.Errorf("go %s against go1.27.3: raised said %v, want %v:\n%s", c.floor, said, c.raised, got)
			}
		})
	}
}

func TestTheRepositorysArchitectureRecordsAPin(t *testing.T) {
	t.Parallel()

	architecture, err := os.ReadFile(filepath.Join("..", "..", "docs", "ARCHITECTURE.md"))
	if err != nil {
		t.Fatalf("read ARCHITECTURE.md: %v", err)
	}

	commit, err := recordedPin(architecture)
	if err != nil {
		t.Fatalf("recordedPin: %v", err)
	}

	if len(commit) != 40 {
		t.Errorf("recorded pin %q is not a full commit hash", commit)
	}
}
