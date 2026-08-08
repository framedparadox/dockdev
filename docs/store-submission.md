# Microsoft Store submission — dockdev

What has to be true before a submission, what the repository already enforces automatically, and
what only a human can do in Partner Center. Design doc §25 covers the build; this covers the
submission.

---

## 1. What is enforced automatically

These run in CI as ordinary unit tests (`tests/dockdev.Tests/Packaging/PackageManifestTests.cs`), so
they fail on a pull request rather than at upload:

| Check | Why it is a submission blocker |
|---|---|
| `Identity/Name` and `Publisher` are not tooling placeholders | A `Publisher` that isn't the account's exact publisher ID is rejected at upload, with an error naming neither field |
| `Identity/Version`'s fourth component is `0` | The Store reserves the revision component and refuses any package that sets it |
| Manifest version == `dockdev.csproj` `<Version>` == `<AssemblyVersion>` == `app.manifest` `assemblyIdentity` | Otherwise the listing, Settings ▸ About and Apps & features report three different builds |
| Every logo the manifest names exists on disk | Packaging otherwise fails inside `makeappx` with a message that names a temp path |
| `DisplayName` agrees between `Properties` and `VisualElements` | Start menu, installed-apps list and listing must read as one product |
| `runFullTrust` is declared, and nothing broader is | `Windows.FullTrustApplication` cannot deploy without it; anything past it lengthens review |
| `windows.startupTask` `TaskId` matches `StartupService.TaskId`, and ships `Enabled="false"` | A mismatch makes the "start with Windows" toggle a silent no-op; enabled-by-default autostart is a policy problem |
| `Application/Executable` matches `<AssemblyName>.exe` | Deployment fails with "the app didn't start" |
| `TargetDeviceFamily/MinVersion` matches `<TargetPlatformMinVersion>` | The Store would offer the app to devices the build does not support |
| `app.manifest` requests `asInvoker`, `uiAccess="false"` | A packaged app cannot request elevation; the Store rejects one that does |
| `app.manifest` declares `PerMonitorV2` | Windows' baseline for a desktop app; without it the dock is bitmap-stretched across mixed-DPI monitors |

## 2. Building the package

```powershell
./scripts/package-store.ps1
```

Verifies the manifest, then produces `artifacts/store/…_x64_arm64_bundle.msixupload`.

- **Both architectures in one bundle.** `AppxBundlePlatforms=x64|arm64`. Without it the bundle
  carries only the platform being built, and Windows-on-ARM users get the x64 build under emulation.
- **Unsigned on purpose.** The Store re-signs every submission with the account's own certificate,
  and a package already signed with a different one is rejected. `-CertificateThumbprint` exists
  only for producing a sideloadable build to test; that artifact is not what gets uploaded.
- **Symbols off by default** (`AppxSymbolPackageEnabled=false`) to avoid the `mspdbcmf.exe`/MSB6011
  failure on a plain SDK install. Turn it on when a crash-analysis feed is wanted.

Sideload the signed build and confirm, on a machine that has never run dockdev:

- [ ] It launches, the dock appears, and a tool window opens.
- [ ] Settings ▸ **Start with Windows** turns on, and the entry appears in Task Manager ▸ Startup
      apps. This is the packaged-only code path (`StartupTask`, not the `Run` key) and the one most
      likely to be broken without anyone noticing — see `PackagedRuntime`.
- [ ] Settings ▸ About shows **no** update-check card. The Store delivers updates; a packaged build
      pointing at GitHub Releases is a distribution route outside the Store.
- [ ] Uninstall leaves nothing behind but `%AppData%\dockdev`, which is the user's own data.

## 3. What only a human can supply in Partner Center

| Field | Value |
|---|---|
| **Privacy policy URL** | `https://github.com/framedparadox/dockdev/blob/HEAD/docs/privacy-policy.md` — required for every submission, and the same document the in-app Settings ▸ About link opens |
| **Category** | Developer tools |
| **Age rating** | Complete the IARC questionnaire. dockdev has no user-generated content, no communication features, no purchases and no data collection |
| **Supported languages** | English (United States) only, deliberately — see below |
| **Screenshots** | At least one 1366×768 or larger. The dock plus one open tool window is the honest picture of what the app is |
| **Description** | Must not claim compliance certification for the Data Masker (design doc §29 risk 2). It finds and masks common personal data; it does not make anything "GDPR compliant" |
| **Pricing** | Free |

### Why the listing declares one language

The app ships eight UI languages and follows the app-level language setting. `Package.appxmanifest`
declares `en-US` only, because Store policy 10.7 requires the *listing* to be translated into every
language the package declares. Declaring one keeps that promise honest; the app being multilingual
is a feature described in the English listing, not a set of promises about translated store pages.

## 4. Policies worth re-reading before each submission

- **10.1.1 Distinct function & value** — dockdev is sixteen tools behind one dock, all working
  offline. The listing should lead with that rather than with the tool count.
- **10.5.1 Personal information** — a privacy policy is required whether or not data is collected,
  and it must be reachable from the listing *and* from inside the app. Both point at the same file.
- **10.2.1 Security** — no elevation, no code download, no execution of downloaded code. dockdev
  downloads nothing at all in the packaged build; the update check is hidden there.
- **10.8.x Notifications / advertising** — none present, nothing to declare.
- **Capability justification** — `runFullTrust` is the only one, and it is the standard capability
  for a Win32 desktop app packaged as MSIX. If review asks: the app uses DWM window attributes, a
  notification-area icon and `RegisterHotKey`, none of which has a WinRT equivalent.

## 5. Known non-blockers, recorded so they are not re-litigated

- **Unplated icon assets.** Only the plated `Square44x44Logo.targetsize-*` variants ship. Windows
  falls back to plating them for the taskbar and Start, which is correct but not ideal. Adding
  `altform-unplated` variants is a design task, not a compliance one.
- **`MaxVersionTested`.** The value in the manifest is documentation; the packaging targets
  overwrite it from `$(TargetPlatformVersion)`. Keep it in step with the csproj's TFM anyway, so
  the file reads truthfully.
