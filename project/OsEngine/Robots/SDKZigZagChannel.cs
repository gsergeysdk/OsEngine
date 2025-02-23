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
    [Bot("SDKZigZagChannel")]
    public class SDKZigZagChannel : BotPanel
    {
        BotTabSimple _tab;

        public StrategyParameterString Regime;
        public StrategyParameterInt ZigZagChannelLen;

        public StrategyParameterDecimal Slippage;
        public StrategyParameterDecimal TrailStop;

        public SDKVolume volume;
        private Aindicator zigzag;

        public SDKZigZagChannel(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);

            _tab = TabsSimple[0];

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });

            ZigZagChannelLen = CreateParameter("ZigZag channel length", 50, 0, 20, 1);

            TrailStop = CreateParameter("Trail stop %", 2.9m, 0, 20, 1m);

            Slippage = CreateParameter("Slippage %", 0, 0, 20, 1m);

            zigzag = IndicatorsFactory.CreateIndicatorByName("ZigZagChannel_indicator", name + "ZigZagChannel", false);
            zigzag = (Aindicator)_tab.CreateCandleIndicator(zigzag, "Prime");

            volume = new SDKVolume(this);

            ParametrsChangeByUser += parametrsChangeByUser;
        }

        public override string GetNameStrategyType()
        {
            return "SDKZigZagChannel";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void parametrsChangeByUser()
        {
            ((IndicatorParameterInt)zigzag.Parameters[0]).ValueInt = ZigZagChannelLen.ValueInt;
            zigzag.Reload();
            zigzag.Save();
        }

        // logic

        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < 5)
            {
                return;
            }

            List<Position> openPositions = _tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
            {
                if (Regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }
                LogicOpenPosition(candles, _tab);
            }
            else
            {
                LogicClosePosition(candles, _tab, openPositions[0]);
            }
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            if (zigzag.DataSeries[4].Values.Count == 0 ||
                zigzag.DataSeries[4].Last == 0)
            {
                return;
            }

            decimal zigZagUpLine = zigzag.DataSeries[4].Last;
            decimal lastCandleClose = candles[candles.Count - 1].Close;

            if (lastCandleClose > zigZagUpLine)
            {
                decimal smaValue = Sma(candles, 150, candles.Count - 1);
                decimal smaPrev = Sma(candles, 150, candles.Count - 2);

                if (smaValue > smaPrev)
                {
                    tab.BuyAtMarket(volume.GetVolume(tab));
                }
            }
        }

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

            decimal lastClose = candles[candles.Count - 1].Close;

            decimal stop = 0;
            decimal stopWithSlippage = 0;

            stop = lastClose - lastClose * (TrailStop.ValueDecimal / 100);
            stopWithSlippage = stop - stop * (Slippage.ValueDecimal / 100);

            if (stop > lastClose)
            {
                tab.CloseAtMarket(position, position.OpenVolume);
                return;
            }

            tab.CloseAtTrailingStop(position, stop, stopWithSlippage);

            if (zigzag.DataSeries[4].Values.Count == 0 ||
                zigzag.DataSeries[4].Last == 0)
            {
                return;
            }

            decimal zigZagUpLine = zigzag.DataSeries[4].Last;
            decimal lastCandleClose = candles[candles.Count - 1].Close;

            if (zigZagUpLine != 0 &&
                lastCandleClose > zigZagUpLine)
            {
                position.StopOrderIsActiv = false;
            }
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