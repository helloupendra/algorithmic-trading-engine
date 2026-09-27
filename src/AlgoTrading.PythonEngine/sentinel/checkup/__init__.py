"""
The desk checkup: a checklist Sentinel runs at fixed times, and on request.

Sentinel's agents are alarms: something is wrong now, an incident opens. The
checkup is the round an operator makes before the open and after the close —
is the Dhan token good until the MCX close, did every planned run start, were
today's forecasts issued and scored, is anything left open that should not be,
is a restart owed, is the archive current — written down as one report where
every item that needs a person says what to do.

It reads, like the rest of Sentinel, and writes only its own table,
``desk_checkups``, which the console shows under System → Checkup. A report
goes to the desk's system channel on Telegram too, except the end-of-day one
when everything is in order (it would arrive after midnight every night) and
one a person asked for from the console (they are looking at it).

    sentinel/checkup/model.py    an item, a report, the verdict
    sentinel/checkup/checks.py   the checks, each a function of what it reads
    sentinel/checkup/slots.py    when each checkup runs and which checks it has
    sentinel/checkup/store.py    the desk_checkups table, and the read-only queries
    sentinel/checkup/report.py   the Telegram message
    sentinel/checkup/agent.py    the Sentinel agent that runs them
"""
