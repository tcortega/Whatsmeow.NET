# Natural undecryptable recovery, 2026-11-05 (#72)

Two messages that session `alice` could not decrypt later reached its `MessageReceived` handler with the original `MessageId` [observed: the warning and `EchoBot.EchoHandler` records below].
This is the natural occurrence allowed by [#72](https://github.com/tcortega/Whatsmeow.NET/issues/72), during the hosted sample's new device pairing run [observed: pairing at raw log lines 164 and 191, failures at 188, 194, 196 and 218].

## Receipt

The private input is `/tmp/whatsmeow-issues/42-live-20261105/echobot.log`, 276 lines, SHA256 `a7b06f572c2894b88d39f24ea34227dc34bb393a82b5a9e8d975a8ac8f6e519d`.
The table retains UTC log timestamps and line numbers, replaces every message ID with SHA256 of its exact UTF-8 bytes without a trailing newline, and omits contact identifiers and message content.
Every failure and matching handler record names session `alice` [observed: the same log records].

| Message | SHA256 of original message ID | Decryption failure | Later successful handler log |
| --- | --- | --- | --- |
| A | `a60a5d92f0621877f05d24e9175f0ac086eb2e37cba5594c4bd380b4fd22985a` | `2026-11-05T19:54:18.559Z` (line 188) | `2026-11-05T19:54:20.302Z` (line 189), `2026-11-05T19:54:20.305Z` (line 190) |
| B | `5c338ba53add0e35344abb4759f8be969c93110e6139d98d0400d65364a5023c` | `2026-11-05T19:54:27.253Z` (line 194) | No matching `Echoed` record |
| C | `853c28ae1e2e09c6a75a5f7e54accf633a6784ceaa5d66b7f8f7ce7bd2f3fb2c` | `2026-11-05T19:54:28.332Z` (line 196) | No matching `Echoed` record |
| D | `e51ecabdb0c881152e8a1cfcc46432f032b6062f182ab3657862db318127c8d1` | `2026-11-05T19:54:39.944Z` (line 218) | `2026-11-05T19:54:44.233Z` (line 227), `2026-11-05T19:54:44.234Z` (line 228) |

Every failure says `failed to decrypt group message: no sender key` [observed: lines 188, 194, 196 and 218].
Messages A and D each have two later `Echoed` records with exactly the failed ID [observed: lines 189/190 and 227/228, compared against the raw ID before hashing].
The first matching log follows the warning by 1.743 s for A and 4.289 s for D [observed: subtraction of the UTC log timestamps].
These intervals include the handler's outgoing send; they do not measure decryption time [observed: `EchoHandler.HandleAsync`, below].
Messages B and C have no matching `Echoed` record in this input; their recovery is [unknown].

## What the records prove

The run's `build-provenance.txt` records product commit `70f1cf34fcf1cf3d3c643644ba66358b96cb20d8` for EchoBot, Whatsmeow.Client and the darwin/arm64 sidecar, with the sidecar's `vcs.modified=false` [observed: that provenance file].
That commit pins `go.mau.fi/whatsmeow v0.0.0-20261030195514-56e5ad0581ce` [observed: [`sidecar/go.mod`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/sidecar/go.mod#L11)].

`EchoHandler` implements `IWhatsAppEventHandler<MessageReceived>` and logs `e.Id.Value` after awaiting `SendTextAsync`; its log contains the received ID, not the generated echo ID [observed: [`EchoHandler.cs:11-25`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/dotnet/samples/EchoBot/EchoHandler.cs#L11)].
The source registration binds `MessageReceived` to that handler [observed: [`EchoBotServices.cs:26`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/dotnet/samples/EchoBot/EchoBotServices.cs#L26)].
The sidecar maps `events.Message.Info.ID` into `MessageInfo.Id`, and the client maps that field into `MessageReceived.Id` [observed: [`mapping.go:28-41,201-208`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/sidecar/internal/events/mapping.go#L28), [`EventMapping.cs:148-155`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/dotnet/src/Whatsmeow.Client/Events/EventMapping.cs#L148)].

The raw module cache at the exact whatsmeow pin was read again for these distinctions:

* The warning logs `info.ID` on a decryption error; for a group sender key failure, the following branch requests a retry and dispatches `UndecryptableMessage` unless the context was canceled [observed: [`message.go:414-437`](https://github.com/tulir/whatsmeow/blob/56e5ad0581ce/message.go#L414)].
* With `SynchronousAck`, the first retry also requests the message from the account's phone [observed: [`retry.go:489-497`](https://github.com/tulir/whatsmeow/blob/56e5ad0581ce/retry.go#L489); both settings are enabled in the product's [`whatsmeow.go:24-30`](https://github.com/tcortega/Whatsmeow.NET/blob/70f1cf34fcf1cf3d3c643644ba66358b96cb20d8/sidecar/internal/session/whatsmeow.go#L24)].
* A whatsmeow sender's retry uses the original message ID, while a phone response is parsed from its message key and dispatched [observed: [`retry.go:368-373`](https://github.com/tulir/whatsmeow/blob/56e5ad0581ce/retry.go#L368), [`message.go:863-881`](https://github.com/tulir/whatsmeow/blob/56e5ad0581ce/message.go#L863), [`client.go:1033-1041`](https://github.com/tulir/whatsmeow/blob/56e5ad0581ce/client.go#L1033)].

Automatic retry recovery explains the sequence [inferred: the pinned branches and the later matching handler records].
Which recovery came from the sender or the phone, and why each ID produced two handler logs, remain [unknown]; these logs do not distinguish those paths.
The sample registers no `UndecryptableMessageReceived` handler [observed: `EchoBotServices.cs:21-28`], so this receipt does not claim an instrumented undecryptable event or its dispatch count [unknown].
The observation covers two recovered messages and does not establish universal ID preservation or change the duplicate rule.
No changed recovered ID was observed, so this evidence calls for no ADR or weaker test.

## Method boundary

This follow-up only read existing logs, build provenance and source; it ran no live experiment and accessed no session database or secrets.
The earlier rejected Signal state experiments supplied none of this evidence and were not retried, rephrased or replaced with another provocation.
The natural failures were already recorded during the separately authorized sample run before this passive analysis.
