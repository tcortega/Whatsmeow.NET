package server

import (
	"slices"
	"time"

	"google.golang.org/protobuf/types/known/durationpb"
)

// DisappearingTimer is a request's disappearing messages timer, or INVALID_ARGUMENT for the request field field: one
// WhatsApp's phones offer, 24 hours, 7 days or 90 days, or zero for off (whatsmeow send.go:636-657). Whether WhatsApp
// accepts any other is [unknown], so none is sent (group.proto, message.proto).
func DisappearingTimer(field string, timer *durationpb.Duration) (time.Duration, error) {
	if timer == nil || !timer.IsValid() {
		return 0, InvalidArgument(field, "unset or not a valid duration")
	}

	d := timer.AsDuration()
	if !slices.Contains([]time.Duration{0, 24 * time.Hour, 7 * 24 * time.Hour, 90 * 24 * time.Hour}, d) {
		return 0, InvalidArgument(field, "not 24 hours, 7 days, 90 days or zero")
	}

	return d, nil
}
