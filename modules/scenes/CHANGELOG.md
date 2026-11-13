# Changelog

## 0.1.0 (Unreleased)

First version. Not released: no real module's command has been run by a scene (see the README, "Not verified").

- A scene is the programs that start it plus up to 8 steps, each one command of another module with its exact arguments and an undo (the value the command reports it had before, a command you write, or nothing).
- One question per scene, from Rigbay itself, listing every step. After the answer the scene runs by itself each time one of its programs starts, with a notification from Rigbay for every run.
  Editing a scene (a step, an argument, an undo, a program) asks again; a rename does not.
- The programs are looked at every 5 s, only while a scene is on and approved or a run is open; the scene is put back 10 s after the last program ended, newest scene first.
- A Rigbay that restarted while a game ran: the open run is adopted while the game runs and ended when it is gone. Turning the module off puts back what an open scene changed.
- The host's refusals (rate limit, changed, revoked, too many runs, a failed step) are shown on the page and never retried in a loop.
- Page: scenes with state line, programs (typed or picked from the programs that have a window), step editor, *Approve*, *Run now*, *Stop*, *Revoke approval*, *Delete*.
- `rigbay scenes list` and `rigbay scenes status` (read only).
- Needs Rigbay host API `>=0.1.4 <0.2`. English and Vietnamese.
