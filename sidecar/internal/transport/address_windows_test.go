package transport

// unixPaths returns paths `--listen unix:<path>` accepts and ones it refuses on Windows: a path needs a drive or a UNC
// root, in either slash; a rooted path with no drive, or a drive with no root, is relative to the current drive or
// directory there (issue #123).
func unixPaths() (absolute, notAbsolute []string) {
	return []string{`C:\x\s`, `C:/x/s`, `\\?\C:\x\s`, `\\server\share\s`}, []string{`relative\s`, `/abs/s`, `\abs\s`, `C:x\s`}
}
