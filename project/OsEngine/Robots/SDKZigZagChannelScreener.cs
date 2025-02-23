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
    [Bot("SDKZigZagChannelScreener")]
    public class SDKZigZagChannelScreener : BotPanel
    {
        BotTabScreener _tabScreener;

        public StrategyParameterString Regime;
        public StrategyParameterInt MaxPositions;
        public StrategyParameterInt ZigZagChannelLen;

        public StrategyParameterDecimal Slippage;
        public StrategyParameterDecimal TrailStop;

        public SDKVolume volume;
        public SDKRsiFilter rsiFilter;

        public int zigzagIndx;

        public SDKZigZagChannelScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);

            _tabScreener = TabsScreener[0];

            _tabScreener.CandleFinishedEvent += _tab_CandleFinishedEvent;

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });

            MaxPositions = CreateParameter("Max positions", 5, 0, 20, 1);

            ZigZagChannelLen = CreateParameter("ZigZag channel length", 50, 0, 20, 1);

            TrailStop = CreateParameter("Trail stop %", 2.9m, 0, 20, 1m);

            Slippage = CreateParameter("Slippage %", 0, 0, 20, 1m);

            int indicatorIndex = 0;
            zigzagIndx = indicatorIndex;
            _tabScreener.CreateCandleIndicator(++indicatorIndex,
                "ZigZagChannel_indicator", new List<string>() { ZigZagChannelLen.ValueInt.ToString() }, "Prime");

            volume = new SDKVolume(this);
            rsiFilter = new SDKRsiFilter(this, ref indicatorIndex);

            if (StartProgram == StartProgram.IsTester && ServerMaster.GetServers() != null)
            {
                TesterServer server = (TesterServer)ServerMaster.GetServers()[0];

                server.TestingStartEvent += Server_TestingStartEvent;
            }
        }

        public override string GetNameStrategyType()
        {
            return "SDKZigZagChannelScreener";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void Server_TestingStartEvent()
        {
           rsiFilter.Restart();
        }



        public Aindicator getZigZagIndicator(BotTabSimple tab)
        {
            Aindicator zigzag = (Aindicator)tab.Indicators[zigzagIndx];
            if (zigzag.ParametersDigit[0].Value != ZigZagChannelLen.ValueInt)
            {
                zigzag.ParametersDigit[0].Value = ZigZagChannelLen.ValueInt;
                zigzag.Save();
                zigzag.Reload();
            }
            return zigzag;
        }

        // logic

        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < 5)
            {
                return;
            }

            rsiFilter.CandleFinishedEvent(candles, tab);

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
            {
                if (Regime.ValueString == "OnlyClosePosition")
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

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            if (_tabScreener.PositionsOpenAll.Count >= MaxPositions.ValueInt)
            {
                return;
            }

            if (rsiFilter.filterSecurityToTrade(tab.Security.Name) == false)
                return;

            Aindicator zigzag = getZigZagIndicator(tab);

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

            //if (stop > lastClose)
            //{
            //    tab.CloseAtMarket(position, position.OpenVolume);
            //    return;
            //}

            tab.CloseAtTrailingStop(position, stop, stopWithSlippage);

            //Aindicator zigzag = getZigZagIndicator(tab);

            //if (zigzag.DataSeries[4].Values.Count == 0 ||
            //    zigzag.DataSeries[4].Last == 0)
            //{
            //    return;
            //}

            //decimal zigZagUpLine = zigzag.DataSeries[4].Last;
            //decimal lastCandleClose = candles[candles.Count - 1].Close;

            //if (zigZagUpLine != 0 &&
            //    lastCandleClose > zigZagUpLine)
            //{
            //    position.StopOrderIsActiv = false;
            //}
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