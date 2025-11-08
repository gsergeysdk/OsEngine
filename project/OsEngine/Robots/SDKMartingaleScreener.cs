using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Indicators;

namespace OsEngine.Robots
{
    [Bot("SDKMartingaleScreener")]
    public class SDKMartingaleScreener : BotPanel
    {
        BotTabScreener _tab;

        public StrategyParameterString Regime;

        private StrategyParameterTimeOfDay TimeStart;
        private StrategyParameterTimeOfDay TimeEnd;

        public StrategyParameterInt MaxPositions;

        // Indicator settings
        private StrategyParameterInt bollingerLen;
        private StrategyParameterDecimal bollingerDev;

        private StrategyParameterDecimal minBollingerDev;
        private StrategyParameterDecimal maxBollingerDev;
        private StrategyParameterBool useCounterTrandBollinger;

        private StrategyParameterInt trandPeriodFast;
        private StrategyParameterInt trandPeriodSlow;
        private StrategyParameterInt trandCounter;
        private StrategyParameterBool useTrandFilter;

        public StrategyParameterDecimal TrailStop;
        public StrategyParameterDecimal MinProfit;

        public StrategyParameterInt maxPositionsOneActive;
        public StrategyParameterDecimal deltaPricePercent;
        public StrategyParameterDecimal deltaPriceMultiplicator;
        private StrategyParameterBool openPositionsInLoss;
        private StrategyParameterBool openPositionsInProfit;

        public SDKVolume volume;

        public SDKMartingaleScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);

            _tab = TabsScreener[0];

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });
            MaxPositions = CreateParameter("Max positions", 5, 0, 20, 1);
            TimeStart = CreateParameterTimeOfDay("Start Trade Time", 7, 35, 0, 0);
            TimeEnd = CreateParameterTimeOfDay("End Trade Time", 22, 25, 0, 0);

            // Indicator settings
            bollingerLen = CreateParameter("Bollinger length", 50, 0, 20, 1);
            bollingerDev = CreateParameter("Bollinger deviation", 2m, 0, 20, 1m);
            minBollingerDev = CreateParameter("Min Bollinger volitility %", 1m, 0, 1, 0.1m);
            maxBollingerDev = CreateParameter("Max Bollinger volitility %", 100m, 0, 100, 5m);
            useCounterTrandBollinger = CreateParameter("Use Counter Trand Bollinger", false);

            trandPeriodFast = CreateParameter("Trand Period Fast", 100, 50, 300, 50);
            trandPeriodSlow = CreateParameter("Trand Period Slow", 200, 100, 500, 50);
            trandCounter = CreateParameter("Trand Counter", 10, 10, 50, 10);
            useTrandFilter = CreateParameter("Use Trand Filter", true);

            TrailStop = CreateParameter("Trail stop %", 4m, 0, 20, 1m);
            MinProfit = CreateParameter("Min profit %", 4m, 0, 20, 1m);
            maxPositionsOneActive = CreateParameter("Max positions for one active", 5, 0, 20, 1);
            deltaPricePercent = CreateParameter("Delta price to next position %", 10m, 1m, 20m, 1m);
            deltaPriceMultiplicator = CreateParameter("Delta price multiplicator", 1m, 1m, 2m, 0.1m);
            openPositionsInLoss = CreateParameter("Open Positions In Loss", true);
            openPositionsInProfit = CreateParameter("Open Positions In Profit", true);

            _tab.CreateCandleIndicator(1, "Bollinger", new List<string>() { "100", "2" }, "Prime");
            _tab.CreateCandleIndicator(2, "TrandPhaseDema", new List<string>() { "200", "600", "10" }, "Trand");

            volume = new SDKVolume(this);
            ParametrsChangeByUser += Screener_ParametrsChangeByUser;
        }

        public override string GetNameStrategyType()
        {
            return "SDKMartingaleScreener";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void Screener_ParametrsChangeByUser()
        {
            _tab.UpdateIndicatorsParameters();
        }

        private void LogicCheckIndicators(BotTabSimple tab)
        {
            Aindicator bollinger = (Aindicator)tab.Indicators[0];

            if (bollinger.ParametersDigit[0].Value != bollingerLen.ValueInt
                || bollinger.ParametersDigit[1].Value != bollingerDev.ValueDecimal)
            {
                bollinger.ParametersDigit[0].Value = bollingerLen.ValueInt;
                bollinger.ParametersDigit[1].Value = bollingerDev.ValueDecimal;
                bollinger.Save();
                bollinger.Reload();
            }

            Aindicator trand = (Aindicator)tab.Indicators[1];

            if (trand.ParametersDigit[0].Value != trandPeriodFast.ValueInt
                || trand.ParametersDigit[1].Value != trandPeriodSlow.ValueInt
                || trand.ParametersDigit[2].Value != trandCounter.ValueInt)
            {
                trand.ParametersDigit[0].Value = trandPeriodFast.ValueInt;
                trand.ParametersDigit[1].Value = trandPeriodSlow.ValueInt;
                trand.ParametersDigit[2].Value = trandCounter.ValueInt;
                trand.Save();
                trand.Reload();
            }
        }

        private Side hasSignalToOpen(List<Candle> candles, BotTabSimple tab)
        {
            if (TimeStart.Value > tab.TimeServerCurrent ||
                TimeEnd.Value < tab.TimeServerCurrent)
            {
                return Side.None;
            }

            decimal lastCandleClose = candles[^1].Close;
            decimal lastCandleOpen = candles[^1].Open;

            Aindicator bollinger = (Aindicator)tab.Indicators[0];
            Aindicator trand = (Aindicator)tab.Indicators[1];

            if (bollinger.DataSeries[0].Values.Count == 0 ||
                bollinger.DataSeries[0].Last == 0)
            {
                return Side.None;
            }

            decimal trandUp = trand.DataSeries[0].Last;
            decimal trandDown = trand.DataSeries[1].Last;

            if (useTrandFilter.ValueBool && (trandDown > 0 || trandUp <= 0))
                return Side.None;

            decimal lastUpBollingerLine = bollinger.DataSeries[0].Last;
            //decimal prevUpBollingerLine = bollinger.DataSeries[0].Values[^2];
            decimal lastDownBollingerLine = bollinger.DataSeries[1].Last;
            //decimal prevDownBollingerLine = bollinger.DataSeries[1].Values[^2];

            if (lastUpBollingerLine < lastDownBollingerLine * (1m + minBollingerDev.ValueDecimal / 100m))
                return Side.None;
            if (lastUpBollingerLine > lastDownBollingerLine * (1m + maxBollingerDev.ValueDecimal / 100m))
                return Side.None;

            if (useCounterTrandBollinger.ValueBool)
            {
                if (lastCandleClose > lastDownBollingerLine && lastCandleOpen < lastDownBollingerLine)
                    return Side.Buy;
            }
            else 
                if (lastCandleClose > lastUpBollingerLine && lastCandleOpen < lastUpBollingerLine)
                    return Side.Buy;

            return Side.None;
        }

        // logic
        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            LogicCheckIndicators(tab);
            if (Regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count <= trandPeriodSlow.ValueInt)
            {
                return;
            }

            List<Position> openPositions = tab.PositionsOpenAll;
            if (openPositions == null)
                return;

            if (_tab.PositionsOpenAll.Count < MaxPositions.ValueInt)
            {
                if (Regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }
                LogicOpenPosition(candles, tab);
            }
            //else
            {
                LogicClosePosition(candles, tab);
            }
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            List<Position> openPositions = tab.PositionsOpenAll;
            if (openPositions.Count >= maxPositionsOneActive.ValueInt)
            {
                return;
            }

            decimal lastPrice = candles[candles.Count - 1].Close;
            decimal minEnterPricePosition = lastPrice * 1000m;
            decimal maxEnterPricePosition = 0m;
            for (int i = 0; i < openPositions.Count; i++)
            {
                Position position = openPositions[i];
                if (position.State == PositionStateType.Open)
                {
                    minEnterPricePosition = Math.Min(minEnterPricePosition, position.EntryPrice);
                    maxEnterPricePosition = Math.Max(maxEnterPricePosition, position.EntryPrice);
                }
            }

            decimal martingaleK = deltaPriceMultiplicator.ValueDecimal;
            decimal priceToNextDownPositionK = 1m - deltaPricePercent.ValueDecimal * ((decimal)openPositions.Count * martingaleK) / 100m;
            decimal priceToNextUpPositionK = 1m + deltaPricePercent.ValueDecimal * ((decimal)openPositions.Count * martingaleK) / 100m;

            if (openPositions.Count == 0 ||
                (openPositionsInLoss && (lastPrice <= minEnterPricePosition * priceToNextDownPositionK)) ||
                (openPositionsInProfit && (lastPrice >= maxEnterPricePosition * priceToNextUpPositionK)))
            {
                if (hasSignalToOpen(candles, tab) == Side.Buy)
                {
                    tab.BuyAtMarket(volume.GetVolume(tab));
                }
            }
        }

        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab)
        {
            decimal lastClose = candles[candles.Count - 1].Close;
            decimal stop = 0;
            decimal allEntryPrice = 0;
            //Aindicator smaFast = (Aindicator)tab.Indicators[0];
            //Aindicator smaSlow = (Aindicator)tab.Indicators[1];
            //decimal lastSmaFast = smaFast.DataSeries[0].Last;
            //decimal lastSmaSlow = smaSlow.DataSeries[0].Last;
            //if (lastSmaFast > lastSmaSlow)
            //    return;

            if (TrailStop.ValueDecimal > 0)
                stop = lastClose - lastClose * (TrailStop.ValueDecimal / 100);

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions.Count > 0)
            {
                decimal allVolume = 0;
                decimal allPrice = 0;
                for (int i = 0; i < openPositions.Count; i++)
                {
                    Position position = openPositions[i];
                    if (position.State == PositionStateType.Open)
                    {
                        allVolume += position.OpenVolume;
                        allPrice += position.OpenVolume * position.EntryPrice;
                    }
                }
                if (allVolume > 0)
                    allEntryPrice = allPrice / allVolume;
            }

            decimal minExitPrice = allEntryPrice * (1m + (MinProfit.ValueDecimal / 100));
            bool needCloseAll = false; // minExitPrice > 0 && lastClose > minExitPrice && hasSignalToOpen(candles, tab) == Side.Sell;

            for (int i = 0; i < openPositions.Count; i++)
            {
                Position position = openPositions[i];

                if (position.State != PositionStateType.Open
                              ||
                              (position.CloseOrders != null
                              && position.CloseOrders.Count > 0)
                              )
                {
                    continue;
                }

                if (needCloseAll)
                    tab.CloseAtMarket(position, position.OpenVolume);
                else
                {
                    minExitPrice = allEntryPrice > 0m ? allEntryPrice : position.EntryPrice;
                    minExitPrice *= 1m + (MinProfit.ValueDecimal / 100);
                    if (stop > minExitPrice)
                        tab.CloseAtTrailingStopMarket(position, stop);
                }
            }
        }
    }
}