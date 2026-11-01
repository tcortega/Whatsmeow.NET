//go:build !windows

package transport

import (
	"errors"
	"net"
)

func listenPipe() (net.Listener, string, error) {
	return nil, "", errors.New("the named pipe transport is Windows only")
}
