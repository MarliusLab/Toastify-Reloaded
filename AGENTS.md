# Cross-platform maintenance

The user requires every functional change, fix and setting to be implemented for Windows, Linux and macOS together, always.

- Review all three applications for every change. Implement native equivalents where the operating systems differ; preserve existing settings, shortcuts and package formats.
- Prefer shared production code and shared regression tests for common behavior. Keep platform-specific integration explicit.
- Run the Windows, Linux and macOS build and packaging checks for the same source commit. Record native execution, cross-builds and interactive acceptance separately.
- Update the three-platform feature matrix and README with changes, tests and any concrete platform limitation. Do not describe an unimplemented feature as parity or quietly defer another platform.
- Keep existing release tags immutable. Release all affected platform updates from the tested source when the user authorizes publication; retain existing stable/prerelease channels.
- Do not change user configuration or install software on the development PC as part of automated tests. Use isolated fixtures and native CI runners.

The parent project's `sources/` directory remains read-only reference material.
