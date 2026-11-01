package redcheck

import "testing"

func TestRace(t *testing.T) {
	t.Parallel()

	if Race() == 0 {
		t.Fatal("counter did not move")
	}
}
