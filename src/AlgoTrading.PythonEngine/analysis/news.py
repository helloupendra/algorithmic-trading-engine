'''
analysis/news.py

Phase 3: a score for every headline (news_items) and NSE announcement
(corporate_announcements), written next to the row, so the morning's
forecasts can record what was known by 08:50 and, once there is history,
whether it helps.

    python -m analysis news-score [--limit N] [--dry-run]

Free and local: no API key, no paid service, nothing leaves the server.

  sentiment    FinBERT (ProsusAI/finbert at a pinned revision) on the CPU:
               P(positive) - P(negative), -1..1. FinBERT is BERT fine-tuned on
               English financial news (the Financial PhraseBank); it knows
               nothing Indian in particular, reads 'Sensex tanks 800 points'
               well and a SEBI circular's legal English poorly, and gives most
               announcements 'neutral'. Its training ended years before these
               headlines, so a late score cannot know what happened next.
  symbols      rules (analysis/symbols.py): NSE cash-equity tickers and
               company names from the instrument master, plus the row's own
               company for an announcement
  importance   rules (analysis/newsrules.py): keywords, the announcement's
  and topics   subject line and the source

ScoreModel records which: 'finbert@<revision>+rules-v1', or
'failed:<reason>' for an item whose output could not be validated twice (it
is not retried again). ScoredUtc is when the item was scored or given up on;
a feature may use a score only if ScoredUtc is before its own cutoff.

The API runs this every 10 minutes. It is sized for the 2-vCPU, 8 GB server
that also trades: one model, loaded once per run, one CPU thread, batches of
16 headlines cut at 128 tokens, a niced process, and at most MAX_LIMIT items a
run. A backlog (after a day the API was down) is worked off over runs.

Headlines are untrusted text. Here that means: they are only ever model input
and SQL parameters, never code or SQL; their length is capped before the
tokenizer sees them; and what is written back is validated against fixed
ranges and vocabularies (a sentiment in -1..1, importance 0-3, tickers of
the master's shape, topics from newsrules.TOPICS).

The API's table-contract test reads every double-quoted capitalised word in
this file as a column of those two tables, so only column names are written
in double quotes here, and the SQL is spelled out in full (see _SELECT).
'''

# No __future__ import of annotations here: NewsTablesContractTests reads the
# word after every FROM, in any case, as a table name, and __future__ is not one.
import json
import logging
import math
import os
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, List, Optional, Sequence, Tuple

from analysis import newsrules, symbols
from analysis.data import DataError, _aware, _rollback

log = logging.getLogger('analysis.news')

FINBERT_MODEL = 'ProsusAI/finbert'
#: The Hugging Face commit the weights are read at: the model cannot change under the scores.
FINBERT_REVISION = '4556d13015211d73dccd3fdd39d39232506f3e43'
SCORE_MODEL = f'finbert@{FINBERT_REVISION[:12]}+{newsrules.RULES_VERSION}'
#: Where the weights are kept (~440 MB, downloaded on the first run): data/models/ in the repo, ignored by git,
#: so the API's service user needs no home directory and a redeploy keeps them.
MODEL_DIR = Path(__file__).resolve().parents[3] / 'data' / 'models' / 'huggingface'

MAX_TOKENS = 128             # a headline is ~20 tokens; an announcement's details are cut here
MAX_TEXT_CHARS = 2000        # untrusted text is cut before the tokenizer sees it
BATCH_SIZE = 16
TORCH_THREADS = 1            # the server has two vCPUs and the trading desk needs one
DEFAULT_LIMIT = 200
MAX_LIMIT = 1000
#: pg_try_advisory_lock key, the word NEWS in ASCII: two runs never score the same rows.
LOCK_KEY = 0x4E455753
#: The sum of three probabilities may drift this far from 1 and still be read.
_PROB_TOLERANCE = 1e-3

# ------------------------------------------------------------- the tables --

NEWS_TABLE = 'news_items'
ANNOUNCEMENTS_TABLE = 'corporate_announcements'

# The SQL is written out in full, table and column names literal, because the
# API's contract test (NewsTablesContractTests) reads it from this file: the
# tables after FROM and UPDATE, every double-quoted name, and what follows SET,
# which must be the six score columns and nothing else — least of all
# FirstSeenUtc, the stamp that makes a replayed forecast honest.

#: The oldest never-scored rows first. Never scored is ScoredUtc IS NULL; a given-up item has it filled in.
_SELECT = {
    NEWS_TABLE: 'SELECT "Id", "Source", "Category", "Title", "Summary", "FirstSeenUtc" FROM news_items '
                'WHERE "ScoredUtc" IS NULL ORDER BY "FirstSeenUtc", "Id" LIMIT %s',
    ANNOUNCEMENTS_TABLE: 'SELECT "Id", "Exchange", "Symbol", "Company", "Subject", "Details", "FirstSeenUtc" '
                         'FROM corporate_announcements '
                         'WHERE "ScoredUtc" IS NULL ORDER BY "FirstSeenUtc", "Id" LIMIT %s',
}

#: One row's six score columns, only if nobody scored it meanwhile.
_UPDATE = {
    NEWS_TABLE: 'UPDATE news_items SET "Sentiment" = %s, "Importance" = %s, "Symbols" = %s, "Topics" = %s, '
                '"ScoredUtc" = %s, "ScoreModel" = %s WHERE "Id" = %s AND "ScoredUtc" IS NULL',
    ANNOUNCEMENTS_TABLE: 'UPDATE corporate_announcements SET "Sentiment" = %s, "Importance" = %s, "Symbols" = %s, '
                         '"Topics" = %s, "ScoredUtc" = %s, "ScoreModel" = %s WHERE "Id" = %s AND "ScoredUtc" IS NULL',
}

#: The columns written, in the order row_values gives them (then the id).
SCORE_COLUMNS = ("Sentiment", "Importance", "Symbols", "Topics", "ScoredUtc", "ScoreModel")


def select_sql(table: str) -> str:
    return _SELECT[table]


def update_sql(table: str) -> str:
    return _UPDATE[table]


# ------------------------------------------------------------------ items --

@dataclass(frozen=True)
class Item:
    table: str
    id: int
    first_seen: datetime
    text: str            # what FinBERT and the rules read
    source: str          # news_items.Source, or the exchange for an announcement
    subject: str         # an announcement's subject line; '' for news
    own_symbol: str      # an announcement's company; '' for news


@dataclass(frozen=True)
class Score:
    sentiment: float
    importance: int
    symbols: Tuple[str, ...]
    topics: Tuple[str, ...]


@dataclass(frozen=True)
class Scored:
    item: Item
    score: Optional[Score]
    failure: str = ''    # why there is no score


_TAGS = re.compile(r'<[^>]{0,200}>')
_SPACE = re.compile(r'\s+')


def clean(text: Any) -> str:
    '''Markup out, whitespace folded, length capped: feeds put HTML in summaries.'''
    return _SPACE.sub(' ', _TAGS.sub(' ', str(text or ''))).strip()[:MAX_TEXT_CHARS]


def news_item(row: Sequence[Any]) -> Item:
    item_id, source, category, title, summary, first_seen = row
    title, summary = clean(title), clean(summary)
    text = title if not summary or summary.lower().startswith(title.lower()) else f'{title}. {summary}'
    return Item(NEWS_TABLE, int(item_id), _aware(first_seen), text[:MAX_TEXT_CHARS], str(source or ''), '', '')


def announcement_item(row: Sequence[Any]) -> Item:
    item_id, exchange, symbol, company, subject, details, first_seen = row
    subject = clean(subject)
    head = clean(company) or clean(symbol)
    text = '. '.join(p for p in (f'{head}: {subject}' if head else subject, clean(details)) if p)
    return Item(ANNOUNCEMENTS_TABLE, int(item_id), _aware(first_seen), text[:MAX_TEXT_CHARS], str(exchange or ''),
                subject, str(symbol or '').strip().upper())


def fetch_unscored(conn, limit: int) -> List[Item]:
    '''Up to `limit` never-scored items across both tables, oldest first seen first.'''
    items: List[Item] = []
    for table, make in ((NEWS_TABLE, news_item), (ANNOUNCEMENTS_TABLE, announcement_item)):
        try:
            with conn.cursor() as cur:
                cur.execute(select_sql(table), (limit,))
                items += [make(row) for row in cur.fetchall()]
        except Exception as ex:
            _rollback(conn)
            raise DataError(f'{table} is not readable ({type(ex).__name__}); news-score needs the '
                            'MarketIntelligence tables') from None
    items.sort(key=lambda i: (i.first_seen, i.table, i.id))
    return items[:limit]


# -------------------------------------------------------------- the model --

class FinBert:
    '''ProsusAI/finbert on the CPU, loaded on first use and kept for the run.'''

    def __init__(self, cache_dir: Path = MODEL_DIR, threads: int = TORCH_THREADS):
        self.cache_dir = cache_dir
        self.threads = threads
        self._torch = None
        self._tokenizer = None
        self._model = None
        self._order: Tuple[int, int, int] = (0, 1, 2)

    def load(self) -> None:
        if self._model is not None:
            return
        os.environ.setdefault('TOKENIZERS_PARALLELISM', 'false')
        os.environ.setdefault('HF_HUB_DISABLE_TELEMETRY', '1')
        if self.cached():
            # Read before huggingface_hub is imported: with the weights here, a run makes no network call at all.
            os.environ.setdefault('HF_HUB_OFFLINE', '1')
        try:
            import torch
            from transformers import AutoModelForSequenceClassification, AutoTokenizer
            from transformers.utils import logging as hf_logging
        except ImportError as ex:
            raise DataError(f'news-score needs torch and transformers ({ex.name} is missing): '
                            'pip install -r src/AlgoTrading.PythonEngine/requirements.txt') from None
        hf_logging.set_verbosity_error()        # no progress bars in the API's log every ten minutes
        hf_logging.disable_progress_bar()
        torch.set_num_threads(self.threads)
        options = {'revision': FINBERT_REVISION, 'cache_dir': str(self.cache_dir)}
        try:
            # Offline once the weights are here: a run never waits on the Hub.
            tokenizer = AutoTokenizer.from_pretrained(FINBERT_MODEL, local_files_only=True, **options)
            model = AutoModelForSequenceClassification.from_pretrained(FINBERT_MODEL, local_files_only=True, **options)
        except OSError:
            log.info('downloading %s at %s into %s (~440 MB, once)', FINBERT_MODEL, FINBERT_REVISION[:12],
                     self.cache_dir)
            try:
                tokenizer = AutoTokenizer.from_pretrained(FINBERT_MODEL, **options)
                model = AutoModelForSequenceClassification.from_pretrained(FINBERT_MODEL, **options)
            except Exception as ex:
                raise DataError(f'{FINBERT_MODEL} could not be loaded ({type(ex).__name__}: {ex})') from None
        model.eval()
        labels = {str(name).lower(): int(k) for k, name in model.config.id2label.items()}
        if not {'positive', 'negative', 'neutral'} <= set(labels):
            raise DataError(f'{FINBERT_MODEL} answers {sorted(labels)}, not positive/negative/neutral')
        self._order = (labels['positive'], labels['negative'], labels['neutral'])
        self._torch, self._tokenizer, self._model = torch, tokenizer, model

    def cached(self) -> bool:
        snapshot = self.cache_dir / 'models--ProsusAI--finbert' / 'snapshots' / FINBERT_REVISION
        return (snapshot / 'config.json').exists() and (snapshot / 'pytorch_model.bin').exists()

    def predict(self, texts: Sequence[str]) -> List[Tuple[float, float, float]]:
        '''(P positive, P negative, P neutral) per text.'''
        self.load()
        torch = self._torch
        with torch.inference_mode():
            encoded = self._tokenizer(list(texts), padding=True, truncation=True, max_length=MAX_TOKENS,
                                      return_tensors='pt')
            probs = torch.softmax(self._model(**encoded).logits, dim=-1).tolist()
        pos, neg, neu = self._order
        return [(p[pos], p[neg], p[neu]) for p in probs]


# ----------------------------------------------------------------- scoring --

def _predict(model, texts: Sequence[str]) -> Optional[list]:
    try:
        out = model.predict(texts)
    except DataError:
        raise
    except Exception as ex:
        log.warning('the model failed on %d text(s): %s', len(texts), type(ex).__name__)
        return None
    return list(out) if out is not None and len(out) == len(texts) else None


def _score(item: Item, probs: Any, tagger: symbols.SymbolTagger) -> Tuple[Optional[Score], str]:
    '''One item's score from the model's probabilities and the rules, validated; or why not.'''
    try:
        pos, neg, neu = (float(p) for p in probs)
    except (TypeError, ValueError):
        return None, 'no-output'
    if not all(math.isfinite(p) and 0.0 <= p <= 1.0 for p in (pos, neg, neu)) \
            or abs(pos + neg + neu - 1.0) > _PROB_TOLERANCE:
        return None, 'bad-probabilities'
    sentiment = round(pos - neg, 3)
    tags = set(tagger.tag(item.text))
    if item.own_symbol and item.own_symbol in tagger.tickers:
        tags.add(item.own_symbol)
    found = tuple(sorted(tags))
    importance, topics = newsrules.classify(item.text, item.source, item.subject, found)
    if not -1.0 <= sentiment <= 1.0 or not newsrules.valid(importance, topics) or not symbols.valid_symbols(found):
        return None, 'invalid-output'
    return Score(sentiment, importance, found, topics), ''


def score_items(items: Sequence[Item], model, tagger: symbols.SymbolTagger,
                batch_size: int = BATCH_SIZE) -> List[Scored]:
    '''
    Batches through the model; an item whose output does not validate is
    tried once more on its own, then given up on with the reason.
    '''
    out: List[Scored] = []
    for start in range(0, len(items), batch_size):
        batch = list(items[start:start + batch_size])
        texts = [i for i in batch if i.text]
        probs = _predict(model, [i.text for i in texts]) if texts else []
        by_item = dict(zip((id(i) for i in texts), probs or [None] * len(texts)))
        for item in batch:
            if not item.text:
                out.append(Scored(item, None, 'empty-text'))
                continue
            score, why = _score(item, by_item.get(id(item)), tagger)
            if score is None:
                again = _predict(model, [item.text])
                score, why = _score(item, again[0] if again else None, tagger)
            out.append(Scored(item, score, '' if score else why))
    return out


def row_values(result: Scored, now_utc: datetime) -> Tuple[Any, ...]:
    '''The six score columns and the id, in update_sql's order.'''
    s = result.score
    if s is None:
        return (None, None, '', '', now_utc, f'failed:{result.failure}'[:80], result.item.id)
    return (s.sentiment, s.importance, ','.join(s.symbols), ','.join(s.topics), now_utc, SCORE_MODEL, result.item.id)


def write(conn, results: Sequence[Scored], now_utc: datetime) -> int:
    '''Write every result, one transaction per table; returns the rows written (a row scored meanwhile is skipped).'''
    written = 0
    for table in (NEWS_TABLE, ANNOUNCEMENTS_TABLE):
        rows = [row_values(r, now_utc) for r in results if r.item.table == table]
        if not rows:
            continue
        try:
            with conn.cursor() as cur:
                for values in rows:
                    cur.execute(update_sql(table), values)
                    written += cur.rowcount
            conn.commit()
        except Exception as ex:
            _rollback(conn)
            raise DataError(f'writing scores to {table} failed ({type(ex).__name__})') from None
    return written


def describe(result: Scored) -> dict:
    '''One dry-run line.'''
    base = {'table': result.item.table, 'id': result.item.id, 'firstSeenUtc': result.item.first_seen.isoformat()}
    if result.score is None:
        return {**base, 'scoreModel': f'failed:{result.failure}'}
    s = result.score
    return {**base, 'sentiment': s.sentiment, 'importance': s.importance, 'symbols': list(s.symbols),
            'topics': list(s.topics), 'scoreModel': SCORE_MODEL}


# ------------------------------------------------------------------- a run --

def run(limit: int, dry_run: bool, connect: Optional[Callable[..., Any]] = None,
        model_factory: Callable[[], Any] = FinBert, now: Optional[Callable[[], datetime]] = None) -> int:
    '''
    One scoring run. Exit status 0 when it ran (items given up on are
    recorded, not fatal); 1 when the tables, the model or the database write
    failed, or when every item in the run failed, which is the model and
    not the headlines.
    '''
    from analysis.data import connect as db_connect

    _be_nice()
    connect = connect or db_connect
    now = now or (lambda: datetime.now(timezone.utc))
    try:
        conn = connect(readonly=dry_run)
    except DataError as ex:
        log.error('%s', ex)
        return 1
    try:
        if not _lock(conn):
            log.info('another news-score run is scoring; nothing to do')
            return 0
        items = fetch_unscored(conn, limit)
        if not items:
            log.info('nothing to score')
            return 0
        tagger = symbols.SymbolTagger(symbols.load_equities(conn))
        results = score_items(items, model_factory(), tagger)
        failed = [r for r in results if r.score is None]
        if dry_run:
            for r in results:
                print(json.dumps(describe(r)))
            written = 0
        else:
            written = write(conn, results, now())
        for r in failed:
            log.warning('given up on %s #%s: %s', r.item.table, r.item.id, r.failure)
        log.info('%d scored, %d given up on%s', len(results) - len(failed), len(failed),
                 ' (dry run, nothing written)' if dry_run else f'; {written} rows written')
        systematic = failed and len(failed) == len(results) and any(r.failure != 'empty-text' for r in failed)
        if systematic:
            log.error('every item failed: the model, not the headlines, is the likely cause')
        return 1 if systematic else 0
    except DataError as ex:
        log.error('%s', ex)
        return 1
    finally:
        conn.close()


def _lock(conn) -> bool:
    with conn.cursor() as cur:
        cur.execute('SELECT pg_try_advisory_lock(%s)', (LOCK_KEY,))
        row = cur.fetchone()
    return bool(row and row[0])


def _be_nice() -> None:
    '''A lower CPU priority than the trading processes it shares the server with (POSIX only).'''
    try:
        os.nice(10)
    except (AttributeError, OSError):
        pass
