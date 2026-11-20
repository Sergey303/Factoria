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
