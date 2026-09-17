"""
Option expiry calendar: every bhavcopy layout (NSE old and UDiFF, BSE old and UDiFF)
yields its index option expiries, the archive URLs follow each exchange's naming,
and a date counts as an expiry only when its own bhavcopy lists it.
"""

import io
import unittest
import zipfile
from datetime import date

from tools import option_expiry_calendar as oec


def zipped(name, text):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as z:
        z.writestr(name, text)
    return buffer.getvalue()


NSE_OLD = (
    "INSTRUMENT,SYMBOL,EXPIRY_DT,STRIKE_PR,OPTION_TYP,OPEN,HIGH,LOW,CLOSE,SETTLE_PR,CONTRACTS,VAL_INLAKH,OPEN_INT,CHG_IN_OI,TIMESTAMP,\n"
    "OPTIDX,NIFTY,04-Mar-2021,12150,CE,0,0,0,2673,15080.75,0,0,450,0,04-MAR-2021,\n"
    "OPTIDX,BANKNIFTY,10-Mar-2021,35000,PE,0,0,0,1,1,0,0,0,0,04-MAR-2021,\n"
    "FUTIDX,NIFTY,25-Mar-2021,0,XX,0,0,0,1,1,0,0,0,0,04-MAR-2021,\n"
    "OPTSTK,RELIANCE,25-Mar-2021,2000,CE,0,0,0,1,1,0,0,0,0,04-MAR-2021,\n"
)
UDIFF = (
    "TradDt,BizDt,Sgmt,Src,FinInstrmTp,FinInstrmId,ISIN,TckrSymb,SctySrs,XpryDt,FininstrmActlXpryDt,StrkPric,OptnTp\n"
    "2025-01-10,2025-01-10,FO,BSE,IDO,1,,SENSEX,,2025-01-14,2025-01-14,77000,CE\n"
    "2025-01-10,2025-01-10,FO,BSE,IDF,2,,SENSEX,,2025-01-31,2025-01-31,,\n"
    "2025-01-10,2025-01-10,FO,BSE,IDO,3,,BANKEX,,2025-01-27,2025-01-27,56000,PE\n"
)
BSE_OLD = (
    "Contract Type,Symbol,Expiry,Strike,Option Type,Open\n"
    "IO,SENSEX2380465800CE,04 Aug 2023,65800.00,CALL,60.00\n"
    "IF,SENSEX23AUGFUT,25 Aug 2023,,,1\n"
)


class ExpiryCalendarTests(unittest.TestCase):
    def test_every_layout_yields_its_index_option_expiries(self):
        self.assertEqual(oec.parse_expiries(zipped("fo04MAR2021bhav.csv", NSE_OLD), "NSE"),
                         {("NIFTY", date(2021, 3, 4)), ("BANKNIFTY", date(2021, 3, 10))})
        self.assertEqual(oec.parse_expiries(UDIFF.encode(), "BSE"),
                         {("SENSEX", date(2025, 1, 14)), ("BANKEX", date(2025, 1, 27))})
        self.assertEqual(oec.parse_expiries(zipped("bhavcopy04-08-23.csv", BSE_OLD), "BSE"),
                         {("SENSEX", date(2023, 8, 4))})
        # Old BSE files with bare carriage returns between lines.
        self.assertEqual(oec.parse_expiries(zipped("bhavcopy04-08-23.csv", BSE_OLD.replace("\n", "\r")), "BSE"),
                         {("SENSEX", date(2023, 8, 4))})

    def test_archive_urls_follow_each_exchanges_naming_and_format_switch(self):
        self.assertEqual(oec.nse_urls(date(2021, 3, 4))[0],
                         "https://nsearchives.nseindia.com/content/historical/DERIVATIVES/2021/MAR/fo04MAR2021bhav.csv.zip")
        self.assertEqual(oec.nse_urls(date(2024, 7, 8))[0],
                         "https://nsearchives.nseindia.com/content/fo/BhavCopy_NSE_FO_0_0_0_20240708_F_0000.csv.zip")
        self.assertEqual(oec.bse_urls(date(2023, 8, 4))[0],
                         "https://www.bseindia.com/download/Bhavcopy/Derivative/bhavcopy04-08-23.zip")

    def test_an_expiry_counts_only_when_its_own_day_lists_it(self):
        days = {
            date(2024, 5, 16): {("NIFTY", date(2024, 5, 16)), ("NIFTY", date(2024, 5, 23))},
            # 23 May listed a week earlier, but the exchange moved it: that day's file no longer has it.
            date(2024, 5, 22): {("NIFTY", date(2024, 5, 22)), ("NIFTY", date(2024, 5, 30))},
            date(2024, 5, 23): {("NIFTY", date(2024, 5, 30))},
        }
        confirmed = oec.calendar(days)
        self.assertEqual(confirmed, {"NIFTY": [date(2024, 5, 16), date(2024, 5, 22)]})
        self.assertEqual(oec.unconfirmed(days, confirmed, date(2024, 5, 23)), {"NIFTY": [date(2024, 5, 23)]})


if __name__ == "__main__":
    unittest.main()
