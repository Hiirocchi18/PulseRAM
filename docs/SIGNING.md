# Code signing (removes the SmartScreen warning)

The build is ready for signing: once the settings below exist, every **published release** is signed
automatically. `PulseRAM.exe`/`PulseRAM.dll` are signed before they are packaged, then the installer. Ordinary
pushes and pull requests are never signed, so you only have to approve releases.

PulseRAM uses [SignPath Foundation](https://signpath.org/), which signs open-source projects for free with a
certificate issued to *SignPath Foundation*. Nothing here costs money.

> SmartScreen works on reputation. A signed installer stops the "unknown publisher" warning straight away for most
> people, and the remaining "not commonly downloaded" warning fades as more people install the same signed release.

## 1. Apply (once)

1. Turn on two-factor authentication for your GitHub account (required by SignPath).
2. Apply at <https://signpath.org/apply> with:
   - Repository: `https://github.com/Hiirocchi18/PulseRAM`
   - License: MIT
   - Build: GitHub Actions on GitHub-hosted runners (`.github/workflows/build.yml`)
   - Releases: the existing GitHub releases
3. SignPath Foundation reviews the project (usually days to a few weeks). The README already has the
   *Code signing policy* section they require.

## 2. Set up the SignPath project (after you're accepted)

In the SignPath web app:

1. **Trusted build systems** → add the predefined **GitHub.com** system and link it to the project.
2. Install the **SignPath GitHub App** on the PulseRAM repository when asked.
3. **Project** slug: `PulseRAM`.
4. **Artifact configurations**: create two, pasting the XML from this repository:
   - slug `app` ← `.signpath/artifact-configurations/app.xml`
   - slug `installer` ← `.signpath/artifact-configurations/installer.xml`
5. **Signing policy** slug: `release-signing` (SignPath Foundation's default), with you as approver.
6. Create an **API token** for a user with *submitter* rights on the project.

## 3. Add the settings to GitHub

Repository → **Settings → Secrets and variables → Actions**:

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | the API token from step 2.6 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | your organization ID (SignPath → Organization settings) |
| Variable | `SIGNPATH_PROJECT_SLUG` | `PulseRAM` (only if you used a different slug) |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` (only if different) |

Signing switches on when `SIGNPATH_ORGANIZATION_ID` is set.

## 4. Each release

1. Publish a release on GitHub (tag `v1.4.0` etc.).
2. The **Release** workflow builds both architectures and pauses at *Sign app files*. SignPath emails you;
   approve the request in SignPath (twice per architecture: app files, then installer). It waits up to an hour.
3. The signed installers, portable zips and `SHA256SUMS.txt` are attached to the release automatically.

To check a downloaded file: right-click → **Properties → Digital Signatures**, or in PowerShell
`Get-AuthenticodeSignature .\PulseRAM-Setup-v1.4.0.exe`.

## Alternative: your own certificate

If you'd rather sign under your own name, [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
(about US$10/month, needs identity validation) or a purchased OV/EV certificate also work. Replace the two SignPath
steps in `build.yml` with the matching signing action.
