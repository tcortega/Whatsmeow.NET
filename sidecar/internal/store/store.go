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
const pragmas = "?_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=busy_timeout(30000)&_pragma=synchronous(FULL)&_txlock=immediate"

// The sidecar's own tables, next to whatsmeow's, as numbered steps: step n brings the database's PRAGMA user_version
// from n-1 to n. A step is never edited once merged; a schema change is a new step (ADR 0022). whatsmeow keeps its own
// version in whatsmeow_version and never reads user_version.

// schemaV1 is #6's schema exactly as merged; it ran with IF NOT EXISTS on every open, so a file from then is at
// version 0 with this table, and the step is a no-op for it.
const schemaV1 = `
CREATE TABLE IF NOT EXISTS session (
	id         TEXT PRIMARY KEY,
	jid        TEXT,
	lid        TEXT,
	-- The state the session keeps across a restart; NULL for one that comes back as Created or Disconnected.
	stop_state BLOB
)`

// schemaV2 is the outbox (#8). A session's outbox rows and cursors go with it (ON DELETE CASCADE; foreign keys are on,
// see pragmas).
const schemaV2 = `
-- The highest seq recorded for the session, kept after its outbox rows are deleted (events.proto).
ALTER TABLE session ADD COLUMN last_seq INTEGER NOT NULL DEFAULT 0;
-- The outbox (ARCHITECTURE §7.3). AUTOINCREMENT: a seq is never reused, also after the newest row was deleted.
CREATE TABLE outbox (
	seq        INTEGER PRIMARY KEY AUTOINCREMENT,
	session_id TEXT NOT NULL REFERENCES session (id) ON DELETE CASCADE,
	-- The event's natural key, so a repeated event keeps its seq; NULL for an event that is never repeated.
	dedupe_key TEXT,
	-- A StreamEventsResponse without its seq.
	payload    BLOB NOT NULL,
	UNIQUE (session_id, dedupe_key)
);
-- Each consumer's cursor: the highest seq it acked.
CREATE TABLE outbox_cursor (
	session_id TEXT NOT NULL REFERENCES session (id) ON DELETE CASCADE,
	consumer   TEXT NOT NULL,
	acked      INTEGER NOT NULL,
	PRIMARY KEY (session_id, consumer)
)`

// schemaV3 is the media retry requests no final answer settled yet (#28): the answer is encrypted with the media key
// the request named, so the sidecar keeps it to decrypt the answer, also after a restart. A final answer deletes its
// request (media.Mapper); one the phone never answers, or answers only with a general error, stays until its session
// is deleted, which takes its requests with it.
const schemaV3 = `
CREATE TABLE media_retry (
	session_id TEXT NOT NULL REFERENCES session (id) ON DELETE CASCADE,
	message_id TEXT NOT NULL,
	-- The message's chat, as the request named it; an answer is matched on the message id, the chat only breaks a tie.
	chat       TEXT NOT NULL,
	-- The requested MediaRef (media.proto).
	media      BLOB NOT NULL,
	PRIMARY KEY (session_id, message_id, chat)
)`

// schemaV4 is the send records (#9, ADR 0025): one row per message the client sent, by the id it chose, written before
// the send is handed to whatsmeow, so a repeat is answered from it. They go with their session (ON DELETE CASCADE).
const schemaV4 = `
CREATE TABLE sent_message (
	session_id        TEXT NOT NULL REFERENCES session (id) ON DELETE CASCADE,
	client_message_id TEXT NOT NULL,
	-- Set once WhatsApp accepted it: the chat it went to and WhatsApp's timestamp, in Unix seconds.
	chat              TEXT,
	message_time      INTEGER,
	-- Set when WhatsApp answered it with an error code. Neither set: the outcome is unknown.
	whatsapp_error    INTEGER,
	PRIMARY KEY (session_id, client_message_id)
)`

// schemaV5 is the history sync queue (#37, ADR 0035): each history sync notification the session's phone sent whose
// blob is not done yet, written by the event handler before whatsmeow answers the notification, so a blob is never
// dropped, also when it arrives before the session caught up or across a restart. The history worker delivers a blob's
// messages in batches, in order, moving messages_done in the transaction of each, sets recorded in the transaction of
// its progress event, and only then deletes the blob from WhatsApp's server and the row. Rows go with their session
// (ON DELETE CASCADE), and with its account at a logout or a new pairing.
const schemaV5 = `
CREATE TABLE history_blob (
	-- The order the notifications arrived in.
	seq           INTEGER PRIMARY KEY AUTOINCREMENT,
	session_id    TEXT NOT NULL REFERENCES session (id) ON DELETE CASCADE,
	-- The notification message's id; the blob's outbox keys derive from it.
	message_id    TEXT NOT NULL,
	-- The waE2E.HistorySyncNotification.
	notification  BLOB NOT NULL,
	-- How many of the blob's messages, in its order, are in the outbox or skipped: the next batch starts there.
	messages_done INTEGER NOT NULL DEFAULT 0,
	-- 1 once the progress event is in the outbox: what is left is deleting the blob from the server.
	recorded      INTEGER NOT NULL DEFAULT 0,
	-- How many downloads failed while the session was online.
	attempts      INTEGER NOT NULL DEFAULT 0,
	UNIQUE (session_id, message_id)
)`

// steps is every schema step, in order: steps()[n-1] is version n.
func steps() []string { return []string{schemaV1, schemaV2, schemaV3, schemaV4, schemaV5} }

// ErrInUse is returned when another whatsmeow-net holds the database.
var ErrInUse = errors.New("in use by another whatsmeow-net")

// Store is the open database. Its lock is held until Close.
type Store struct {
	// DB is the database, for the sidecar's own tables.
	DB *sql.DB
	// Writer is the gate every write of the sidecar takes (ADR 0040).
	Writer *Writer
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

	if err := migrate(ctx, db, steps()); err != nil {
		_ = db.Close()

		return nil, fmt.Errorf("upgrade the sidecar's tables in %s: %w", path, err)
	}

	return &Store{DB: db, Writer: newWriter(db), Devices: devices}, nil
}

// migrate runs the steps after the database's version, and sets the version, in one transaction: a step that fails
// leaves the database as it was.
func migrate(ctx context.Context, db *sql.DB, all []string) (err error) {
	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin: %w", err)
	}

	defer func() {
		if err != nil {
			_ = tx.Rollback()
		}
	}()

	var version int
	if err = tx.QueryRowContext(ctx, "PRAGMA user_version").Scan(&version); err != nil {
		return fmt.Errorf("read the version: %w", err)
	}

	if version > len(all) {
		err = fmt.Errorf("the database is at version %d; this whatsmeow-net knows %d", version, len(all))

		return err
	}

	for n := version + 1; n <= len(all); n++ {
		if _, err = tx.ExecContext(ctx, all[n-1]); err != nil {
			return fmt.Errorf("step %d: %w", n, err)
		}
	}

	// PRAGMA takes no parameter; the value is a number this code made.
	if _, err = tx.ExecContext(ctx, fmt.Sprintf("PRAGMA user_version = %d", len(all))); err != nil {
		return fmt.Errorf("set the version: %w", err)
	}

	if err = tx.Commit(); err != nil {
		return fmt.Errorf("commit: %w", err)
	}

	return nil
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
