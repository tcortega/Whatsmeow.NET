package main

import (
	"flag"

	"go.mau.fi/whatsmeow/proto/waCompanionReg"
	"google.golang.org/protobuf/proto"
)

// history is the process-wide history options (ARCHITECTURE §7.2, ADR 0035): what the device asks the phone for when
// it is paired. whatsmeow sends store.DeviceProps only in the registration payload (store/clientpayload.go:174-183),
// so they apply to the sessions paired while the sidecar runs with them, never to one paired before. A flag not given
// leaves whatsmeow's default; what WhatsApp makes of each is [unknown] (RESEARCH item 16, #42).
type history struct {
	fullSync                        *bool
	daysLimit, sizeLimit, quotaSize *uint
}

// historyFlags declares the history flags on flags.
func historyFlags(flags *flag.FlagSet) history {
	return history{
		fullSync:  flags.Bool("history-full-sync", false, "ask the phone for the full history, not only the recent (RequireFullSync), for sessions paired from now on"),
		daysLimit: flags.Uint("history-days-limit", 0, "the full history's age limit in days (FullSyncDaysLimit); 0: whatsmeow's default"),
		sizeLimit: flags.Uint("history-size-limit-mb", 0, "the full history's size limit in MB (FullSyncSizeMbLimit); 0: whatsmeow's default"),
		quotaSize: flags.Uint("history-storage-quota-mb", 0, "the device's storage quota for history in MB (StorageQuotaMb); 0: whatsmeow's default"),
	}
}

// apply sets the flags given on props, once at startup, before any session pairs.
func (h history) apply(props *waCompanionReg.DeviceProps) {
	if *h.fullSync {
		props.RequireFullSync = proto.Bool(true)
	}

	if props.HistorySyncConfig == nil {
		props.HistorySyncConfig = &waCompanionReg.DeviceProps_HistorySyncConfig{}
	}

	for _, f := range []struct {
		value *uint
		field **uint32
	}{
		{h.daysLimit, &props.HistorySyncConfig.FullSyncDaysLimit},
		{h.sizeLimit, &props.HistorySyncConfig.FullSyncSizeMbLimit},
		{h.quotaSize, &props.HistorySyncConfig.StorageQuotaMb},
	} {
		if *f.value > 0 {
			*f.field = proto.Uint32(uint32(min(*f.value, 1<<32-1)))
		}
	}
}
