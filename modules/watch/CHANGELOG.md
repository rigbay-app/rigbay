# Changelog

## 0.1.0 (not released)

First version. Alerts on any reading of Rigbay's metrics bus: a rule says "this reading is at or above (or at or below) X for N seconds", and when that
happens Watch raises one Warning notification (a tray balloon, because Rigbay runs elevated). **Read only**: it subscribes to readings, keeps its rules in
its own settings and shows notifications; it publishes nothing, starts nothing and asks the host to approve nothing.

- Rules: at or above, at or below, rises by (a counter that went up within a time), has bits set (a status mask such as the GPU clock-limit reasons); a
  second limit that ends the alert (hysteresis); a quiet period after an alert; an optional "only while another reading is past a limit".
- One notification per episode. An episode that starts while alerts are paused or quiet is announced when that ends, if it is still open.
- Asks for readings with `SubscribeBackground` at the rule's own interval (default 10 s, 5 s to 1 h) and never with `Subscribe`, so it does not hold the
  publishers at their fast rate. A reading older than twice that interval is "no data": the rule neither fires nor clears and the page says "Waiting for ...".
- Page: the rules with their state (Quiet, Waiting, Past the limit for x of y, Firing since, last alert), add / edit / remove, a picker of every reading in the
  catalogue grouped by module with its value, six suggestions that are rules only after a click, a test alert, "Send alerts" (off = pause for an hour).
- Tray item "Pause alerts for 1 hour" (off until the owner asks for it on the page). `rigbay watch status | pause <minutes> | resume`, declared read only because
  pause and resume change only Watch's own state.
- Host API `>=0.1.4 <0.2` (background demand and verb metadata). No administrator needed.
