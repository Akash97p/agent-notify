# Releases and GitHub Pages

The repository is currently local-only. These files do not configure a remote or publish anything by themselves.

## GitHub setup after the owner pushes the repository

1. Push `main` and `dev` to `https://github.com/Akash97p/agent-notify`.
2. Make `main` the default protected branch and require the CI workflow before merge.
3. In **Settings → Pages**, select **GitHub Actions** as the publishing source.
4. Keep normal development on topic branches merged into `dev`; promote a tested release from `dev` to `main` through review.
5. Obtain and configure Authenticode signing before presenting a public build as trusted. The current workflow produces unsigned binaries.

The Pages workflow deploys the static `site/` documentation after changes reach `main`. It uses the official GitHub Pages actions and requests only read, Pages, and OIDC permissions.

## Create a release

1. Update `Version`, `AssemblyVersion`, and `FileVersion` in `Directory.Build.props`.
2. Update release notes, the verification record, and any migration documentation.
3. Run:

   ```bash
   ./scripts/build.sh
   ./scripts/test.sh
   ./scripts/package.sh
   ```

4. Verify the installer and `artifacts/SHA256SUMS.txt` on Windows.
5. After the release commit is on `main`, create and push an exact matching tag, for example:

   ```bash
   git tag -a v1.1.0 -m "AgentNotify 1.1.0"
   git push origin v1.1.0
   ```

The tag workflow independently restores, builds, tests, packages, checks the tag against `Directory.Build.props`, and creates a GitHub Release containing:

- `AgentNotifySetup.exe`;
- `SHA256SUMS.txt`; and
- the distributable `SKILL.md`.

The workflow fails rather than publishing when tests fail, packaging fails, or the tag does not exactly match the product version.

## Local packaging

PowerShell is the portable packaging implementation:

```powershell
./scripts/package.ps1
```

The WSL `scripts/package.sh` wrapper resolves the configured Windows SDK path and invokes the same script, preventing local and hosted release logic from drifting.

