# Offline SDK tests

This single xUnit v3 / Microsoft Testing Platform project contains focused unit tests and offline component workflows. Tests exercise real SDK entry points, cryptography, SQLite migrations and in-memory HTTP/WebSocket peers. No running relay, wallet, network connection or sibling checkout is needed after restore.

## Where tests belong

Paths in the table and code references below are relative to `tests/Meshline.Sdk.Tests/` unless stated otherwise.

| Location | Responsibility |
| --- | --- |
| `Protocol/` | Model validation, canonical serialization, identifiers, content and fixed signing inputs/signatures. |
| `Transport/` | Relay discovery, authentication, HTTP, WebSocket, dependency injection and caller-owned transport resources. Authentication vector adapters live here because they observe actual authentication at the public signer boundary. |
| `Transport/Responses/` | Typed response deserialization and validation over **both** HTTP and WebSocket, organized by response model. Standalone model validation belongs in `Protocol/`. |
| `Storage/` | Database setup, binding, constraints, transactions and timestamp persistence; `QueryReaderTests` owns pagination, snapshots, concurrent reads, cancellation and reader disposal. |
| `Components/Accounts/` | Establishment, routes, account recovery, home-relay migration and account synchronization. |
| `Components/Devices/` | Device keys, authorization, renewal and fixed key-agreement vectors through the device API. |
| `Components/Profiles/` | Profile publication, resolution, caching and restart recovery. |
| `Components/Contacts/` | Contact requests, grants, aliases, invitations and expiry. |
| `Components/Messages/` | Sending, outbox retention, send-status waiting, message reception/synchronization and fixed decryption vectors. |
| `Components/History/` | Local message/group/channel query contracts, ordering, exclusive bounds, snapshots, restart continuation and API compatibility. |
| `Components/Conversations/` | Conversation summaries, unread counts and read positions across direct messages, groups and channels. |
| `Components/Synchronization/` | Shared foreground/background sync contracts and observable status; `ResourceSyncTrackerTests` isolates the internal state tracker from component workflows. |
| `Components/Groups/` | Group workflows, membership, admission, invitations, rotation, recovery and persisted-ciphertext vector recovery. |
| `Components/Channels/` | Channel lifecycle, signed projections, history and recovery. |
| `Components/Lifecycle/` | Component start/stop, cancellation, draining and background errors. |
| `ClockScopeTests.cs` | The SDK's shared clock scope, nesting and execution-flow isolation. |
| `Support/` | Reusable setup and scripted peers; contains no test classes. |
| `Conformance/` | Native adapters for the shared SDK behavior scenarios. |
| `../../../tests/` | Shared protocol vector snapshots, behavior scenarios and cross-language tests; see the [shared test guide](../tests/README.md). |

Choose the layer by the behavior under test, not by whether its input happens to be a vector. Component tests may use isolated storage to seed a documented crash boundary. Keep private SDK helpers private.

## Writing and maintaining tests

- Give each method one coherent behavior and a descriptive English name. Parameterize variations of that behavior with `InlineData` or `MemberData`; keep each scenario visible in runner output.
- Separate independent checks into independently initialized tests. Keep a continuous workflow when its earlier state is required to prove restart recovery, idempotency or transaction atomicity.
- Put preparation, actions and verification in readable blocks, with one statement per line and multiline complex initializers. Keep assertions close to the observed behavior.
- Keep model construction, input mutations and expected errors in the test. Extract repeated preparation into narrowly named helpers; test classes must not call other test classes.
- Enter `Clock.Use(new ManualClock(...))` in the calling test **before** constructing fixtures. Do not establish an `AsyncLocal` clock in an awaited fixture factory. Dispose clients, pools and background tasks before leaving the clock scope.
- Use a separate `TestClient`, database and clock per test. `TestClient` owns its relay, pool, database and account signer, including a signer supplied to its constructor. Directly constructed pools and `HttpClient` instances retain the ownership exercised by their tests.
- Advance `ManualClock` to drive SDK timers and expiry. Real-time waits are bounded hang detection only. Use `AsyncTest.Signal` for event handshakes and `AsyncTest.UntilAsync` for observable state; propagate unexpected background errors.
- Keep actual outcome assertions in tests. `ResponseTransport` only scripts the response and selects a transport; its request-inspection callback is supplied by the test. Scripted peers intentionally validate outgoing signed SDK requests and fail on unscripted operations.
- Preserve independent expected bytes and signatures in [protocol vector snapshots](#protocol-vector-snapshots). Never regenerate expected values with the SDK being tested.

## Support building blocks

`TestNetwork` defines the network context. `ProtocolVectors` reads local snapshots and decodes their bytes. `AccountSigner`, `DeviceSigner`, `SecretProtector` and `TestDatabase` are separate resources. `ContactSetup`, `EncryptionVectorSetup` and `ProtocolResults` prepare specific test inputs; `RequestQuery` parses queries shared by scripted relays and synchronization tests.

`StoredMessageSetup` seeds local projections shared by history and read-position tests. Callers explicitly choose whether the account participates in the group or follows the channel. History queries use nonparticipating resources; conversation/read-position tests use participating resources. This helper does not simulate synchronization or assert outcomes. Keep the clock scope in the calling test.

`OfflineRelay` and `MemorySocket` exercise actual transport code without network I/O. `GroupRelay` and `ChannelRelay` provide domain scripts. Their state is per test; avoid shared mutable fixtures or a universal scenario framework.

## Regression ownership

Split independent contracts into separately initialized cases, even when they share preparation. History range validation, caller mutation, snapshot behavior, restart continuation and overload compatibility have separate tests. Keep dependent transaction/recovery sequences together so the test still proves atomicity or continuity.

For outgoing messages, `MessageSendingTests` owns submission and retries, `SendHistoryTests` owns bounded retention and restart persistence, and `SendStatusWaitingTests` owns waiting milestones, cancellation, disposal and observer failures. The retention test also checks an active waiter across rollback and immediate terminal eviction; that is an intentional boundary regression, not duplicate happy-path coverage.

Account establishment tests are grouped by contract rather than by the repair that
introduced a case. Keep interruption sequences intact: the failure, retained state,
restart and successful retry together prove resumability.

| Contract | Test owners |
| --- | --- |
| Public setup scenarios, options, retained/new installation, selection and failure details | `Accounts/AccountEstablishmentTests` |
| Durable steps and replay after failed checkpoints | `Accounts/AccountEstablishmentStepTests` |
| Validity through profile publication and workflow cleanup | `Accounts/AccountEstablishmentCompletionTests` |
| Profile preservation, stale reads, publication outcomes and owner disposal | `Accounts/AccountProfileInitializationTests` |
| Signing versus submission failure classification | `Accounts/AccountPublicationFailureTests` |
| Startup reservation, cancellation and disposal | `Accounts/AccountEstablishmentLifecycleTests` |
| Draft keys, activation receipts and failed secret access | `Accounts/AccountDevicePreparationTests`, `Accounts/LocalDataIntegrityTests`, `Accounts/DeviceKeyAccessTests` |
| Device publication reconciliation and certificate renewal | `Accounts/DeviceAuthorizationRetryTests`, `Devices/DeviceStatePublicationTests` |
| Handoff between standalone device operations and account setup | `Devices/DeviceAuthorizationIntegrationTests` |
| Replacement proposals, interrupted choice checkpoints and revocation | `Accounts/DeviceReplacementTests` |
| Route bootstrap, renewal, conflict discovery and expired-request evidence | `Accounts/AccountRouteAuthorizationTests`, `Accounts/AccountRouteRetryTests`, `Accounts/EnsureRouteTests`, `Accounts/ExpiredRouteRecoveryTests` |
| Explicit recovery and migration | `Accounts/MigrationTests`, `Accounts/DevicePublicationBoundaryTests` |
| Device activation and renewal through the shared pool | `Accounts/DeviceSessionBindingTests` |
| Pool reuse, certificate binding and session retirement | `Transport/RelayClientPoolTests` |
| Session expiry, reauthentication and cancellation | `Transport/RelayAuthenticationTests` |
| Runtime suspension, socket drainage and shutdown failures | `Accounts/RevokedDeviceRuntimeTests`, `Lifecycle/LifecycleTests` |
| Contact continuity and atomic publication | `Contacts/ContactAuthorizationContinuityTests`, `Contacts/ContactContinuityBoundaryTests`, `Contacts/ContactPublicationTransactionTests` |
| Background grant supplementation and delivery | `Contacts/BackgroundContactAuthorizationTests` |
| Device-state notification delivery and recovery | `Contacts/DeviceStateNotificationTests` |
| Standalone mutation serialization and signing without holding the database writer | `Devices/DeviceMutationConcurrencyTests` |
| Push gateway creation, renewal keep/set/remove, local persistence, session rebinding and publication replay | `Devices/PushGatewayTests` |
| Gateway intent, retained declarations, recovery and interrupted device preparation | `Accounts/PushGatewayEstablishmentTests` |
| Push certificate signing, identity and URL validation | `Protocol/PushCertificateTests` |
| Fresh schema, direct upgrade from `InitialCreate` and gateway intent migration preserving unfinished setup | `Storage/DatabaseTests` |

Preserve these boundary assertions when reorganizing cases:

- Retained or entirely absent local data are the supported cases. Unknown, temporary
  and permanent key-access failures preserve keys, drafts, workflow and signed
  requests; restored access reuses them. Intermediate development schemas have no
  compatibility tests.
- A checkpoint is not relay confirmation. Interrupt after staging, acceptance,
  activation, progress saves and cleanup; verify exact request/key reuse after
  reopening. Unknown outcomes, definitive rejection and pre-submission signing
  failures must remain distinguishable.
- Replacement keeps the full device list, original identity and accepted/proposed
  choices through interruption. A different choice cannot replace an unresolved
  request. Remote conflicts reread current state; same-client device mutation runs
  before or after establishment, never during it.
- Route tests distinguish retained original-route evidence from an expired initial
  request, preserve successor revision floors and reject unsafe bootstrap. The
  scripted relay requires a valid route for device-state reads and validates signed
  requests independently of the SDK.
- Healthy-account tests assert request, signature and protected-key read counts.
  Renewal and removal tests let unrelated SQLite writers and message operations
  proceed while account signing or relay submission is paused.
- Continuity tests distinguish source acceptance from target acceptance, cover
  pending outgoing consent across restart, and include grants committed during
  signing. The 1000-contact case reports preparation time without a hardware-dependent
  threshold. Background tests await both grant persistence and notification retries.
- Suspension tests await `Stopped`, advance time and verify quiet device sockets,
  no wallet signatures and successful explicit restart. Injected storage/task failures
  must still drain all children and retain errors and pending sends.

## Running tests

Run these commands from `sdk/dotnet` after restoring dependencies:

```sh
dotnet build Meshline.Sdk.slnx -c Release --no-restore
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --minimum-expected-tests 1 --fail-skips on --report-xunit-trx --results-directory TestResults
```

TRX reports go to the ignored `TestResults/` directory. Add `--report-xunit-trx-filename offline.trx` for a fixed filename or `--report-xunit-html` for HTML output. The test runner does not restore packages or require network access. Filter by namespace, class, or method; namespace filters include `*` when they should include subdirectories:

```sh
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-namespace Meshline.Tests.Protocol --minimum-expected-tests 1 --fail-skips on
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-namespace 'Meshline.Tests.Transport*' --minimum-expected-tests 1 --fail-skips on
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-namespace Meshline.Tests.Components.Groups --minimum-expected-tests 1 --fail-skips on
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-class '*QueryReaderTests' --minimum-expected-tests 1 --fail-skips on
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-class '*MigrationTests' --minimum-expected-tests 1 --fail-skips on
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-method '*Socket_disconnect*' --minimum-expected-tests 1 --fail-skips on
```

After changing shared setup or concurrency behavior, also exercise both scheduling modes:

```sh
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --seed 20260926 --parallel collections --parallel-algorithm aggressive --max-threads 4 --minimum-expected-tests 1 --fail-skips on --timeout 10m
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --seed 17 --parallel none --minimum-expected-tests 1 --fail-skips on --timeout 10m
```

`--timeout` limits the entire test session, not each test. Allow time for the full
serial suite; individual asynchronous test handshakes still use bounded waits.

## Coverage

| Area | Coverage |
| --- | --- |
| Protocol and cryptography | Canonical JSON, validation, identifiers, signing vectors, network/origin binding, encryption, and invalid input rejection. |
| Transport | Authentication, session renewal, timeout/cancellation, HTTP and WebSocket framing, notifications, reconnection, and disposal. |
| Storage | Migrations, account/network binding, transactions, pagination, snapshot isolation, concurrent reads, and cancellation. |
| Client workflows | Accounts/devices, profiles/contacts, messaging, channels/groups, recovery, deduplication, atomic synchronization cursors, and conversations. |
| Lifecycle | Dependency ordering, repeated start/stop/disposal, draining active operations, background diagnostics, and shared-pool ownership. |

Acceptance uses behavioral checks without an overall coverage-percentage threshold.

## Protocol vector snapshots

`../tests/vectors/*.json` are byte-for-byte snapshots from the protocol repository's `v1/test-vectors` directory, at commit `e293e37b800c42d2ee9d20317344cc1220b80c5e`. The source checkout was clean at capture time; its status and each file's SHA-256 are recorded in [the vector manifest](../tests/vectors/manifest.json). `SerializationTests.Snapshot_files_match_recorded_hashes` checks their integrity on every test run.

Expected bytes, hashes, ciphertext, keys, and signatures come from these snapshots, never from an SDK-generated golden-file update. Dynamic workflow fixtures sign their own requests and compare observable behavior; they complement these independent known-answer tests. Do not regenerate expected vectors using the SDK under test.

Vector adapters follow the tested behavior: pure signing and serialization checks live in `Protocol`; real authentication lives in `Transport/AuthenticationVectorTests`; key agreement, message reception and group recovery live in `Components/Devices/DeviceKeyAgreementTests`, `Components/Messages/MessageEncryptionTests` and `Components/Groups/GroupEncryptionTests`. MSBuild copies the shared vectors and scenarios into `TestData/` in the test output. `Support/ProtocolVectors` reads those copies, and `Support/EncryptionVectorSetup` only seeds the device state needed by the public API tests. The test organization and fixture rules above also apply to vector adapters.

### Applied sections

Protocol paths below are source identifiers in that recorded revision. They are not runtime dependencies or links requiring a sibling checkout.

| Snapshot | Executed vector sections | Applicable protocol sections |
| --- | --- | --- |
| `common-v1.json` | `canonical_json.vectors`, `canonical_json.rejection_vectors`, `base64url.cases`, `network_binding.format_cases` | `v1/en/general.md`; `v1/en/test-vectors/common.md` |
| `identity-auth-v1.json` | `device_identity`, `device_certificate`, `relay_origin.normalization_cases`, `session_auth.verification_cases` with both signing inputs | `client-relay/core-objects/accounts-and-devices.md`; `client-relay/methods/authentication-and-sessions.md`; `test-vectors/identity-auth.md` under `v1/en` |
| `push-notifications-v1.json` | `device_certificates`, `certificate_tampering`, `gateway_urls`; gateway changes retain device identity and are covered by both certificate signatures | `client-relay/core-objects/accounts-and-devices.md`; `test-vectors/push-notifications.md` under `v1/en` |
| `contacts-v1.json` | `signing.vector`, every `signing.verification_cases` row | `v1/en/client-relay/concepts/contacts.md`; `v1/en/test-vectors/contacts.md` |
| `message-encryption-v1.json` | `timeline` sender/recipient X25519 agreements through the device API; sender-copy reception, fixed plaintext and envelope signing input; key/ciphertext/AAD tampering and foreign-network signature rejection; `notification` field cases, exact signing inputs/signatures, tampering, variant decryption and canonical retry comparison | `client-relay/core-objects/messages-and-content.md`; `client-relay/concepts/message-timeline.md`; `test-vectors/message-encryption.md` under `v1/en` |
| `message-content-v1.json` | `body.body_cases`, `body.invalid_body_cases`, `attachment_encryption.reference` and plaintext integrity | `v1/en/client-relay/core-objects/messages-and-content.md`; `v1/en/test-vectors/message-content.md` |
| `channels-v1.json` | `channel_id`, every `channel_write_cases` signing input and signature | `client-relay/channels/core-objects.md`; `client-relay/channels/methods/timeline.md`; `test-vectors/channels.md` under `v1/en` |
| `groups-v1.json` | `group_id`, `keying` fixed message recovery from persisted ciphertext, `management_chain.chain` signing inputs/signatures and management hashes; `notification` field cases, exact signing inputs/signatures, tampering, persisted variant decryption and canonical retry comparison | `client-relay/groups/core-objects.md`; `client-relay/groups/concepts/model-and-keys.md`; `client-relay/groups/concepts/messaging-and-encryption.md`; `test-vectors/groups.md` under `v1/en` |

Origin normalization is observed through real HTTP/WebSocket authentication at the public signer boundary. The message vector's recipient box uses a synthetic device ID without a corresponding certificate, so its fixed shared secret is checked through `DeviceManager.DeriveSharedSecretAsync`; complete recipient-box reception is exercised by signed workflow fixtures. The fixed sender copy has a valid certificate and is decrypted through public timeline synchronization. Group keying vectors likewise use a synthetic signer ID: tests seed the persisted-event crash boundary, restart the client, and observe recovery, stored plaintext, events, and authentication failures. Signed group admission is covered by the separate workflow tests. Private SDK helpers remain private.

The original complete files are retained for provenance. Inclusion of a file does not claim execution of every section. Other SDK behaviors (grant expiry, invitations, channel projection, group membership/rotation/recovery, transport, and storage) have dedicated behavioral tests outside these vector adapters. Source examples testing DHT routing/keyspace, relay admission/retention enforcement, server nonce replay prevention, and renderer output are outside SDK coverage. `dht-keyspace-v1.json` is deliberately not copied.

To update the snapshots, copy raw applicable files from a reviewed protocol revision, record that revision and workspace status, recompute SHA-256 over the copied bytes, review changes to applicable sections, and run the entire offline suite. Keep the provenance manifest and this applicability table synchronized. No vector-generation or download step runs during tests.
