/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;

namespace OsEngine.Robots.MyRobots.Tests
{
    internal static class SDKRebalancerByKorovinCalculatorTestCases
    {
        #region Test catalog

        public static List<KorovinNamedTest> GetTests()
        {
            return new List<KorovinNamedTest>
            {
                CreateTest("calm allocation", TestCalmAllocation),
                CreateTest("daily schedule is idempotent", TestDailySchedule),
                CreateTest("pending buy expires without active order", TestPendingBuyExpiration),
                CreateTest("daily index accepts latest completed point", TestDailyIndexDateReadiness),
                CreateTest("portfolio valuation uses open position profit", TestPortfolioValuation),
                CreateTest("maximum panic", TestMaximumPanic),
                CreateTest("gold selloff", TestGoldSelloff),
                CreateTest("gold panic tilt controls risk split", TestGoldPanicTilt),
                CreateTest("three dividend gaps", TestThreeDividendGaps),
                CreateTest("incremental dividend signal matches full series", TestIncrementalDividendSignal),
                CreateTest("multiple dividends on one ex-date", TestMultipleDividendsOnOneDate),
                CreateTest("expected price fallback", TestExpectedPriceFallback),
                CreateTest("insufficient history", TestInsufficientHistory),
                CreateTest("new instrument waits for history", TestNewInstrument),
                CreateTest("dividend selloff and positive move", TestDividendSelloffAndPositiveMove),
                CreateTest("stock cheapness and multiplier", TestStockCheapnessAndMultiplier),
                CreateTest("one cap redistribution", TestOneCapRedistribution),
                CreateTest("multiple caps redistribution", TestMultipleCapsRedistribution),
                CreateTest("caps shortfall moves to cash", TestCapsShortfall),
                CreateTest("dividend neutral rebalance weight", TestDividendNeutralRebalanceWeight),
                CreateTest("rebalance zone and rate", TestRebalanceZoneAndRate),
                CreateTest("recovery sells money market first", TestRecoverySell),
                CreateTest("falling stock has no forced stop", TestNoStopForFallingStock),
                CreateTest("no cash preserves hard reserve", TestNoCashHardReserve),
                CreateTest("free cash is swept into money market", TestMoneyMarketCashSweep),
                CreateTest("money market sale funds executable buys only", TestMoneyMarketExecutableFunding),
                CreateTest("buy scaling is proportional", TestProportionalBuyScaling),
                CreateTest("buy and hold never sells", TestBuyAndHoldNeverSells),
                CreateTest("external contribution funds buys", TestExternalContribution),
                CreateTest("dividend cash funds buys", TestDividendCashReceipt),
                CreateTest("rising gold and falling equities", TestRisingGoldFallingEquities),
                CreateTest("disabled stock position is sold", TestDisabledStockPosition),
                CreateTest("index and asset lookbacks are independent", TestSeparateHistoryLookbacks),
                CreateTest("custom calculation parameters", TestCustomCalculationParameters),
                CreateTest("lot step rounds down", TestLotStepRounding),
                CreateTest("tiny lot floors to no order", TestTinyLot),
                CreateTest("sell sizing never opens short", TestSellSizingNoShort),
                CreateTest("reopen target volume follows planned change", TestReopenTargetVolume),
                CreateTest("floating point allocation stays normalized", TestFloatingPointAllocation),
                CreateTest("sequential decline raises cheapness", TestSequentialDecline),
                CreateTest("recovery sells overweight gradually", TestGradualRecoverySell)
            };
        }

        private static KorovinNamedTest CreateTest(string name, Action action)
        {
            KorovinNamedTest result = new KorovinNamedTest();
            result.Name = name;
            result.Action = action;
            return result;
        }

        #endregion

        #region Portfolio valuation tests

        private static void TestPortfolioValuation()
        {
            KorovinPortfolioValuationResult fallingMarket =
                SDKRebalancerByKorovinCalculator.CalculatePortfolioValuation(
                    1000000m,
                    -200000m,
                    600000m,
                    true);

            KorovinAssert.True(fallingMarket.IsValid, "Falling market valuation must be valid.");
            KorovinAssert.Equal(800000m, fallingMarket.NetAssetValue, 0.0000001m,
                "Open loss must reduce mark-to-market NAV.");
            KorovinAssert.Equal(200000m, fallingMarket.FreeCash, 0.0000001m,
                "A market loss must not create virtual free cash.");

            KorovinPortfolioValuationResult risingMarket =
                SDKRebalancerByKorovinCalculator.CalculatePortfolioValuation(
                    1000000m,
                    200000m,
                    1000000m,
                    true);

            KorovinAssert.Equal(1200000m, risingMarket.NetAssetValue, 0.0000001m,
                "Open profit must increase mark-to-market NAV.");
            KorovinAssert.Equal(200000m, risingMarket.FreeCash, 0.0000001m,
                "A market profit must not consume unchanged free cash.");

            KorovinPortfolioValuationResult livePortfolio =
                SDKRebalancerByKorovinCalculator.CalculatePortfolioValuation(
                    1200000m,
                    200000m,
                    1000000m,
                    false);

            KorovinAssert.Equal(1200000m, livePortfolio.NetAssetValue, 0.0000001m,
                "A live connector NAV must not add open profit twice.");
            KorovinAssert.Equal(0m, livePortfolio.OpenPositionsProfit, 0.0000001m,
                "Open profit is already included in a live connector NAV.");
        }

        #endregion

        #region Panic and allocation tests

        private static void TestDailySchedule()
        {
            DateTime serverTime = new DateTime(2026, 8, 17, 12, 0, 0);

            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.ShouldRunDaily(
                    serverTime.AddMinutes(-1),
                    new TimeSpan(12, 0, 0),
                    DateTime.MinValue),
                "The scheduler must wait for configured server time.");
            KorovinAssert.True(
                SDKRebalancerByKorovinCalculator.ShouldRunDaily(
                    serverTime,
                    new TimeSpan(12, 0, 0),
                    DateTime.MinValue),
                "The scheduler must run at configured server time.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.ShouldRunDaily(
                    serverTime.AddHours(1),
                    new TimeSpan(12, 0, 0),
                    serverTime.Date),
                "A completed trading date must never run twice.");
        }

        private static void TestPendingBuyExpiration()
        {
            DateTime planDate = new DateTime(2022, 2, 24);
            TimeSpan scheduledTime = new TimeSpan(13, 0, 0);

            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.ShouldExpirePendingBuy(
                    planDate,
                    planDate.AddHours(14),
                    scheduledTime,
                    false),
                "A pending buy must remain available during its rebalance date.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.ShouldExpirePendingBuy(
                    planDate,
                    planDate.AddDays(1).AddHours(12),
                    scheduledTime,
                    false),
                "A previous-date pending buy must remain available before the next scheduled rebalance.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.ShouldExpirePendingBuy(
                    planDate,
                    planDate.AddDays(1).AddHours(13),
                    scheduledTime,
                    true),
                "An active execution order must protect its pending buy from expiration.");
            KorovinAssert.True(
                SDKRebalancerByKorovinCalculator.ShouldExpirePendingBuy(
                    planDate,
                    planDate.AddDays(1).AddHours(13),
                    scheduledTime,
                    false),
                "An inactive previous-date pending buy must not block the next rebalance.");
        }

        private static void TestDailyIndexDateReadiness()
        {
            DateTime serverDate = new DateTime(2026, 8, 20);

            KorovinAssert.True(
                SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                    serverDate,
                    serverDate.AddDays(-1),
                    true),
                "A daily index must use the latest completed trading-day point.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                    serverDate,
                    serverDate.AddDays(-1),
                    false),
                "An intraday index must still provide a current-date point.");
            KorovinAssert.True(
                SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                    serverDate,
                    serverDate,
                    false),
                "A current intraday index point must be accepted.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                    serverDate,
                    DateTime.MinValue,
                    true),
                "A daily index without data must not be accepted.");
            KorovinAssert.False(
                SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                    serverDate,
                    serverDate.AddDays(1),
                    true),
                "A future daily index point must not be accepted.");
        }

        private static void TestCalmAllocation()
        {
            List<decimal> market = ConstantHistory(100m, 252);
            List<KorovinEnabledStockHistory> stocks = new List<KorovinEnabledStockHistory>
            {
                EnabledStock("A", ConstantHistory(100m, 200)),
                EnabledStock("B", ConstantHistory(110m, 200))
            };

            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(market, stocks);
            KorovinAllocationResult allocation = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252));

            KorovinAssert.True(panic.IsValid, "Calm panic result must be valid.");
            KorovinAssert.Equal(0m, panic.Panic, 0.0000001m, "Calm panic must be zero.");
            KorovinAssert.Equal(0.30m, allocation.TargetCashWeight, 0.0000001m, "Calm cash target.");
            KorovinAssert.Equal(0.49999999m, allocation.TargetEquityWeight, 0.000001m, "Calm equity target.");
            KorovinAssert.Equal(0.20000001m, allocation.TargetGoldWeight, 0.000001m, "Calm gold target.");
            KorovinAssert.Equal(1m,
                allocation.TargetCashWeight + allocation.TargetEquityWeight + allocation.TargetGoldWeight,
                0.0000001m,
                "Allocation weights must sum to one.");
        }

        private static void TestMaximumPanic()
        {
            List<decimal> market = HistoryWithLast(100m, 60m, 252);
            List<KorovinEnabledStockHistory> stocks = new List<KorovinEnabledStockHistory>
            {
                EnabledStock("A", HistoryWithLast(100m, 1m, 200)),
                EnabledStock("B", HistoryWithLast(100m, 1m, 200))
            };

            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(market, stocks);
            KorovinAllocationResult allocation = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252));

            KorovinAssert.Equal(1m, panic.PanicByDrawdown, 0.0000001m, "Drawdown panic must be clipped.");
            KorovinAssert.Equal(1m, panic.PanicByBreadth, 0.0000001m, "Breadth panic must be clipped.");
            KorovinAssert.Equal(1m, panic.Panic, 0.0000001m, "Maximum panic.");
            KorovinAssert.Equal(0.10m, allocation.TargetCashWeight, 0.0000001m, "Maximum panic cash target.");
        }

        private static void TestGoldSelloff()
        {
            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(
                ConstantHistory(100m, 252),
                new List<KorovinEnabledStockHistory>());

            KorovinAllocationResult calmGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252));
            KorovinAllocationResult soldGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                HistoryWithLast(100m, 75m, 252));

            KorovinAssert.Equal(0.5642857m, soldGold.EquityRiskShare, 0.0000001m, "Gold selloff equity share.");
            KorovinAssert.True(soldGold.TargetGoldWeight > calmGold.TargetGoldWeight,
                "Gold selloff must increase the gold allocation.");
        }

        private static void TestGoldPanicTilt()
        {
            KorovinMarketPanicResult panic = new KorovinMarketPanicResult();
            panic.IsValid = true;
            panic.Panic = 1m;

            KorovinCalculationParameters parameters = new KorovinCalculationParameters();
            parameters.RelativeCheapnessRiskAdjustment = 0m;
            parameters.MinimumEquityRiskShare = 0m;
            parameters.MaximumEquityRiskShare = 1m;

            KorovinAllocationResult neutral = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252),
                parameters);

            parameters.GoldPanicTilt = 0.20m;
            KorovinAllocationResult moreGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252),
                parameters);

            parameters.GoldPanicTilt = -0.20m;
            KorovinAllocationResult lessGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(100m, 252),
                parameters);

            KorovinAssert.Equal(neutral.TargetCashWeight, moreGold.TargetCashWeight, 0.0000001m,
                "Gold panic tilt must not change cash target.");
            KorovinAssert.True(moreGold.TargetGoldWeight > neutral.TargetGoldWeight,
                "Positive gold panic tilt must move the risk allocation toward gold.");
            KorovinAssert.True(moreGold.TargetEquityWeight < neutral.TargetEquityWeight,
                "Positive gold panic tilt must reduce the equity risk share.");
            KorovinAssert.True(lessGold.TargetGoldWeight < neutral.TargetGoldWeight,
                "Negative gold panic tilt must move the risk allocation away from gold.");
            KorovinAssert.True(lessGold.TargetEquityWeight > neutral.TargetEquityWeight,
                "Negative gold panic tilt must increase the equity risk share.");
        }

        private static void TestInsufficientHistory()
        {
            KorovinMarketPanicResult shortMarket = SDKRebalancerByKorovinCalculator.CalculatePanic(
                ConstantHistory(100m, 251),
                new List<KorovinEnabledStockHistory>());

            List<KorovinEnabledStockHistory> stocks = new List<KorovinEnabledStockHistory>
            {
                EnabledStock("A", ConstantHistory(100m, 199))
            };
            KorovinMarketPanicResult shortBreadth = SDKRebalancerByKorovinCalculator.CalculatePanic(
                ConstantHistory(100m, 252),
                stocks);
            KorovinStockScoreResult shortStock = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                ConstantHistory(100m, 251),
                0m);

            KorovinAssert.False(shortMarket.IsValid, "Market needs 252 samples.");
            KorovinAssert.False(shortBreadth.IsValid, "Every enabled breadth member needs 200 samples.");
            KorovinAssert.False(shortStock.IsValid, "Stock score needs 252 samples.");
        }

        #endregion

        #region Dividend tests

        private static void TestThreeDividendGaps()
        {
            DateTime start = new DateTime(2026, 1, 1);
            List<KorovinDailyPrice> prices = new List<KorovinDailyPrice>
            {
                Price(start, 100m),
                Price(start.AddDays(1), 90m),
                Price(start.AddDays(2), 80m),
                Price(start.AddDays(3), 70m)
            };
            List<KorovinDividendEvent> dividends = new List<KorovinDividendEvent>
            {
                Dividend(start.AddDays(1), 10m),
                Dividend(start.AddDays(2), 10m),
                Dividend(start.AddDays(3), 10m)
            };

            List<KorovinSignalPoint> result = SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalSeries(
                prices,
                dividends);

            for (int index = 0; index < result.Count; index++)
            {
                KorovinAssert.Equal(100m, result[index].SignalValue, 0.0000001m,
                    "A pure dividend gap must not lower the signal.");
            }
        }

        private static void TestIncrementalDividendSignal()
        {
            DateTime start = new DateTime(2026, 1, 1);
            List<KorovinDailyPrice> prices = new List<KorovinDailyPrice>
            {
                Price(start, 100m),
                Price(start.AddDays(1), 91m),
                Price(start.AddDays(2), 83m),
                Price(start.AddDays(3), 86m)
            };
            List<KorovinDividendEvent> dividends = new List<KorovinDividendEvent>
            {
                Dividend(start.AddDays(1), 6m),
                Dividend(start.AddDays(1), 4m),
                Dividend(start.AddDays(3), 5m)
            };
            List<KorovinSignalPoint> full =
                SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalSeries(prices, dividends);
            List<KorovinSignalPoint> incremental = new List<KorovinSignalPoint>();

            for (int index = 0; index < prices.Count; index++)
            {
                decimal dividendAmount = index == 1 ? 10m : index == 3 ? 5m : 0m;
                KorovinSignalPoint point = index == 0
                    ? SDKRebalancerByKorovinCalculator.BuildFirstDividendAdjustedSignalPoint(
                        prices[index],
                        dividendAmount)
                    : SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalPoint(
                        prices[index - 1],
                        incremental[index - 1],
                        prices[index],
                        dividendAmount);
                incremental.Add(point);
            }

            KorovinAssert.Equal(full.Count, incremental.Count, 0m, "Incremental point count.");

            for (int index = 0; index < full.Count; index++)
            {
                KorovinAssert.Equal(
                    full[index].SignalValue,
                    incremental[index].SignalValue,
                    0.0000001m,
                    "Incremental signal value.");
                KorovinAssert.Equal(
                    full[index].AdjustedReturn,
                    incremental[index].AdjustedReturn,
                    0.0000001m,
                    "Incremental adjusted return.");
                KorovinAssert.Equal(
                    full[index].DividendAmount,
                    incremental[index].DividendAmount,
                    0m,
                    "Incremental dividend amount.");
            }
        }

        private static void TestMultipleDividendsOnOneDate()
        {
            DateTime start = new DateTime(2026, 2, 1);
            List<KorovinDailyPrice> prices = new List<KorovinDailyPrice>
            {
                Price(start, 100m),
                Price(start.AddDays(1), 85m)
            };
            List<KorovinDividendEvent> dividends = new List<KorovinDividendEvent>
            {
                Dividend(start.AddDays(1), 10m),
                Dividend(start.AddDays(1), 5m)
            };

            List<KorovinSignalPoint> result = SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalSeries(
                prices,
                dividends);

            KorovinAssert.Equal(15m, result[1].DividendAmount, 0m, "Same-day dividends must be summed.");
            KorovinAssert.Equal(100m, result[1].SignalValue, 0.0000001m, "Summed dividend adjustment.");
            KorovinAssert.True(result[1].DividendAdjustmentIsValid, "Positive expected price must be valid.");
        }

        private static void TestExpectedPriceFallback()
        {
            DateTime start = new DateTime(2026, 3, 1);
            List<KorovinDailyPrice> prices = new List<KorovinDailyPrice>
            {
                Price(start, 10m),
                Price(start.AddDays(1), 1m)
            };
            List<KorovinDividendEvent> dividends = new List<KorovinDividendEvent>
            {
                Dividend(start.AddDays(1), 15m)
            };

            List<KorovinSignalPoint> result = SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalSeries(
                prices,
                dividends);

            KorovinAssert.False(result[1].DividendAdjustmentIsValid, "Expected <= 0 must be marked invalid.");
            KorovinAssert.Equal(-0.90m, result[1].AdjustedReturn, 0.0000001m, "Fallback normal return.");
            KorovinAssert.Equal(1m, result[1].SignalValue, 0.0000001m, "Fallback normal ratio.");
        }

        private static void TestNewInstrument()
        {
            KorovinStockScoreResult result = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                new List<decimal> { 100m },
                0m);

            KorovinAssert.False(result.IsValid, "A new instrument must wait for 252 signal points.");
        }

        private static void TestDividendSelloffAndPositiveMove()
        {
            DateTime start = new DateTime(2026, 4, 1);
            List<KorovinDailyPrice> prices = new List<KorovinDailyPrice>
            {
                Price(start, 1000m),
                Price(start.AddDays(1), 850m),
                Price(start.AddDays(2), 810m)
            };
            List<KorovinDividendEvent> dividends = new List<KorovinDividendEvent>
            {
                Dividend(start.AddDays(1), 100m),
                Dividend(start.AddDays(2), 50m)
            };

            List<KorovinSignalPoint> result = SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalSeries(
                prices,
                dividends);

            KorovinAssert.Equal(-0.0555555556m, result[1].AdjustedReturn, 0.0000001m,
                "Additional ex-date selloff must remain in the signal.");
            KorovinAssert.Equal(0.0125m, result[2].AdjustedReturn, 0.0000001m,
                "Price above expected ex-date price must produce a positive return.");
        }

        #endregion

        #region Stock allocation tests

        private static void TestStockCheapnessAndMultiplier()
        {
            KorovinStockScoreResult result = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                HistoryWithLast(100m, 60m, 252),
                0.20m);

            decimal expectedMultiplier = (decimal)Math.Exp((double)(0.70m * 1m));
            KorovinAssert.True(result.IsValid, "Stock score must be valid.");
            KorovinAssert.Equal(0.40m, result.Drawdown, 0.0000001m, "Stock drawdown.");
            KorovinAssert.Equal(1m, result.Cheapness, 0.0000001m, "Stock cheapness.");
            KorovinAssert.Equal(expectedMultiplier, result.Multiplier, 0.0000001m, "Cheapness multiplier.");
        }

        private static void TestOneCapRedistribution()
        {
            List<KorovinStockAllocationInput> stocks = new List<KorovinStockAllocationInput>
            {
                Allocation("FALLING", 1m, 2m, 0.20m),
                Allocation("SECOND", 1m, 1m, 1m),
                Allocation("THIRD", 1m, 1m, 1m)
            };

            KorovinCappedAllocationResult result = SDKRebalancerByKorovinCalculator.AllocateStocksWithCaps(stocks, 0.60m);

            KorovinAssert.Equal(0.20m, FindWeight(result, "FALLING"), 0.0000001m, "First stock cap.");
            KorovinAssert.Equal(0.20m, FindWeight(result, "SECOND"), 0.0000001m, "Redistributed second weight.");
            KorovinAssert.Equal(0.20m, FindWeight(result, "THIRD"), 0.0000001m, "Redistributed third weight.");
            KorovinAssert.Equal(0m, result.CashShortfallWeight, 0.0000001m, "One cap has no shortfall.");
        }

        private static void TestMultipleCapsRedistribution()
        {
            List<KorovinStockAllocationInput> stocks = new List<KorovinStockAllocationInput>
            {
                Allocation("A", 5m, 1m, 0.10m),
                Allocation("B", 3m, 1m, 0.15m),
                Allocation("C", 1m, 1m, 1m)
            };

            KorovinCappedAllocationResult result = SDKRebalancerByKorovinCalculator.AllocateStocksWithCaps(stocks, 0.50m);

            KorovinAssert.Equal(0.10m, FindWeight(result, "A"), 0.0000001m, "First iterative cap.");
            KorovinAssert.Equal(0.15m, FindWeight(result, "B"), 0.0000001m, "Second iterative cap.");
            KorovinAssert.Equal(0.25m, FindWeight(result, "C"), 0.0000001m, "Remainder redistribution.");
        }

        private static void TestCapsShortfall()
        {
            List<KorovinStockAllocationInput> stocks = new List<KorovinStockAllocationInput>
            {
                Allocation("A", 1m, 1m, 0.10m),
                Allocation("B", 1m, 1m, 0.15m)
            };

            KorovinCappedAllocationResult result = SDKRebalancerByKorovinCalculator.AllocateStocksWithCaps(stocks, 0.50m);

            KorovinAssert.Equal(0.25m, result.AllocatedEquityWeight, 0.0000001m, "Sum of feasible caps.");
            KorovinAssert.Equal(0.25m, result.CashShortfallWeight, 0.0000001m, "Unallocated equity moves to cash.");
        }

        #endregion

        #region Rebalance tests

        private static void TestDividendNeutralRebalanceWeight()
        {
            decimal exDateWeight = SDKRebalancerByKorovinCalculator.CalculateDividendNeutralRebalanceWeight(
                true,
                0.20m,
                0m,
                0.18m);
            decimal regularWeight = SDKRebalancerByKorovinCalculator.CalculateDividendNeutralRebalanceWeight(
                false,
                0.20m,
                0m,
                0.18m);

            KorovinAssert.Equal(0.20m, exDateWeight, 0.0000001m, "Dividend gap must use adjusted return.");
            KorovinAssert.Equal(0.18m, regularWeight, 0.0000001m, "Normal day uses current weight.");
        }

        private static void TestRebalanceZoneAndRate()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 100m, 0.10m, 0.05m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("IN_ZONE", 0.10m, 0.119m),
                Asset("BUY", 0.30m, 0.20m)
            };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);

            KorovinAssert.Null(FindOrder(result, "IN_ZONE"), "Error inside 20% target zone must not trade.");
            KorovinRebalanceOrder buy = KorovinAssert.NotNull(FindOrder(result, "BUY"), "Buy order expected.");
            KorovinAssert.Equal(50m, buy.RequestedMoney, 0.0000001m, "Planner trades half the weight error.");
        }

        private static void TestRecoverySell()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 50m, 0.10m, 0.25m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("EQUITY", 0.70m, 0.50m)
            };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);

            KorovinAssert.True(result.Orders.Count >= 2, "Recovery must sell money market and buy equity.");
            KorovinAssert.Equal(KorovinRebalanceOrderSide.Sell, result.Orders[0].Side,
                "Sells must be planned before buys.");
            KorovinAssert.Equal("MM", result.Orders[0].Name, "Money market must fund recovery.");
            KorovinAssert.Equal(1m, result.BuyScale, 0.0000001m, "Recovery sale fully funds the buy.");
        }

        private static void TestNoStopForFallingStock()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 100m, 0.20m, 0.15m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("FALLING", 0.30m, 0.20m)
            };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);
            KorovinRebalanceOrder order = KorovinAssert.NotNull(
                FindOrder(result, "FALLING"),
                "Falling stock below target must still rebalance.");

            KorovinAssert.Equal(KorovinRebalanceOrderSide.Buy, order.Side,
                "There is no price-based stop or forced sell.");
        }

        private static void TestNoCashHardReserve()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 0m, 0.10m, 0.05m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("EQUITY", 0.50m, 0.30m)
            };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);

            KorovinAssert.Equal(0.05m, result.TargetFreeCashWeight, 0.0000001m, "Hard reserve is part of cash target.");
            KorovinAssert.Equal(0.05m, result.TargetMoneyMarketWeight, 0.0000001m, "Remaining target cash goes to MM.");
            KorovinAssert.Equal(0m, result.ProjectedAvailableForBuys, 0.0000001m, "No cash is available above reserve.");
            KorovinAssert.Null(FindOrder(result, "EQUITY"), "Zero-scaled buy must not create an order.");
        }

        private static void TestMoneyMarketCashSweep()
        {
            KorovinCalculationParameters parameters = new KorovinCalculationParameters();
            parameters.SweepFreeCashToMoneyMarket = true;

            KorovinRebalancePlanInput cashInput = BasicPlanInput(1000m, 100m, 0.10m, 0m);
            KorovinRebalancePlanResult cashPlan =
                SDKRebalancerByKorovinCalculator.BuildRebalancePlan(cashInput, parameters);
            KorovinRebalanceOrder cashBuy = KorovinAssert.NotNull(
                FindOrder(cashPlan, "MM"),
                "The entire free cash balance must be sent to the money-market asset.");

            KorovinAssert.Equal(0m, cashPlan.TargetFreeCashWeight, 0.0000001m,
                "Money-market storage must not target idle free cash.");
            KorovinAssert.Equal(0.10m, cashPlan.TargetMoneyMarketWeight, 0.0000001m,
                "The full cash allocation, including hard reserve, belongs to money market.");
            KorovinAssert.Equal(KorovinRebalanceOrderSide.Buy, cashBuy.Side,
                "Free cash must create a money-market buy.");
            KorovinAssert.Equal(100m, cashBuy.PlannedMoney, 0.0000001m,
                "The sweep must include cash initially protected as hard reserve.");

            KorovinRebalancePlanInput fundingInput = BasicPlanInput(1000m, 0m, 0.10m, 0.30m);
            fundingInput.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("EQUITY", 0.50m, 0.35m)
            };
            KorovinRebalancePlanResult fundingPlan =
                SDKRebalancerByKorovinCalculator.BuildRebalancePlan(fundingInput, parameters);
            KorovinRebalanceOrder moneyMarketSell = KorovinAssert.NotNull(
                FindOrder(fundingPlan, "MM"),
                "Money market must fund the planned risk-asset buy.");
            KorovinRebalanceOrder equityBuy = KorovinAssert.NotNull(
                FindOrder(fundingPlan, "EQUITY"),
                "The underweight risk asset must be bought.");

            KorovinAssert.Equal(KorovinRebalanceOrderSide.Sell, moneyMarketSell.Side,
                "Only the amount actually needed for another buy must leave money market.");
            KorovinAssert.Equal(-50m, moneyMarketSell.PlannedMoney, 0.0000001m,
                "Unused money-market sale proceeds must remain invested.");
            KorovinAssert.Equal(50m, equityBuy.PlannedMoney, 0.0000001m,
                "The risk buy amount must remain unchanged.");
        }

        private static void TestMoneyMarketExecutableFunding()
        {
            decimal noBuySale = SDKRebalancerByKorovinCalculator.CalculateRequiredMoneyMarketSale(
                150m,
                0m,
                0m,
                0m);
            decimal partiallyFundedSale = SDKRebalancerByKorovinCalculator.CalculateRequiredMoneyMarketSale(
                150m,
                100m,
                20m,
                30m);
            decimal cappedSale = SDKRebalancerByKorovinCalculator.CalculateRequiredMoneyMarketSale(
                40m,
                100m,
                0m,
                0m);

            KorovinAssert.Equal(0m, noBuySale, 0.0000001m,
                "Money market must not be sold when all planned buys disappear after lot rounding.");
            KorovinAssert.Equal(50m, partiallyFundedSale, 0.0000001m,
                "Only the executable buy shortfall must be funded from money market.");
            KorovinAssert.Equal(40m, cappedSale, 0.0000001m,
                "The adjusted sale must never exceed the planner sale limit.");
        }

        private static void TestProportionalBuyScaling()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 100m, 0.10m, 0.05m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("A", 0.40m, 0.20m),
                Asset("B", 0.30m, 0.10m)
            };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);
            KorovinRebalanceOrder first = KorovinAssert.NotNull(FindOrder(result, "A"), "First scaled buy.");
            KorovinRebalanceOrder second = KorovinAssert.NotNull(FindOrder(result, "B"), "Second scaled buy.");

            KorovinAssert.Equal(0.25m, result.BuyScale, 0.0000001m, "Only cash above reserve can be used.");
            KorovinAssert.Equal(first.RequestedMoney / second.RequestedMoney,
                first.PlannedMoney / second.PlannedMoney,
                0.0000001m,
                "All buys must use the same scale.");
        }

        private static void TestBuyAndHoldNeverSells()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 100m, 0.30m, 0.40m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("OVERWEIGHT", 0.20m, 0.40m),
                Asset("UNDERWEIGHT", 0.30m, 0m)
            };

            KorovinCalculationParameters parameters = new KorovinCalculationParameters();
            parameters.MinimumRebalanceZone = 0m;
            parameters.RelativeRebalanceZone = 0m;
            parameters.RebalanceRate = 1m;
            parameters.AllowSellOrders = false;

            KorovinRebalancePlanResult result =
                SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input, parameters);
            KorovinRebalanceOrder buy = KorovinAssert.NotNull(
                FindOrder(result, "UNDERWEIGHT"),
                "Buy-only mode must invest available cash into an underweight asset.");

            KorovinAssert.Null(FindOrder(result, "OVERWEIGHT"),
                "Buy-only mode must not sell an overweight risk asset.");
            KorovinAssert.Null(FindOrder(result, "MM"),
                "Buy-only mode must not sell an overweight money-market position.");
            KorovinAssert.Equal(50m, result.ProjectedAvailableForBuys, 0.0000001m,
                "Only free cash above the hard reserve can fund buys.");
            KorovinAssert.Equal(50m, buy.PlannedMoney, 0.0000001m,
                "The buy must be scaled to actual free cash without projected sale proceeds.");
        }

        private static void TestExternalContribution()
        {
            KorovinRebalancePlanInput before = BasicPlanInput(1000m, 50m, 0.10m, 0.05m);
            before.RiskAssets = new List<KorovinRebalanceAssetInput> { Asset("A", 0.40m, 0.20m) };
            KorovinRebalancePlanInput after = BasicPlanInput(1200m, 250m, 0.10m, 0.05m);
            after.RiskAssets = new List<KorovinRebalanceAssetInput> { Asset("A", 0.40m, 0.20m) };

            KorovinRebalancePlanResult beforePlan = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(before);
            KorovinRebalancePlanResult afterPlan = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(after);

            KorovinAssert.Null(FindOrder(beforePlan, "A"), "Hard reserve leaves no cash before contribution.");
            KorovinAssert.NotNull(FindOrder(afterPlan, "A"), "External contribution must become available cash.");
        }

        private static void TestDividendCashReceipt()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 150m, 0.10m, 0.05m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput> { Asset("A", 0.40m, 0.20m) };

            KorovinRebalancePlanResult result = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input);
            KorovinRebalanceOrder order = KorovinAssert.NotNull(
                FindOrder(result, "A"),
                "Dividend cash above reserve must fund a buy.");

            KorovinAssert.Equal(100m, order.PlannedMoney, 0.0000001m, "Received cash is immediately usable.");
        }

        private static void TestRisingGoldFallingEquities()
        {
            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(
                HistoryWithLast(100m, 70m, 252),
                new List<KorovinEnabledStockHistory>());
            KorovinAllocationResult allocation = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                ConstantHistory(120m, 252));

            KorovinAssert.True(allocation.EquityRiskShare > 0.7142857m,
                "Falling equities and gold at its high must tilt risk toward equities.");
        }

        private static void TestDisabledStockPosition()
        {
            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 100m, 0.30m, 0.25m);
            KorovinRebalanceAssetInput disabled = Asset("DISABLED", 0m, 0.10m);
            disabled.Reason = "Disabled stock exit";
            input.RiskAssets = new List<KorovinRebalanceAssetInput> { disabled };

            KorovinRebalanceOrder order = KorovinAssert.NotNull(
                FindOrder(SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input), "DISABLED"),
                "A disabled stock with an open position must have a sell plan.");

            KorovinAssert.Equal(KorovinRebalanceOrderSide.Sell, order.Side, "Disabled position side.");
            KorovinAssert.Equal("Disabled stock exit", order.Reason, "Disabled position reason.");
        }

        private static void TestSeparateHistoryLookbacks()
        {
            KorovinCalculationParameters parameters = new KorovinCalculationParameters();
            parameters.IndexHistoryLookback = 4;
            parameters.AssetHistoryLookback = 2;
            parameters.BreadthSmaLength = 2;

            KorovinMarketPanicResult shortIndex = SDKRebalancerByKorovinCalculator.CalculatePanic(
                new List<decimal> { 100m, 100m, 90m },
                new List<KorovinEnabledStockHistory>(),
                parameters);
            KorovinMarketPanicResult validIndex = SDKRebalancerByKorovinCalculator.CalculatePanic(
                new List<decimal> { 100m, 100m, 100m, 90m },
                new List<KorovinEnabledStockHistory>(),
                parameters);
            KorovinAllocationResult shortGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                validIndex,
                new List<decimal> { 100m },
                parameters);
            KorovinAllocationResult validGold = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                validIndex,
                new List<decimal> { 100m, 90m },
                parameters);
            KorovinStockScoreResult shortStock = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                new List<decimal> { 100m },
                validIndex.MarketDrawdown,
                parameters);
            KorovinStockScoreResult validStock = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                new List<decimal> { 100m, 90m },
                validIndex.MarketDrawdown,
                parameters);

            KorovinAssert.False(shortIndex.IsValid, "Index must use its own longer lookback.");
            KorovinAssert.True(validIndex.IsValid, "Index history must be valid at index lookback.");
            KorovinAssert.False(shortGold.IsValid, "Gold must use asset lookback.");
            KorovinAssert.True(validGold.IsValid, "Gold history must be valid at asset lookback.");
            KorovinAssert.False(shortStock.IsValid, "Stock must use asset lookback.");
            KorovinAssert.True(validStock.IsValid, "Stock history must be valid at asset lookback.");
        }

        private static void TestCustomCalculationParameters()
        {
            KorovinCalculationParameters parameters = new KorovinCalculationParameters();
            parameters.IndexHistoryLookback = 3;
            parameters.AssetHistoryLookback = 3;
            parameters.BreadthSmaLength = 2;
            parameters.PanicDrawdownStart = 0m;
            parameters.PanicDrawdownRange = 0.20m;
            parameters.PanicDrawdownWeight = 1m;
            parameters.PanicBreadthWeight = 0m;
            parameters.BaseCashWeight = 0.40m;
            parameters.PanicCashReduction = 0.10m;
            parameters.StockMultiplierExponent = 0m;
            parameters.HardCashReserveWeight = 0.10m;
            parameters.MinimumRebalanceZone = 0m;
            parameters.RelativeRebalanceZone = 0m;
            parameters.RebalanceRate = 1m;

            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(
                new List<decimal> { 100m, 100m, 80m },
                new List<KorovinEnabledStockHistory>(),
                parameters);
            KorovinAllocationResult allocation = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                new List<decimal> { 100m, 100m, 100m },
                parameters);
            KorovinStockScoreResult score = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                new List<decimal> { 100m, 100m, 80m },
                panic.MarketDrawdown,
                parameters);

            KorovinRebalancePlanInput input = BasicPlanInput(1000m, 200m, 0.20m, 0.10m);
            input.RiskAssets = new List<KorovinRebalanceAssetInput>
            {
                Asset("A", 0.40m, 0.20m)
            };
            KorovinRebalancePlanResult plan = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(input, parameters);
            KorovinRebalanceOrder buy = KorovinAssert.NotNull(FindOrder(plan, "A"), "Custom-rate buy expected.");

            KorovinAssert.Equal(1m, panic.Panic, 0.0000001m, "Custom panic thresholds.");
            KorovinAssert.Equal(0.30m, allocation.TargetCashWeight, 0.0000001m, "Custom cash formula.");
            KorovinAssert.Equal(1m, score.Multiplier, 0.0000001m, "Custom multiplier exponent.");
            KorovinAssert.Equal(0.10m, plan.TargetFreeCashWeight, 0.0000001m, "Custom hard reserve.");
            KorovinAssert.Equal(200m, buy.RequestedMoney, 0.0000001m, "Custom rebalance rate.");
        }

        #endregion

        #region Order sizing tests

        private static void TestLotStepRounding()
        {
            KorovinOrderSizeResult result = SDKRebalancerByKorovinCalculator.CalculateOrderSize(
                1050m,
                100m,
                1m,
                3m,
                0,
                0m);

            KorovinAssert.Equal(9m, result.Volume, 0m, "Volume must floor to the exchange lot step.");
            KorovinAssert.Equal(900m, result.Money, 0m, "Rounded money value.");
        }

        private static void TestTinyLot()
        {
            KorovinOrderSizeResult result = SDKRebalancerByKorovinCalculator.CalculateOrderSize(
                99m,
                10m,
                10m,
                1m,
                0,
                0m);

            KorovinAssert.Equal(KorovinRebalanceOrderSide.None, result.Side,
                "Money below one lot must not place an order.");
            KorovinAssert.Equal(0m, result.Volume, 0m, "Tiny order volume.");
        }

        private static void TestSellSizingNoShort()
        {
            KorovinOrderSizeResult result = SDKRebalancerByKorovinCalculator.CalculateOrderSize(
                -10000m,
                100m,
                1m,
                0.1m,
                1,
                2.34m);

            KorovinAssert.Equal(KorovinRebalanceOrderSide.Sell, result.Side, "Sell side.");
            KorovinAssert.Equal(2.3m, result.Volume, 0.0000001m, "Sell must floor and stay within long volume.");
            KorovinAssert.True(result.Volume <= 2.34m, "Sell must never open a short.");
        }

        private static void TestReopenTargetVolume()
        {
            KorovinAssert.Equal(
                11m,
                SDKRebalancerByKorovinCalculator.CalculateReopenTargetVolume(
                    10m,
                    KorovinRebalanceOrderSide.Buy,
                    1m),
                0m,
                "A reopen buy must add the planned volume to the existing position.");
            KorovinAssert.Equal(
                9m,
                SDKRebalancerByKorovinCalculator.CalculateReopenTargetVolume(
                    10m,
                    KorovinRebalanceOrderSide.Sell,
                    1m),
                0m,
                "A reopen sell must subtract the planned volume from the existing position.");
            KorovinAssert.Equal(
                0m,
                SDKRebalancerByKorovinCalculator.CalculateReopenTargetVolume(
                    10m,
                    KorovinRebalanceOrderSide.Sell,
                    20m),
                0m,
                "A reopen sell must never create a short position.");
            KorovinAssert.Equal(
                10m,
                SDKRebalancerByKorovinCalculator.CalculateReopenTargetVolume(
                    10m,
                    KorovinRebalanceOrderSide.None,
                    1m),
                0m,
                "An invalid side must preserve the current volume.");
        }

        private static void TestFloatingPointAllocation()
        {
            List<KorovinStockAllocationInput> stocks = new List<KorovinStockAllocationInput>
            {
                Allocation("A", 1m, 1.3333333m, 1m),
                Allocation("B", 1m, 1.6666667m, 1m),
                Allocation("C", 1m, 1.9999999m, 1m)
            };

            KorovinCappedAllocationResult result =
                SDKRebalancerByKorovinCalculator.AllocateStocksWithCaps(stocks, 0.5333333m);
            decimal sum = FindWeight(result, "A") + FindWeight(result, "B") + FindWeight(result, "C");

            KorovinAssert.Equal(0.5333333m, sum, 0.0000001m, "Allocation must remain normalized.");
        }

        private static void TestSequentialDecline()
        {
            KorovinStockScoreResult first = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                HistoryWithLast(100m, 90m, 252),
                0m);
            KorovinStockScoreResult second = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                HistoryWithLast(100m, 80m, 252),
                0m);
            KorovinStockScoreResult third = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                HistoryWithLast(100m, 70m, 252),
                0m);

            KorovinAssert.True(first.Multiplier < second.Multiplier && second.Multiplier < third.Multiplier,
                "Several declining days must progressively raise the cheapness multiplier.");
        }

        private static void TestGradualRecoverySell()
        {
            KorovinRebalancePlanInput firstInput = BasicPlanInput(1000m, 100m, 0.30m, 0.25m);
            firstInput.RiskAssets = new List<KorovinRebalanceAssetInput> { Asset("RECOVERED", 0.20m, 0.40m) };
            KorovinRebalanceOrder first = KorovinAssert.NotNull(
                FindOrder(SDKRebalancerByKorovinCalculator.BuildRebalancePlan(firstInput), "RECOVERED"),
                "First recovery sell.");

            KorovinRebalancePlanInput secondInput = BasicPlanInput(1000m, 200m, 0.30m, 0.25m);
            secondInput.RiskAssets = new List<KorovinRebalanceAssetInput> { Asset("RECOVERED", 0.20m, 0.30m) };
            KorovinRebalanceOrder second = KorovinAssert.NotNull(
                FindOrder(SDKRebalancerByKorovinCalculator.BuildRebalancePlan(secondInput), "RECOVERED"),
                "Second recovery sell.");

            KorovinAssert.Equal(-100m, first.RequestedMoney, 0.0000001m, "First sell removes half the error.");
            KorovinAssert.Equal(-50m, second.RequestedMoney, 0.0000001m, "Second sell is progressively smaller.");
        }

        #endregion

        #region Builders and lookup helpers

        private static List<decimal> ConstantHistory(decimal value, int count)
        {
            List<decimal> result = new List<decimal>();

            for (int index = 0; index < count; index++)
            {
                result.Add(value);
            }

            return result;
        }

        private static List<decimal> HistoryWithLast(decimal value, decimal last, int count)
        {
            List<decimal> result = ConstantHistory(value, count);
            result[result.Count - 1] = last;
            return result;
        }

        private static KorovinEnabledStockHistory EnabledStock(string name, IReadOnlyList<decimal> history)
        {
            KorovinEnabledStockHistory result = new KorovinEnabledStockHistory();
            result.Name = name;
            result.IsEnabled = true;
            result.SignalHistory = history;
            return result;
        }

        private static KorovinDailyPrice Price(DateTime date, decimal price)
        {
            KorovinDailyPrice result = new KorovinDailyPrice();
            result.Date = date;
            result.Price = price;
            return result;
        }

        private static KorovinDividendEvent Dividend(DateTime date, decimal amount)
        {
            KorovinDividendEvent result = new KorovinDividendEvent();
            result.ExDate = date;
            result.Amount = amount;
            return result;
        }

        private static KorovinStockAllocationInput Allocation(
            string name,
            decimal baseWeight,
            decimal multiplier,
            decimal cap)
        {
            KorovinStockAllocationInput result = new KorovinStockAllocationInput();
            result.Name = name;
            result.BaseWeight = baseWeight;
            result.Multiplier = multiplier;
            result.Cap = cap;
            return result;
        }

        private static decimal FindWeight(KorovinCappedAllocationResult result, string name)
        {
            for (int index = 0; index < result.StockWeights.Count; index++)
            {
                if (result.StockWeights[index].Name == name)
                {
                    return result.StockWeights[index].Weight;
                }
            }

            throw new InvalidOperationException("Stock target not found: " + name);
        }

        private static KorovinRebalancePlanInput BasicPlanInput(
            decimal nav,
            decimal freeCash,
            decimal effectiveCashWeight,
            decimal moneyMarketWeight)
        {
            KorovinRebalancePlanInput result = new KorovinRebalancePlanInput();
            result.NetAssetValue = nav;
            result.FreeCash = freeCash;
            result.EffectiveTargetCashWeight = effectiveCashWeight;
            result.MoneyMarketName = "MM";
            result.MoneyMarketRebalanceWeight = moneyMarketWeight;
            return result;
        }

        private static KorovinRebalanceAssetInput Asset(string name, decimal target, decimal current)
        {
            KorovinRebalanceAssetInput result = new KorovinRebalanceAssetInput();
            result.Name = name;
            result.TargetWeight = target;
            result.RebalanceWeight = current;
            return result;
        }

        private static KorovinRebalanceOrder? FindOrder(KorovinRebalancePlanResult result, string name)
        {
            for (int index = 0; index < result.Orders.Count; index++)
            {
                if (result.Orders[index].Name == name)
                {
                    return result.Orders[index];
                }
            }

            return null;
        }

        #endregion
    }

    internal static class KorovinAssert
    {
        #region Assertions

        public static void True(bool value, string message)
        {
            if (value == false)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void False(bool value, string message)
        {
            True(value == false, message);
        }

        public static void Equal(decimal expected, decimal actual, decimal tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", actual " + actual + ".");
            }
        }

        public static void Equal(KorovinRebalanceOrderSide expected, KorovinRebalanceOrderSide actual, string message)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", actual " + actual + ".");
            }
        }

        public static void Equal(string expected, string actual, string message)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal) == false)
            {
                throw new InvalidOperationException(message + " Expected " + expected + ", actual " + actual + ".");
            }
        }

        public static void Null(object? value, string message)
        {
            if (value != null)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static KorovinRebalanceOrder NotNull(KorovinRebalanceOrder? value, string message)
        {
            if (value == null)
            {
                throw new InvalidOperationException(message);
            }

            return value;
        }

        #endregion
    }
}
