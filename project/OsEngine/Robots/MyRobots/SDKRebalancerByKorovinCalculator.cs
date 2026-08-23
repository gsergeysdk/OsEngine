/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;

namespace OsEngine.Robots.MyRobots
{
    #region Data transfer objects

    public class KorovinDailyPrice
    {
        public DateTime Date;

        public decimal Price;
    }

    public class KorovinDividendEvent
    {
        public DateTime ExDate;

        public decimal Amount;
    }

    public class KorovinSignalPoint
    {
        public DateTime Date;

        public decimal MarketPrice;

        public decimal SignalValue;

        public decimal AdjustedReturn;

        public decimal DividendAmount;

        public bool DividendAdjustmentIsValid;
    }

    public class KorovinEnabledStockHistory
    {
        public string Name = string.Empty;

        public bool IsEnabled;

        public IReadOnlyList<decimal> SignalHistory = Array.Empty<decimal>();
    }

    public class KorovinMarketPanicResult
    {
        public bool IsValid;

        public decimal MarketDrawdown;

        public decimal Breadth;

        public decimal PanicByDrawdown;

        public decimal PanicByBreadth;

        public decimal Panic;

        public int EnabledStocksCount;
    }

    public class KorovinAllocationResult
    {
        public bool IsValid;

        public decimal MarketDrawdown;

        public decimal GoldDrawdown;

        public decimal TargetCashWeight;

        public decimal TargetEquityWeight;

        public decimal TargetGoldWeight;

        public decimal EquityRiskShare;
    }

    public class KorovinStockScoreResult
    {
        public bool IsValid;

        public decimal Drawdown;

        public decimal RelativeDrawdown;

        public decimal AbsoluteCheapness;

        public decimal RelativeCheapness;

        public decimal Cheapness;

        public decimal Multiplier;
    }

    public class KorovinStockAllocationInput
    {
        public string Name = string.Empty;

        public decimal BaseWeight;

        public decimal Multiplier;

        public decimal Cap;
    }

    public class KorovinStockTargetWeight
    {
        public string Name = string.Empty;

        public decimal Weight;

        public bool IsCapped;
    }

    public class KorovinCappedAllocationResult
    {
        public bool IsValid;

        public List<KorovinStockTargetWeight> StockWeights = new List<KorovinStockTargetWeight>();

        public decimal AllocatedEquityWeight;

        public decimal CashShortfallWeight;
    }

    public class KorovinRebalanceAssetInput
    {
        public string Name = string.Empty;

        public decimal CurrentWeight;

        public decimal TargetWeight;

        public decimal RebalanceWeight;

        public string Reason = string.Empty;
    }

    public enum KorovinRebalanceOrderSide
    {
        None,
        Buy,
        Sell
    }

    public class KorovinRebalanceOrder
    {
        public string Name = string.Empty;

        public KorovinRebalanceOrderSide Side;

        public decimal CurrentWeight;

        public decimal RebalanceWeight;

        public decimal TargetWeight;

        public decimal ErrorWeight;

        public decimal RequestedMoney;

        public decimal PlannedMoney;

        public string Reason = string.Empty;
    }

    public class KorovinRebalancePlanInput
    {
        public decimal NetAssetValue;

        public decimal FreeCash;

        public decimal EffectiveTargetCashWeight;

        public string MoneyMarketName = string.Empty;

        public decimal MoneyMarketRebalanceWeight;

        public IReadOnlyList<KorovinRebalanceAssetInput> RiskAssets = Array.Empty<KorovinRebalanceAssetInput>();
    }

    public class KorovinRebalancePlanResult
    {
        public bool IsValid;

        public decimal TargetFreeCashWeight;

        public decimal TargetMoneyMarketWeight;

        public decimal HardReserveMoney;

        public decimal ProjectedAvailableForBuys;

        public decimal BuyScale;

        public List<KorovinRebalanceOrder> Orders = new List<KorovinRebalanceOrder>();
    }

    public class KorovinPortfolioValuationResult
    {
        public bool IsValid;

        public decimal EngineBalance;

        public decimal OpenPositionsProfit;

        public decimal NetAssetValue;

        public decimal StrategyMarketValue;

        public decimal FreeCash;
    }

    public class KorovinOrderSizeResult
    {
        public KorovinRebalanceOrderSide Side;

        public decimal Volume;

        public decimal Money;
    }

    public class KorovinCalculationParameters
    {
        public int IndexHistoryLookback = 252;

        public int AssetHistoryLookback = 252;

        public int BreadthSmaLength = 200;

        public decimal PanicDrawdownStart = 0.10m;

        public decimal PanicDrawdownRange = 0.30m;

        public decimal PanicBreadthStart = 0.50m;

        public decimal PanicBreadthRange = 0.40m;

        public decimal PanicDrawdownWeight = 0.70m;

        public decimal PanicBreadthWeight = 0.30m;

        public decimal BaseCashWeight = 0.30m;

        public decimal PanicCashReduction = 0.20m;

        public decimal MinimumCashWeight = 0.10m;

        public decimal EquityCheapnessDrawdown = 0.35m;

        public decimal GoldCheapnessDrawdown = 0.25m;

        public decimal BaseEquityRiskShare = 0.7142857m;

        public decimal RelativeCheapnessRiskAdjustment = 0.15m;

        public decimal GoldPanicTilt;

        public decimal MinimumEquityRiskShare = 0.55m;

        public decimal MaximumEquityRiskShare = 0.85m;

        public decimal StockAbsoluteCheapnessDrawdown = 0.40m;

        public decimal StockRelativeDrawdownOffset = 0.10m;

        public decimal StockRelativeCheapnessRange = 0.30m;

        public decimal StockAbsoluteCheapnessWeight = 0.70m;

        public decimal StockRelativeCheapnessWeight = 0.30m;

        public decimal StockMultiplierExponent = 0.70m;

        public decimal HardCashReserveWeight = 0.05m;

        public decimal MinimumRebalanceZone = 0.0025m;

        public decimal RelativeRebalanceZone = 0.20m;

        public decimal RebalanceRate = 0.50m;

        public bool AllowSellOrders = true;

        public bool SweepFreeCashToMoneyMarket;
    }

    #endregion

    public static class SDKRebalancerByKorovinCalculator
    {
        #region Constants

        public const int IndexHistoryLookback = 252;

        public const int AssetHistoryLookback = 252;

        public const int BreadthSmaLength = 200;

        public const decimal HardCashReserveWeight = 0.05m;

        #endregion

        #region Daily schedule

        public static bool ShouldRunDaily(
            DateTime serverTime,
            TimeSpan scheduledTime,
            DateTime lastRebalanceDate)
        {
            return serverTime != DateTime.MinValue
                && serverTime.TimeOfDay >= scheduledTime
                && lastRebalanceDate.Date != serverTime.Date;
        }

        public static bool ShouldExpirePendingBuy(
            DateTime pendingRebalanceDate,
            DateTime serverTime,
            TimeSpan scheduledTime,
            bool hasActiveOrder)
        {
            return pendingRebalanceDate != DateTime.MinValue
                && serverTime != DateTime.MinValue
                && serverTime.Date > pendingRebalanceDate.Date
                && serverTime.TimeOfDay >= scheduledTime
                && hasActiveOrder == false;
        }

        public static bool IsMarketIndexDateReady(
            DateTime serverDate,
            DateTime latestIndexDate,
            bool usesDailySources)
        {
            if (serverDate == DateTime.MinValue
                || latestIndexDate == DateTime.MinValue
                || latestIndexDate.Date > serverDate.Date)
            {
                return false;
            }

            return usesDailySources || latestIndexDate.Date == serverDate.Date;
        }

        #endregion

        #region Portfolio valuation

        public static KorovinPortfolioValuationResult CalculatePortfolioValuation(
            decimal engineBalance,
            decimal openPositionsProfit,
            decimal strategyMarketValue,
            bool includeOpenPositionsProfit)
        {
            KorovinPortfolioValuationResult result = new KorovinPortfolioValuationResult();
            result.EngineBalance = engineBalance;
            result.OpenPositionsProfit = includeOpenPositionsProfit ? openPositionsProfit : 0m;
            result.StrategyMarketValue = strategyMarketValue;

            if (engineBalance <= 0m || strategyMarketValue < 0m)
            {
                return result;
            }

            result.NetAssetValue = engineBalance + result.OpenPositionsProfit;

            if (result.NetAssetValue <= 0m)
            {
                return result;
            }

            result.FreeCash = Math.Max(0m, result.NetAssetValue - strategyMarketValue);
            result.IsValid = true;
            return result;
        }

        #endregion

        #region Dividend adjusted signal

        public static List<KorovinSignalPoint> BuildDividendAdjustedSignalSeries(
            IReadOnlyList<KorovinDailyPrice> prices,
            IReadOnlyList<KorovinDividendEvent> dividends)
        {
            List<KorovinSignalPoint> result = new List<KorovinSignalPoint>();

            if (prices == null || prices.Count == 0)
            {
                return result;
            }

            Dictionary<DateTime, decimal> dividendsByDate = BuildDividendsByDate(dividends);
            for (int index = 0; index < prices.Count; index++)
            {
                KorovinDailyPrice price = prices[index];

                if (index > 0 && price.Date.Date <= prices[index - 1].Date.Date)
                {
                    throw new ArgumentException("Daily prices must be ordered by unique ascending dates.", nameof(prices));
                }

                decimal dividendAmount = 0m;
                dividendsByDate.TryGetValue(price.Date.Date, out dividendAmount);
                KorovinSignalPoint point = index == 0
                    ? BuildFirstDividendAdjustedSignalPoint(price, dividendAmount)
                    : BuildDividendAdjustedSignalPoint(
                        prices[index - 1],
                        result[index - 1],
                        price,
                        dividendAmount);
                result.Add(point);
            }

            return result;
        }

        public static KorovinSignalPoint BuildFirstDividendAdjustedSignalPoint(
            KorovinDailyPrice price,
            decimal dividendAmount)
        {
            if (price == null)
            {
                throw new ArgumentNullException(nameof(price));
            }

            KorovinSignalPoint point = new KorovinSignalPoint();
            point.Date = price.Date;
            point.MarketPrice = price.Price;
            point.SignalValue = price.Price;
            point.AdjustedReturn = 0m;
            point.DividendAmount = Math.Max(0m, dividendAmount);
            point.DividendAdjustmentIsValid = true;
            return point;
        }

        public static KorovinSignalPoint BuildDividendAdjustedSignalPoint(
            KorovinDailyPrice previousPrice,
            KorovinSignalPoint previousPoint,
            KorovinDailyPrice price,
            decimal dividendAmount)
        {
            if (previousPrice == null)
            {
                throw new ArgumentNullException(nameof(previousPrice));
            }

            if (previousPoint == null)
            {
                throw new ArgumentNullException(nameof(previousPoint));
            }

            if (price == null)
            {
                throw new ArgumentNullException(nameof(price));
            }

            KorovinSignalPoint point = new KorovinSignalPoint();
            point.Date = price.Date;
            point.MarketPrice = price.Price;
            point.DividendAmount = Math.Max(0m, dividendAmount);
            point.DividendAdjustmentIsValid = true;

            decimal ratio;

            if (point.DividendAmount > 0m)
            {
                decimal expectedPrice = previousPrice.Price - point.DividendAmount;

                if (expectedPrice > 0m)
                {
                    ratio = price.Price / expectedPrice;
                }
                else
                {
                    ratio = GetNormalPriceRatio(previousPrice.Price, price.Price);
                    point.DividendAdjustmentIsValid = false;
                }
            }
            else
            {
                ratio = GetNormalPriceRatio(previousPrice.Price, price.Price);
            }

            point.AdjustedReturn = ratio - 1m;
            point.SignalValue = previousPoint.SignalValue * ratio;
            return point;
        }

        private static Dictionary<DateTime, decimal> BuildDividendsByDate(IReadOnlyList<KorovinDividendEvent> dividends)
        {
            Dictionary<DateTime, decimal> result = new Dictionary<DateTime, decimal>();

            if (dividends == null)
            {
                return result;
            }

            for (int index = 0; index < dividends.Count; index++)
            {
                KorovinDividendEvent dividend = dividends[index];

                if (dividend.Amount <= 0m)
                {
                    continue;
                }

                DateTime date = dividend.ExDate.Date;
                decimal currentAmount = 0m;
                result.TryGetValue(date, out currentAmount);
                result[date] = currentAmount + dividend.Amount;
            }

            return result;
        }

        private static decimal GetNormalPriceRatio(decimal previousPrice, decimal currentPrice)
        {
            if (previousPrice <= 0m)
            {
                return 1m;
            }

            return currentPrice / previousPrice;
        }

        #endregion

        #region Panic and allocation

        public static KorovinMarketPanicResult CalculatePanic(
            IReadOnlyList<decimal> marketSignalHistory,
            IReadOnlyList<KorovinEnabledStockHistory> stocks)
        {
            return CalculatePanic(marketSignalHistory, stocks, new KorovinCalculationParameters());
        }

        public static KorovinMarketPanicResult CalculatePanic(
            IReadOnlyList<decimal> marketSignalHistory,
            IReadOnlyList<KorovinEnabledStockHistory> stocks,
            KorovinCalculationParameters parameters)
        {
            KorovinMarketPanicResult result = new KorovinMarketPanicResult();

            if (ArePanicParametersValid(parameters) == false
                || HasValidHistory(marketSignalHistory, parameters.IndexHistoryLookback) == false)
            {
                return result;
            }

            decimal currentMarket = marketSignalHistory[marketSignalHistory.Count - 1];
            decimal marketMaximum = GetMaximumLast(
                marketSignalHistory,
                parameters.IndexHistoryLookback);

            if (marketMaximum <= 0m)
            {
                return result;
            }

            int enabledCount = 0;
            int belowSmaCount = 0;

            if (stocks != null)
            {
                for (int index = 0; index < stocks.Count; index++)
                {
                    KorovinEnabledStockHistory stock = stocks[index];

                    if (stock == null || stock.IsEnabled == false)
                    {
                        continue;
                    }

                    if (HasValidHistory(stock.SignalHistory, parameters.BreadthSmaLength) == false)
                    {
                        return result;
                    }

                    enabledCount++;
                    decimal currentSignal = stock.SignalHistory[stock.SignalHistory.Count - 1];
                    decimal sma = GetAverageLast(stock.SignalHistory, parameters.BreadthSmaLength);

                    if (currentSignal < sma)
                    {
                        belowSmaCount++;
                    }
                }
            }

            result.IsValid = true;
            result.MarketDrawdown = Clip01(1m - currentMarket / marketMaximum);
            result.EnabledStocksCount = enabledCount;
            result.Breadth = enabledCount == 0 ? 0m : (decimal)belowSmaCount / enabledCount;
            result.PanicByDrawdown = Clip01(
                (result.MarketDrawdown - parameters.PanicDrawdownStart) / parameters.PanicDrawdownRange);
            result.PanicByBreadth = Clip01(
                (result.Breadth - parameters.PanicBreadthStart) / parameters.PanicBreadthRange);
            result.Panic = parameters.PanicDrawdownWeight * result.PanicByDrawdown
                + parameters.PanicBreadthWeight * result.PanicByBreadth;
            return result;
        }

        public static KorovinAllocationResult CalculateAllocation(
            KorovinMarketPanicResult panic,
            IReadOnlyList<decimal> goldSignalHistory)
        {
            return CalculateAllocation(panic, goldSignalHistory, new KorovinCalculationParameters());
        }

        public static KorovinAllocationResult CalculateAllocation(
            KorovinMarketPanicResult panic,
            IReadOnlyList<decimal> goldSignalHistory,
            KorovinCalculationParameters parameters)
        {
            KorovinAllocationResult result = new KorovinAllocationResult();

            if (panic == null || panic.IsValid == false || AreAllocationParametersValid(parameters) == false
                || HasValidHistory(goldSignalHistory, parameters.AssetHistoryLookback) == false)
            {
                return result;
            }

            decimal currentGold = goldSignalHistory[goldSignalHistory.Count - 1];
            decimal goldMaximum = GetMaximumLast(
                goldSignalHistory,
                parameters.AssetHistoryLookback);

            if (goldMaximum <= 0m)
            {
                return result;
            }

            decimal targetCash = Math.Max(
                parameters.MinimumCashWeight,
                parameters.BaseCashWeight - parameters.PanicCashReduction * panic.Panic);
            decimal riskWeight = 1m - targetCash;
            decimal equityCheapness = Clip01(panic.MarketDrawdown / parameters.EquityCheapnessDrawdown);
            decimal goldDrawdown = Clip01(1m - currentGold / goldMaximum);
            decimal goldCheapness = Clip01(goldDrawdown / parameters.GoldCheapnessDrawdown);
            decimal equityRiskShare = Clip(
                parameters.BaseEquityRiskShare
                    + parameters.RelativeCheapnessRiskAdjustment * (equityCheapness - goldCheapness)
                    - parameters.GoldPanicTilt * panic.Panic,
                parameters.MinimumEquityRiskShare,
                parameters.MaximumEquityRiskShare);

            result.IsValid = true;
            result.MarketDrawdown = panic.MarketDrawdown;
            result.GoldDrawdown = goldDrawdown;
            result.TargetCashWeight = targetCash;
            result.EquityRiskShare = equityRiskShare;
            result.TargetEquityWeight = riskWeight * equityRiskShare;
            result.TargetGoldWeight = riskWeight * (1m - equityRiskShare);
            return result;
        }

        #endregion

        #region Stock score and capped allocation

        public static KorovinStockScoreResult CalculateStockScore(
            IReadOnlyList<decimal> stockSignalHistory,
            decimal marketDrawdown)
        {
            return CalculateStockScore(stockSignalHistory, marketDrawdown, new KorovinCalculationParameters());
        }

        public static KorovinStockScoreResult CalculateStockScore(
            IReadOnlyList<decimal> stockSignalHistory,
            decimal marketDrawdown,
            KorovinCalculationParameters parameters)
        {
            KorovinStockScoreResult result = new KorovinStockScoreResult();

            if (AreStockScoreParametersValid(parameters) == false
                || HasValidHistory(stockSignalHistory, parameters.AssetHistoryLookback) == false)
            {
                return result;
            }

            decimal currentSignal = stockSignalHistory[stockSignalHistory.Count - 1];
            decimal maximum = GetMaximumLast(
                stockSignalHistory,
                parameters.AssetHistoryLookback);

            if (maximum <= 0m)
            {
                return result;
            }

            result.IsValid = true;
            result.Drawdown = Clip01(1m - currentSignal / maximum);
            result.RelativeDrawdown = result.Drawdown - marketDrawdown;
            result.AbsoluteCheapness = Clip01(result.Drawdown / parameters.StockAbsoluteCheapnessDrawdown);
            result.RelativeCheapness = Clip01(
                (result.RelativeDrawdown + parameters.StockRelativeDrawdownOffset)
                / parameters.StockRelativeCheapnessRange);
            result.Cheapness = parameters.StockAbsoluteCheapnessWeight * result.AbsoluteCheapness
                + parameters.StockRelativeCheapnessWeight * result.RelativeCheapness;
            result.Multiplier = (decimal)Math.Exp((double)(parameters.StockMultiplierExponent * result.Cheapness));
            return result;
        }

        public static KorovinCappedAllocationResult AllocateStocksWithCaps(
            IReadOnlyList<KorovinStockAllocationInput> stocks,
            decimal targetEquityWeight)
        {
            KorovinCappedAllocationResult result = new KorovinCappedAllocationResult();

            if (stocks == null || targetEquityWeight < 0m)
            {
                return result;
            }

            result.IsValid = true;

            if (targetEquityWeight == 0m || stocks.Count == 0)
            {
                result.CashShortfallWeight = targetEquityWeight;
                return result;
            }

            decimal sumBase = 0m;

            for (int index = 0; index < stocks.Count; index++)
            {
                if (stocks[index].BaseWeight > 0m)
                {
                    sumBase += stocks[index].BaseWeight;
                }
            }

            if (sumBase <= 0m)
            {
                result.CashShortfallWeight = targetEquityWeight;
                AddZeroStockTargets(result, stocks);
                return result;
            }

            decimal[] rawWeights = new decimal[stocks.Count];
            decimal rawSum = 0m;

            for (int index = 0; index < stocks.Count; index++)
            {
                KorovinStockAllocationInput stock = stocks[index];

                if (stock.BaseWeight <= 0m || stock.Multiplier <= 0m || stock.Cap <= 0m)
                {
                    continue;
                }

                rawWeights[index] = stock.BaseWeight / sumBase * stock.Multiplier;
                rawSum += rawWeights[index];
            }

            decimal[] allocated = new decimal[stocks.Count];
            bool[] isAvailable = new bool[stocks.Count];
            bool[] isCapped = new bool[stocks.Count];

            for (int index = 0; index < stocks.Count; index++)
            {
                isAvailable[index] = rawWeights[index] > 0m;
            }

            decimal remainingWeight = targetEquityWeight;

            while (remainingWeight > 0m && rawSum > 0m)
            {
                bool capWasApplied = false;

                for (int index = 0; index < stocks.Count; index++)
                {
                    if (isAvailable[index] == false)
                    {
                        continue;
                    }

                    decimal proposedWeight = remainingWeight * rawWeights[index] / rawSum;
                    decimal cap = Math.Max(0m, stocks[index].Cap);

                    if (proposedWeight > cap)
                    {
                        allocated[index] = cap;
                        remainingWeight -= cap;
                        rawSum -= rawWeights[index];
                        isAvailable[index] = false;
                        isCapped[index] = true;
                        capWasApplied = true;
                    }
                }

                if (capWasApplied)
                {
                    continue;
                }

                for (int index = 0; index < stocks.Count; index++)
                {
                    if (isAvailable[index])
                    {
                        allocated[index] = remainingWeight * rawWeights[index] / rawSum;
                    }
                }

                remainingWeight = 0m;
            }

            decimal allocatedSum = 0m;

            for (int index = 0; index < stocks.Count; index++)
            {
                KorovinStockTargetWeight target = new KorovinStockTargetWeight();
                target.Name = stocks[index].Name;
                target.Weight = allocated[index];
                target.IsCapped = isCapped[index];
                result.StockWeights.Add(target);
                allocatedSum += allocated[index];
            }

            result.AllocatedEquityWeight = allocatedSum;
            result.CashShortfallWeight = Math.Max(0m, targetEquityWeight - allocatedSum);
            return result;
        }

        private static void AddZeroStockTargets(
            KorovinCappedAllocationResult result,
            IReadOnlyList<KorovinStockAllocationInput> stocks)
        {
            for (int index = 0; index < stocks.Count; index++)
            {
                KorovinStockTargetWeight target = new KorovinStockTargetWeight();
                target.Name = stocks[index].Name;
                result.StockWeights.Add(target);
            }
        }

        #endregion

        #region Rebalance weight and planner

        public static decimal CalculateDividendNeutralRebalanceWeight(
            bool isExDate,
            decimal previousPortfolioWeight,
            decimal adjustedReturn,
            decimal currentPortfolioWeight)
        {
            if (isExDate)
            {
                return Math.Max(0m, previousPortfolioWeight * (1m + adjustedReturn));
            }

            return Math.Max(0m, currentPortfolioWeight);
        }

        public static KorovinRebalancePlanResult BuildRebalancePlan(KorovinRebalancePlanInput input)
        {
            return BuildRebalancePlan(input, new KorovinCalculationParameters());
        }

        public static KorovinRebalancePlanResult BuildRebalancePlan(
            KorovinRebalancePlanInput input,
            KorovinCalculationParameters parameters)
        {
            KorovinRebalancePlanResult result = new KorovinRebalancePlanResult();

            if (input == null || input.NetAssetValue <= 0m || input.FreeCash < 0m
                || input.EffectiveTargetCashWeight < 0m || AreRebalanceParametersValid(parameters) == false)
            {
                return result;
            }

            result.IsValid = true;
            result.TargetFreeCashWeight = parameters.SweepFreeCashToMoneyMarket
                ? 0m
                : parameters.HardCashReserveWeight;
            result.TargetMoneyMarketWeight = Math.Max(
                0m,
                input.EffectiveTargetCashWeight - result.TargetFreeCashWeight);
            result.HardReserveMoney = parameters.HardCashReserveWeight * input.NetAssetValue;

            List<KorovinRebalanceOrder> sellOrders = new List<KorovinRebalanceOrder>();
            List<KorovinRebalanceOrder> buyOrders = new List<KorovinRebalanceOrder>();

            if (input.RiskAssets != null)
            {
                for (int index = 0; index < input.RiskAssets.Count; index++)
                {
                    AddRequestedOrder(
                        input.RiskAssets[index],
                        input.NetAssetValue,
                        parameters,
                        sellOrders,
                        buyOrders);
                }
            }

            if (string.IsNullOrWhiteSpace(input.MoneyMarketName) == false)
            {
                KorovinRebalanceAssetInput moneyMarket = new KorovinRebalanceAssetInput();
                moneyMarket.Name = input.MoneyMarketName;
                moneyMarket.CurrentWeight = input.MoneyMarketRebalanceWeight;
                moneyMarket.TargetWeight = result.TargetMoneyMarketWeight;
                moneyMarket.RebalanceWeight = input.MoneyMarketRebalanceWeight;
                moneyMarket.Reason = "Money market rebalance";
                AddRequestedOrder(moneyMarket, input.NetAssetValue, parameters, sellOrders, buyOrders);
            }

            decimal requestedSellMoney = 0m;

            for (int index = 0; parameters.AllowSellOrders && index < sellOrders.Count; index++)
            {
                requestedSellMoney += Math.Abs(sellOrders[index].RequestedMoney);
                sellOrders[index].PlannedMoney = sellOrders[index].RequestedMoney;
                result.Orders.Add(sellOrders[index]);
            }

            result.ProjectedAvailableForBuys = Math.Max(
                0m,
                input.FreeCash + requestedSellMoney - result.HardReserveMoney);

            decimal requestedBuyMoney = 0m;

            for (int index = 0; index < buyOrders.Count; index++)
            {
                requestedBuyMoney += buyOrders[index].RequestedMoney;
            }

            result.BuyScale = requestedBuyMoney <= 0m
                ? 1m
                : Math.Min(1m, result.ProjectedAvailableForBuys / requestedBuyMoney);

            for (int index = 0; index < buyOrders.Count; index++)
            {
                buyOrders[index].PlannedMoney = buyOrders[index].RequestedMoney * result.BuyScale;

                if (buyOrders[index].PlannedMoney > 0m)
                {
                    result.Orders.Add(buyOrders[index]);
                }
            }

            if (parameters.SweepFreeCashToMoneyMarket)
            {
                ApplyMoneyMarketCashSweep(input, result);
            }

            return result;
        }

        private static void ApplyMoneyMarketCashSweep(
            KorovinRebalancePlanInput input,
            KorovinRebalancePlanResult result)
        {
            if (string.IsNullOrWhiteSpace(input.MoneyMarketName))
            {
                return;
            }

            decimal projectedFreeCash = input.FreeCash;
            KorovinRebalanceOrder moneyMarketOrder = new KorovinRebalanceOrder();
            int moneyMarketOrderIndex = -1;
            bool hasMoneyMarketOrder = false;

            for (int index = 0; index < result.Orders.Count; index++)
            {
                KorovinRebalanceOrder order = result.Orders[index];

                if (order.PlannedMoney < 0m)
                {
                    projectedFreeCash += Math.Abs(order.PlannedMoney);
                }
                else
                {
                    projectedFreeCash -= order.PlannedMoney;
                }

                if (string.Equals(order.Name, input.MoneyMarketName, StringComparison.OrdinalIgnoreCase))
                {
                    moneyMarketOrder = order;
                    moneyMarketOrderIndex = index;
                    hasMoneyMarketOrder = true;
                }
            }

            if (projectedFreeCash <= 0m)
            {
                return;
            }

            if (hasMoneyMarketOrder == false)
            {
                moneyMarketOrder = new KorovinRebalanceOrder();
                moneyMarketOrder.Name = input.MoneyMarketName;
                moneyMarketOrder.CurrentWeight = input.MoneyMarketRebalanceWeight;
                moneyMarketOrder.RebalanceWeight = input.MoneyMarketRebalanceWeight;
                moneyMarketOrder.TargetWeight = result.TargetMoneyMarketWeight;
                moneyMarketOrder.ErrorWeight = projectedFreeCash / input.NetAssetValue;
                moneyMarketOrder.Side = KorovinRebalanceOrderSide.Buy;
                moneyMarketOrder.RequestedMoney = projectedFreeCash;
                moneyMarketOrder.PlannedMoney = projectedFreeCash;
                moneyMarketOrder.Reason = "Free cash sweep";
                result.Orders.Add(moneyMarketOrder);
                return;
            }

            decimal netMoney = moneyMarketOrder.PlannedMoney + projectedFreeCash;
            result.Orders.RemoveAt(moneyMarketOrderIndex);

            if (netMoney == 0m)
            {
                return;
            }

            moneyMarketOrder.RequestedMoney = netMoney;
            moneyMarketOrder.PlannedMoney = netMoney;
            moneyMarketOrder.Side = netMoney > 0m
                ? KorovinRebalanceOrderSide.Buy
                : KorovinRebalanceOrderSide.Sell;
            moneyMarketOrder.ErrorWeight = netMoney / input.NetAssetValue;
            moneyMarketOrder.Reason = "Money market rebalance and free cash sweep";

            if (moneyMarketOrder.Side == KorovinRebalanceOrderSide.Buy)
            {
                result.Orders.Add(moneyMarketOrder);
                return;
            }

            int insertIndex = 0;

            while (insertIndex < result.Orders.Count
                && result.Orders[insertIndex].Side == KorovinRebalanceOrderSide.Sell)
            {
                insertIndex++;
            }

            result.Orders.Insert(insertIndex, moneyMarketOrder);
        }

        private static void AddRequestedOrder(
            KorovinRebalanceAssetInput asset,
            decimal netAssetValue,
            KorovinCalculationParameters parameters,
            List<KorovinRebalanceOrder> sells,
            List<KorovinRebalanceOrder> buys)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.Name))
            {
                return;
            }

            decimal targetWeight = Math.Max(0m, asset.TargetWeight);
            decimal rebalanceWeight = Math.Max(0m, asset.RebalanceWeight);
            decimal error = targetWeight - rebalanceWeight;
            decimal zone = Math.Max(parameters.MinimumRebalanceZone, parameters.RelativeRebalanceZone * targetWeight);

            if (Math.Abs(error) <= zone)
            {
                return;
            }

            KorovinRebalanceOrder order = new KorovinRebalanceOrder();
            order.Name = asset.Name;
            order.CurrentWeight = Math.Max(0m, asset.CurrentWeight);
            order.RebalanceWeight = rebalanceWeight;
            order.TargetWeight = targetWeight;
            order.ErrorWeight = error;
            order.RequestedMoney = parameters.RebalanceRate * error * netAssetValue;
            order.Reason = asset.Reason;

            if (order.RequestedMoney < 0m)
            {
                order.Side = KorovinRebalanceOrderSide.Sell;
                sells.Add(order);
            }
            else if (order.RequestedMoney > 0m)
            {
                order.Side = KorovinRebalanceOrderSide.Buy;
                buys.Add(order);
            }
        }

        #endregion

        #region Order sizing

        public static decimal CalculateRequiredMoneyMarketSale(
            decimal plannedMoneyMarketSale,
            decimal executableBuyMoney,
            decimal freeCash,
            decimal otherSellMoney)
        {
            decimal maximumSale = Math.Max(0m, plannedMoneyMarketSale);
            decimal fundingShortfall = Math.Max(
                0m,
                Math.Max(0m, executableBuyMoney)
                - Math.Max(0m, freeCash)
                - Math.Max(0m, otherSellMoney));

            return Math.Min(maximumSale, fundingShortfall);
        }

        public static KorovinOrderSizeResult CalculateOrderSize(
            decimal requestedMoney,
            decimal price,
            decimal lot,
            decimal volumeStep,
            int volumeDecimals,
            decimal currentLongVolume)
        {
            KorovinOrderSizeResult result = new KorovinOrderSizeResult();

            if (requestedMoney == 0m || price <= 0m || lot <= 0m || volumeStep <= 0m)
            {
                return result;
            }

            int safeDecimals = Math.Max(0, Math.Min(28, volumeDecimals));
            decimal rawVolume = Math.Abs(requestedMoney) / (price * lot);

            if (requestedMoney < 0m)
            {
                if (currentLongVolume <= 0m)
                {
                    return result;
                }

                rawVolume = Math.Min(rawVolume, currentLongVolume);
                result.Side = KorovinRebalanceOrderSide.Sell;
            }
            else
            {
                result.Side = KorovinRebalanceOrderSide.Buy;
            }

            decimal volume = FloorToStep(rawVolume, volumeStep);
            volume = FloorToDecimals(volume, safeDecimals);
            volume = FloorToStep(volume, volumeStep);

            if (requestedMoney < 0m && volume > currentLongVolume)
            {
                volume = FloorToStep(currentLongVolume, volumeStep);
                volume = FloorToDecimals(volume, safeDecimals);
            }

            if (volume <= 0m)
            {
                result.Side = KorovinRebalanceOrderSide.None;
                return result;
            }

            result.Volume = volume;
            result.Money = volume * price * lot;
            return result;
        }

        public static decimal CalculateReopenTargetVolume(
            decimal currentLongVolume,
            KorovinRebalanceOrderSide side,
            decimal changeVolume)
        {
            decimal currentVolume = Math.Max(0m, currentLongVolume);
            decimal safeChangeVolume = Math.Max(0m, changeVolume);

            if (side == KorovinRebalanceOrderSide.Buy)
            {
                return currentVolume + safeChangeVolume;
            }

            if (side == KorovinRebalanceOrderSide.Sell)
            {
                return Math.Max(0m, currentVolume - safeChangeVolume);
            }

            return currentVolume;
        }

        private static decimal FloorToStep(decimal value, decimal step)
        {
            if (value <= 0m || step <= 0m)
            {
                return 0m;
            }

            return Math.Floor(value / step) * step;
        }

        private static decimal FloorToDecimals(decimal value, int decimals)
        {
            decimal multiplier = 1m;

            for (int index = 0; index < decimals; index++)
            {
                multiplier *= 10m;
            }

            return Math.Floor(value * multiplier) / multiplier;
        }

        #endregion

        #region Math helpers

        private static bool ArePanicParametersValid(KorovinCalculationParameters parameters)
        {
            return parameters != null
                && parameters.IndexHistoryLookback > 0
                && parameters.BreadthSmaLength > 0
                && parameters.PanicDrawdownRange > 0m
                && parameters.PanicBreadthRange > 0m
                && parameters.PanicDrawdownWeight >= 0m
                && parameters.PanicBreadthWeight >= 0m;
        }

        private static bool AreAllocationParametersValid(KorovinCalculationParameters parameters)
        {
            return parameters != null
                && parameters.AssetHistoryLookback > 0
                && parameters.EquityCheapnessDrawdown > 0m
                && parameters.GoldCheapnessDrawdown > 0m
                && parameters.MinimumCashWeight >= 0m
                && parameters.BaseCashWeight <= 1m
                && parameters.GoldPanicTilt >= -1m
                && parameters.GoldPanicTilt <= 1m
                && parameters.MinimumEquityRiskShare >= 0m
                && parameters.MaximumEquityRiskShare <= 1m
                && parameters.MinimumEquityRiskShare <= parameters.MaximumEquityRiskShare;
        }

        private static bool AreStockScoreParametersValid(KorovinCalculationParameters parameters)
        {
            return parameters != null
                && parameters.AssetHistoryLookback > 0
                && parameters.StockAbsoluteCheapnessDrawdown > 0m
                && parameters.StockRelativeCheapnessRange > 0m
                && parameters.StockAbsoluteCheapnessWeight >= 0m
                && parameters.StockRelativeCheapnessWeight >= 0m;
        }

        private static bool AreRebalanceParametersValid(KorovinCalculationParameters parameters)
        {
            return parameters != null
                && parameters.HardCashReserveWeight >= 0m
                && parameters.HardCashReserveWeight <= 1m
                && parameters.MinimumRebalanceZone >= 0m
                && parameters.RelativeRebalanceZone >= 0m
                && parameters.RebalanceRate >= 0m
                && parameters.RebalanceRate <= 1m;
        }

        private static bool HasValidHistory(IReadOnlyList<decimal> history, int requiredCount)
        {
            if (history == null || history.Count < requiredCount)
            {
                return false;
            }

            int startIndex = history.Count - requiredCount;

            for (int index = startIndex; index < history.Count; index++)
            {
                if (history[index] <= 0m)
                {
                    return false;
                }
            }

            return true;
        }

        private static decimal GetMaximumLast(IReadOnlyList<decimal> values, int count)
        {
            int startIndex = values.Count - count;
            decimal maximum = values[startIndex];

            for (int index = startIndex + 1; index < values.Count; index++)
            {
                if (values[index] > maximum)
                {
                    maximum = values[index];
                }
            }

            return maximum;
        }

        private static decimal GetAverageLast(IReadOnlyList<decimal> values, int count)
        {
            int startIndex = values.Count - count;
            decimal sum = 0m;

            for (int index = startIndex; index < values.Count; index++)
            {
                sum += values[index];
            }

            return sum / count;
        }

        private static decimal Clip01(decimal value)
        {
            return Clip(value, 0m, 1m);
        }

        private static decimal Clip(decimal value, decimal minimum, decimal maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        #endregion
    }
}
