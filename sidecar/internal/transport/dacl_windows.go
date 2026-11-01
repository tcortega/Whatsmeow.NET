package transport

import (
	"fmt"

	"golang.org/x/sys/windows"
)

// ownerOnly replaces the directory's DACL with one protected entry: full access for the current user,
// inherited by the socket file. os.Chmod sets no ACL on Windows (ARCHITECTURE §10).
func ownerOnly(dir string) error {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return fmt.Errorf("read the process user: %w", err)
	}

	sd, err := windows.SecurityDescriptorFromString("D:P(A;OICI;FA;;;" + user.User.Sid.String() + ")")
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
