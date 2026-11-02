## What and why

<!-- One change, one reason. Link the issue if there is one. -->

## Checklist

- [ ] `dotnet build Rigbay.sln --no-incremental -warnaserror` is clean (0 warnings) and `dotnet test Rigbay.sln` passes. Totals: <!-- e.g. 1353 passed -->
- [ ] New behaviour has tests; a bug fix has a test that failed before.
- [ ] LangCheck is clean (`tools\ci\langcheck.ps1`); every new text is a language key with `en` and `vi`.
- [ ] `tools\ci\local-check.ps1` ran (or: the in-app self-test was skipped because ...).
- [ ] Screenshots of the window (rendered, not a capture of the whole screen) for any UI change, dark and light.
- [ ] Docs updated (`docs/`, README, CHANGELOG) where behaviour or a contract changed.
- [ ] No secrets, no vendor files, no personal paths or names in the diff.
- [ ] **Changes hardware: how it is confirmed and undone.** <!-- Delete this line if the change cannot write to the machine. Otherwise: which call and value, the approval flow used, how the result is read back and decoded (or "not verified"), how it is undone, on which machine and Windows build it was tried, and what was NOT tried. -->

## What I did not verify

<!-- Be honest: it saves the reviewer a round trip. -->
