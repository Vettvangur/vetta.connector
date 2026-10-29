# Releases

All three packages use the same version: `Vetta.Connector`,
`Vetta.Connector.AspNetCore`, and `Vetta.Connector.SqlServer`. Their package dependencies
use the matching version. Source and packages are licensed under MIT.

## One-time NuGet setup

A Vettvangur NuGet organization member must configure a
[trusted publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
on NuGet.org with these values:

| Setting | Value |
| --- | --- |
| NuGet package/policy owner | `vettvangur` |
| GitHub repository owner | `Vettvangur` |
| GitHub repository | `vetta.connector` |
| Workflow file | `publish.yml` |
| GitHub environment | `nuget` |
| Package scopes (one per line) | `Vetta.Connector`, `Vetta.Connector.AspNetCore`, `Vetta.Connector.SqlServer` |

**Before the first 0.3.0 release**, extend the existing policy to include
`Vetta.Connector.SqlServer` (or verify its wildcard scope covers it).
Allow publishing new packages and new versions. Set the GitHub repository variable
`NUGET_USER` to the **personal NuGet username that creates the policy** (not an
email address or an organization name, unless that name is a personal account).
The organization owns the packages; the member's account performs the token exchange.
Verify that new packages are owned by Vettvangur after the first release.

The configured publishing account is `Vettvangur`; the repository variable
`NUGET_USER` matches this NuGet account. The policy is named **Vetta Connector**.

No permanent NuGet API key is stored in GitHub. Only the publish job can obtain a
short-lived credential. Pull requests and ordinary branch pushes only build/test.

## Publish a version

1. Merge the release changes into `main` and check that **Build and test** passes.
2. Create and publish a GitHub release on a commit from `main`, using a tag such as
   `v0.1.0` or `v0.2.0-rc.1`. Use a prerelease version suffix for NuGet prereleases;
   GitHub's prerelease checkbox alone does not change the NuGet version.
3. **Publish to NuGet** rebuilds, tests, packs, and publishes all three packages and symbols.
   The tag determines the package version. It refuses commits outside `main` history.
4. Check the workflow result and the three NuGet package pages. Package indexing can
   take time. An already-published version is immutable; use a new version for fixes.

If one package succeeds and another fails, fix the external publishing issue and
rerun the failed workflow. Duplicate versions are skipped. Do not move an existing
release tag to publish different contents under the same version.

## Local verification

With .NET 10 and Node.js 24 installed, run:

```sh
bash scripts/build.sh 0.3.0
```

This runs the .NET and browser-helper tests and creates `.nupkg` and `.snupkg`
files in `artifacts/`. It does not publish anything. Tests use synthetic identities
and do not require service credentials. Build artifacts are also downloadable from
successful GitHub Actions runs.
