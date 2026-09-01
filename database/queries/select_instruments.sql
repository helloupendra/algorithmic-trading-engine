SELECT "Symbol", "Description", "Underlying", "ExpiryDate", "StrikePrice", "OptionType"
FROM instruments
WHERE "Underlying" = 'BANKNIFTY'
ORDER BY "ExpiryDate", "StrikePrice"
LIMIT 50;
