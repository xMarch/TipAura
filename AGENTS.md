# Documentation

`README.md` is the human-facing user introduction and usage guide. `TECHNICAL.md` is the primary development and agent-maintenance reference for current behavior contracts, implementation details, the encounter YAML schema, threading, audio, settings compatibility, diagnostics, validation and build/release internals. Read the relevant sections before changing these areas, and update them when the implementation or behavior changes. Update `README.md` when user-facing requirements, usage or visible behavior changes; keep internal specifications, agent instructions and detailed validation procedures in `TECHNICAL.md` rather than adding them to the README. Link to the technical reference instead of duplicating precise specifications. If documentation conflicts with the implementation, investigate and correct the mismatch. Keep the disclaimer in both documents aligned with `Disclaimer` in `Ui/AuxiliaryWindows.cs`.

# Agent-only self-tests

Self-test and command-line smoke-test code and entry points must be guarded by `#if TIPAURA_AGENT_SELF_TEST`. Enable this symbol only for validation during agent work with `-p:EnableAgentSelfTests=true`, for example `dotnet run -c Release -p:EnableAgentSelfTests=true -- --self-test`. The property defaults to false in every configuration so normal builds and Native AOT releases exclude test code before trimming. Keep new test-only helpers and native declarations inside the same guard. Publish production artifacts without enabling the property; if agent validation needs an AOT test build, enable it explicitly and use a separate output directory such as `artifacts/aot-agent-tests/`.

Smoke tests must not type into whatever window has focus: the real-hook test sends the keypad 5 key as non-extended `VK_CLEAR` (NumLock-off form), never Numpad digits. Audio tests play at volume 0.

# Native AOT on this machine

The ILC linker step needs `vswhere.exe`. If `dotnet publish -p:PublishAot=true` fails with "'vswhere.exe' is not recognized", prepend `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it from PowerShell.
