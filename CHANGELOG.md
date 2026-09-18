# Changelog

All notable changes to TomLabs.UiAutomation, newest first. Versions come from `Directory.Build.props`.

## 0.1.1 — 2026-09-18

### Changed
- Both packages target .NET 10 (`net10.0`) instead of .NET 8, in line with every TomLabs project.

## 0.1.0 — 2026-09-18

### Added
- `TomLabs.UiAutomation`: `UiAutomationServer`, the in-app loopback HTTP channel (`/screenshot`, `/tree`, `/find`,
  `/click`, `/key`, `/type`, `/get`, `/set`, `/invoke`, `/do/<action>`, `/theme`, `/resize`, `/wait`, `/quit`),
  enabled by `<APP>_AUTOMATION=<port>`; `UsePlatformDetectOrHeadless(args)` for the `--headless` switch;
  `UiAutomationHost` for tests and custom hosts.
- `TomLabs.UiAutomation.Tool`: the `tomlabs-ui` dotnet tool (`start`, `stop`, `status`, `call`, `shot`, `tree`).
- Extracted from AppShelf, where the channel was first built.
