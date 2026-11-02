package main

import (
	"flag"
	"io"
	"testing"

	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	wastore "go.mau.fi/whatsmeow/store"
	"google.golang.org/protobuf/proto"
)

func TestTheHistoryFlagsSetOnlyTheirOwnDevicePropsFields(t *testing.T) {
	t.Parallel()

	for _, c := range []struct {
		args []string
		set  func(*waCompanionReg.DeviceProps)
	}{
		{nil, func(*waCompanionReg.DeviceProps) {}},
		{[]string{"--history-full-sync"}, func(p *waCompanionReg.DeviceProps) { p.RequireFullSync = proto.Bool(true) }},
		{[]string{"--history-days-limit", "30"}, func(p *waCompanionReg.DeviceProps) { p.HistorySyncConfig.FullSyncDaysLimit = proto.Uint32(30) }},
		{[]string{"--history-size-limit-mb", "512"}, func(p *waCompanionReg.DeviceProps) { p.HistorySyncConfig.FullSyncSizeMbLimit = proto.Uint32(512) }},
		{[]string{"--history-storage-quota-mb", "2048"}, func(p *waCompanionReg.DeviceProps) { p.HistorySyncConfig.StorageQuotaMb = proto.Uint32(2048) }},
	} {
		flags := flag.NewFlagSet("whatsmeow-net", flag.ContinueOnError)
		flags.SetOutput(io.Discard)
		history := historyFlags(flags)

		if err := flags.Parse(c.args); err != nil {
			t.Fatalf("%v: %v", c.args, err)
		}

		// A copy of whatsmeow's defaults: the real ones are process-wide (store/clientpayload.go:124-156).
		got := proto.CloneOf(wastore.DeviceProps)
		history.apply(got)

		want := proto.CloneOf(wastore.DeviceProps)
		c.set(want)

		if !proto.Equal(got, want) {
			t.Errorf("%v: %v, want %v", c.args, got, want)
		}
	}
}
