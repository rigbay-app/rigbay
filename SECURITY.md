# Security policy

## Reporting a vulnerability

Please report security problems **privately**, not in a public issue:

1. Open <https://github.com/rigbay-app/rigbay/security/advisories/new> (GitHub private vulnerability reporting).
2. Say what you found, which version, and how to reproduce it. Do not include real secrets or other people's data.

If that page is not available to you, open a public issue that says only "I have a security report, please enable a private way to send it" and **nothing
else about the problem**. A maintainer will open a private channel.

This is a small, volunteer project: expect an acknowledgement within about a week and a fix or an honest "not planned, because ..." after that. Credit is given if you want it.

## Supported versions

Rigbay is pre-release. Only the **latest release** (currently 0.1.x) and `main` get fixes. Modules are separate repositories with their own versions
(`rigbay-app/module-*`); each is supported at its latest release, and a bad release is removed from the [registry](https://github.com/rigbay-app/registry).

## What is in scope

Modules are loaded **into the app's process, which runs as administrator**. That is the main thing to keep in mind: a module is code with administrator rights.
The host shows what a module declares it uses; that is information, not a sandbox.

In scope:

- The host app (`Rigbay.App`, `Rigbay.Core`, `Rigbay.Abstractions`): for example a way to make it run code, write files, or change settings without the user installing or approving it.
- The installer and the update path: a module zip that is accepted although its SHA-256 or its declared contents do not match the registry, path traversal on extract, a downgrade or a replacement that the app should have refused.
- The approval flow for hardware changes: any way a module can change hardware without the user's approval for that call and value.
- The registry (`rigbay-app/registry`) and the first-party modules (`module-predatorsense`, `module-xtu`, `module-recorder`, `module-steam-bypass`): for example a module that writes more than it declares.
- Secrets or personal data committed to, or shipped in, any of these repositories.

Out of scope (please do not report them as vulnerabilities):

- "A module you chose to install and run as administrator can do harmful things." That is the documented trust model; install only modules you trust.
- Findings that need an attacker who already has administrator rights on the machine.
- Missing code signing: the releases are not signed yet and say so; compare the published SHA-256.
- Vulnerabilities in third-party software (Windows, the .NET runtime, vendor services). Report those to their owners.
- Denial of service by a module that you installed yourself.

Please do not run tests against machines or networks you do not own.
