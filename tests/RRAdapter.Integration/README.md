# RRAdapter integration checks

A dependency-free console regression suite for `Factograph.Data` and `Polar.DB`.
It creates a unique temporary `rr:` database, tests FOG loading, primary-key lookup,
name/word/inverse indexes, updates, tombstones and repeated reopen, then deletes that
one temporary directory. It never uses the application's real cassette paths.

Run from the repository root with a compatible .NET 8 runtime:

```sh
dotnet run --project tests/RRAdapter.Integration/RRAdapter.Integration.csproj -c Release
```

For a cross-version compatibility experiment, compile this same test harness in an
isolated checkout of Factoria before the Polar.DB upgrade (with Polar.DB 2.1.0), and
run its DLL with `seed-legacy /tmp/factoria-rradapter-integration/legacy-case`.
Then run the new 3.1.1-linked test DLL with `verify-legacy` and the same directory.
The test deliberately retains that *synthetic* directory between these two steps.
Do not point the modes at a real cassette or database.

On a machine with only the .NET 10 runtime installed, run the built DLL with
`DOTNET_ROLL_FORWARD=Major dotnet <path-to-test-dll>` instead of using its apphost.

## Larger dataset and abrupt-process-stop regression (stage 3)

Build the console project, then run the complete isolated scenario:

```sh
dotnet run --project tests/RRAdapter.Integration/RRAdapter.Integration.csproj -c Release -- stage3-all 12000
```

The runner creates its own uniquely named database below the test-only temporary
folder and removes it afterward. It loads 12,000 synthetic FOG records, reopens
and updates 10% of them, and checks names, words and inverse links again. It then
launches a child process, appends 100 flushed updates, forcibly terminates that
process *without* calling `RRAdapter.Close()`, and verifies recovery on two opens.
The crash test covers abrupt termination **after successful Flush**, not power loss,
corruption mid-write, multiple writers, or durability of unflushed operations.

On a machine lacking the .NET 8 runtime, compile with an available SDK and run
`DOTNET_ROLL_FORWARD=Major dotnet tests/RRAdapter.Integration/bin/Release/net8.0/RRAdapter.Integration.dll stage3-all 12000`.
The runner never reads the application's configured production cassette paths.

The GitHub Actions workflow `.github/workflows/rradapter-integration.yml` runs
both the small regression suite and the 12,000-record crash/reopen suite for
changes to the adapter or its tests. For cross-version coverage, build the harness
in a separate pre-upgrade checkout and run `stage3-seed <test-path> 5000` against
`Polar.DB 2.1.0`, then `stage3-verify <test-path> 5000` using `3.1.1`.
