using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Market.Servers;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Indicators;
using OsEngine.Market.Servers.Tester;
using OsEngine.Charts.CandleChart.Indicators;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace OsEngine.Robots
{
    [Bot("SDKTimeTradingScreener")]
    public class SDKTimeTradingScreener : BotPanel
    {
        BotTabScreener _tab;

        public StrategyParameterString Regime;
        public StrategyParameterInt MaxPositions;
        public StrategyParameterInt PChannelLen;

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
            PChannelLen = CreateParameter("Price channel length", 50, 0, 20, 1);
            TrailStop = CreateParameter("Trail stop %", 4m, 0, 20, 1m);
            MinProfit = CreateParameter("Min profit %", 4m, 0, 20, 1m);
            maxPositionsOneActive = CreateParameter("Max positions for one active", 5, 0, 20, 1);
            deltaPricePercent = CreateParameter("Delta price to next position %", 10m, 1m, 20m, 1m);
            _tab.CreateCandleIndicator(1,
                "PriceChannel",
                new List<string>() { PChannelLen.ValueInt.ToString(), PChannelLen.ValueInt.ToString() },
                "Prime");

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

            _tab._indicators[0].Parameters = new List<string>() { PChannelLen.ValueInt.ToString(), PChannelLen.ValueInt.ToString() };

            _tab.UpdateIndicatorsParameters();
        }


        // logic
        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < PChannelLen.ValueInt)
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
                    minEnterPricePosition = Math.Min(minEnterPricePosition, position.EntryPrice);
            }

            const decimal martingaleK = 1m / 3m;
            decimal priceToNextPositionK = 1m - deltaPricePercent.ValueDecimal * (1m + (decimal)openPositions.Count * martingaleK) / 100m;
            if (lastPrice >= minEnterPricePosition * priceToNextPositionK)
                return;

            Aindicator priceChannel = (Aindicator)tab.Indicators[0];
            decimal lastPcUp = priceChannel.DataSeries[0].Values[priceChannel.DataSeries[0].Values.Count - 2];

            if (lastPrice > lastPcUp)
            {
                tab.BuyAtMarket(volume.GetVolume(tab));
            }
        }

        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab)
        {
            decimal lastClose = candles[candles.Count - 1].Close;
            decimal stop = 0;
            decimal allEntryPrice = 0;
            if (TrailStop.ValueDecimal > 0)
                stop = lastClose - lastClose * (TrailStop.ValueDecimal / 100);
            else
            {
                Aindicator priceChannel = (Aindicator)tab.Indicators[0];
                decimal lastPcDown = priceChannel.DataSeries[1].Values[priceChannel.DataSeries[1].Values.Count - 2];
                stop = lastPcDown;
            }

            List<Position> openPositions = tab.PositionsOpenAll;

            int countAverage = openPositions.Count; // Количество позиций в усреднении

            if (openPositions.Count >= 2)
            {
                decimal allVolume = 0;
                decimal allPrice = 0;
                for (int i = Math.Max(0, openPositions.Count - countAverage); i < openPositions.Count; i++)
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

                decimal minExitPrice = allEntryPrice > 0m && i >= openPositions.Count - countAverage ? allEntryPrice : position.EntryPrice;
                minExitPrice *= 1m + (MinProfit.ValueDecimal / 100);

                if (stop > minExitPrice)
                    tab.CloseAtTrailingStopMarket(position, stop);
            }
        }
    }

}