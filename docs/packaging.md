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

`.github/workflows/packages.yml` restores, builds, tests, packs, and verifies both packages on pull requests and pushes to `develop` and `main`. A manual workflow run also builds packages without publishing. Download the `nuget-packages` artifact to inspect the `.nupkg` and `.snupkg` files. Non-release builds use `0.1.0-ci.RUN.ATTEMPT` versions and are never published to a feed.

### GitHub Packages publishing setup

The publish job uses the automatically supplied `GITHUB_TOKEN` with `packages: write`; no NuGet API key or custom secret is required. Package repository metadata links the packages to `rflechner/RuleMaskDb`. If a package ID already exists, ensure this repository has Actions write access to it.

Merge the packaging workflow into the repository's default branch so GitHub can trigger it for published releases. Development PRs target `develop`; promote the changes to `main` before publishing the first release.

### Publish a version

Publish a GitHub Release targeting the intended commit with a tag such as `v0.1.0` or `v0.2.0-rc.1`. The pipeline removes `v` and uses that exact version for both packages. Draft releases do not publish; published prereleases do. Marking a release as a prerelease does not add a suffix automatically: include the suffix in the tag.

The publish job only runs after all build, test, packaging, and consumption checks succeed. It downloads the verified artifacts and pushes the two `.nupkg` files to `https://nuget.pkg.github.com/rflechner/index.json`. The SDK `.snupkg` remains available in the workflow artifact; symbol pushing is disabled for this feed. `--skip-duplicate` permits retrying a partially completed publication. Never reuse a version for changed contents; publish a new version instead.

## Install from GitHub Packages

Authenticate with a GitHub personal access token (classic) with `read:packages` and access to the packages. GitHub's NuGet registry requires authentication even for public packages. See the [GitHub NuGet registry documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

Add this feed to your user-level NuGet configuration (keep credentials out of the repository):

```sh
dotnet nuget add source https://nuget.pkg.github.com/rflechner/index.json --name github_rulemaskdb
```

Supply credentials through the `NuGetPackageSourceCredentials_github_rulemaskdb` environment variable, with the value `Username=YOUR_GITHUB_LOGIN;Password=YOUR_READ_PACKAGES_TOKEN;ValidAuthenticationTypes=Basic`. Set it using your shell or credential manager; do not commit the token. Alternatively, use a NuGet credential provider or user-level source credentials.

Then install the tool or reference the SDK:

```sh
dotnet tool install --global RuleMaskDb.Tool --add-source https://nuget.pkg.github.com/rflechner/index.json
dotnet add package RuleMaskDb.Sdk --source https://nuget.pkg.github.com/rflechner/index.json
```

Keep the authenticated source configured for subsequent restores and updates. In another GitHub Actions repository, grant that repository read access to the packages and use its `GITHUB_TOKEN` with `packages: read`, or use an appropriately scoped classic token.
