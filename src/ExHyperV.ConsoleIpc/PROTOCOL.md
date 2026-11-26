# ExHyperV local console IPC v1

Desktop and Server discover the external backend on the Windows host. The service owns VM authorization, control arbitration and resource production. Server is a trusted cleartext distributor and enforces each WebSocket viewer's admission and revocation at its final send gate.

## Registration and authentication

An administrator provisions the **64-bit** registry key `HKLM\SOFTWARE\ExHyperV\ConsoleExtensions\External`. Grant consumers read access. Only trusted administrators and the installer may change the key, its ancestors, service configuration, executable or installation directory. Consumers open the key read-only on every new connection. An absent key or stopped service gives host 2, authorization 1 and diagnostic `EXT-001`.

| Value | Type | Meaning |
| --- | --- | --- |
| PipeName | REG_SZ | Bare local name or `\\.\pipe\` followed by the name. Use 1 to 240 ASCII letters, digits, underscores, hyphens or periods. Two consecutive periods and the reserved name anonymous are invalid. |
| ServiceName | REG_SZ | SCM service name, optionally prefixed with `NT SERVICE\`. Resolved to the service SID. |
| ServiceSid | REG_SZ | Explicit Windows SID starting with `S-1-5-`. At least one identity field is required. When both are present they must identify the same SID. |
| SignerThumbprint | REG_SZ | Exactly 64 hex digits encoding the SHA-256 fingerprint of the expected Authenticode leaf certificate. |
| AllowProtectedSystemPathOnly | REG_DWORD | Default 1. Require the resolved executable beneath the native Program Files or Windows System32 directory. An administrator can set 0 for a separately ACL-protected installation. SID and signature checks always apply. |

Invalid values produce host 3, authorization 1 and `EXT-TRUST-01` before opening the endpoint. Authentication failure produces host 3, authorization 1 and `EXT-TRUST-02`. Diagnostics contain no paths, account names or certificate data.

The service creates a byte-mode duplex named pipe with an explicit DACL, first-instance protection, remote clients rejected, bounded simultaneous instances and handshake deadlines. Maintain a listening instance while publishing. Grant approved clients read data, write data, read attributes and synchronize. Reserve create-instance rights to the service. The client uses overlapped CreateFileW with identification SQOS. For each connected handle it obtains the server PID, opens the process with limited query access, and checks its creation time against the pipe-open time. It checks the token user or an enabled service SID group, pins the executable file for reading, resolves its final path and runs WinVerifyTrust. It compares the SHA-256 of the first leaf from the verified signer chain and rechecks the process instance. Verification precedes Hello on both channels.

The service obtains the client PID from the connected pipe and fixes the target process instance before admitting transfers. Authenticate its Windows token and login or service identity, check the consumer role and authorize every VM, operation and delivery domain. Identification-level impersonation permits inspection of the client identity. Revert on the same thread even on error. The role, viewer subject, requested VM, context string and numeric handles are never proof of identity. Disconnect when either identity check fails. Repeat authentication after reconnection.

If using ImpersonateNamedPipeClient, read the bounded Hello first to establish the last-message security context, then verify the client before HelloAck. Open the destination process for handle duplication only on the authenticated service side. Installations should use a SID specific to their service. [Windows named pipe access rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)

## Framing

All integers are little-endian. There is no alignment or padding in messages. The fixed header is 32 bytes. Every message has an additional mandatory 8-byte flag prefix immediately following the header. The first type-specific field starts at offset 40.

| Offset | Field | Value or encoding |
| --- | --- | --- |
| 0 | Magic | u32 0x43485845, bytes 45 58 48 43 |
| 4 | Major, Minor | u16 each, writers send 1 and 0 |
| 8 | HeaderSize, MessageType | u16 each, HeaderSize is 32 |
| 12 | TotalLength | u32 including header and flags, range 40 through 16777216 |
| 16 | RequestId | u64 positive for a request or response, zero for a notification |
| 24 | SessionId | u64 zero on the control channel and before session assignment |
| 32 | RequiredFlags, OptionalFlags | u32 each, writers send zero |
| 40 | Body | Type-specific fields followed by any optional trailing data |

Read exact lengths across arbitrary pipe boundaries. Reject wrong magic, header size, major, unknown required bits or invalid lengths and close the channel. Version 1.0 is the minimum. A higher minor is acceptable with an understood mandatory layout. Ignore unknown optional bits and skip trailing optional bytes within TotalLength. Concurrent requests may complete out of order. A response uses the original request and session IDs and the request type with bit 15 set. A correlated error uses type 65535 and body `code:u32`. A truncated record, unrecognized push, wrong session or unsolicited response terminates the channel.

In the tables, unmarked integers are i32. A flag is one byte, 0 or 1. A GUID is the 16 bytes produced by Windows GUID memory layout or Guid.ToByteArray. Text is a signed i32 UTF-8 byte count and exactly that many bytes. A count of -1 encodes null, 0 encodes empty, maximum 4096. Binary blobs are an i32 count and bytes. Lists begin with an i32 count. Opaque diagnostic fields allow null or 1 to 32 ASCII alphanumeric characters or hyphens. Public `reason` fields allow null or up to 512 UTF-8 characters of localized, sanitized status text without file paths, accounts, tokens, control/bidi characters, semicolons, or middle dots. A target is `VmId:guid, ViewerSubject:text, IsDesktop:flag`. An empty VM requests host-only status. A mode is `id:text, width, height, refreshNumerator:u32, refreshDenominator:u32`. A control state is `CanView:flag, HasControl:flag, Generation:u64`.

## Control channel

| ID | Request or push | Body | Response body |
| --- | --- | --- | --- |
| 1 | Hello | role:i32 (1 Desktop, 2 Server), dedicatedSession:flag, offeredFeatures:u64 | ServiceInstanceId:guid, selectedFeatures:u64 |
| 2 | InspectSnapshot | target | snapshot specified below |
| 3 | Reserved | — | — |
| 4 | GetModes | target, snapshotVersion:u64 | list of modes |
| 5 | AllowsFallback | target, targetBackend:text | allowed:flag |
| 6 | SnapshotChanged | vm:guid, subject:nullable text | push |

HelloAck is 0x8001. Generate a nonempty service instance ID at process startup. Feature bit 0 is the required CPU ring, bit 1 is keyed-mutex NT texture delivery, and bits 2 through 63 are reserved for future extension capabilities. Select a subset of the offered bits. Session and control connections must agree on the service instance. Keep the verified control connection open for concurrent queries and invalidations. ExHyperV is a read-only consumer and never triggers host activation, host maintenance, or guest component deployment over IPC; the extension's background Windows service should run automatically so status and activation state are always queryable, while heavy capture or device kernel components may start on demand after authorization.

A Desktop control Hello with request ID 1 and both transports offered has these 53 bytes.

```text
45 58 48 43 01 00 00 00 20 00 01 00 35 00 00 00
01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 01 00 00 00 00 03 00 00
00 00 00 00 00
```

Snapshot fields in order are version:u64, Host, Authorization, Guest, Transport, transportKind, surfaceEpoch:u64, supported:u64, granted:u64, control state, list of modes (max 256), guestSession:text, isolationDomain:text, reason:text, diagnostic:text. Use the Core numeric enums. Stage values 0 through 6 mean unknown, unverified, unreachable, access denied, missing, incompatible and ready. Low capability bits 0 through 5 mean video, input, clipboard, discrete resize, audio output and audio input, with bits 6 through 63 reserved for future extension modules. Transport 1 means CPU and 2 means GPU. Increment snapshot version on changes to any effective grant, scope, mode, status reason or capability. Publish invalidation for the VM before allowing continued delivery under changed rights. A zero VM invalidates all watched VMs.

For SnapshotChanged, a non-null subject invalidates that viewer's grant. Null or an omitted trailing subject invalidates every subject in the selected VM scope. On Server the final viewer send gate cuts off that subscription. A shared source remains bound to the trusted Server, guest session and isolation domain. Send ControlChanged or SessionDisconnected on its dedicated pipe to withdraw the source grant itself.

An empty VM GUID requests host-only status. Permit fallback only after confirming equivalent rights and the same guest session.

## Dedicated session

Open another instance of the same registered pipe and authenticate again. Hello uses dedicatedSession=1. Each open connection owns a separate stream of requests, events and resource IDs. Successful responses have empty bodies unless a layout is given.

| ID | Operation | Body or result |
| --- | --- | --- |
| 16 | OpenSession | vm:guid, width, height, desktopScale:u32, connectTimeoutTicks:i64, hostCursor:flag, clipboard:flag, audioOutput:flag, audioInput:flag, viewerContext:text, viewerSubject:text, snapshotVersion:u64, guestSession:text, isolationDomain:text. Result is assignedSession:u64, nonzero. |
| 17 | StartSession | Empty. Use assignedSession in all subsequent envelopes including this request. |
| 18 | CloseSession | Empty. Acknowledge after stopping writes and releasing session resources. |
| 19, 20 | SessionConnected, SessionLoginCompleted | Empty pushes. |
| 21 | SessionDisconnected | code:u32, reason:text. Terminal push. |
| 22 | ControlChanged | Control state push. Increase generation at every control handoff. |
| 23 | SetMode | generation:u64, id:text. Result is accepted:flag, hasMode:flag, followed by one mode when present. |
| 24 | CursorShape | present:flag, then width, height, hotX, hotY and exactly width × height × 4 BGRA bytes if present. Maximum 512 per dimension. |
| 25 | CursorState | x, y, visible:flag, composited:flag, coordinateSpace (1 desktop pixels), shapeVersion:u64. |
| 26 | ClipboardAvailability | providerId:text, vm:guid, guestSession:text, ready:flag, canRead:flag, canWrite:flag. |
| 27 | ClipboardData | list of up to 256 formats, each id:u32, name:text, data:blob. |
| 28 | SetClipboard | generation:u64 and same list, client notification. |
| 29 | AudioAvailability | inputDirection:flag, available:flag, followed by a format only for available input. |
| 30 | AudioOutput | format, sequence:i64, timestampTicks:i64, samples:blob. |
| 31 | AudioInput | generation:u64, format, samples:blob. Client notification. |
| 40 | InputKey | input prefix, scanCode:u16, extended:flag, down:flag. |
| 41 | InputKeys | input prefix, list of 1 through 1024 keys in the same layout. |
| 42 | InputPause | input prefix. |
| 43 | InputSyncLocks | input prefix, keyboard toggle mask:u32. |
| 44 | InputPointer | input prefix, x:u16, y:u16, button:i32, down:flag, wheel:i16. |
| 45 | ReleaseAllInputs | input prefix. |

Input prefix is generation:u64, sequence:u64. Input messages are client notifications. Reject stale generations and non-increasing input sequences, including delayed releases. Release keys held by the old owner at handoff or disconnection. Reject new input during close. Audio format is sampleRate, channelCount, sampleFormat (0 signed little-endian 16-bit PCM, 1 little-endian float32). Limit packets to 1 MiB of complete interleaved sample frames, 1 through 32 channels, rates up to 384000 Hz. Time fields in ticks count 100 ns intervals.

## Directed resources

The trusted sender uses DuplicateHandle on the process fixed to the verified pipe connection. Disable inheritance and restrict access. Never ask a client to open arbitrary named objects or to supply a destination PID. Raw handles are positive zero-extended Windows handle values in u64 fields. The first two fields of each attach are transferId:u64 and rawHandle:u64. Transfer IDs start positive and strictly increase across **all** new transfers on a session. A retry must repeat the identical ID and already-transferred value in stream order. Process a new ID exactly once and immediately take ownership. A repeated ID must never close a recycled handle. TransferAck acknowledges receipt only. The service must bound outstanding transfers and stop transferring after an uncertain write. It may retransmit an identical notification on the same live stream. Never remotely close a reported client handle. Process exit closes outstanding destination handles.

| ID | Operation | Body |
| --- | --- | --- |
| 64 | CpuRingAttach | transfer prefix, epoch:u64, mappingBytes, slotCount, slotStride. |
| 65 | CpuFrameReady | epoch:u64, slot, frameSequence:i64. |
| 66 | FrameConsumedAck | epoch:u64, slot, frameSequence:i64. Client has finished its read attempt. |
| 67 | GpuSurfaceAttach | transfer prefix, deliveryId:u64, epoch:u64, slot, adapterLuid:i64, width, height. |
| 68 | GpuFrameReady | deliveryId:u64, epoch:u64, slot, sequence:i64, readKey:u64, returnKey:u64. |
| 69 | RequestCpuPath | deliveryId:u64. Response gives newEpoch:u64 strictly greater than the retired GPU epoch. |
| 70, 71 | OpenDelivery, CloseDelivery | deliveryId:u64, client notifications scoped to this session. |
| 72 | TransferAck | transferId:u64, client notification. |

The CPU stream serves legacy frame consumers of the same authorized session. The service supplies complete snapshots for CPU subscribers and starts a new CPU ring epoch with a full frame on downgrade. OpenDelivery allocates a separate bounded GPU pool for that delivery. CloseDelivery releases its GPU subscription. A CPU downgrade continues on the session CPU stream even if the renderer subsequently closes the GPU subscription. Other GPU deliveries and sessions continue independently. Fence transport is reserved for future negotiation.

### CPU ring

Duplicate a section with FILE_MAP_READ. MappingBytes must equal slotCount × slotStride, max 256 MiB, 1 through 64 slots. Stride is divisible by 8. Slot-relative offsets are in bytes and divisible by 4. Pixels and nonempty arrays must start after the 72-byte header and fit entirely within the slot. Zero-length arrays may have offset zero.

| Slot offset | Encoding |
| --- | --- |
| 0, 8, 16, 24 | commitSequence:i64, epoch:u64, frameSequence:i64, baseSequence:i64 |
| 32, 36, 40, 44 | updateKind, width, height, BGRA stride in bytes |
| 48, 52 | pixelOffset, pixelLength |
| 56, 60, 64, 68 | dirtyCount, moveCount, dirtyOffset, moveOffset |

A dirty record is x, y, width, height (16 bytes). A move is sourceX, sourceY and a destination dirty record (24 bytes). At most 4096 entries per array. All rectangles and move sources must be inside the desktop. Width and height are 1 through 8192, pixelLength equals stride × height and is at most 64 MiB. Stride is divisible by 4 and at least width × 4. Each slot contains the **entire** desktop, including on delta or unchanged updates. Kind 2 is full with exactly one full-desktop dirty rectangle and zero moves, 1 is delta with at least one region, 0 is unchanged with no regions. Base is nonnegative and less than sequence, zero only for a full update.

Publish an odd aligned atomic commit value before modifying any slot bytes, order subsequent writes after that mark, then release-publish a new positive even value when complete. Never reuse a commit value within the epoch. Take an acquire read, copy the header, validate ranges with widened arithmetic, copy regions and pixels into private storage, issue a read barrier and verify the same commit and epoch. Discard a torn or malformed frame. Bounded retries wait for the next notification. Consumers can retain their own snapshots across overwrites, disconnect or mode changes. FrameConsumedAck does not certify GPU completion.

### GPU textures and teardown

Export a BGRA8 single-sample, single-mip, single-array D3D11 texture using SHARED_NTHANDLE and SHARED_KEYEDMUTEX and a read-only DXGI shared handle. Keep the capture pool private. Allocate resources per authorized delivery. Index slots 0 through 63 once per epoch and keep their descriptors stable until retirement. The consumer duplicates the cached slot handle locally for each frame lease. It matches the adapter LUID, opens via OpenSharedResource1 and reads only after successful IDXGIKeyedMutex AcquireSync on readKey. ReleaseSync on returnKey follows submitted reads. The service must obtain valid synchronization evidence before reuse, bound in-flight slots and isolate slow or failed consumers. The desktop waits up to 16 ms, the Server encoder up to 10 ms. Selection, open, synchronization or encoder failure can request a CPU path on this delivery only.

Stop writing any resource accessible to a revoked principal before confirming revocation. Retire its epoch and rotate resources for remaining authorized consumers. Old handles may stay open until their holders exit. Enforce quotas across retired and active pools. On CloseSession stop accepting input, release held keys, stop new delivery and drain in-flight work. Acknowledge close before disconnecting. Clients bound the wait, cancel pending I/O, dispose local views, slot handles and the pipe. Reconnection creates a new session and trust context.

| Error | Meaning |
| --- | --- |
| 0xEC000001 BackendUnavailable | Cannot reach the backend or the service disconnected unexpectedly. |
| 0xEC000002 ProtocolIncompatible | Incompatible version, required feature or framing. |
| 0xEC000003 ViewDenied | Authentication failed or view authority was withdrawn. |

Only an independently authorized fallback may follow unavailability or incompatibility. ViewDenied ends the subscription.
