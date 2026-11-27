# Factoria tests

This repository keeps application contract tests separate from the storage engine.
Use **Polar.DB 3.1.1** as declared by `Factograph.Data`; do not test historical
Polar.DB binary-file compatibility here.

## Layout

- `tests/Factograph.Data.Tests/`: xUnit tests that exercise **IFDataService**
  with isolated Fog/config/ontology files. Tests do not inspect Polar.DB files.
- `tests/RRAdapter.Integration/`: existing dependency-light console smoke
  suite for the adapter, large synthetic data sets, and controlled process stops.
- `Factoria.Tests.sln`: both test projects and their production dependency.

The contract fixture creates a unique directory in the system temporary folder,
containing a writable Fog, minimal ontology, configuration, and fresh `rr:`
storage. Each test owns and deletes only its own directory. No production
cassette paths are referenced.

## Run locally

With .NET 8 SDK and runtime:

```sh
dotnet test Factoria.Tests.sln -c Release
dotnet run --project tests/RRAdapter.Integration/RRAdapter.Integration.csproj -c Release
```

The console project is not a VSTest project: `dotnet test Factoria.Tests.sln`
discovers xUnit contracts; run the RRAdapter executable separately.

On a machine with only .NET 10 runtime installed, enable major roll-forward:

```sh
DOTNET_ROLL_FORWARD=Major dotnet test Factoria.Tests.sln -c Release
DOTNET_ROLL_FORWARD=Major dotnet tests/RRAdapter.Integration/bin/Release/net8.0/RRAdapter.Integration.dll
```

For extended RRAdapter regression:

```sh
dotnet run --project tests/RRAdapter.Integration/RRAdapter.Integration.csproj -c Release -- stage3-all 12000
```

## CI cadence

- `.github/workflows/rradapter-integration.yml`: xUnit contracts + RRAdapter
  smoke on PRs and relevant pushes to `main`.
- `.github/workflows/factoria-extended.yml`: extended 12,000-record process
  stop/reopen suite nightly or by manual workflow dispatch.

## Boundary of responsibility

This suite tests the Factoria behavior of the public `IFDataService` interface:
CRUD, Fog persistence, visible search/index results, owner authorization, and
safe rejection of unsupported incremental `substitute`.

It intentionally does **not** test Polar.DB internal file corruption, checkpoint
formats, or migration from Polar.DB 2.1.0 to 3.1.1.

Current fixture uses the `rr:` implementation because its existing integration
baseline is established. Tests themselves use `IFDataService`. A second backend
should get its own fixture configuration and run the same contract assertions
where it supports the same contract; unsupported capabilities must be explicitly
documented, not silently skipped.

## Fault-injection and interrupted-write recovery

The IFDataService suite exercises Factoria's public service contract, using
temporary Fog/config/journal files and no Polar.DB physical-file access.

Verified cases:
- Failed Fog publication leaves the original document and the cached source
  intact; a later successful write does not replay the failed change.
- An unsuccessful publication does not consume the generated Fog ID counter.
- If the journal is unavailable at open time, the operation fails before
  changing Fog or the adapter's visible state.
- Before publishing Fog, Factoria writes and flushes a
  `.fog-index-pending` intent marker. Once the adapter update and journal
  append complete, the marker is removed.
- A service restart with a pending marker rebuilds adapter state from the
  authoritative Fog source; after success the marker is removed. If the Fog
  cannot be parsed, startup fails and the marker remains for retry.
- A second operation in the same service first rebuilds from Fog and invalidates
  cached XML if a previous operation failed.
- An exclusive `.fog-writer.lock` handle rejects competing service writes
  while the first writer is active.

Fog publication writes and flushes a temporary file in the destination
directory before replacing the old Fog. The intent marker is a recovery flag,
not a distributed transaction. Index rebuilding uses the public adapter API,
not Polar.DB on-disk internals.

**Remaining limitations and required next tests:**
- Journal append can still fail after Fog and adapter publication, leaving the
  journal incomplete. The pending marker repairs derived adapter state, **not**
  missing journal entries. Decide whether the log is authoritative or merely
  diagnostic before asserting a three-way atomic commit.
- A single exclusive writer lease prevents *overlapping* writes but does not
  guarantee cache coherence across two already-open independent services.
- No guaranteed atomicity across all files for power loss, directory-entry
  durability, or cancellation at every instruction boundary.
- Actual killed-child-process checkpoints between publication stages, full
  incremental `substitute`, and HTTP application tests are not yet covered.
- Recovery rebuilds only from valid Fog; it does not attempt to recover
  corrupted Polar.DB physical files (owned by Polar.DB tests).

Passing this suite therefore establishes recovery for the tested interrupted
application write scenarios, not an arbitrary crash-proof multi-file transaction.

## Multiple active services and journal semantics

`FDataService` now publishes a `.fog-commit-generation` token after an
accepted write (and after successful interrupted-write recovery). Public read
and write methods compare their startup token with the published version.
An older service **fails closed** with `InvalidOperationException`; callers
must dispose/close it and create a fresh `FDataService`. The exclusive
`.fog-writer.lock` serializes writes; the generation check prevents sequential
writers from silently clobbering newer Fog data.

The token detects operations committed **through this Factoria service**. It
does not track manual edits to Fog files, external writers, or the exact moment
between Fog publication and token publication. A pending marker still governs
recovery after that interval. An already materialized lazy enumeration is not
a transactional snapshot.

`logfile_put.txt` is currently **append-only diagnostic history**: there is no
replay or journal-reading code in `Factograph.Data`. Its file handle is
preflight-opened and a failed append can still leave Fog/index committed while
the diagnostic history lacks an entry. The unresolved contract decision is
whether this must become a durable audit trail; do not treat it as one yet.
