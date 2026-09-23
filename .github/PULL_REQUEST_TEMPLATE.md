## What this changes, and why

<!-- One paragraph. What was wrong or missing, and what the change does about
     it. If it fixes an issue, "Fixes #123" here. -->

## How it was checked

<!-- What you ran, and what it said. "dotnet test: 645 passed" beats "tests
     pass". For a UI change, a screenshot. For a strategy or a backtest change,
     the run: instrument, period, costs, and the numbers before and after. -->

## Checklist

- [ ] `dotnet test tests/AlgoTrading.UnitTests` passes.
- [ ] `npm test` and `npx tsc --noEmit -p tsconfig.app.json` pass, if `web/` changed.
- [ ] Python tests pass (`python3 -m unittest discover -s tests`), if the engine changed.
- [ ] No credentials, tokens, `.env` files or generated output are in the diff.
- [ ] Documentation is updated where the behaviour is documented — a strategy
      change updates its specification in [`docs/strategies/`](../docs/strategies).

## If this touches trading

- [ ] The result is measured with costs, over a stated period and instrument.
- [ ] It was checked against data that was not used to build it, and that number
      is in the description too — including when it is worse.
- [ ] Nothing in the code, the docs or the console claims more than the evidence
      supports.

<!-- A change that loses money honestly is welcome here; a claim that outruns
     its evidence is not. If you did not measure it, say so plainly instead of
     leaving the boxes ticked. -->
