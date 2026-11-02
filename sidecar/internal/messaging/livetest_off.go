//go:build !livetest

package messaging

// The id rule of a release build: the format whatsmeow generates (send.go:41, 47-61), ADR 0025.
const (
	clientMessageIDPattern = `^3EB0[0-9A-F]{18}$`
	clientMessageIDRule    = "3EB0 and 18 uppercase hex digits, the id format whatsmeow makes"
)
