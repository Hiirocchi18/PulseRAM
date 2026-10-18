# Listing PulseRAM on winget

Once listed, anyone can install PulseRAM with:

```powershell
winget install PulseRAM          # or: winget install Hiirocchi18.PulseRAM
```

## First version (once, by hand)

winget can only list files that are already downloadable, so do this **after** publishing the release on GitHub
(the Release workflow attaches the installers within ~20 minutes).

1. On a Windows PC, in the repository folder:
   ```powershell
   .\winget\Submit-Winget.ps1 -Version 1.4.0 -Submit
   ```
   This downloads the release installers, writes the three manifest files (`winget\manifests\h\Hiirocchi18\PulseRAM\1.4.0`),
   checks them with `winget validate`, installs **wingetcreate** if needed and opens a pull request on
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) from your GitHub account (it asks you to sign in).
2. Microsoft's bots test the installer (install, uninstall, malware scan). A maintainer usually merges within a few days.
   If a bot comments with a problem, fix it and run the script again.

## Every later version (automatic)

1. Create a classic GitHub token at <https://github.com/settings/tokens> with the **public_repo** scope.
2. In this repo: **Settings → Secrets and variables → Actions → New repository secret**, name `WINGET_TOKEN`.
3. Make sure you have a fork of `microsoft/winget-pkgs` (step 1 above creates it).

From then on, publishing a GitHub release also opens the winget pull request for it.
