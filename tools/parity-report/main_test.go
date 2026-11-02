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

type GroupServiceServer interface {
	CreateGroup(context.Context) error
	LeaveGroup(context.Context) error
	SetName(context.Context) error
	SetTopic(context.Context) error
	Unused(context.Context) error
	Watch(context.Context) error
	mustEmbedUnimplementedGroupServiceServer()
}

type UnimplementedGroupServiceServer struct{}

func (UnimplementedGroupServiceServer) CreateGroup(context.Context) error      { return nil }
func (UnimplementedGroupServiceServer) LeaveGroup(context.Context) error       { return nil }
func (UnimplementedGroupServiceServer) SetName(context.Context) error          { return nil }
func (UnimplementedGroupServiceServer) SetTopic(context.Context) error         { return nil }
func (UnimplementedGroupServiceServer) Unused(context.Context) error           { return nil }
func (UnimplementedGroupServiceServer) Watch(context.Context) error            { return nil }
func (UnimplementedGroupServiceServer) mustEmbedUnimplementedGroupServiceServer() {}

type UnsafeGroupServiceServer interface {
	mustEmbedUnimplementedGroupServiceServer()
}

// RegisterGroupServiceServer takes the service as the interface, as grpc's does.
func RegisterGroupServiceServer(srv GroupServiceServer) { _ = srv }
`

	// A slice that reaches whatsmeow every way the sidecar does: through its small interface and a helper, in a
	// closure its RPC queues, through shared code that calls a builder it was handed, and from a worker no RPC runs.
	slice = `package groups

import (
	"context"
	"errors"

	"go.mau.fi/whatsmeow"

	whatsmeowv1 "example.com/sidecar/gen/whatsmeow/v1"
)

type Client interface {
	CreateGroup() error
	LeaveGroup() error
	SetGroupName() error
	SetGroupTopic() error
	Sync() error
	DecryptPollVote() error
	MarkRead() error
	GetBlocklist() error
	GetUserInfo() error
	ResetConnection() error
	GetPrivacySettings() error
	MarkNotDirty() error
	SetStatusMessage() error
}

// doer is a seam of the slice's own, which worker implements; whatsmeow's Helper does too.
type doer interface {
	Do()
}

type worker struct {
	cli Client
}

func (w worker) Do() { _ = w.cli.MarkRead() }

// box is generic: only its instance, box[int] in create[int], is converted to doer.
type box[T any] struct {
	cli Client
}

func (b box[T]) Do() { _ = b.cli.GetBlocklist() }

// pointer implements doer with a pointer receiver.
type pointer struct {
	cli Client
}

func (p *pointer) Do() { _ = p.cli.GetUserInfo() }

// quiet is an unexported service that main registers; its RPC is promoted from base.
type quiet struct {
	*base
}

type base struct {
	whatsmeowv1.UnimplementedGroupServiceServer

	cli Client
}

func Quiet(cli *whatsmeow.Client) whatsmeowv1.GroupServiceServer { return &quiet{base: &base{cli: cli}} }

func (b *base) Unused(context.Context) error { return b.cli.GetPrivacySettings() }

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
	describe(errors.New("a universe method value").Error)
	_ = cli.Connect()
	hold(func(*whatsmeow.Client) error { return nil })
	keep(cli.Disconnect)
	keep(s.cli.SetStatusMessage)

	return s
}

func (s *Service) CreateGroup(context.Context) error {
	defer s.audit()

	return create[int](s)
}

func (s *Service) audit() { _ = s.cli.MarkNotDirty() }

func create[T any](s *Service) error {
	_ = whatsmeow.Probe(nil)
	hold(whatsmeow.Hand)

	var d doer = worker{cli: s.cli}
	d.Do()

	d = box[T]{cli: s.cli}
	d.Do()

	d = &pointer{cli: s.cli}
	d.Do()

	return s.cli.CreateGroup()
}

func hold(f func(*whatsmeow.Client) error) { _ = f }

func keep(f func() error) { _ = f }

func (s *Service) LeaveGroup(context.Context) error {
	s.queue <- func() { _ = s.cli.LeaveGroup() }
	_ = s.cli.Sync()

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

func (s *Service) sync() {
	if s.cli.Sync() != nil {
		s.sync()
	}
}

// Hook takes the event handler as a value, as whatsmeow's AddEventHandler does; whatsmeow calls it later.
func Hook(handler func()) { _ = handler }

func describe(text func() string) { _ = text }

func (s *Service) OnEvent() { _ = s.cli.DecryptPollVote() }

// Watch registers a callback that runs later, after the RPC returned, as an event handler does.
func (s *Service) Watch(context.Context) error {
	s.register()

	return nil
}

func (s *Service) register() {
	Hook(s.onChange)
	Hook(tick)
	keep((&whatsmeow.Client{}).GetStatusPrivacy)
}

func tick() { _ = (&whatsmeow.Client{}).GetBusinessProfile() }

func (s *Service) onChange() { _ = s.cli.ResetConnection() }
`

	// What the release build runs: the slice, and its event handler handed over as a method value.
	program = `package main

import (
	"go.mau.fi/whatsmeow"

	whatsmeowv1 "example.com/sidecar/gen/whatsmeow/v1"
	"example.com/sidecar/internal/groups"
)

func main() {
	cli := &whatsmeow.Client{}
	s := groups.New(cli)
	groups.Hook(s.OnEvent)
	whatsmeowv1.RegisterGroupServiceServer(s)
	whatsmeowv1.RegisterGroupServiceServer(groups.Quiet(cli))
}
`

	// Code the release build does not run: a test, a file behind a build tag, a package nothing links.
	test = `package groups

import (
	"context"
	"testing"

	"go.mau.fi/whatsmeow"

	whatsmeowv1 "example.com/sidecar/gen/whatsmeow/v1"
)

// stub implements the service in a test, as the fake WhatsApp's control does.
type stub struct {
	whatsmeowv1.UnimplementedGroupServiceServer
}

func (stub) SetTopic(context.Context) error { return (&whatsmeow.Client{}).Logout() }

func init() { whatsmeowv1.RegisterGroupServiceServer(stub{}) }

func TestLogout(t *testing.T) { _ = (&whatsmeow.Client{}).Logout() }
`
	tagged   = "//go:build fakewhatsapp\n\npackage main\n\nimport \"go.mau.fi/whatsmeow\"\n\nfunc main() { _ = (&whatsmeow.Client{}).IsLoggedIn() }\n"
	unlinked = `package unused

import (
	"context"

	"go.mau.fi/whatsmeow"

	whatsmeowv1 "example.com/sidecar/gen/whatsmeow/v1"
)

// Service is a service nothing registers in the release build: it registers itself, but no main imports it.
type Service struct {
	whatsmeowv1.UnimplementedGroupServiceServer
}

func Register() { whatsmeowv1.RegisterGroupServiceServer(&Service{}) }

func (*Service) Unused(context.Context) error { return (&whatsmeow.Client{}).SetPassive() }

func Helper() { _ = (&whatsmeow.Client{}).SetPassive() }
`
)

// whatsmeow is a stand-in whatsmeow whose client.go declares the given Client methods.
func whatsmeow(methods ...string) map[string]string {
	src := "package whatsmeow\n\ntype Client struct{}\n\n"
	for _, name := range methods {
		src += "func (cli *Client) " + name + "() error { return nil }\n"
	}

	// whatsmeow's own code calls its Client too, which covers nothing.
	src += "\nfunc Probe(cli *Client) error { return cli.Logout() }\n\nfunc Hand(cli *Client) error { return cli.Logout() }\n"
	src += "\ntype Helper struct{}\n\nfunc (Helper) Do() { _ = (&Client{}).Logout() }\n\nvar Doer interface{ Do() } = Helper{}\n"

	return map[string]string{"go.mod": modGo126, "client.go": src}
}

// pinned is the stand-in whatsmeow at the pin: what the release build calls, what only code it does not run calls,
// one more method, and one only a test declares.
func pinned(extra ...string) map[string]string {
	files := whatsmeow(append([]string{"CreateGroup", "LeaveGroup", "SetGroupName", "SetGroupTopic", "Sync", "DecryptPollVote", "MarkRead", "Connect", "Disconnect", "GetBlocklist", "GetUserInfo", "ResetConnection", "GetPrivacySettings", "MarkNotDirty", "SetStatusMessage", "GetStatusPrivacy", "GetBusinessProfile", "Logout", "IsLoggedIn", "SetPassive", "SetProxy"}, extra...)...)
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
		"internal/groups/groups_test.go": test,
		"internal/unused/unused.go":      unlinked,
		"cmd/sidecar/main.go":            program,
		"cmd/fake/main.go":               tagged,
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
		"`go.mau.fi/whatsmeow` `v0.0.0`: 21 exported `Client` methods, 17 covered, 1 excluded, **3 missing**.",
		"| `MarkNotDirty` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"| `MarkRead` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"| `GetBlocklist` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"| `GetUserInfo` | covered | `whatsmeow.v1.GroupService/CreateGroup` |",
		"| `GetPrivacySettings` | covered | `whatsmeow.v1.GroupService/Unused` |",
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

	contains(t, got,
		"| `Sync` | covered | `whatsmeow.v1.GroupService/LeaveGroup`; no RPC: `groups.(*Service).sync` |",
		"| `GetBusinessProfile` | covered | no RPC: `groups.tick` |",
		"| `GetStatusPrivacy` | covered | no RPC: `groups.(*Service).register` |",
		"| `DecryptPollVote` | covered | no RPC: `groups.(*Service).OnEvent` |",
		"| `Connect` | covered | no RPC: `groups.New` |",
		"| `ResetConnection` | covered | no RPC: `groups.(*Service).onChange` |",
		"| `SetStatusMessage` | covered | no RPC: `groups.New` |",
		"| `Disconnect` | covered | no RPC: `groups.New` |",
	)
}

func TestOnlyWhatTheReleaseBuildRunsCovers_NotATestATaggedFileOrAnUnlinkedPackage(t *testing.T) {
	t.Parallel()

	got := report(t, module(t, pinned()), "")

	contains(t, got,
		"| `Logout` | **missing** | |",
		"| `IsLoggedIn` | **missing** | |",
		"| `SetPassive` | **missing** | |",
	)
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

	excluded := "SetProxy: proxies are not configurable in v1\nLogout: not yet\nIsLoggedIn: not yet\nSetPassive: not yet\n"

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
