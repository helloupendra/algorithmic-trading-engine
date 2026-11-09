#!/usr/bin/env bash
# shellcheck disable=SC2034  # variables are read by the extracted deployment functions
set -euo pipefail
cd "$(dirname "$0")/../.."
python3 - <<'PY'
import sys, unittest
from datetime import datetime
sys.path.insert(0, 'scripts/lib')
from session_schedule import plan, build_schedule, IST
from unittest.mock import patch
from morning_checks import feed_verdict

def row(exchange, opened, closed, special=True):
    return dict(exchange=exchange, isTradingDay=True, sessionOpenUtc=opened,
                sessionCloseUtc=closed, specialSessionName='Muhurat' if special else None)

def special():
    return [row(e, '2026-11-08T14:20:00+05:30', '2026-11-08T'+c+':00+05:30')
            for e,c in [('NSE','15:40'),('BSE','15:40'),('MCX','15:30')]]

class ScheduleTests(unittest.TestCase):
    def test_sunday_muhurat(self):
        p=plan(special(), datetime(2026,11,8,13,50,tzinfo=IST))
        self.assertEqual((p['state'],p['openAt'],p['openUntil'],p['closeAt']),('trading','1350','1540','1543'))
        self.assertEqual((p['feedOpen'],p['feedJudge'],p['mastersUntil']),('1420','142130','1417'))
    def test_ordinary_weekend(self):
        self.assertEqual(plan([{'isTradingDay':False}]*3,datetime(2026,11,7,tzinfo=IST))['state'],'closed')
    def test_unknown_calendar_is_not_a_session(self):
        rows=special(); rows[0]['calendarWarning']='No holiday calendar for 2026'
        self.assertEqual(plan(rows,datetime(2026,11,8,tzinfo=IST))['state'],'unknown')
        self.assertEqual(plan([],datetime(2026,11,8,tzinfo=IST))['state'],'unknown')
    def test_mcx_only_evening(self):
        rows=[{'isTradingDay':False}]*2+[row('MCX','2026-11-10T17:00:00+05:30','2026-11-10T23:55:00+05:30',False)]
        p=plan(rows,datetime(2026,11,10,tzinfo=IST))
        self.assertEqual((p['state'],p['openAt'],p['feedOpen'],p['closeAt']),('trading','1630','1700','2358'))
    def test_mixed_special_and_regular_markets_do_not_close_early(self):
        rows=special(); rows[2]=row('MCX','2026-11-08T09:00:00+05:30','2026-11-08T23:55:00+05:30',False)
        self.assertEqual(plan(rows,datetime(2026,11,8,tzinfo=IST))['closeAt'],'2358')
    def test_monday_catches_up_sunday_close(self):
        closed=[{'isTradingDay':False}]*3
        with patch('session_schedule.fetch',side_effect=[closed,special()]):
            p=build_schedule('unused',datetime(2026,11,9,0,10,tzinfo=IST))
        self.assertEqual(p['closeDay'],'2026-11-08')
    def test_close_is_not_due_before_the_special_bell_and_grace(self):
        for hour,minute,expected in [(15,39,'-'),(15,42,'-'),(15,43,'2026-11-08')]:
            with patch('session_schedule.fetch',return_value=special()):
                p=build_schedule('unused',datetime(2026,11,8,hour,minute,tzinfo=IST))
            self.assertEqual(p['closeDay'],expected)
    def test_wrong_day_is_rejected(self):
        self.assertEqual(plan(special(),datetime(2026,11,9,tzinfo=IST))['state'],'unknown')
    def test_pre_special_quote_does_not_pass_feed_gate(self):
        now=datetime(2026,11,8,14,22,tzinfo=IST)
        sessions=plan(special(),now)['sessions']
        quote=dict(symbol='NSE:NIFTY50-INDEX',lastTradedPrice=100,exchangeTimestampUtc='2026-11-08T09:30:00+05:30')
        self.assertEqual(feed_verdict([quote],['NIFTY'],now,sessions)[0],'fail')
        quote['exchangeTimestampUtc']='2026-11-08T14:21:00+05:30'
        self.assertEqual(feed_verdict([quote],['NIFTY'],now,sessions)[0],'pass')
    def test_closed_equity_does_not_fail_valid_mcx_session(self):
        now=datetime(2026,11,10,17,5,tzinfo=IST)
        quote=dict(symbol='MCX:CRUDEOIL26NOVFUT',lastTradedPrice=100,exchangeTimestampUtc=now.isoformat())
        sessions={'NSE':None,'MCX':dict(open='2026-11-10T17:00:00+05:30')}
        self.assertEqual(feed_verdict([quote],['NIFTY','CRUDEOIL'],now,sessions)[0],'pass')

unittest.main()
PY
DESK_STATE_DIR="${TMPDIR:-/tmp}"
# Include the actual shell deployment guards, then enable a Sunday session.
eval "$(sed -n '/^DEPLOY_NOW_FILE=/,/^# Why deploy_allowed/{ /^# Why deploy_allowed/d; p; }' scripts/lib/desk-common.sh)"
DESK_SESSION_STATE=trading DESK_DEPLOY_UNTIL=1345
! deploy_clock_allows 7 1420 0
! deploy_allowed 7 1420 0 0
deploy_allowed 7 1300 0 0
! deploy_allowed 7 1300 0 1
