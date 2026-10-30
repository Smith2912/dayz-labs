# Follow-up blindspot fixes — October 29, 2026

This report covers the eleven findings from the independent follow-up review of this fork.
The implementation is the commit containing this report. Changes are maintained on the
fork's `dev` branch; no upstream pull request or release is required.

| ID | Implemented correction | Regression evidence |
| --- | --- | --- |
| B01 | Dictionary sessions bind loaded paths and revisions. Stale base-list and combo mutations are rejected. | Core dictionary session test and tray manager test switch between disposable missions and preserve the second mission. |
| B02 | Reject duplicate Type names within a target file before any backup/write. | Core collision test preserves disk bytes; tray collision test preserves draft and undo. |
| B03 | Dirty Types drafts block restore at the VM boundary; the UI offers Save/Discard/Cancel. Restore also validates expected mission and full backup directory. | Tray restore test preserves dirty rows, history and disk, then restores after explicit discard. |
| B04 | Return revisions of actually committed files on partial save; the VM retains those revisions alongside its draft. Partial saves remain explicitly reported. | Core and tray tests fail one file's backup, repair the fixture, and successfully retry without discarding edits. This is retry recovery, not an all-files atomic transaction. |
| B05 | Pass canonical `--config` into monitor arguments; consume it before `DZL_CONFIG`; check the current config-scoped singleton. | Core argument/mutex test; actual app startup with conflicting explicit/environment configs captures only under the explicit config and exits 0. |
| B06 | Distinguish successful creation from exit/timeout in de-elevation. A started child never triggers fallback creation. | Launch-outcome tests cover running child, exited child and creation failure. Actual elevated Windows token creation was not exercised. |
| B07 | Process storage uses the canonical config directory. Global saves target the resolved file, preserving file links. Identity lookup uses directory handles so it does not hold a replaceable regular config open. | Real directory-junction state/config test and a 100-update concurrent-config test pass. The real file-symlink test is explicitly skipped on this host because Windows denies link creation privilege. |
| B08 | Single builds, packs and engine invocations use unique staging trees. Old failed-cleanup files are never overlaid into a new build. | Synthetic engine test holds an obsolete staging file without delete sharing, then verifies it is absent from the new PBO. |
| B09 | Disable cache reuse/receipt creation for binarized builds until external dependency inputs can be tracked. Non-binarized reuse remains available. | Service cache tests cover the conservative rebuild and valid non-binarized reuse. |
| B10 | Incremental extraction checks output digests; missing/corrupt files force extraction. Verify / repair bypasses reuse. Extraction must produce expected files and lengths before receiving a new receipt. | Disposable PBO/extractor-adapter tests cover missing files, same-length corruption, forced repair, and false-success extraction without output. Native BankRev was not run. |
| B11 | Validate stored PBO headers, names, prefix, payload sizes, trailer and SHA-1 container checksum. Detect changed input lengths; verify before publishing and reusing cache output. Cache schema updated to v3. | Tests reject changed packing inputs, payload corruption, truncation, wrong prefix and malformed cached output, preserving the prior output on packing failure. |

## Verification

- Full solution Debug build: 0 warnings, 0 errors.
- Core suite: **754 passed, 1 skipped**.
- Tray suite on self-contained Windows .NET 8: **216 passed, 0 skipped**.
- App smoke: exit 0 with `--config` overriding a conflicting `DZL_CONFIG`; one screenshot
  under the explicit fixture config and none under the environment config.
- Website production build and whitespace validation are checked before committing.

Commands:

```powershell
dotnet build Dzl.sln -c Debug --no-restore
dotnet test tests/Dzl.Core.Tests/Dzl.Core.Tests.csproj --no-restore
dotnet test tests/Dzl.Tray.Tests/Dzl.Tray.Tests.csproj -r win-x64 -p:SelfContained=true --no-restore
npm --prefix web run build
git diff --check
```

Behavior-focused regressions live in
[`BlindspotRegressionTests.cs`](../../tests/Dzl.Core.Tests/BlindspotRegressionTests.cs) and
[`MissionOwnershipRegressionTests.cs`](../../tests/Dzl.Tray.Tests/MissionOwnershipRegressionTests.cs).
The symlink fact probes the host and records an explicit skip when privilege is unavailable;
it does not treat an unexecuted native scenario as passed.

## Remaining proof boundaries

All eleven source corrections are implemented. Unit/integration fixtures do not establish
native DayZ build/gameplay, BI signature acceptance, or installed-release behavior. Archive
checksum validation is separate from cryptographic signature verification. Binarized builds
intentionally trade cache speed for correct handling of unresolved external inputs. Legacy
extraction receipts without output digests trigger extraction again; verifying large output
trees performs real disk reads and is not described as instant.

**Candidate/artifact:** this source commit, its self-contained tray candidate, and the new
regression tests. **Owner:** Codex, with the machine owner providing any required host
privileges. **Next proof:** run the file-symlink regression on a Windows host already
permitted to create such links; separately qualify linked-token creation and native
BankRev output in disposable environments. **Pass evidence:** an unskipped alias test,
one correctly scoped limited-token child, and verified extraction output. No privileges
were changed, live game processes started, or native mods signed/deployed for this work.
