# Releases and GitHub Pages

The repository is published at [github.com/Akash97p/agent-notify](https://github.com/Akash97p/agent-notify). These files document the release process; GitHub Actions performs the hosted build and publication after an authorized tag push.

## GitHub repository setup

1. Keep `main` as the stable release line and `dev` as the integration line.
2. Make `main` the default protected branch and require the CI workflow before merge.
3. In **Settings → Pages**, select **GitHub Actions** as the publishing source.
4. Keep normal development on topic branches merged into `dev`; promote a tested release from `dev` to `main` through review.
5. Obtain and configure Authenticode signing before presenting a public build as trusted. The current workflow produces unsigned binaries.

The Pages workflow deploys the static `site/` documentation after changes reach `main`. It uses the official GitHub Pages actions and requests only read, Pages, and OIDC permissions.

## Published prerelease: `v0.0.1-alpha.1`

The first hosted prerelease was prepared on the `dev` integration line and published on 2026-08-12:

- Tag: [`v0.0.1-alpha.1`](https://github.com/Akash97p/agent-notify/releases/tag/v0.0.1-alpha.1)
- Merge commit: `8186aed` (`merge: prepare v0.0.1-alpha.1 prerelease`)
- Actions run: [31566620009](https://github.com/Akash97p/agent-notify/actions/runs/31566620009)
- Result: successful Windows build, test, packaging, and prerelease publication
- Assets: `AgentNotifySetup.exe`, `SHA256SUMS.txt`, and `SKILL.md`
- Local installer checksum recorded in [`docs/VERIFICATION.md`](VERIFICATION.md): `2000b536dc8eac4b72821d0ac6df7b79cb258f4ce7b2f0bfb7456a4df3d7e78b`

This is an alpha evaluation release, not the mature `v1.0.0` release. It may contain incomplete features, breaking changes, unsigned binaries, and unverified provider integrations.

### Version progression

Use the committed `Version` value and exact `v`-prefixed tag together:

| Stage | Example | Meaning |
|---|---|---|
| Pre-alpha | `v0.0.1-pre-alpha.1` | Internal or very early evaluation |
| Alpha | `v0.0.1-alpha.1` | Active development; unstable and incomplete |
| Beta | `v0.0.1-beta.1` | Feature-complete target with ongoing stabilization |
| Release candidate | `v0.0.1-rc.1` | Candidate for a stable release |
| First mature release | `v1.0.0` | Stable public milestone after release criteria pass |

Any tag containing a hyphen is created by the workflow as a GitHub prerelease. A mature `v1.0.0` release should be promoted from a tested `dev` merge to `main` and should also complete signing, human verification, and release review.

## Create a release

1. Update `Version`, `InformationalVersion`, `AssemblyVersion`, and `FileVersion` in `Directory.Build.props`. Keep the informational value and tag suffix identical; keep the assembly/file values numeric.
2. Update release notes, the verification record, and any migration documentation.
3. Run:

   ```bash
   ./scripts/build.sh
   ./scripts/test.sh
   ./scripts/package.sh
   ```

4. Verify the installer and `artifacts/SHA256SUMS.txt` on Windows.
5. After the release commit is on the intended release branch, create and push an exact matching tag. Prerelease tags use SemVer-style suffixes and are marked as prereleases automatically:

   ```bash
   git tag -a v0.0.1-alpha.1 -m "AgentNotify v0.0.1-alpha.1"
   git push origin v0.0.1-alpha.1
   ```

   The tag must exactly equal `v` plus the committed `Version` value. Examples include `v0.0.1-pre-alpha.1`, `v0.0.1-alpha.1`, `v0.0.1-beta.1`, `v0.0.1-rc.1`, and finally `v1.0.0`. The release workflow passes `--prerelease` whenever the tag contains a hyphen.

The tag workflow independently restores, builds, tests, packages, checks the tag against `Directory.Build.props`, and creates a GitHub Release containing:

- `AgentNotifySetup.exe`;
- `SHA256SUMS.txt`; and
- the distributable `SKILL.md`.

The workflow fails rather than publishing when tests fail, packaging fails, the version is not SemVer-style, or the tag does not exactly match the product version. Numeric assembly/file metadata remains `0.0.1.0` for the current prerelease because Windows version-resource fields are numeric; API, CLI, installer, registry, package, and release display metadata use `0.0.1-alpha.1`.

## Local packaging

PowerShell is the portable packaging implementation:

```powershell
./scripts/package.ps1
```

The WSL `scripts/package.sh` wrapper resolves the configured Windows SDK path and invokes the same script, preventing local and hosted release logic from drifting.
