-- Enable the TimescaleDB extension
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Create the standard PostgreSQL table for our tick data
CREATE TABLE IF NOT EXISTS market_ticks (
    time TIMESTAMPTZ NOT NULL,
    symbol VARCHAR(20) NOT NULL,
    last_traded_price DOUBLE PRECISION NOT NULL,
    volume BIGINT NOT NULL
);

-- Convert the standard table into a TimescaleDB hypertable partitioned by time
SELECT create_hypertable('market_ticks', 'time', if_not_exists => TRUE);

-- Create an index on the symbol to speed up querying specific instruments (like 'BANKNIFTY')
CREATE INDEX ix_symbol_time ON market_ticks (symbol, time DESC);