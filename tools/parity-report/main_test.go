package main

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const (
	sidecarMod = "module example.com/sidecar\n\ngo 1.26.0\n\nrequire go.mau.fi/whatsmeow v0.0.0\n\nreplace go.mau.fi/whatsmeow => ./whatsmeow\n"
	modGo126   = "module go.mau.fi/whatsmeow\n\ngo 1.26.0\n"

	// The contract: one service whose generated code is shaped as protoc-gen-go-grpc's is.
	contract = `package whatsmeowv1

import "context"

const (
	GroupService_CreateGroup_FullMethodName = "/whatsmeow.v1.GroupService/CreateGroup"
	GroupService_LeaveGroup_FullMethodName  = "/whatsmeow.v1.GroupService/LeaveGroup"
	GroupService_SetName_FullMethodName     = "/whatsmeow.v1.GroupService/SetName"
	GroupService_SetTopic_FullMethodName    = "/whatsmeow.v1.GroupService/SetTopic"
	GroupService_Unused_FullMethodName      = "/whatsmeow.v1.GroupService/Unused"
)

type GroupServiceServer interface {
	CreateGroup(context.Context) error
	LeaveGroup(context.Context) error
	SetName(context.Context) error
	SetTopic(context.Context) error
	Unused(context.Context) error
	mustEmbedUnimplementedGroupServiceServer()
}

type UnimplementedGroupServiceServer struct{}

func (UnimplementedGroupServiceServer) CreateGroup(context.Context) error      { return nil }
func (UnimplementedGroupServiceServer) LeaveGroup(context.Context) error       { return nil }
func (UnimplementedGroupServiceServer) SetName(context.Context) error          { return nil }
func (UnimplementedGroupServiceServer) SetTopic(context.Context) error         { return nil }
func (UnimplementedGroupServiceServer) Unused(context.Context) error           { return nil }
func (UnimplementedGroupServiceServer) mustEmbedUnimplementedGroupServiceServer() {}

type UnsafeGroupServiceServer interface {
	mustEmbedUnimplementedGroupServiceServer()
}
`

	// A slice that reaches whatsmeow every way the sidecar does: through its small interface and a helper, in a
	// closure its RPC queues, through shared code that calls a builder it was handed, and from a worker no RPC runs.
	slice = `package groups

import (
	"context"

	"go.mau.fi/whatsmeow"

	whatsmeowv1 "example.com/sidecar/gen/whatsmeow/v1"
)

type Client interface {
	CreateGroup() error
	LeaveGroup() error
	SetGroupName() error
	SetGroupTopic() error
	Sync() error
}

var _ Client = (*whatsmeow.Client)(nil)

type Service struct {
	whatsmeowv1.UnimplementedGroupServiceServer

	cli   Client
	queue chan func()
}

func New(cli *whatsmeow.Client) *Service {
	s := &Service{cli: cli, queue: make(chan func())}
	go s.run()
	go s.sync()

	return s
}

func (s *Service) CreateGroup(context.Context) error { return s.create() }

func (s *Service) create() error { return s.cli.CreateGroup() }

func (s *Service) LeaveGroup(context.Context) error {
	s.queue <- func() { _ = s.cli.LeaveGroup() }

	return nil
}

func (s *Service) SetName(context.Context) error { return s.send(func() error { return s.cli.SetGroupName() }) }

func (s *Service) SetTopic(context.Context) error { return s.send(func() error { return s.cli.SetGroupTopic() }) }

func (s *Service) send(build func() error) error { return build() }

func (s *Service) run() {
	for job := range s.queue {
		job()
	}
}

func (s *Service) sync() { _ = s.cli.Sync() }
`
)

// whatsmeow is a stand-in whatsmeow whose client.go declares the given Client methods.
func whatsmeow(methods ...string) map[string]string {
	src := "package whatsmeow\n\ntype Client struct{}\n\n"
	for _, name := range methods {
		src += "func (cli *Client) " + name + "() error { return nil }\n"
	}

	return map[string]string{"go.mod": modGo126, "client.go": src}
}

// pinned is the stand-in whatsmeow at the pin: what the slice calls, one more method, and one only a test declares.
func pinned(extra ...string) map[string]string {
	files := whatsmeow(append([]string{"CreateGroup", "LeaveGroup", "SetGroupName", "SetGroupTopic", "Sync", "Logout", "SetProxy"}, extra...)...)
	files["client_test.go"] = "package whatsmeow\n\nfunc (cli *Client) FromATest() error { return nil }\n"

	return files
}

// module writes a sidecar module with the contract and the slice, and the given whatsmeow beside it.
func module(t *testing.T, upstream map[string]string) string {
	t.Helper()

	dir := t.TempDir()
	files := map[string]string{
		"go.mod":                         sidecarMod,
		"gen/whatsmeow/v1/group.go":      contract,
		"internal/groups/groups.go":      slice,
		"internal/groups/groups_test.go": "package groups\n",
	}

	for name, content := range upstream {
		files[filepath.Join("whatsmeow", name)] = content
	}

	for name, content := range files {
		path := filepath.Join(dir, filepath.FromSlash(name))

		err := os.MkdirAll(filepath.Dir(path), 0o750)
		if err != nil {
			t.Fatal(err)
		}

		err = os.WriteFile(path, []byte(content), 0o600)
		if err != nil {
			t.Fatal(err)
		}
	}

	return dir
}

func report(t *testing.T, dir, excluded string) string {
	t.Helper()

	out := &bytes.Buffer{}

	err := run(context.Background(), config{dir: dir, excluded: []byte(excluded), out: out})
	if err != nil {
		t.Fatalf("run: %v", err)
	}

	return out.String()
}

func contains(t *testing.T, got string, want ...string) {
	t.Helper()

	for _, w := range want {
		if !strings.Contains(got, w) {
			t.Errorf("missing %q:\n%s", w, got)
		}
	}
}

func TestEachMethodIsCoveredByItsRPCExcludedWithItsReasonOrMissing(t *testing.T) {
	t.Parallel()

	got := report(t, module(t, pinned()), "# a comment\n\nSetProxy: proxies are not configurable in v1\n")

	contains(t, got,
		"`go.mau.fi/whatsmeow` `v0.0.0`: 7 exported `Client` methods, 5 covered, 1 excluded, **1 missing**.",
		"### client.go",
		"| `CreateGroup` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"| `LeaveGroup` | covered | `whatsmeow.v1.GroupService/LeaveGroup` |",
		"| `SetProxy` | excluded | proxies are not configurable in v1 |",
		"| `Logout` | **missing** | |",
	)

	if strings.Contains(got, "FromATest") {
		t.Errorf("a method only a test declares is listed:\n%s", got)
	}
}

func TestAMethodNoRPCReachesNamesTheFunctionThatCallsIt(t *testing.T) {
	t.Parallel()

	got := report(t, module(t, pinned()), "")

	contains(t, got, "| `Sync` | covered | no RPC: `groups.(*Service).sync` |")
}

func TestABuilderHandedToSharedCodeIsCreditedToTheRPCThatMadeItOnly(t *testing.T) {
	t.Parallel()

	got := report(t, module(t, pinned()), "")

	contains(t, got,
		"| `SetGroupName` | covered | `whatsmeow.v1.GroupService/SetName` |",
		"| `SetGroupTopic` | covered | `whatsmeow.v1.GroupService/SetTopic` |",
	)
}

func TestAMethodABumpAddsIsMissing_AndTheReportStillSucceeds(t *testing.T) {
	t.Parallel()

	excluded := "SetProxy: proxies are not configurable in v1\nLogout: not yet\n"

	before := report(t, module(t, pinned()), excluded)
	if strings.Contains(before, "**missing**") || !strings.Contains(before, "**0 missing**") {
		t.Fatalf("the pin has a missing method:\n%s", before)
	}

	after := report(t, module(t, pinned("RejectCall")), excluded)

	contains(t, after, "| `RejectCall` | **missing** | |", "**1 missing**")
}

func TestStaleExclusionsAreListed(t *testing.T) {
	t.Parallel()

	got := report(t, module(t, pinned()), "CreateGroup: once out of scope\nGone: removed upstream\n")

	contains(t, got,
		"| `CreateGroup` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"- `CreateGroup` is covered; drop its exclusion.",
		"- `Gone` is not a `Client` method at this pin; drop its exclusion.",
	)
}

func TestAMalformedExclusionFailsWithItsLine(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, excluded, want string
	}{
		{"no reason", "# head\nLogout:\n", "parity.excluded:2: Logout has no reason"},
		{"no colon", "Logout not yet\n", "parity.excluded:1: want Name: reason"},
		{"not an exported name", "logout: not yet\n", "parity.excluded:1: logout is not an exported method name"},
		{"twice", "Logout: a\nSetProxy: b\nLogout: c\n", "parity.excluded:3: Logout is listed twice"},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			_, err := parseExcluded([]byte(c.excluded))
			if err == nil || err.Error() != c.want {
				t.Errorf("parseExcluded(%q) = %v, want %q", c.excluded, err, c.want)
			}
		})
	}
}

func TestTheRepositorysExclusionsParse(t *testing.T) {
	t.Parallel()

	raw, err := os.ReadFile(filepath.Join("..", "..", "sidecar", "parity.excluded"))
	if err != nil {
		t.Fatalf("read sidecar/parity.excluded: %v", err)
	}

	_, err = parseExcluded(raw)
	if err != nil {
		t.Error(err)
	}
}
