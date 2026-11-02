//go:build !windows

package media

import "syscall"

// openNonBlocking opens a FIFO without waiting for a writer (open(2) O_NONBLOCK).
const openNonBlocking = syscall.O_NONBLOCK
