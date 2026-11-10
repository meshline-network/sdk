# SDK portable backup format v1

This v1 draft defines the Meshline SDK's portable backup format. It requires no
network protocol changes or relay, contract, or cloud-backup services. Use the
[C# recovery guide](../dotnet/docs/guides/recovery-and-migration.md) or
[TypeScript recovery guide](../typescript/docs/guides/recovery-and-migration.md) for application
workflows, component stream ownership and notifications.

## Ownership and scope

A file belongs to exactly one network context and account. The importer checks
both values against the independently initialized target database before applying
data records. A local device is required when protecting or unprotecting group
secrets; other records can be processed before device creation.

Contacts (including deletions and grants), group references and management history,
held group secrets, nickname display state, device verification material, and
channel entries/follow choices are always exported. Direct and group chat history
are independently optional and **excluded by default**.

Wallet, account and device private keys, local protector keys, profile data, read
positions, send/retry queues, pending contact requests and group rotations,
sessions, push settings, local paths and account timeline cursors are excluded.
Attachments remain references; attachment bytes and channel posts are excluded.

The caller supplies a password; the SDK generates salts and derives encryption
keys. The public header contains derivation parameters, never the password or key.

Each component exports one consistent snapshot established when its stream is
read. Components can represent different instants; a group's references, events,
nicknames and keys remain within the same group snapshot.

After reading every component, and before writing `end` or completing encryption,
the exporter rechecks the device states needed by the exported owner and active
contacts. Changed or removed states, and available states omitted from the device
snapshot, fail export. States absent from both snapshots remain absent. This check
uses local data and permits unrelated changes and historical authorization that
is no longer valid. Callers discard incomplete output and retry with a fresh file.

## Binary container

The container fixes one algorithm combination: length-prefixed JSON records,
continuous gzip compression, scrypt password derivation, and Tink
AES-256-GCM-HKDF Streaming encryption. Files contain:

1. Eight ASCII bytes `MLBK0001` (format and version).
2. A positive 32-bit big-endian header length, then exactly that many UTF-8 JSON
   bytes (maximum 65,536 bytes).
3. One Tink encrypted stream, extending to physical EOF.

The public header has exactly this structure (the salt below is an example):

```json
{"key_derivation":{"algorithm":"scrypt","salt":"AAECAwQFBgcICQoLDA0ODw==","n":65536,"r":8,"p":2}}
```

All fields are required and non-null; unknown and duplicate fields are rejected.
`salt` is a fresh, cryptographically random 16-byte password salt, encoded as
canonical standard padded Base64. It is separate from the 32-byte salt in the
Tink stream header. This header is **unauthenticated until an encrypted segment authenticates it**; the SDK validates the fixed profile before running the KDF.

The concatenation of the magic, four header-length bytes, and **exact original
header bytes** is the Tink AssociatedData. Do not reserialize the header before
authentication. Unknown versions are rejected rather than guessed or retried with
another algorithm.

### Password derivation

The input must be a nonempty valid Unicode string. Normalize it to NFC and encode
it as strict UTF-8, without a BOM. Do not trim whitespace, change case, discard
embedded NUL characters, truncate, or replace invalid Unicode. The C# API rejects
null, empty strings and unpaired UTF-16 surrogates. Strength guidance belongs to
the client; whitespace is part of the password.

Apply [RFC 7914 scrypt](https://www.rfc-editor.org/rfc/rfc7914.html) to those bytes
and the decoded password salt, with `N=65536`, `r=8`, `p=2`, and `dkLen=32`.
These values are fixed for v1. Reject any different algorithm, work factor, salt
length or noncanonical salt encoding **before** allocating KDF working memory;
never execute arbitrary parameters supplied by a file. This profile uses about
64 MiB of main working memory and is among the scrypt CPU/memory tradeoffs listed
in the [OWASP password storage guidance](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#scrypt).
It does not reuse the wallet's NEP-6 salt or work factors.

Wrong passwords fail authentication before any data record can commit. Damage in
a later segment stops import and preserves records committed from earlier,
authenticated segments; see [merge and failure semantics](#merge-and-failure-semantics).

### Authenticated stream

The body follows the public
[Tink AES-GCM-HKDF Streaming definition](https://developers.google.com/tink/streaming-aead/aes_gcm_hkdf_streaming)
with these fixed parameters:

| Parameter | Value |
| --- | --- |
| `KeyValue` | The 32-byte scrypt result |
| `HkdfHashType` | SHA256 |
| `DerivedKeySize` | 32 bytes (AES-256) |
| `CiphertextSegmentSize` | 1,048,576 bytes |
| Output prefix / first-segment offset | RAW / zero, relative to the Tink body |

The 40-byte Tink header is `0x28 || salt[32] || nonce_prefix[7]`. Both salt and nonce
prefix are cryptographically random and freshly generated for every export,
including repeated exports using the same password.

HKDF-SHA256 derives a 32-byte file key using the scrypt result as IKM, the Tink salt,
and the entire Meshline preamble as `info`. The per-segment AES-GCM associated data
is **empty**, as required by Tink; no custom per-segment AAD is added.

For zero-based segment index `i`, the 12-byte nonce is
`nonce_prefix[7] || uint32_big_endian(i) || final[1]`, where `final` is 1 only for
the final segment. Each segment ends with a 16-byte GCM tag. Non-final segments
are full. The first has at most 1,048,520 bytes of compressed plaintext, allowing
for the Tink header and tag; subsequent segments have at most 1,048,560 bytes.
The final segment may be full. There are no additional segment-length prefixes.
At most 2^32 segments are permitted, with no counter wrap. The enclosing Meshline
preamble does not count toward the Tink segment size or offset.

Readers authenticate each complete segment before releasing its plaintext to the
decompressor. They must distinguish a short read from EOF and verify the final
segment, including when the file ends exactly on a segment boundary. Records may
commit incrementally, but full success requires authentication through physical EOF.

### Compressed record stream

Exporters encode the authenticated plaintext as a complete
[RFC 1952 gzip stream](https://www.rfc-editor.org/rfc/rfc1952.html), using DEFLATE.
The whole logical record stream is compressed continuously. Records can share an
encryption segment or cross segment boundaries. Compression level is an exporter
choice, not an interoperability requirement; compressed output need not be byte
identical across platforms.

Exports omit original filenames, comments and timestamps from the gzip header.
Readers use a standard gzip decoder and propagate its decoding errors. No
additional gzip-specific checks are required for header length, member count,
trailers or compressed trailing bytes beyond those performed by that decoder.
Decoder tolerance for nonconforming gzip input may vary; exporters must still
produce a valid complete stream. The decoded bytes must satisfy all logical
record rules, including the end marker, count and absence of subsequent decoded
data. Even if the decoder stops early, the reader must consume and authenticate
the remaining encrypted stream through its final segment and physical EOF before
reporting success. All compression and encryption I/O is bounded and supports
cancellation; no complete plaintext backup is buffered or written to a temporary
file by the SDK.

A malformed length, malformed JSON, duplicate JSON property names, unknown record
kind or unknown container/data field stops import, retaining earlier commits. Protocol objects
retain their existing extension-field rules. JSON nesting is limited to 32 levels.

## Logical records

After decompression, each record is a positive four-byte big-endian length followed
by that many UTF-8 JSON bytes: `{"type":"<kind>","data":{...}}`. The length excludes
the four-byte prefix and is at most 4,194,304 bytes (4 MiB). This allows protocol
objects of up to 1 MiB to retain their metadata and JSON string escaping in backup
records. There is no relationship between this record length and the
1 MiB encryption segment boundary.
JSON property order and escaping are not significant; authentication always uses
the original bytes. The C# writer uses only JSON-required character escapes to avoid
inflating valid messages past the record limit. SDK-specific fields use snake_case. Integers are signed
64-bit values unless noted; implementations must preserve them without JavaScript
floating-point rounding. Byte arrays use standard padded Base64 in SDK records;
embedded protocol objects retain the protocol's Base64url representation. IDs
retain their protocol spelling. Optional values are null or omitted.

### History selections

The encrypted manifest contains two required selections, `direct_history` and
`group_history`. Each is an object with required `mode` (string) and `ids` (array
of strings) fields, with no other fields:

```json
{"direct_history":{"mode":"none","ids":[]},"group_history":{"mode":"only","ids":["grp_AAAAAAAAAAAAAAAAAAAAAA"]}}
```

- `none`: no chat history of that kind; `ids` must be empty. This is the SDK default
  for both properties, including when options are omitted.
- `all`: all locally available eligible history of that kind; `ids` must be empty.
- `only`: history for the listed peer account IDs (direct) or group IDs (group).
  Direct history includes both directions, using the other account as the
  conversation ID; a self-addressed message uses the owning account ID. Peers need
  not still be contacts. IDs are matched exactly. Duplicate IDs have no additional
  effect; an empty `only` selection is equivalent to `none`.

Unknown modes, missing/null fields, incorrect ID types, or invalid ID formats fail
the import. Exporters copy, deduplicate and ordinally sort explicit selections;
an empty selection is emitted as `none`. Valid IDs without local history produce
no history records. The complete framed manifest remains subject to the 4 MiB
record limit; C# checks it and the identifiers before writing any file bytes.

The selections do not filter contacts, group references, nickname display state,
epoch evidence, secrets, management/rotation events or channel entries. Group messages are
identified by their `GroupMessageEnvelope` payload, including undecrypted or
rejected envelopes, rather than by the stored `is_message` flag. Every direct
message and group message envelope must fall within the corresponding manifest
selection, even if a local conflict would otherwise cause that record to be skipped.
An out-of-scope record stops import, retaining earlier commits. The manifest selects
exported history; import merges all records and preserves omitted target history.

### Record kinds

| Kind | Data and ordering |
| --- | --- |
| `manifest` | First record only: `network` (canonical context string), `account`, `created_at` (nonnegative Unix seconds), `direct_history`, `group_history` (selections defined above). Both bindings must match the target. |
| `device_state` | Signed protocol `AccountDeviceState` for the owner or active contacts. Owner state is verification material only and never overwrites the target device set. Remote states merge by revision; equal differing revisions conflict. Import updates the normal metadata cache without recording an online refresh or extending its refresh interval. |
| `contact` | Protocol `ContactRecord`: `account`, `alias`, `status`, `updated_at` and both grants. Deleted records have no grants. Expired grants retain their original expiry; importing them does not grant authorization. |
| `group` | `group_id`, `relay_id`, `locally_closed`. Introduces the group reference before its events. Existing local flags remain unchanged. Derived group fields and synchronization positions are not imported. |
| `group_event` | Fields listed below. Ordered by group and sequence; chat envelopes are filtered by `group_history`. Management and relay rotation events are always included. New events rebuild the verified projection using normal synchronization rules. |
| `group_nickname` | `group_id`, `account_id`, `joined_at_sequence`, `sequence`, `nickname` (nullable). Follows events. Contains the retained display value, including a clear, and the original update position. Restores only a matching current membership incarnation and a newer nickname position; equal-position differences conflict. Never creates membership or grants a role. Included independently of chat selection. |
| `group_epoch` | `group_id`, `epoch`, `commitment`, `member_public_key`, `client_secret`, `application_secret`. The public key and each secret are nullable 32-byte values in standard padded Base64. Follows events. Contains all locally held material for this epoch and commits it as one logical record. Fills missing values without replacing conflicting evidence or secrets. May exist without a group reference, as with account-synchronized historical keys. |
| `group_secret` | `group_id`, `kind`, `id`, `secret` (32 bytes). The only kind is `member`: ID = Base64url X25519 public key derived from the private secret. Epoch client and application secrets belong exclusively to `group_epoch`. |
| `channel` | `channel_id`, `relay_id`, `is_followed`. No posts, remote descriptor or publish operation. |
| `message` | `sender`, `message_id`, `sender_device_id`, `recipient`, `created_at` (Unix seconds), `payload` (protocol `DirectMessage`). Filtered by the peer in `direct_history`. No local sequence or relay timeline position is transferred. |
| `end` | Last logical record: `records` equals the number of data records, excluding manifest and end. No further decompressed bytes may follow. Decoder errors must propagate, and the final encryption segment and physical EOF must also validate. |

Component order is device verification, contacts/direct history, groups, then
channels. Within these components, exports use `device_state`; `contact` then
`message`; `group`, `group_event`, `group_nickname`, `group_epoch`, `group_secret`;
and finally `channel`. Manifest/end frame the file.
Identifier order within kinds is stable; direct history uses local arrival order.
Consumers reject reordered components and missing prerequisites.
Group member keys and epoch material may precede remote group discovery; they
remain unusable without the normal verified group state.

Group state is reconstructed from its retained events. A new group starts before
creation (sequence/epoch -1). Creating the group verifies the group ID derivation
and creator. Subsequent management events verify signatures, `prev_hash`, actor
membership/role/bans in the preceding state, and epoch transitions. Relay rotation
events advance the epoch without changing the management hash. Sequence gaps are
allowed; missing management dependencies are not. Group properties, members,
roles, bans, commitments and management hashes are derived, never copied from a
backup projection.

The backup acts as a local timeline source. New events use the same local apply
flow as normal synchronization, including confirmation of matching pending group
operations. Each applied event, its derived state, processing position and local
operation confirmation commit together. Unrelated pending operations are retained.
An interruption leaves a usable verified prefix. Both retrying import and normal
synchronization can continue that prefix. Import does not send approvals/messages,
submit requests, or initiate key sharing. Committed changes raise the existing
group, timeline and operation-related notifications.

Nickname updates live inside encrypted message envelopes. A `group_nickname`
record preserves the last known display state even when those envelopes are not
selected. Its `sequence` must be at least `joined_at_sequence`, which must match
the current member's join event; an earlier membership's nickname is ignored.
Nickname snapshots do not advance the group's event processing position.

`group_event` fields: `group_id`, `sequence`, `payload_json`, `certificate_json`,
`epoch`, `message_id`, `sender`, `sender_device_id`, `created_at`,
`decrypted_payload_json`, `rejection`, `is_message`. Protocol documents here are
canonical JSON strings. `created_at` is nullable ISO-8601 UTC date/time, preserving
the stored timestamp. Accepted event signature, group/epoch binding and message
metadata must agree. Only rejected message envelopes may carry rejection results;
management events must pass validation and cannot be skipped as rejected history.
New events extend the verified state. Older chat may fill absent history without
changing that state. Imported history follows the target conversation read position;
duplicates preserve record positions and do not repeat notifications.

Secret bytes are decrypted from the exporting environment's `ISecretProtector`,
carried only inside the encrypted body, and protected again for the target device.
An epoch's evidence, client secret and application secret are validated and merged
in one transaction. A conflict preserves the entire local epoch record; a failure
while protecting or saving either secret commits neither change. Missing source
material remains null and does not clear target material. A client secret must
match the record's network-bound commitment. Commitment and member-key evidence
conflict only when both source and target supply different values. Missing evidence
does not prevent an application secret from filling the target's missing secret;
an existing secret must still match. Import writes only the identified epoch, even
when other epochs share its client secret; it never fills their secret fields.
Device-addressed relay key boxes are not portable. The SDK does not back up relay
secrets. Current and historical locally held secrets are treated equally.

## Merge and failure semantics

Each fully read record is committed after its contributing encrypted segments,
structure, bindings and business rules validate. Only the current incomplete
write is rolled back. Later authentication, compression, EOF/count validation,
cancellation or storage/protector failure preserves earlier commits, including
records in the same component. Full success still requires a valid logical end,
decoder completion, final encryption authentication and physical EOF. A partially
committed import must not be reported as full success. Retry starts at the file
beginning and merges by identity/version; there is no byte-offset checkpoint.
The import operation itself performs no network I/O or message/management actions.

- Contacts: compare original `updated_at` for relationship and alias selection;
  older records are ignored, and equal-time differences in relationship or alias
  keep the local record with a conflict. Tombstones participate in the same order.
  For an adopted active record or matching equal-time metadata, merge each grant
  independently. An omitted grant preserves the held grant. Identical grant bodies
  merge verified device signatures without replacing a valid local signature with
  an invalid one. Different bodies replace a held grant only when the incoming
  grant passes local authorization checks and has a later expiry; no expiry means
  unlimited. Keeping the held grant under this rule is not a conflict and does not
  prevent adopting the alias. Missing device-state evidence never permits replacing
  a held grant with a different body. With no evidence, signatures for the same body
  remain verification material, with held signatures winning same-device differences.
  A first restore retains held grant material, including expired grants; normal
  authorization checks still apply before use. Repeated imports are unchanged.
  Newly applied active contacts schedule ordinary background maintenance after
  commit, allowing device-state notifications and grant supplementation/delivery
  through the existing lifecycle. Unchanged, conflicting and deleted records do
  not schedule this work.
- Direct messages: deduplicate by `(sender, message_id)`. Different content keeps
  local data with a conflict. Duplicate history preserves its local read state.
- Group events: deduplicate by `(group_id, sequence)`; different content keeps
  local data with a conflict. Matching envelopes can fill missing plaintext and
  chat classification without replacing existing plaintext or a local rejection;
  newly visible chat raises the ordinary timeline notification once.
  Older imports do not roll back verified state; newer
  events extend it only through the normal verification rules. A conflicting event
  or relay binding skips subsequent dependent records for that group in this call,
  without blocking other groups. Missing management dependencies fail the import
  and preserve its previously committed prefix. New keys fill missing slots;
  incompatible epoch evidence or key bytes conflict and cannot enable replacement
  secrets.
  Reconstructed membership describes retained events; current relay access must be
  checked through normal synchronization.
- Channels: existing local follow choices remain; only missing entries are added.
- Read state: import neither transfers source read cursors nor creates or advances
  target read cursors. Normal unread rules apply: an incoming chat message beyond
  the target cursor is unread; a message at or before it is read. Direct messages
  use their target-assigned local sequence, while group messages retain their group
  sequence. With no read record the cursor is -1. Own sends and non-chat events
  keep their normal unread exclusions. Duplicate imports preserve positions.

The report counts each logical input record once as applied, unchanged or
conflicting. An epoch record containing both secrets still contributes one count.
The report returns aggregate counts without collecting individual conflict details.

## SDK-owned conformance fixture

[backup-v1.json](../tests/vectors/backup-v1.json) covers password derivation,
container bytes, selections and logical group restoration;
[tink-streaming-aead.json](../tests/vectors/tink-streaming-aead.json) covers the
underlying authenticated stream. Both contain independent expected values and
record their source implementations and parameters. Implementations must consume
these fixtures and verify bidirectional exports; see the
[shared test guide](../tests/README.md). Fixture keys and passwords are public test
values and must never protect real data.
