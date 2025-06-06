using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Indicators;

namespace OsEngine.Robots
{
    [Bot("SDKTimeTradingScreener")]
    public class SDKTimeTradingScreener : BotPanel
    {
        BotTabScreener _tab;

        public StrategyParameterString Regime;
        public StrategyParameterInt MaxPositions;
        public StrategyParameterInt PChannelLen;
        public StrategyParameterInt SmaFilterFastLen;
        public StrategyParameterInt SmaFilterSlowLen;
        public StrategyParameterInt WilliamsRangeLen;
        public StrategyParameterInt WilliamsRangeUpLine;
        public StrategyParameterInt WilliamsRangeDownLine;

        public StrategyParameterDecimal TrailStop;
        public StrategyParameterDecimal MinProfit;

        public StrategyParameterInt maxPositionsOneActive;
        public StrategyParameterDecimal deltaPricePercent;

        public SDKVolume volume;

        public SDKTimeTradingScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);

            _tab = TabsScreener[0];

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });
            MaxPositions = CreateParameter("Max positions", 5, 0, 20, 1);
            //PChannelLen = CreateParameter("Price channel length", 50, 0, 20, 1);
            SmaFilterFastLen = CreateParameter("Sma filter fast Len", 50, 50, 300, 10);
            SmaFilterSlowLen = CreateParameter("Sma filter slow Len", 200, 100, 800, 10);
            WilliamsRangeLen = CreateParameter("Willams Range Len", 100, 100, 800, 10);
            WilliamsRangeUpLine = CreateParameter("Willams Range Up Line", -20, -50, -10, 1);
            WilliamsRangeDownLine = CreateParameter("Willams Range Down Line", -80, -100, -50, 1);
            TrailStop = CreateParameter("Trail stop %", 4m, 0, 20, 1m);
            MinProfit = CreateParameter("Min profit %", 4m, 0, 20, 1m);
            maxPositionsOneActive = CreateParameter("Max positions for one active", 5, 0, 20, 1);
            deltaPricePercent = CreateParameter("Delta price to next position %", 10m, 1m, 20m, 1m);
            _tab.CreateCandleIndicator(1,
                "Ssma",
                new List<string>() { SmaFilterFastLen.ValueInt.ToString() },
                "Prime");
            _tab.CreateCandleIndicator(2,
                "Ssma",
                new List<string>() { SmaFilterSlowLen.ValueInt.ToString() },
                "Prime");
            _tab.CreateCandleIndicator(3,
                "WilliamsRange",
                new List<string>() { WilliamsRangeLen.ValueInt.ToString() },
                "WilliamsRangeArea");

            volume = new SDKVolume(this);
            volume.Volume.ValueDecimal = 100m / MaxPositions.ValueInt;
            ParametrsChangeByUser += Screener_ParametrsChangeByUser;
        }

        public override string GetNameStrategyType()
        {
            return "SDKTimeTradingScreener";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void Screener_ParametrsChangeByUser()
        {
            volume.Volume.ValueDecimal = 100m / MaxPositions.ValueInt;

            _tab._indicators[0].Parameters = new List<string>() { SmaFilterFastLen.ValueInt.ToString() };
            _tab._indicators[1].Parameters = new List<string>() { SmaFilterSlowLen.ValueInt.ToString() };
            _tab._indicators[2].Parameters = new List<string>() { WilliamsRangeLen.ValueInt.ToString() };

            _tab.UpdateIndicatorsParameters();
        }

        private void LogicCheckIndicators(BotTabSimple tab)
        {
            Aindicator smaFast = (Aindicator)tab.Indicators[0];
            if (smaFast.ParametersDigit[0].Value != SmaFilterFastLen.ValueInt)
            {
                smaFast.ParametersDigit[0].Value = SmaFilterFastLen.ValueInt;
                smaFast.Save();
                smaFast.Reload();
            }
            Aindicator smaSlow = (Aindicator)tab.Indicators[1];
            if (smaSlow.ParametersDigit[0].Value != SmaFilterSlowLen.ValueInt)
            {
                smaSlow.ParametersDigit[0].Value = SmaFilterSlowLen.ValueInt;
                smaSlow.Save();
                smaSlow.Reload();
            }
            Aindicator williamsRange = (Aindicator)tab.Indicators[2];
            if (williamsRange.ParametersDigit[0].Value != WilliamsRangeLen.ValueInt)
            {
                williamsRange.ParametersDigit[0].Value = WilliamsRangeLen.ValueInt;
                williamsRange.Save();
                williamsRange.Reload();
            }
        }

        private Side hasSignalToOpen(List<Candle> candles, BotTabSimple tab)
        {
            Aindicator smaFast = (Aindicator)tab.Indicators[0];
            Aindicator smaSlow = (Aindicator)tab.Indicators[1];
            decimal lastSmaFast = smaFast.DataSeries[0].Last;
            decimal lastSmaSlow = smaSlow.DataSeries[0].Last;
            decimal prevSmaFast = smaFast.DataSeries[0].Values[smaFast.DataSeries[0].Values.Count - 2];
            decimal prevSmaSlow = smaSlow.DataSeries[0].Values[smaSlow.DataSeries[0].Values.Count - 2];

            Aindicator williamsRange = (Aindicator)tab.Indicators[2];
            decimal lastWilliamsRange = williamsRange.DataSeries[0].Last;
            decimal prevWilliamsRange = williamsRange.DataSeries[0].Values[williamsRange.DataSeries[0].Values.Count - 2];


            if (prevWilliamsRange < WilliamsRangeDownLine.ValueInt && lastWilliamsRange >= WilliamsRangeDownLine.ValueInt)
                return Side.Buy;
            if (prevWilliamsRange > WilliamsRangeUpLine.ValueInt && lastWilliamsRange <= WilliamsRangeUpLine.ValueInt)
                return Side.Sell;
            //if (lastSmaSlow > prevSmaSlow && lastSmaFast > prevSmaFast && lastSmaFast > lastSmaSlow)
            //    return Side.Buy;
            //if (lastSmaSlow < prevSmaSlow && lastSmaFast < prevSmaFast && lastSmaFast < lastSmaSlow)
            //    return Side.Sell;

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

            if (candles.Count <= SmaFilterSlowLen.ValueInt)
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
            decimal minEnterPricePosition = lastPrice * 2m;
            for (int i = 0; i < openPositions.Count; i++)
            {
                Position position = openPositions[i];
                if (position.State == PositionStateType.Open)
                {
                    minEnterPricePosition = Math.Min(minEnterPricePosition, position.EntryPrice);
                }
            }

            const decimal martingaleK = 1m / 3m;
            decimal priceToNextDownPositionK = 1m - deltaPricePercent.ValueDecimal * (1m + (decimal)openPositions.Count * martingaleK) / 100m;
            decimal priceToNextUpPositionK = openPositions.Count < 4 ? (1m + deltaPricePercent.ValueDecimal / 100m) : 1000m;
            if (lastPrice >= minEnterPricePosition * priceToNextDownPositionK)
                return;

            if (hasSignalToOpen(candles, tab) == Side.Buy)
            {
                tab.BuyAtMarket(volume.GetVolume(tab));
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
            bool needCloseAll = minExitPrice > 0 && lastClose > minExitPrice && hasSignalToOpen(candles, tab) == Side.Sell;

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