# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What gets signed

Only the release executables of VLC Redesign (`VLC-Redesign-<version>-win-x64.exe` and `VLC-Redesign-<version>-win-x64-small.exe`). They are built from this repository's source by the GitHub Actions workflow [`vlc-redesign-release.yml`](../.github/workflows/vlc-redesign-release.yml). Nothing built on a personal machine is signed.

The Microsoft Store package (`.msix`) is signed by Microsoft through the Store and isn't part of this policy.

## Team roles

| Role | Who |
|---|---|
| Committers and reviewers | [Sayandeep1013](https://github.com/Sayandeep1013) |
| Approvers (approve each signing request) | [Sayandeep1013](https://github.com/Sayandeep1013) |

Every signing request is approved manually by an approver.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. See [PRIVACY.md](PRIVACY.md).

## Setting it up (maintainers)

1. Apply at <https://signpath.org/apply> for the open-source program. Point them at this folder; the license is MIT ([LICENSE](LICENSE)).
2. Once accepted, in SignPath:
   - create a project (e.g. `vlc-redesign`) linked to this GitHub repository, with *Trusted build system: GitHub.com*;
   - add an artifact configuration that signs every `*.exe` in the artifact zip with Authenticode;
   - create a release signing policy with manual approval.
3. In GitHub → Settings → Secrets and variables → Actions:
   - variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`;
   - secret `SIGNPATH_API_TOKEN` (a SignPath CI user token with submitter rights).
4. Push a tag such as `v1.0.1`. The workflow builds the exes, waits for the signing approval, and publishes the signed files.

Note: a newly signed app can still see a SmartScreen notice for its first downloads while its reputation builds up. Signing removes the "unknown publisher" warning and speeds that up.
