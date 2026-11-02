// Command bump-report writes the part of a whatsmeow bump PR's body that reads whatsmeow's source (#48):
//
//   - whatsmeow's Go floor, said loudly when it is above the pinned toolchain;
//   - each line of docs/ARCHITECTURE.md that cites a whatsmeow file which changed since the pin §14 records, so a
//     reviewer re-checks that fact before moving §14's pin (a later bump keeps listing it until then);
//   - the exported Client methods added, removed or changed between main's pin and the new one.
//
// A citation is a path ending in .go, .proto or .sql. It names a file at the module root when one is there, and
// otherwise every file whose path ends with it; a path that names neither, such as mautrix's, is not whatsmeow's.
//
// build/bump.sh runs it. Run it from the tools module: go -C tools run ./bump-report -from <version> -to <version>.
package main

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"go/version"
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
	"unicode/utf8"

	"golang.org/x/mod/modfile"

	"github.com/tcortega/Whatsmeow.NET/tools/internal/whatsmeowapi"
)

const (
	modulePath = "go.mau.fi/whatsmeow"
	docName    = "docs/ARCHITECTURE.md"
	excerpt    = 100
)

var (
	// The sentence in ARCHITECTURE §14 that says which commit "at the pin" means.
	pinSentence = regexp.MustCompile("\\*\\*The whatsmeow pin\\*\\* is `([0-9a-f]{40})`")
	citation    = regexp.MustCompile(`[A-Za-z0-9_./-]+\.(?:go|proto|sql)\b`)

	errNoPin     = errors.New(docName + " records no whatsmeow pin")
	errDownload  = errors.New("download failed")
	errNoVersion = errors.New("go.mod has no go line")
)

func main() {
	from := flag.String("from", "", "the whatsmeow version main pins")
	to := flag.String("to", "", "the whatsmeow version the bump pins")
	flag.Parse()

	err := start(*from, *to)
	if err != nil {
		fmt.Fprintln(os.Stderr, "bump-report:", err)
		os.Exit(1)
	}
}

// start reads ARCHITECTURE.md and the sidecar's go.mod, whose toolchain line is the pinned Go, from the tools module.
func start(from, to string) error {
	if from == "" || to == "" {
		return errors.New("-from and -to are required")
	}

	docPath := filepath.Join("..", "docs", "ARCHITECTURE.md")
	goModPath := filepath.Join("..", "sidecar", "go.mod")

	architecture, err := os.ReadFile(docPath) //nolint:gosec // G304: a fixed repository file
	if err != nil {
		return fmt.Errorf("read %s: %w", docPath, err)
	}

	goMod, err := os.ReadFile(goModPath) //nolint:gosec // G304: a fixed repository file
	if err != nil {
		return fmt.Errorf("read %s: %w", goModPath, err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	return run(ctx, config{doc: architecture, goMod: goMod, from: from, to: to, out: os.Stdout, download: goModDownload})
}

// module is a downloaded whatsmeow version: the version the go command resolved, and its files.
type module struct {
	version string
	files   fs.FS
}

// downloader fetches a module version or commit. goModDownload is the real one; tests inject fakes.
type downloader func(ctx context.Context, path, version string) (module, error)

type config struct {
	doc      []byte
	goMod    []byte
	from     string
	to       string
	out      io.Writer
	download downloader
}

func run(ctx context.Context, cfg config) error {
	commit, err := recordedPin(cfg.doc)
	if err != nil {
		return err
	}

	atRecorded, err := cfg.download(ctx, modulePath, commit)
	if err != nil {
		return err
	}

	atFrom, err := cfg.download(ctx, modulePath, cfg.from)
	if err != nil {
		return err
	}

	atTo, err := cfg.download(ctx, modulePath, cfg.to)
	if err != nil {
		return err
	}

	var b strings.Builder

	fmt.Fprintf(&b, "whatsmeow `%s` to `%s` ([commits](https://github.com/tulir/whatsmeow/compare/%s...%s)).\n\n",
		atFrom.version, atTo.version, commitOf(atFrom.version), commitOf(atTo.version))

	floor, err := goFloor(atTo.files, cfg.goMod)
	if err != nil {
		return err
	}

	b.WriteString(floor)

	err = writeFacts(ctx, &b, cfg.doc, commit, atRecorded.files, atTo.files)
	if err != nil {
		return err
	}

	err = writeMethods(&b, atFrom.files, atTo.files)
	if err != nil {
		return err
	}

	_, err = io.WriteString(cfg.out, b.String())
	if err != nil {
		return fmt.Errorf("report: %w", err)
	}

	return nil
}

// recordedPin is the commit ARCHITECTURE §14 says "at the pin" means.
func recordedPin(architecture []byte) (string, error) {
	match := pinSentence.FindSubmatch(architecture)
	if match == nil {
		return "", errNoPin
	}

	return string(match[1]), nil
}

// commitOf is a pseudo-version's commit, its last dash-separated part.
func commitOf(pseudo string) string {
	return pseudo[strings.LastIndex(pseudo, "-")+1:]
}

// goFloor compares the new whatsmeow's go line with the sidecar's toolchain line.
func goFloor(atTo fs.FS, sidecarMod []byte) (string, error) {
	raw, err := fs.ReadFile(atTo, "go.mod")
	if err != nil {
		return "", fmt.Errorf("read whatsmeow's go.mod: %w", err)
	}

	upstream, err := modfile.ParseLax("go.mod", raw, nil)
	if err != nil {
		return "", fmt.Errorf("parse whatsmeow's go.mod: %w", err)
	}

	sidecar, err := modfile.Parse("sidecar/go.mod", sidecarMod, nil)
	if err != nil {
		return "", fmt.Errorf("parse sidecar/go.mod: %w", err)
	}

	if upstream.Go == nil || sidecar.Go == nil {
		return "", errNoVersion
	}

	floor := upstream.Go.Version

	toolchain := "go" + sidecar.Go.Version
	if sidecar.Toolchain != nil {
		toolchain = sidecar.Toolchain.Name
	}

	if version.Compare("go"+floor, toolchain) > 0 {
		return fmt.Sprintf("**whatsmeow now needs go %s, above the pinned toolchain %s**: the build fails until mise.toml and the go.mod toolchain lines move to it.\n\n", floor, toolchain), nil
	}

	return fmt.Sprintf("whatsmeow needs go %s; the pinned toolchain is %s.\n\n", floor, toolchain), nil
}

// writeFacts lists each ARCHITECTURE line that cites a file which differs between the recorded pin and the new one.
func writeFacts(ctx context.Context, b *strings.Builder, architecture []byte, commit string, atRecorded, atTo fs.FS) error {
	changed, err := changedFiles(ctx, atRecorded, atTo)
	if err != nil {
		return err
	}

	fmt.Fprintf(b, "### Facts unconfirmed at the new pin\n\n")
	fmt.Fprintf(b, "%d whatsmeow files changed since `%s`, the pin ARCHITECTURE §14 records. ", len(changed.differs), commit[:12])
	b.WriteString("Each line below cites one of them; re-check its fact against the new source, then move §14's pin.\n\n")

	listed := 0

	for i, line := range strings.Split(string(architecture), "\n") {
		var cites []string

		for _, cite := range citation.FindAllString(line, -1) {
			if !slices.Contains(cites, cite) && changed.cites(cite) {
				cites = append(cites, cite)
			}
		}

		if len(cites) == 0 {
			continue
		}

		listed++

		fmt.Fprintf(b, "- %s:%d `%s`: %s\n", docName, i+1, strings.Join(cites, "`, `"), shorten(line))
	}

	if listed == 0 {
		b.WriteString("No file a fact cites changed.\n")
	}

	b.WriteString("\n")

	return nil
}

func shorten(line string) string {
	line = strings.TrimLeft(line, " -*0123456789.")
	if utf8.RuneCountInString(line) <= excerpt {
		return line
	}

	return string([]rune(line)[:excerpt]) + "..."
}

// changes is what differs between two versions of the module, and every file either has.
type changes struct {
	files   []string
	differs map[string]bool
}

// cites is whether a citation names a changed file: the root file of that name, else any file whose path ends in it.
func (c changes) cites(cite string) bool {
	if slices.Contains(c.files, cite) {
		return c.differs[cite]
	}

	for _, name := range c.files {
		if strings.HasSuffix(name, "/"+cite) && c.differs[name] {
			return true
		}
	}

	return false
}

func changedFiles(ctx context.Context, a, b fs.FS) (changes, error) {
	before, err := read(ctx, a)
	if err != nil {
		return changes{}, err
	}

	after, err := read(ctx, b)
	if err != nil {
		return changes{}, err
	}

	c := changes{differs: map[string]bool{}}

	for name, content := range before {
		other, ok := after[name]
		if !ok || !bytes.Equal(content, other) {
			c.differs[name] = true
		}
	}

	for name := range after {
		if _, ok := before[name]; !ok {
			c.differs[name] = true
		}
	}

	all := maps.Clone(before)
	maps.Copy(all, after)
	c.files = slices.Sorted(maps.Keys(all))

	return c, nil
}

// read is every file of the module, keyed by its slash path.
func read(ctx context.Context, fsys fs.FS) (map[string][]byte, error) {
	files := map[string][]byte{}

	err := fs.WalkDir(fsys, ".", func(name string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}

		if ctx.Err() != nil {
			return fmt.Errorf("walk: %w", ctx.Err())
		}

		if entry.IsDir() {
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
		return nil, fmt.Errorf("walk the module: %w", err)
	}

	return files, nil
}

// writeMethods lists the exported Client methods added, removed or changed from one version to the other; a method
// that only moved to another file is unchanged.
func writeMethods(b *strings.Builder, atFrom, atTo fs.FS) error {
	before, err := whatsmeowapi.ClientMethods(atFrom)
	if err != nil {
		return fmt.Errorf("main's pin: %w", err)
	}

	after, err := whatsmeowapi.ClientMethods(atTo)
	if err != nil {
		return fmt.Errorf("the new pin: %w", err)
	}

	b.WriteString("### Client methods\n\n")

	listed := 0

	for _, name := range slices.Sorted(maps.Keys(after)) {
		old, ok := before[name]
		switch {
		case !ok:
			fmt.Fprintf(b, "- added `%s`\n", after[name].Signature)
		case old.Signature != after[name].Signature:
			fmt.Fprintf(b, "- changed `%s` to `%s`\n", old.Signature, after[name].Signature)
		default:
			continue
		}

		listed++
	}

	for _, name := range slices.Sorted(maps.Keys(before)) {
		if _, ok := after[name]; !ok {
			fmt.Fprintf(b, "- removed `%s`\n", before[name].Signature)

			listed++
		}
	}

	if listed == 0 {
		b.WriteString("No exported `Client` method was added, removed or changed.\n")
	}

	b.WriteString("\n")

	return nil
}

// goModDownload fetches a module with `go mod download -json`, in an empty temporary directory so it never edits a
// go.mod or go.sum, and reads its zip. A module whose go line is above the local toolchain is still downloaded and
// checked, with no extracted directory and an Error that says so; its zip is read all the same [observed: go 1.27.3].
func goModDownload(ctx context.Context, path, query string) (m module, err error) {
	scratch, err := os.MkdirTemp("", "bump-report-")
	if err != nil {
		return module{}, fmt.Errorf("create a scratch directory: %w", err)
	}

	defer func() {
		removeErr := os.RemoveAll(scratch)
		if removeErr != nil {
			err = errors.Join(err, fmt.Errorf("remove %s: %w", scratch, removeErr))
		}
	}()

	// The arguments are this module's path and a version from the repository, passed without a shell.
	cmd := exec.CommandContext(ctx, "go", "mod", "download", "-json", path+"@"+query) //nolint:gosec // G204: no shell
	cmd.Dir = scratch
	cmd.Stderr = os.Stderr

	// On failure the go command still prints the JSON object, with the reason in Error.
	stdout, runErr := cmd.Output()

	var info struct {
		Version string
		Zip     string
		Error   string
	}

	decodeErr := json.Unmarshal(stdout, &info)
	tooNew := info.Zip != "" && strings.Contains(info.Error, "requires go >=")

	switch {
	case decodeErr != nil:
		return module{}, fmt.Errorf("%w: go mod download %s@%s: %w", errDownload, path, query, errors.Join(runErr, decodeErr))
	case info.Error != "" && !tooNew:
		return module{}, fmt.Errorf("%w: go mod download %s@%s: %s", errDownload, path, query, info.Error)
	case runErr != nil && !tooNew:
		return module{}, fmt.Errorf("%w: go mod download %s@%s: %w", errDownload, path, query, runErr)
	}

	raw, err := os.ReadFile(info.Zip)
	if err != nil {
		return module{}, fmt.Errorf("read the module zip: %w", err)
	}

	archive, err := zip.NewReader(bytes.NewReader(raw), int64(len(raw)))
	if err != nil {
		return module{}, fmt.Errorf("open the module zip: %w", err)
	}

	files, err := fs.Sub(archive, path+"@"+info.Version)
	if err != nil {
		return module{}, fmt.Errorf("open the module zip: %w", err)
	}

	return module{version: info.Version, files: files}, nil
}
