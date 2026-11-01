# Worker rules (all Rigbay workers)

## Project context (you only have the files, not the conversation)

Rigbay is one Windows host app that manages modules (separate repos) for many kinds of machines. Read `README.md` and `docs/DESIGN.md` in your worktree first; DESIGN.md is the source of truth. The author's machine is an Acer Predator Triton 500 SE (i7-11800H, RTX 3060, Windows 11) but nothing may be tied to it. GitHub org: https://github.com/rigbay-app (everything public, MIT).

## Rules that apply to you (the coordinator will check them)

1. **Hardware safety.** Reading is fine. Anything that WRITES to the machine (fan speed, performance/turbo mode, power or charge limits, EC/WMI methods that change state, drivers, services, registry that controls hardware) needs the user's approval for that specific write. Do NOT run any write. If you think one is needed, stop and ask through `csm gate open <task> "<exact call and value>" --options "approve,reject"`. A wrong fan or power write can overheat or damage hardware. When unsure whether a call is read-only, treat it as a write.
2. **Reverse engineering scope.** Only software installed on this machine, only to make a compatible module for this same machine. Do not commit, copy or redistribute Acer or Intel files or decompiled code. Reference installed files in place. Write findings in your own words and your own code. Short identifiers (class names, method names, ids, numeric ranges) are fine as facts; pasted code bodies are not.
3. **No GitHub actions.** Do not create, rename, transfer or push repositories, and do not change GitHub or organisation settings. Local commits on your branch are fine; end commit messages with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Git identity is already set.
4. **No system changes without approval.** Do not install SDKs/workloads/Visual Studio/winget packages, and do not change UAC, services, scheduled tasks you did not create for the test, or the registry outside what your task states. Ask through `csm gate open` first.
5. **Stay in your area.** One owner per area of the repo. Only touch the paths listed in your task; if you need a change elsewhere, mail the coordinator.
6. **Never capture the user's real screen.** For visual checks render the window itself (PrintWindow / `RenderTargetBitmap` / off-screen) and look at the PNG.
7. **Language.** Code, docs, README, commit messages in English. Your `csm note` lines (one line, at real checkpoints) and anything addressed to the user are in Vietnamese, short and concrete.
8. **Elevated processes.** The user's UAC auto-elevates for their admin account, so `Start-Process -Verb RunAs` works without a prompt. An elevated process cannot be stopped from a normal shell; use an elevated PowerShell (`Start-Process powershell -Verb RunAs -ArgumentList ...`). Only start processes you can stop again, and stop them when finished.
9. **Report.** When done, blocked or failed: `csm task done|fail ...` for your task, then `csm mail send job:rigbay-1 --type result --subject "<task id> <state>" --body "<short report>"`. Ask questions by mail, never with a question tool. Your last message must begin with `RESULT: done`, `RESULT: failed` or `RESULT: blocked`, followed by what you verified and what you did NOT verify. Do not claim something works unless you ran it and saw it work. Then stop; do not start other work.
