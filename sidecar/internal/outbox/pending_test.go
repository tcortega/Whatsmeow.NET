package outbox

import (
	"testing"
)

func TestPendingCountsTheKeysOfAPrefixUntilEveryConsumerAckedThem(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1", "s2")

	record(ctx, t, o, "s1", "h\x00N1\x000", message("H1"))
	record(ctx, t, o, "s1", "m\x00chat\x00sender\x00M1", message("M1"))
	record(ctx, t, o, "s1", "h\x00N1\x00p", message("H2"))
	record(ctx, t, o, "s2", "h\x00N9\x000", message("H9"))
	record(ctx, t, o, "s1", "", state())

	pending := func(want int) {
		t.Helper()

		got, err := o.Pending(ctx, "s1", "h\x00")
		if err != nil || got != want {
			t.Fatalf("Pending %d, %v; want %d", got, err, want)
		}
	}

	// No consumer: a session keeps everything, so all of it is pending.
	pending(2)

	all := seqs(ctx, t, o, "s1")

	if _, err := o.Cursor(ctx, "s1", "a"); err != nil {
		t.Fatalf("Cursor: %v", err)
	}

	if err := o.Ack(ctx, "s1", "b", all[0]); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	// Consumer a acked nothing.
	pending(2)

	if err := o.Ack(ctx, "s1", "a", all[2]); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	// Both acked the first; b has not acked the progress row.
	pending(1)

	if err := o.Ack(ctx, "s1", "b", all[3]); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	pending(0)
}

func TestAnAckWakesTheSessionsAckedWatch(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1", "s2")
	record(ctx, t, o, "s1", "", state())

	acked, other := o.Acked("s1"), o.Acked("s2")

	if err := o.Ack(ctx, "s1", "a", 0); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	select {
	case <-acked:
	case <-ctx.Done():
		t.Fatal("an ack did not wake its session's watch")
	}

	select {
	case <-other:
		t.Fatal("an ack woke another session's watch")
	default:
	}

	// The next ack wakes a watch taken after this one.
	next := o.Acked("s1")

	select {
	case <-next:
		t.Fatal("a watch taken after the ack is already woken")
	default:
	}
}
