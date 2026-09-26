"""
Sentinel — the desk's own watchman.

It watches the running system the way an operator would on a good day, every
day: is the API answering, is the feed still printing, is every run the morning
plan asked for actually alive, is anything in the logs that was not there
yesterday, has anyone signed in who should not have. When something is wrong it
opens an incident — what happened, where, since when, with the evidence already
gathered — sends it to Telegram, and closes it again once the condition clears.

It is deliberately made of rules, not a model. Every rule it carries is one of
this desk's own past failures: the runners that died on the sign-in limiter, the
Dhan feed that reconnected without delivering a tick, the FYERS token that was
"authenticated" and dead, the tunnel that answered 502. Rules are cheap, run all
day, and are wrong in ways that can be read and fixed.

Two lines it never crosses:

* It changes nothing in production. It reads, and it writes only its own
  incidents. A fix is proposed in the incident; a person applies it.
* Nothing it reads is ever executed. Logs carry text from outside — news
  headlines, vendor messages — and a watchman that acted on what it read could
  be told what to do by a headline.

The agents live in ``sentinel.agents``; each one checks one part of the system
on its own cadence and returns findings. ``sentinel.engine`` turns findings into
incidents, de-duplicates them, and resolves them when they stop recurring.
"""
