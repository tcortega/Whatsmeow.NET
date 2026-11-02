# 0039. Phone numbers and JIDs redacted from the sidecar's log lines and status messages

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #62 and its plan review; `sidecar/internal/log` tests (`pii_test.go`), `cmd/whatsmeow-net` tests `TestTheLoggerRedactsEveryComponentsLines` and `TestWhatsmeowsOwnLinesAndErrorsCarryNoNumber`, `internal/server` test `TestAStatusCarriesNoNumberOrCredential`, the FullCycle `SendText_ToADevice_ThrowsWithoutTheNumber` and `JidParseTests`; whatsmeow, libsignal v0.2.2 and zerolog v1.35.1 at the pin, read as cited below
- Amends: ARCHITECTURE §7.8 (the account's own JID no longer stays in info lines), §8.7 and §8.8; the residual that [ADR 0028](0028-contacts-blocklist-and-lid-resolution.md) and [ADR 0030](0030-groups-participants-settings-invites-and-group-events.md) left as a follow-up

## Context

ARCHITECTURE §8.8 allows JIDs and phone numbers only at debug level or redacted.
The sidecar writes info and above to stderr, and the client forwards every line into the consumer's logs (§7.8, §8.8).
What reached them at the pin:

- whatsmeow's own warn and error lines name other people's JIDs, on failures and on normal paths (message.go:327, 345, 351, 412, 415; notification.go:44, 48, 174, 183, 332; user.go:214, 255, 672-724; client.go:1098; group.go:564, 691, 695) [observed].
- whatsmeow's store logs bare phone numbers at info, through its own logger, which the sidecar gave the plain `waLog.Zerolog`, not the URL-cutting wrapper: "Migrated %d sessions ... from %s to %s" with the signal users `5511900000001` and `111111111111111_1` (store/sqlstore/store.go:248-300, container.go:95, 127) [observed: `TestWhatsmeowsOwnLinesAndErrorsCarryNoNumber`].
- A decryption error names the signal address, `5511900000001:0` (libsignal SessionCipher.go:168), and whatsmeow logs it (message.go:415) [observed].
- `server.toStatus` copied `err.Error()` into the status message, which becomes the .NET exception's message, and into its INTERNAL error line. Slices wrap whatsmeow's text, which names JIDs (user.go:1036-1040) and prints a `*url.Error` with the media `auth=` token (#65 review), and echo a JID they refuse (messaging.go:211-216) [observed].
- `Jid.Parse` put the text it refused into its message, which the client logs at error for an event it cannot read (`EventUnreadable`, `EphemeralEventUnreadable`) [observed: Jid.cs, SessionEvents.cs].
- whatsmeow's `zerolog.Ctx(ctx)` lines are dropped: the sidecar attaches no logger to a context and sets no `DefaultContextLogger`, so zerolog returns a disabled logger (zerolog ctx.go:44-51) [observed]. A bump that moves more lines there loses them, it does not leak them.
- libsignal's 39 info-and-above calls format no address at v0.2.2 [observed: grep].

## Decision

- **One rule.** Every run of seven or more ASCII digits is a phone number or a JID's user part, and becomes `~` and 8 hex digits: the first 4 bytes of a SHA-256 of a per-process random 32-byte key and the digits (`internal/log.Redactor`).
  Seven is whatsmeow's own floor: it refuses a phone number of six digits or fewer (pair-code.go:98); LIDs and group ids are longer.
  It fails closed: a run stays only when it is plainly not a number on its own. That is one that touches a letter or a digit (a hex message id such as `3EB0123456789ABC`, which needs the letter guard), one after a `.` that follows a digit (a version such as `2.3000.1027954720`), and a token already written, exactly eight digits after a `~`.
  So `foo.5511900000001@s.whatsapp.net` and `~5511900000001@s.whatsapp.net` are redacted; `x5511900000001` stays, the price of the letter guard [observed: `pii_test.go`].
  The rest of the JID stays, so `5511900000001:3@s.whatsapp.net` is `~1a2b3c4d:3@s.whatsapp.net`, and one user has one token in every form, JID, signal address, bare number or `+` number, for the life of the process.
  The key is random: the number space is small, so an unkeyed hash would be reversed by trying every number. It lives for one process, so tokens change when the sidecar restarts; correlating across restarts is not a goal.
- **One choke point for zerolog's lines.** `newLogger` writes through `log.Writer`, a `zerolog.LevelWriter` that redacts each line of info level and above inside its JSON strings only. So every zerolog line is covered, whatsmeow's, its store's, libsignal's and the sidecar's own that attach a whatsmeow error, with no key passed to them.
  Only strings, because the client parses each line for its level and a number field such as `parent_pid` can have seven digits (Linux `pid_max` 4194304): a token there would break the JSON. An escape sequence (`\n`, `\u0001`) separates, so a number on its own line after one is redacted.
  The time field is RFC 3339 (zerolog `TimeFieldFormat`, globals.go:115), with no run of seven digits.
  A debug line is written as it is; the sidecar logs none (§7.8).
- **Session ids are logged as given, in both processes.** The writer skips the value of `session_id`: it is the application's own name for a session, not WhatsApp's data, and the .NET client logs it raw. A token would only break the correlation between the two logs; an all-digit id would always get one, and a random GUID about 2.7% of the time [observed: 400,000 `uuid4` values against the rule; `TestTheWriterKeepsTheSessionID`].
- **What stderr carries besides zerolog's lines is not redacted.** The flag package's usage errors (`flags.SetOutput(stderr)`, cmd/whatsmeow-net/main.go), a Go runtime panic, whatsmeow's one `log.Fatalf` (newsletter.go:226, "argo to map error", reached only through the newsletter calls and `SetStatusMessage`, none of which the sidecar makes [observed: grep]), and grpclog, which grpc-go writes to stderr at error level by default.
  None of them carries a JID at the pin: the flags echo the .NET client's own arguments [observed], and grpc-go does not see WhatsApp data [inferred]; a panic's text is whatever panicked [inferred: none known to hold a JID]. The client takes a line that is not JSON at Information, unchanged.
- **Status messages.** `toStatus` puts `Redactor.Text(err.Error())` in the status message and in its INTERNAL error line: URL queries cut, then the same rule. The reason and its metadata are not touched. A status that passes through, from grpc-go or this package, keeps its code and details and has its message rebuilt with `Text` too [observed: `TestAPassedThroughStatusCarriesNoNumber`]. The debug line keeps the error as it is. The server gets the process's redactor, so a status message and a log line give a user the same token.
- **The account's own JID is redacted too.** whatsmeow logs it at info ("Successfully paired", pair.go:155; "Updated LID", connectionevents.go:182). Keeping it would need a per-session exception; the application has the JID from the API.
- **.NET.** `Jid.Parse` throws without the text it refused. Every other .NET line that carries an exception from the sidecar carries the redacted status message.

## Consequences

- A false positive is a token where a number was: a Unix time in a node whatsmeow dumps (`t="1730000000"`), a large count, a seq above a million in a status message, digits in a URL path, a consumer's client message id of seven or more digits. Accepted.
- A token is 32 bits, so two users can share one: among n users the chance is about n²/2³³, about 1.2% for 10,000. A token is for reading a log, not a key.
- The writer's cost per line, measured with `BenchmarkWriter` (Apple M4 Pro, Go 1.27.3): about 190 ns and no allocation for a line without a number, about 1.2 µs for a line with three, about 3.8 µs for a 2 KB node dump. A redacted line allocates its output once, sized a quarter above the line, and grows it only when its tokens outgrow that (a token is at most 2 bytes longer than its run). On a loaded machine the same benchmark varies by 2 to 4 times between runs, old and new code alike.
- On a whatsmeow bump, `TestWhatsmeowsOwnLinesAndErrorsCarryNoNumber` drives the pinned whatsmeow on the release store and logger and fails when it stops writing one of its lines, so the line is read again; the §14 entry lists the lines it relies on (#48).
- The live tests' own log redaction (`LogRedaction`) stays: it also hides the test number wherever the .NET side writes it.
