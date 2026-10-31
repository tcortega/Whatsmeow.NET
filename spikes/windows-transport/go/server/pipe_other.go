//go:build !windows

package main

import (
	"errors"
	"net"
)

func listenPipe() (net.Listener, string, error) { return nil, "", errors.New("named pipes are Windows only") }
