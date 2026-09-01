-- ---------------------------------------------------------------------------
-- Derivative expiry calendars.
--
-- The strategy engine uses these rows to resolve which option contract an
-- underlying is currently trading, so the table must be populated before any
-- option-chain lookup will work.
--
-- Prerequisite: the `expiry_rules` table is created by EF Core migrations,
-- which AlgoTrading.Api applies automatically on first start. Run this file
-- AFTER the API has booted once.
--
-- Safe to re-run: the ON CONFLICT clause targets the unique index on
-- ("Exchange", "Underlying"), so repeat executions refresh the rule instead of
-- inserting duplicates.
--
-- Column encodings:
--   *ExpiryDay        1=Monday … 7=Sunday
--   HolidayShiftRule  1=PreviousTradingDay
--   PreferredExpiryType 1=Weekly, 2=Monthly
-- ---------------------------------------------------------------------------

INSERT INTO expiry_rules
(
    "Exchange",
    "Underlying",
    "HasWeekly",
    "HasMonthly",
    "HasQuarterly",
    "HasSemiAnnual",
    "WeeklyExpiryDay",
    "MonthlyExpiryDay",
    "QuarterlyExpiryDay",
    "SemiAnnualExpiryDay",
    "HolidayShiftRule",
    "PreferredExpiryType",
    "IsEnabled",
    "CreatedUtc",
    "UpdatedUtc"
)
VALUES
(
    'NSE',
    'BANKNIFTY',
    false,          -- HasWeekly
    true,           -- HasMonthly
    true,           -- HasQuarterly
    false,          -- HasSemiAnnual
    null,           -- WeeklyExpiryDay
    2,              -- MonthlyExpiryDay    -> Tuesday
    2,              -- QuarterlyExpiryDay  -> Tuesday
    null,           -- SemiAnnualExpiryDay
    1,              -- HolidayShiftRule    -> PreviousTradingDay
    2,              -- PreferredExpiryType -> Monthly
    true,
    NOW(),
    NOW()
),
(
    'BSE',
    'SENSEX',
    true,           -- HasWeekly
    true,           -- HasMonthly
    true,           -- HasQuarterly
    true,           -- HasSemiAnnual
    4,              -- WeeklyExpiryDay     -> Thursday
    4,              -- MonthlyExpiryDay    -> Thursday
    4,              -- QuarterlyExpiryDay  -> Thursday
    4,              -- SemiAnnualExpiryDay -> Thursday
    1,              -- HolidayShiftRule    -> PreviousTradingDay
    1,              -- PreferredExpiryType -> Weekly
    true,
    NOW(),
    NOW()
)
ON CONFLICT ("Exchange", "Underlying") DO UPDATE SET
    "HasWeekly"           = EXCLUDED."HasWeekly",
    "HasMonthly"          = EXCLUDED."HasMonthly",
    "HasQuarterly"        = EXCLUDED."HasQuarterly",
    "HasSemiAnnual"       = EXCLUDED."HasSemiAnnual",
    "WeeklyExpiryDay"     = EXCLUDED."WeeklyExpiryDay",
    "MonthlyExpiryDay"    = EXCLUDED."MonthlyExpiryDay",
    "QuarterlyExpiryDay"  = EXCLUDED."QuarterlyExpiryDay",
    "SemiAnnualExpiryDay" = EXCLUDED."SemiAnnualExpiryDay",
    "HolidayShiftRule"    = EXCLUDED."HolidayShiftRule",
    "PreferredExpiryType" = EXCLUDED."PreferredExpiryType",
    "IsEnabled"           = EXCLUDED."IsEnabled",
    "UpdatedUtc"          = NOW();
