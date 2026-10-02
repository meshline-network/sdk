# Shared behavior scenarios

These are SDK behavior cases, separate from the externally sourced protocol
vectors. Expected results are authored from the behavior contract, never captured
from one SDK and used as the oracle for another.

The first suite is [channel-post-updates.json](channel-post-updates.json), version 1.
The [.NET adapter](../../dotnet/tests/Meshline.Sdk.Tests/Conformance/ChannelPostUpdateTests.cs)
executes the suite through the public SDK.

Each case has a stable `id`, an `initial` post and ordered `steps`. Each step has a
stable `id`, an `update`, and the complete expected content after the edit. Content
uses protocol field names. `body: null` means no body; `attachments: []` means no
attachments. These are normalized observations, not signed wire envelopes.

The adapter creates a channel and publishes the initial post through the public SDK.
For each step it calls the public edit API, compares body and attachment content,
and checks that the post reference, message ID and author stay unchanged. After the
last step it reopens persistent storage and verifies the same post and content.
No attachment download or live relay is needed; the attachment names a public test
location and the known SHA-256 digest of empty content.

Version 1 supports only `body` and `attachments` updates. An omitted field stays
unchanged, JSON null deletes it, and a supplied value replaces the entire field.
Nested extension fields are part of that value. The adapter rejects unknown
update fields, rather than silently skipping an operation. Each case starts with
fresh state; steps within a case share that state.

Add domain-specific scenario files and native adapters as behavior is extracted.
Avoid a universal scripting language for private helpers, database schemas or
platform lifecycle details. Adding a language means implementing these observable
operations and running the same case IDs, alongside that language's own tests.
