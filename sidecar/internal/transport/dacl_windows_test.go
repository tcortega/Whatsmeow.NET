package transport

import (
	"context"
	"regexp"
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

	// Protected (P: nothing inherited from %TEMP%), and one ACE: full access for this user, inherited by the
	// socket file (OICI).
	want := regexp.MustCompile(`^D:P[A-Z]*\(A;OICI;FA;;;` + regexp.QuoteMeta(user.User.Sid.String()) + `\)$`)
	if got := sd.String(); !want.MatchString(got) {
		t.Fatalf("DACL = %s, want %s", got, want)
	}
}
