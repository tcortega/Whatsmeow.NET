// Package redcheck is a THROWAWAY red check for #2; this branch is deleted after the run.
package redcheck

import "reflect"

var counter int

// Name uses runtime reflection.
func Name(v any) string {
	return reflect.TypeOf(v).Name()
}

// Race increments counter from two goroutines without synchronization.
func Race() int {
	done := make(chan struct{})

	go func() {
		counter++

		close(done)
	}()

	counter++

	<-done

	return counter
}
