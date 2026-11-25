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

**Not yet covered:** failure injection between Fog save, adapter update and log
append; fault-tolerant Fog writes; full incremental substitute semantics; HTTP
application tests. Passing the current suite does not establish those properties.
