using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Indicators;

namespace OsEngine.Robots.Screeners
{
    [Bot("SDKBollingerScreener")] // We create an attribute so that we don't write anything to the BotFactory
    public class SDKBollingerScreener : BotPanel
    {
        private BotTabScreener _tabScreener;

        // Basic settings
        private StrategyParameterString _regime;
        private StrategyParameterInt _maxPositions;

        // Indicator settings
        private StrategyParameterInt _bollingerLen;
        private StrategyParameterDecimal _bollingerDev;

        // Exit setting
        private StrategyParameterDecimal _trailStop;
        public StrategyParameterInt PriceChannelLength;
        private StrategyParameterDecimal _takeProfit;

        public StrategyParameterBool SmaFilterIsOn;
        public StrategyParameterInt SmaFilterLen;

        private StrategyParameterTimeOfDay TimeStart;
        private StrategyParameterTimeOfDay TimeEnd;

        public SDKVolume volume;

        public SDKBollingerScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);
            _tabScreener = TabsScreener[0];

            _tabScreener.CandleFinishedEvent += _screenerTab_CandleFinishedEvent;
            _tabScreener.CandleUpdateEvent += _screenerTab_CandleUpdateEvent;


            // Create indicator Bollinger
            _tabScreener.CreateCandleIndicator(1, "Bollinger", new List<string>() { "100", "2" }, "Prime");
            _tabScreener.CreateCandleIndicator(2, "PriceChannel", new List<string>() { "10", "10" }, "Prime");

            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });
            _maxPositions = CreateParameter("Max positions", 5, 0, 20, 1);

            TimeStart = CreateParameterTimeOfDay("Start Trade Time", 7, 35, 0, 0);
            TimeEnd = CreateParameterTimeOfDay("End Trade Time", 22, 25, 0, 0);

            // Indicator settings
            _bollingerLen = CreateParameter("Bollinger length", 50, 0, 20, 1);
            _bollingerDev = CreateParameter("Bollinger deviation", 2m, 0, 20, 1m);

            // Exit setting
            _trailStop = CreateParameter("Trail stop %", 2.9m, 0, 20, 1m);
            PriceChannelLength = CreateParameter("Price channel length", 10, 10, 80, 5);
            _takeProfit = CreateParameter("Take profit %", 7m, 0, 20, 1m);

            SmaFilterIsOn = CreateParameter("Sma filter is on", true);
            SmaFilterLen = CreateParameter("Sma filter Len", 100, 100, 300, 10);


            volume = new SDKVolume(this);
        }

        // The name of the robot in OsEngine
        public override string GetNameStrategyType()
        {
            return "SDKBollingerScreener";
        }

        // Show settings GUI
        public override void ShowIndividualSettingsDialog()
        {

        }

        // logic

        private void _screenerTab_CandleUpdateEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (_regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < 5)
            {
                return;
            }

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions != null && openPositions.Count > 0)
            {
                LogicClosePosition(candles, tab, openPositions[0]);
            }
        }

        private void _screenerTab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (_regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < 5)
            {
                return;
            }

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
            {
                if (_regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }
                LogicOpenPosition(candles, tab);
            }
            else
            {
                LogicClosePosition(candles, tab, openPositions[0]);
            }
        }

        // Logic open position
        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            if (_tabScreener.PositionsOpenAll.Count >= _maxPositions.ValueInt)
            {
                return;
            }

            if (TimeStart.Value > tab.TimeServerCurrent ||
                TimeEnd.Value < tab.TimeServerCurrent)
            {
                return;
            }

            decimal lastCandleClose = candles[^1].Close;
            decimal prevCandleClose = candles[^2].Close;

            Aindicator bollinger = (Aindicator)tab.Indicators[0];

            if (bollinger.ParametersDigit[0].Value != _bollingerLen.ValueInt
                || bollinger.ParametersDigit[1].Value != _bollingerDev.ValueDecimal)
            {
                bollinger.ParametersDigit[0].Value = _bollingerLen.ValueInt;
                bollinger.ParametersDigit[1].Value = _bollingerDev.ValueDecimal;
                bollinger.Save();
                bollinger.Reload();
            }

            if (bollinger.DataSeries[0].Values.Count == 0 ||
                bollinger.DataSeries[0].Last == 0)
            {
                return;
            }

            decimal lastUpBollingerLine = bollinger.DataSeries[0].Last;
            decimal prevUpBollingerLine = bollinger.DataSeries[0].Values[^2];

            if (lastCandleClose > lastUpBollingerLine && prevCandleClose < prevUpBollingerLine)
            {
                if (SmaFilterIsOn.ValueBool == true)
                {
                    decimal smaValue = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 1);
                    decimal smaPrev = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 2);
                    if (smaValue <= smaPrev)
                        return;
                }

                // ухудшает результаты
                //decimal lastBollingerLine = bollinger.DataSeries[2].Last;
                //decimal prevBollingerLine = bollinger.DataSeries[2].Values[^2];
                //if (lastBollingerLine <= prevBollingerLine)
                //    return;

                decimal vol = volume.GetVolume(tab);
                if (vol > 0)
                    tab.BuyAtMarket(vol);
            }
        }

        // Logic close position
        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab, Position position)
        {
            if (position.State != PositionStateType.Open
                          ||
                          (position.CloseOrders != null
                          && position.CloseOrders.Count > 0)
                          )
            {
                return;
            }

            Aindicator pc = (Aindicator)tab.Indicators[1];

            if (PriceChannelLength.ValueInt != pc.ParametersDigit[0].Value ||
                PriceChannelLength.ValueInt != pc.ParametersDigit[1].Value)
            {
                pc.ParametersDigit[0].Value = PriceChannelLength.ValueInt;
                pc.ParametersDigit[1].Value = PriceChannelLength.ValueInt;
                pc.Save();
                pc.Reload();
            }

            decimal lastClose = candles[^1].Close;
            decimal priceChannelDown = pc.DataSeries[1].Values[^2];
            decimal priceChannelTrail = priceChannelDown > position.EntryPrice ? (position.EntryPrice + priceChannelDown) / 2m : 0m;

            decimal stop = 0;

            if (position.Direction == Side.Buy)
            {
                if (position.StopOrderPrice == 0m)
                    stop = priceChannelDown - priceChannelDown * (0.5m / 100m); // -0.5 % 
                else
                    stop = Math.Max(priceChannelTrail, lastClose - lastClose * (_trailStop.ValueDecimal / 100));
            }


            tab.CloseAtTrailingStopMarket(position, stop);
            if (position.ProfitOrderPrice == 0m && _takeProfit.ValueDecimal != 0m)
                tab.CloseAtProfitMarket(position, position.EntryPrice * (1m + _takeProfit.ValueDecimal / 100m));
        }

        private decimal Sma(List<Candle> candles, int len, int index)
        {
            if (candles.Count == 0
                || index >= candles.Count
                || index <= 0)
            {
                return 0;
            }

            decimal summ = 0;

            int countPoints = 0;

            for (int i = index; i >= 0 && i > index - len; i--)
            {
                countPoints++;
                summ += candles[i].Close;
            }

            if (countPoints == 0)
            {
                return 0;
            }

            return summ / countPoints;
        }

    }
}