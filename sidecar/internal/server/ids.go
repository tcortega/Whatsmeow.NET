package server

import "regexp"

// idPattern is the rule for the names a client chooses: session ids and consumer names (session.proto, events.proto).
var idPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`)

// CheckID fails with INVALID_ARGUMENT for the request field field unless value is 1 to 64 of [A-Za-z0-9._-]
// starting with a letter or a digit.
func CheckID(field, value string) error {
	if !idPattern.MatchString(value) {
		return InvalidArgument(field, "%q is not 1 to 64 of [A-Za-z0-9._-] starting with a letter or a digit", value)
	}

	return nil
}
