#!/usr/bin/env python3
"""Offline CPU inference through the production news scorer, using tiny local weights."""
import os
from pathlib import Path
import sys
import tempfile

os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['HF_HUB_DISABLE_TELEMETRY'] = '1'
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src' / 'AlgoTrading.PythonEngine'))
import torch
from transformers import BertConfig, BertForSequenceClassification, BertTokenizer
from analysis.news import FinBert

torch.set_num_threads(1)
torch.manual_seed(7)
with tempfile.TemporaryDirectory() as folder:
    vocabulary = Path(folder) / 'vocab.txt'
    vocabulary.write_text('[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nprofit\nloss\n')
    tokenizer = BertTokenizer(vocab=str(vocabulary))
    model = BertForSequenceClassification(BertConfig(vocab_size=7, hidden_size=16, num_hidden_layers=1,
        num_attention_heads=2, intermediate_size=32, num_labels=3)).eval()
    scorer = FinBert()
    scorer._torch, scorer._tokenizer, scorer._model = torch, tokenizer, model
    result = scorer.predict(['profit', 'loss'])
    assert len(result) == 2 and all(len(row) == 3 and abs(sum(row) - 1) < 1e-5 for row in result)
    assert next(model.parameters()).device.type == 'cpu'
print('Offline news scorer CPU inference passed (synthetic weights; no model download).')
