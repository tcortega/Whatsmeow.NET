package transport

import (
	"context"
	"regexp"
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

func TestListenGivesTheSocketDirectoryAnOwnerOnlyDACL(t *testing.T) {
	t.Parallel()

	dir := socketDir(t)

	lis, _, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		t.Fatalf("token user: %v", err)
	}

	sd, err := windows.GetNamedSecurityInfo(dir, windows.SE_FILE_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatalf("read the DACL: %v", err)
	}

	// SDDL writes a well-known account as its alias (the built-in Administrator, RID 500, is LA), so the user is
	// rendered the same way Windows renders it.
	rendered, err := windows.SecurityDescriptorFromString("D:(A;;FA;;;" + user.User.Sid.String() + ")")
	if err != nil {
		t.Fatalf("render the user SID: %v", err)
	}

	account := strings.TrimSuffix(strings.TrimPrefix(rendered.String(), "D:(A;;FA;;;"), ")")

	// Protected (P: nothing inherited from %TEMP%), and one ACE: full access for this user, inherited by the
	// socket file (OICI).
	want := regexp.MustCompile(`^D:P[A-Z]*\(A;OICI;FA;;;` + regexp.QuoteMeta(account) + `\)$`)
	if got := sd.String(); !want.MatchString(got) {
		t.Fatalf("DACL = %s, want %s", got, want)
	}
}
