"""
Exchange bhavcopies: the five formats parse to one schema, a file that is not
a real session file is refused, a holiday is told apart from a data gap, and a
re-run never fetches a verified file twice.
"""

import io
import os
import tempfile
import unittest
import zipfile
from datetime import date

from research.equities import bhavcopy as bc

DAY = date(2025, 9, 2)
OLD_DAY = date(2023, 9, 1)

UDIFF_HEADER = ("TradDt,BizDt,Sgmt,Src,FinInstrmTp,FinInstrmId,ISIN,TckrSymb,SctySrs,XpryDt,FininstrmActlXpryDt,"
                "StrkPric,OptnTp,FinInstrmNm,OpnPric,HghPric,LwPric,ClsPric,LastPric,PrvsClsgPric,UndrlygPric,"
                "SttlmPric,OpnIntrst,ChngInOpnIntrst,TtlTradgVol,TtlTrfVal,TtlNbOfTxsExctd,SsnId,NewBrdLotQty,Rmks,"
                "Rsvd1,Rsvd2,Rsvd3,Rsvd4")


def udiff(day, exchange="NSE", count=3, symbol="20MICRONS", series="EQ"):
    lines = [UDIFF_HEADER]
    for i in range(count):
        sym = symbol if i == 0 else f"{symbol}{i}"
        lines.append(f"{day:%Y-%m-%d},{day:%Y-%m-%d},CM,{exchange},STK,{1000 + i},INE000000{i:03d},{sym},{series},,,,,"
                     f"{sym} LTD,233.13,237.00,231.31,232.56,232.00,231.98,,232.56,,,57705,13497571.2,2084,F1,1,,,,,")
    return "\n".join(lines) + "\n"


def nse_legacy(day, count=3):
    lines = ["SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN,"]
    for i in range(count):
        lines.append(f"STOCK{i},EQ,100,110,95,105,104.5,99,5000,525000,{day:%d-%b-%Y}".upper() + f",321,INE1{i:05d},")
    return "\n".join(lines) + "\n"


def bse_legacy(day, count=3):
    lines = ["SC_CODE,SC_NAME,SC_GROUP,SC_TYPE,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,NO_TRADES,NO_OF_SHRS,NET_TURNOV,"
             "TDCLOINDI,ISIN_CODE,TRADING_DATE,FILLER2,FILLER3"]
    for i in range(count):
        lines.append(f"{500002 + i},ABB LTD.    ,A ,Q,4376.00,4390.85,4272.45,4291.10,4291.10,4379.75,1020,5817,"
                     f"25070250.00,,INE117A01022,{day:%d-%b-%y},,")
    return "\n".join(lines) + "\n"


def delivery(day, count=3, symbol="20MICRONS"):
    lines = ["SYMBOL, SERIES, DATE1, PREV_CLOSE, OPEN_PRICE, HIGH_PRICE, LOW_PRICE, LAST_PRICE, CLOSE_PRICE, "
             "AVG_PRICE, TTL_TRD_QNTY, TURNOVER_LACS, NO_OF_TRADES, DELIV_QTY, DELIV_PER"]
    for i in range(count):
        sym = symbol if i == 0 else f"{symbol}{i}"
        lines.append(f"{sym}, EQ, {day:%d-%b-%Y}, 231.98, 233.13, 237.00, 231.31, 232.00, 232.56, 233.92, 57705, "
                     f"134.98, 2084, 30517, 52.88")
    lines.append(f"1018GS2026, GS, {day:%d-%b-%Y}, 110.45, 111.25, 111.25, 107.95, 108.90, 108.90, 109.48, 296, "
                 "0.32, 9, -, -")
    return "\n".join(lines) + "\n"


def zipped(text, name="file.csv"):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr(name, text)
    return buf.getvalue()


HTML_404 = b"<!DOCTYPE html>\n<html lang=\"en\"><body>Not found</body></html>"


class ParseTests(unittest.TestCase):
    def test_udiff_rows_normalise_every_price_and_size_field(self):
        row = bc.parse("nse_udiff", zipped(udiff(DAY, count=1)))[0]
        self.assertEqual("2025-09-02", row["trade_date"])
        self.assertEqual(("NSE", "20MICRONS", "EQ"), (row["exchange"], row["symbol"], row["series"]))
        self.assertEqual((233.13, 237.0, 231.31, 232.56), (row["open"], row["high"], row["low"], row["close"]))
        self.assertEqual(231.98, row["prev_close"])
        self.assertEqual((57705.0, 13497571.2, 2084.0), (row["volume"], row["turnover"], row["trades"]))
        self.assertIsNone(row["delivery_pct"])

    def test_bse_udiff_is_a_plain_csv_with_the_same_schema(self):
        row = bc.parse("bse_udiff", udiff(DAY, exchange="BSE", count=1, symbol="ABB", series="A").encode())[0]
        self.assertEqual(("BSE", "ABB", "A", "1000"), (row["exchange"], row["symbol"], row["series"],
                                                      row["security_id"]))

    def test_legacy_formats_parse_to_the_same_schema(self):
        nse = bc.parse("nse_legacy", zipped(nse_legacy(OLD_DAY, count=1)))[0]
        self.assertEqual(("2023-09-01", "NSE", "STOCK0", "EQ"), (nse["trade_date"], nse["exchange"], nse["symbol"],
                                                                  nse["series"]))
        self.assertEqual((105.0, 99.0, 5000.0, 525000.0), (nse["close"], nse["prev_close"], nse["volume"],
                                                          nse["turnover"]))
        bse = bc.parse("bse_legacy", zipped(bse_legacy(OLD_DAY, count=1)))[0]
        # BSE's legacy file pads names and groups with spaces and has no ticker.
        self.assertEqual(("2023-09-01", "BSE", "500002", "A", "ABB LTD."),
                         (bse["trade_date"], bse["exchange"], bse["security_id"], bse["series"], bse["name"]))
        self.assertEqual(5817.0, bse["volume"])

    def test_delivery_maps_symbol_and_series_and_dashes_become_none(self):
        out = bc.parse("nse_delivery", delivery(DAY).encode())
        self.assertEqual((30517.0, 52.88), out[("20MICRONS", "EQ")])
        self.assertEqual((None, None), out[("1018GS2026", "GS")])

    def test_an_html_error_page_or_a_non_zip_is_refused(self):
        with self.assertRaises(bc.NotABhavcopy):
            bc.parse("nse_udiff", HTML_404)
        with self.assertRaises(bc.NotABhavcopy):
            bc.parse("nse_delivery", HTML_404)


class CheckTests(unittest.TestCase):
    def test_a_real_session_file_passes_and_returns_its_row_count(self):
        self.assertEqual(600, bc.check("nse_udiff", zipped(udiff(DAY, count=600)), DAY))

    def test_a_file_with_a_handful_of_rows_is_not_a_session_file(self):
        # BSE serves a tiny UDiFF file for 01 Sep 2023; it must not count as that day.
        with self.assertRaisesRegex(bc.NotABhavcopy, "only 40 rows"):
            bc.check("bse_udiff", udiff(OLD_DAY, exchange="BSE", count=40).encode(), OLD_DAY)

    def test_a_file_for_another_date_is_refused(self):
        with self.assertRaisesRegex(bc.NotABhavcopy, "dated"):
            bc.check("nse_udiff", zipped(udiff(date(2025, 9, 1), count=600)), DAY)
        with self.assertRaisesRegex(bc.NotABhavcopy, "dated"):
            bc.check("nse_delivery", delivery(date(2025, 9, 1), count=600).encode(), DAY)


class PlanTests(unittest.TestCase):
    def test_the_format_tried_first_follows_the_switch_to_udiff(self):
        self.assertEqual(("nse_legacy", "nse_udiff"), bc.bhavcopy_sources("nse", date(2024, 7, 5)))
        self.assertEqual(("bse_udiff", "bse_legacy"), bc.bhavcopy_sources("bse", date(2024, 7, 8)))

    def test_a_weekday_session_fetches_nse_then_delivery_then_bse(self):
        results = {}
        order = []
        answers = {"nse_udiff": "ok", "nse_delivery": "ok", "bse_udiff": "ok"}
        while True:
            todo = bc.plan_day(DAY, results)
            if not todo:
                break
            order.append(todo[0])
            results[todo[0]] = answers[todo[0]]
        self.assertEqual(["nse_udiff", "nse_delivery", "bse_udiff"], order)

    def test_a_weekday_holiday_tries_both_formats_on_both_exchanges(self):
        results, order = {}, []
        while True:
            todo = bc.plan_day(date(2025, 8, 15), results)
            if not todo:
                break
            order.append(todo[0])
            results[todo[0]] = "missing"
        self.assertEqual(["nse_udiff", "nse_legacy", "bse_udiff", "bse_legacy"], order)

    def test_an_ordinary_weekend_costs_one_request(self):
        self.assertEqual(["nse_udiff"], bc.plan_day(date(2025, 9, 6), {}))
        self.assertEqual([], bc.plan_day(date(2025, 9, 6), {"nse_udiff": "missing"}))


class CoverageTests(unittest.TestCase):
    def test_verdicts(self):
        ok = {"nse_udiff": "ok", "nse_delivery": "ok", "bse_udiff": "ok"}
        self.assertEqual("session", bc.day_coverage(DAY, ok).verdict)
        closed = {"nse_udiff": "missing", "nse_legacy": "missing", "bse_udiff": "missing", "bse_legacy": "missing"}
        self.assertEqual("closed", bc.day_coverage(DAY, closed).verdict)
        self.assertEqual("gap: bse", bc.day_coverage(DAY, {**closed, "nse_udiff": "ok", "nse_delivery": "ok"}).verdict)
        self.assertEqual("gap: nse_delivery", bc.day_coverage(DAY, {**ok, "nse_delivery": "missing"}).verdict)
        failed = {**closed, "nse_udiff": "error"}
        self.assertEqual("gap: nse_udiff", bc.day_coverage(DAY, failed).verdict)
        self.assertEqual("not tried", bc.day_coverage(DAY, {}).verdict)


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.calls = []
        self.served = {
            bc.url_for("nse_udiff", DAY): (200, zipped(udiff(DAY, count=600))),
            bc.url_for("nse_delivery", DAY): (200, delivery(DAY, count=600).encode()),
            bc.url_for("bse_udiff", DAY): (200, udiff(DAY, exchange="BSE", count=1200, series="A").encode()),
        }

    def fetch(self, url):
        self.calls.append(url)
        return self.served.get(url, (404, HTML_404))

    def test_download_keeps_verified_files_and_a_rerun_fetches_nothing(self):
        bc.download(self.root, DAY, DAY, fetch=self.fetch, log=lambda _: None)
        self.assertEqual(3, len(self.calls))
        for source in ("nse_udiff", "nse_delivery", "bse_udiff"):
            self.assertTrue(os.path.exists(bc.raw_path(self.root, source, DAY)), source)
        self.assertEqual("session", bc.coverage(self.root, DAY, DAY)[0].verdict)

        self.calls.clear()
        bc.download(self.root, DAY, DAY, fetch=self.fetch, log=lambda _: None)
        self.assertEqual([], self.calls)

    def test_bse_holiday_page_served_with_200_counts_as_missing_not_as_a_gap(self):
        holiday = date(2025, 8, 15)
        self.served = {bc.url_for("bse_udiff", holiday): (200, HTML_404),
                       bc.url_for("bse_legacy", holiday): (200, HTML_404)}
        bc.download(self.root, holiday, holiday, fetch=self.fetch, log=lambda _: None)
        self.assertEqual("closed", bc.coverage(self.root, holiday, holiday)[0].verdict)

    def test_server_errors_are_retried_and_recorded_when_they_persist(self):
        self.served[bc.url_for("bse_udiff", DAY)] = (503, b"busy")
        manifest = bc.Manifest(self.root)
        attempt = bc.fetch_one(self.root, manifest, "bse_udiff", DAY, self.fetch, attempts=3, pause=0,
                               sleep=lambda _: None)
        self.assertEqual(("error", 503), (attempt.status, attempt.http))
        self.assertEqual(3, self.calls.count(bc.url_for("bse_udiff", DAY)))
        self.assertFalse(os.path.exists(bc.raw_path(self.root, "bse_udiff", DAY)))

    def test_normalise_merges_delivery_into_nse_rows(self):
        bc.download(self.root, DAY, DAY, fetch=self.fetch, log=lambda _: None)
        paths = bc.normalise(self.root, DAY, DAY, log=lambda _: None)
        self.assertEqual(2, len(paths))
        import csv
        import gzip
        with gzip.open(os.path.join(self.root, "normalized", "nse", "2025-09.csv.gz"), "rt") as fh:
            rows = list(csv.DictReader(fh))
        self.assertEqual(600, len(rows))
        first = next(r for r in rows if r["symbol"] == "20MICRONS")
        self.assertEqual(("30517.0", "52.88"), (first["delivery_qty"], first["delivery_pct"]))


if __name__ == "__main__":
    unittest.main()
