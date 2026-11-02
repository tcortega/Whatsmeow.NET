//go:build livetest

package messaging

// The id rule of the live tests' build only (ADR 0029): any id of letters and digits, so a live test can measure
// whether WhatsApp takes an id whatsmeow would not make (ADR 0025). A release build never links this file
// (build/ci.sh).
const (
	clientMessageIDPattern = `^[0-9A-Za-z]{1,64}$`
	clientMessageIDRule    = "1 to 64 letters and digits, the live tests' id rule"
)
