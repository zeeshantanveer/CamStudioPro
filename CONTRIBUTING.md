# Contributing to CamStudio Pro

Thanks for helping! Bug reports, fixes, features, docs, and testing on more phones are all welcome.

## Ways to help

- **Report a bug** or **suggest a feature** using the [issue templates](https://github.com/zeeshantanveer/CamStudioPro/issues/new/choose).
- **Test on your phone** and report the model, Android version, and what worked or didn't.
- **Improve docs**: clearer setup steps, screenshots, and translations.
- **Send a pull request**. For larger changes, open an issue first so we can agree on the approach.

## Development setup

| Part | Requirements | Build |
|---|---|---|
| Windows app | Windows 11, .NET 10 SDK | `windows\build.ps1` |
| Android app | JDK 17, Android SDK 35+ | `android\gradlew.bat -p android :app:assembleDebug` |
| Editor / streaming | FFmpeg on `PATH` or in `ffmpeg\` next to the app | |

No phone? `python windows\tools\fake_phone.py --pair-key <key>` streams a test pattern over the real
protocol (see the README).

The phone and PC talk over the protocol in [docs/PROTOCOL.md](docs/PROTOCOL.md). If you change the
protocol, update that file and keep older app versions working where you can.

## Tests

Smoke tests live in `windows/tests/`. Run the ones that cover your change, for example:

```powershell
dotnet run --project windows/tests/RecordingSmoke -c Release -p:Platform=x64
dotnet run --project windows/tests/LiveStreamSmoke -c Release -p:Platform=x64 -- --policy
android\gradlew.bat -p android :app:testDebugUnitTest
```

## Pull requests

1. Fork the repo and create a branch from `main`.
2. Keep each PR focused on one change, and describe what you changed and how you tested it.
3. Never commit secrets: signing keys, `keystore.properties`, `store-identity.json`, stream keys, or
   recordings of real people. The `.gitignore` already excludes these.
4. By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).

Please follow our [Code of Conduct](CODE_OF_CONDUCT.md).
