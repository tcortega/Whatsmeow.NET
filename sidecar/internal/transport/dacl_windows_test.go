package transport

import (
	"context"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

// account is the current user as SDDL writes it: a well-known account becomes its alias (the built-in
// Administrator, RID 500, is LA).
func account(t *testing.T) string {
	t.Helper()

	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		t.Fatalf("token user: %v", err)
	}

	rendered, err := windows.SecurityDescriptorFromString("D:(A;;FA;;;" + user.User.Sid.String() + ")")
	if err != nil {
		t.Fatalf("render the user SID: %v", err)
	}

	return strings.TrimSuffix(strings.TrimPrefix(rendered.String(), "D:(A;;FA;;;"), ")")
}

func dacl(t *testing.T, path string) string {
	t.Helper()

	sd, err := windows.GetNamedSecurityInfo(path, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatalf("read the DACL of %s: %v", path, err)
	}

	return sd.String()
}

func TestListenGivesTheSocketDirectoryAnOwnerOnlyDACLThatTheSocketInherits(t *testing.T) {
	t.Parallel()

	dir := socketDir(t)

	lis, endpoint, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	user := regexp.QuoteMeta(account(t))

	// Protected (P: nothing inherited from %TEMP%), and one ACE: full access for this user, inherited (OICI).
	if got, want := dacl(t, dir), regexp.MustCompile(`^D:P[A-Z]*\(A;OICI;FA;;;`+user+`\)$`); !want.MatchString(got) {
		t.Fatalf("directory DACL = %s, want %s", got, want)
	}

	// The socket file carries only that ACE, inherited (ID).
	if got, want := dacl(t, filepath.Clean(endpoint)), regexp.MustCompile(`^D:[A-Z]*\(A;ID;FA;;;`+user+`\)$`); !want.MatchString(got) {
		t.Fatalf("socket DACL = %s, want %s", got, want)
	}
}
