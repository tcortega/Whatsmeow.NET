package transport

import (
	"fmt"

	"golang.org/x/sys/windows"
)

// userSID is the current user. ACLs name it rather than OW (owner rights): on an elevated administrator the
// owner of a new object is BUILTIN\Administrators, not the user (ARCHITECTURE §10).
func userSID() (string, error) {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return "", fmt.Errorf("read the process user: %w", err)
	}

	return user.User.Sid.String(), nil
}

// ownerOnly replaces the directory's DACL with one protected entry: full access for the current user,
// inherited by the socket file. os.Chmod sets no ACL on Windows (ARCHITECTURE §10).
func ownerOnly(dir string) error {
	sid, err := userSID()
	if err != nil {
		return err
	}

	sd, err := windows.SecurityDescriptorFromString("D:P(A;OICI;FA;;;" + sid + ")")
	if err != nil {
		return fmt.Errorf("build the security descriptor: %w", err)
	}

	dacl, _, err := sd.DACL()
	if err != nil {
		return fmt.Errorf("read the DACL: %w", err)
	}

	err = windows.SetNamedSecurityInfo(dir, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil)
	if err != nil {
		return fmt.Errorf("set the DACL: %w", err)
	}

	return nil
}
