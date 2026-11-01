// Package store is the sidecar's one SQLite database: whatsmeow's device store and the sidecar's own tables, in a
// file that a single whatsmeow-net owns at a time (ARCHITECTURE §5, §7.4, §10).
package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"os"
	"strings"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/store/sqlstore"
	waLog "go.mau.fi/whatsmeow/util/log"

	// The CGO-free SQLite driver, registered as "sqlite" (ARCHITECTURE §5).
	_ "modernc.org/sqlite"
)

// pragmas apply to every connection (ARCHITECTURE §5, §14 question 3): whatsmeow's Upgrade refuses to run without
// foreign keys, and immediate transactions plus the busy timeout keep concurrent writers from SQLITE_BUSY.
const pragmas = "?_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=synchronous(FULL)&_txlock=immediate"

// schema is the sidecar's own tables, next to whatsmeow's.
const schema = `
CREATE TABLE IF NOT EXISTS session (
	id         TEXT PRIMARY KEY,
	jid        TEXT,
	lid        TEXT,
	-- The state the session keeps across a restart; NULL for one that comes back as Created or Disconnected.
	stop_state BLOB
)`

// ErrInUse is returned when another whatsmeow-net holds the database.
var ErrInUse = errors.New("in use by another whatsmeow-net")

// Store is the open database. Its lock is held until Close.
type Store struct {
	// DB is the database, for the sidecar's own tables.
	DB *sql.DB
	// Devices is whatsmeow's device store in the same database.
	Devices *sqlstore.Container
	lock    *os.File
}

// Open locks path, creates it owner-only if it is missing, opens it and brings both schemas up to date. A path the
// SQLite driver would read a query from (one with '?') is refused.
func Open(ctx context.Context, path string, log zerolog.Logger) (*Store, error) {
	if path == "" || strings.ContainsRune(path, '?') {
		return nil, fmt.Errorf("the database path %q must be a file path without '?'", path)
	}

	lockPath := path + ".lock"

	lock, err := lockFile(lockPath)
	if err != nil {
		return nil, fmt.Errorf("the database %s: lock %s: %w", path, lockPath, err)
	}

	s, err := open(ctx, path, log)
	if err != nil {
		_ = lock.Close()

		return nil, err
	}

	s.lock = lock

	return s, nil
}

func open(ctx context.Context, path string, log zerolog.Logger) (*Store, error) {
	// SQLite would create the file with the umask's mode; it holds long-term private keys (§10).
	file, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0o600) //nolint:gosec // G304: the configured database
	if err != nil {
		return nil, fmt.Errorf("create the database %s: %w", path, err)
	}

	_ = file.Close()

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		return nil, fmt.Errorf("open the database %s: %w", path, err)
	}

	devices := sqlstore.NewWithDB(db, "sqlite", waLog.Zerolog(log.With().Str("component", "store").Logger()))
	if err := devices.Upgrade(ctx); err != nil {
		_ = db.Close()

		return nil, fmt.Errorf("upgrade whatsmeow's store in %s: %w", path, err)
	}

	if _, err := db.ExecContext(ctx, schema); err != nil {
		_ = db.Close()

		return nil, fmt.Errorf("create the sidecar's tables in %s: %w", path, err)
	}

	return &Store{DB: db, Devices: devices}, nil
}

// Close closes the database, then releases the lock.
func (s *Store) Close() error {
	err := s.DB.Close()
	if lockErr := s.lock.Close(); lockErr != nil {
		err = errors.Join(err, lockErr)
	}

	if err != nil {
		return fmt.Errorf("close the store: %w", err)
	}

	return nil
}
