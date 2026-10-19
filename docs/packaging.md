# Packaging and releases

| Project | NuGet package | Purpose |
| --- | --- | --- |
| `Sdk` | `RuleMaskDb.Sdk` | Dependency-free plugin contracts; includes a `.snupkg` symbol package |
| `RuleMaskDb.ConsoleApp` | `RuleMaskDb.Tool` | .NET tool exposing `rulemaskdb`, including the engine, SDK, and runtime dependencies |

Both packages target .NET 10 and share one release version. The engine, tests, and sample plugin are not published as separate packages. The tool is framework-dependent and requires a .NET 10 runtime; it supports global and local tool installation. The repository's BSD-3-Clause license and README are included in both packages.

## Local packaging

Run from the repository root with the .NET 10 SDK:

```sh
dotnet restore src/RuleMaskDb/RuleMaskDb.slnx
dotnet build src/RuleMaskDb/RuleMaskDb.slnx -c Release -p:Version=0.1.0
dotnet test src/RuleMaskDb/RuleMaskDb.slnx -c Release --no-build
dotnet pack src/RuleMaskDb/Sdk/Sdk.csproj -c Release --no-build -p:Version=0.1.0 -o artifacts/packages
dotnet pack src/RuleMaskDb/RuleMaskDb.ConsoleApp/RuleMaskDb.ConsoleApp.csproj -c Release --no-build -p:Version=0.1.0 -o artifacts/packages
bash scripts/verify-packages.sh 0.1.0 artifacts/packages
```

The verification script requires Bash (Linux, macOS, or Git Bash/WSL). It installs the actual tool archive, executes help and generator listing, builds the sample plugin against the actual SDK package, then checks plugin discovery. It uses a temporary package cache and cleans up afterward.

To try the package manually:

```sh
dotnet tool install --global RuleMaskDb.Tool --add-source ./artifacts/packages --version 0.1.0
rulemaskdb --help
```

## GitHub Actions

`.github/workflows/packages.yml` restores, builds, tests, packs, and verifies both packages on pull requests and pushes to `develop` and `main`. A manual workflow run also builds packages without publishing. Download the `nuget-packages` artifact to inspect the `.nupkg` and `.snupkg` files. Non-release builds use `0.1.0-ci.RUN.ATTEMPT` versions and are never pushed to nuget.org.

### One-time nuget.org setup

1. Ensure your nuget.org account can publish the `RuleMaskDb.Sdk` and `RuleMaskDb.Tool` package IDs.
2. Create a nuget.org API key scoped to pushing new versions of these packages (including first publication), and store it as the GitHub Actions secret `NUGET_API_KEY` in the repository or its `nuget` environment. Renew it before expiry.
3. Create the GitHub environment `nuget`; configure required reviewers there if you want a manual release gate.
4. Merge the packaging workflow into the repository's default branch so GitHub can trigger it for published releases. Development PRs target `develop`; promote the changes to `main` before publishing the first release.

### Publish a version

Publish a GitHub Release targeting the intended commit with a tag such as `v0.1.0` or `v0.2.0-rc.1`. The pipeline removes `v` and uses that exact version for both packages. Draft releases do not publish; published prereleases do. Marking a release as a prerelease does not add a suffix automatically: include the suffix in the tag.

The publish job only runs after all build, test, packaging, and consumption checks succeed. It downloads the verified artifacts and pushes them to `https://api.nuget.org/v3/index.json`; NuGet also pushes the SDK symbol package alongside its `.nupkg`. `--skip-duplicate` permits retrying a partially completed publication. Never reuse a version for changed contents; publish a new version instead.
