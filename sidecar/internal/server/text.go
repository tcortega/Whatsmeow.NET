package server

import (
	"strconv"
	"strings"
)

// Text is s as a proto3 string field may carry it: every invalid UTF-8 sequence replaced by U+FFFD. Every string a
// slice takes from WhatsApp data into the contract goes through it: whatsmeow copies a push name, a group name and
// the like unchecked (client.go:1039-1040), and protobuf refuses to marshal a proto3 string that is not UTF-8, so
// one would fail its event's recording on every redispatch, or its call's answer.
func Text(s string) string { return strings.ToValidUTF8(s, "�") }

// Texts is Text of each.
func Texts(s []string) []string {
	out := make([]string, len(s))
	for i, v := range s {
		out[i] = Text(v)
	}

	return out
}

// ReadableJID reports whether the client reads s as a JID, as its Jid.TryParse does (dotnet/src/Whatsmeow.Client/Jid.cs):
// a server alone, or user[.agent][:device]@server with a server that is not empty, an agent that fits a byte, a
// device that fits a ushort, and a user that keeps no '.' or ':'. A slice records an event only when every JID the
// client requires in it passes, since the client skips and acks one it cannot read (ARCHITECTURE §8.7, #122):
// whatsmeow's ParseJID gives the empty JID for "" and keeps the whole user when it fails, which JID.String writes back.
func ReadableJID(s string) bool {
	user, server, found := strings.Cut(s, "@")
	if !found {
		return s != ""
	}

	if server == "" || strings.Contains(server, "@") {
		return false
	}

	if u, ad, ok := strings.Cut(user, "."); ok {
		agent, device, hasDevice := strings.Cut(ad, ":")

		return fits(agent, 8) && (!hasDevice || fits(device, 16)) && !strings.ContainsAny(u, ".:")
	}

	if _, device, ok := strings.Cut(user, ":"); ok {
		return fits(device, 16)
	}

	return true
}

// fits reports whether s is a number of ASCII digits that fits bits, as .NET parses one with NumberStyles.None.
func fits(s string, bits int) bool {
	_, err := strconv.ParseUint(s, 10, bits)

	return err == nil
}
