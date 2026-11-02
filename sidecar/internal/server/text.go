package server

import "strings"

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
