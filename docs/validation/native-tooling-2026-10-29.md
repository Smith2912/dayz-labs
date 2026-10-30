# Native tooling and elevated-token validation — October 29, 2026

## Scope and result

Local Windows validation used installed Steam DayZ Tools (app 830640, build
25980502), disposable source/output trees, and disposable signing keys. Tests ran
from a workspace whose path contains spaces and an emoji. No game/server was
launched, P: mount changed, live content deployed, or installer produced.

| Boundary | Observed result |
| --- | --- |
| CfgConvert | Valid config converted to raP binary and back to text; invalid input rejected by both wrapper and build engine. |
| BankRev | Extracted our stored PBO; unchanged output reused; corrupted and missing output repaired. |
| Binarize | Original synthetic triangle converted from MLOD to ODOL; engine packed it alongside binary config. |
| FileBank / AddonBuilder | Both packed isolated config-only sources, preserving the output archive name; resulting PBO checksums and entries verified. AddonBuilder used pack-only mode. |
| ImageToPAA | Converted an original 16×16 TGA texture to nonempty PAA output. |
| DSCreateKey / DSSignFile | Disposable key pair and signature produced; existing key pair protected from overwrite; signed engine build verified. |
| DSCheckSignatures + independent verification | Valid signature accepted; changed payload rejected, including after recomputing the PBO checksum. |
| Elevated parent | TokenElevationType Full (2); system child returned 17; receipt-writing children were non-elevated Limited (3). |
| Zero-wait launch | Exactly one receipt-writing child; no fallback duplicate. Waited launch returned 17; spaced and trailing-backslash arguments survived. |
| File symlink identity | Existing file-alias process-storage regression passed unskipped under the elevated test process. |

The synthetic Binarize context emitted preload/terrain warnings. ODOL/config.bin
output and archive integrity were checked; this does not establish model quality,
script compilation, real-game loading, multiplayer, or production mod acceptance.

## Corrections discovered through native execution

- Legacy tools misread Unicode directory arguments. Short-lived ASCII junction
  aliases now preserve relative includes and route output to the original directory.
  Cleanup removes only owned aliases. Non-ASCII file basenames and shell
  metacharacters in paths requiring aliases fail explicitly. If the normal temp
  location is non-ASCII, set `DZL_NATIVE_TEMP` to a private ASCII directory.
- CfgConvert could return success without producing output. Conversion now checks
  a unique temporary output, requires binary magic, and publishes only on success.
  Requested binary config conversion fails the build instead of retaining raw cpp.
- FileBank, ImageToPAA and AddonBuilder also failed on Unicode directory paths;
  their wrappers now use ASCII aliases. Pack wrappers verify output checksums
  rather than trusting exit status alone (AddonBuilder reported success despite
  a failed output copy). Source leaf names are preserved for archive naming.
- The installed signature checker accepted a stale signature after the container
  checksum was recomputed. The wrapper now independently hashes the current PBO
  and verifies its first BI RSA/SHA-1 signature against a trusted public `.bikey`,
  then requires native checker success. Signed builds also verify their payload
  against the sibling public key. A container checksum alone is not authentication.
- Duplicating the elevated parent's linked token failed with Win32 1346 on this
  host; the previous fallback then ran elevated. Launch now duplicates the actual
  non-elevated interactive shell token in the same Windows session and fails
  closed if that cannot be established. It does not enable privileges or edit ACLs.
- The full regression run exposed equal-timestamp backup ordering. Listing and
  pruning now use the snapshot filename as a deterministic secondary ordering;
  the regression forces a timestamp tie.

## Verification and reproduction

- Default core suite before adding two additional native facts: **761 passed, 5 skipped** (four opt-in native facts and one
  file-symlink privilege gate).
- Final full opt-in core suite: **767 passed, 1 skipped**, including **6 native
  facts passed** against the installed binaries. The remaining skip is the
  file-symlink privilege gate, qualified separately below. Default runs skip the
  six native facts unless explicitly enabled.
- Elevated file-alias run: **1 passed, 0 skipped**.
- Tray suite: **216 passed, 0 skipped**.
- Solution Debug build and standalone native probe build: **0 warnings, 0 errors**.
- Final UAC probe: `Passed: true`, two limited-token receipt children, one child
  from the zero-wait call, exit code 17 from both waited calls, no diagnostics.

Behavior tests are in `tests/Dzl.Core.Tests/NativeToolIntegrationTests.cs` and
`NativeBoundaryRegressionTests.cs`. Native tests are opt-in because they execute
installed tools and generate private disposable keys. Keep their artifact root
outside the checkout; never commit the generated keys.

```powershell
$env:DZL_NATIVE_TOOLS = 'C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools'
$env:DZL_NATIVE_ARTIFACTS = 'D:\private-test-artifacts\dayz-native'
dotnet test tests/Dzl.Core.Tests/Dzl.Core.Tests.csproj --filter FullyQualifiedName~NativeToolIntegrationTests
```

`tests/Dzl.NativeProbe` is a Windows-only, manually invoked helper. Build it,
launch its DLL with `dotnet.exe` using `Start-Process -Verb RunAs`, and pass
`--parent` plus a fresh empty receipt directory. UAC consent is required. The
helper only launches itself and `cmd.exe /c exit 17`; the parent writes
`parent-result.json` and exits nonzero if any assertion fails. It does not start
games or manipulate drives. The privilege-dependent file test can similarly be
run in an elevated test process with filter
`FullyQualifiedName~File_aliases_share_process_storage`.

Raw TRX, token receipts, native logs, and executable SHA-256/version provenance
were retained outside the checkout in the local review evidence directory.
They include machine paths and disposable private keys and are not repository
artifacts. This report records the observed results without shipping those keys.

Windows API references: [GetShellWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow),
[DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex),
[CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw).

Remaining acceptance boundaries: real DayZ client/server behavior, production
asset/dependency coverage, game-side signature enforcement, P: visibility in an
actual game session, interactive GUI tool workflows (Workbench, Object Builder,
terrain editors and Publisher), and installer/update/uninstall behavior. Binarized build
cache reuse remains disabled until external dependency inputs can be tracked.
