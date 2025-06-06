using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Market.Servers.Tester;
using OsEngine.Market.Servers;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;


namespace OsEngine.Robots
{
    [Bot("SDKBuyAndHoldScreener")]
    public class SDKBuyAndHoldScreener : BotPanel
    {
        BotTabScreener _tab;

        public StrategyParameterString Regime;
        public StrategyParameterInt MaxPositions;
        public StrategyParameterDecimal deltaRebalancePercent;
        public StrategyParameterInt DayDelay;

        public SDKVolume volume;

        public SDKBuyAndHoldScreener(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);

            _tab = TabsScreener[0];

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On" });
            MaxPositions = CreateParameter("Max positions", 5, 0, 20, 1);
            deltaRebalancePercent = CreateParameter("Rebalance percent", 10m, 0, 20, 1);
            DayDelay = CreateParameter("Day Delay", 5, 0, 20, 1);

            volume = new SDKVolume(this);
            volume.Volume.ValueDecimal = 100m / (MaxPositions.ValueInt + 0);
            ParametrsChangeByUser += Screener_ParametrsChangeByUser;

            if (startProgram == StartProgram.IsTester)
            {
                List<IServer> servers = ServerMaster.GetServers();

                if (servers != null
                    && servers.Count > 0
                    && servers[0].ServerType == ServerType.Tester)
                {
                    TesterServer server = (TesterServer)servers[0];
                    server.TestingStartEvent += Server_TestingStartEvent;
                    server.TestingEndEvent += Server_TestingEndEvent;
                }
            }

        }

        class TradeData
        {
            public string SecName = "";
            public string SecClass = "";
            public decimal initialVolume = 0m;
            public decimal lastVolume = 0m;
            public decimal initialMoney = 0m;
            public decimal lastMoney = 0m;
            public int rebalanceCount = 0;
        };

        private List<TradeData> tradeData = new List<TradeData>();

        private void Server_TestingEndEvent()
        {
            this.SendNewLogMessage("Result of testing", Logging.LogMessageType.Trade);
            for (int i = 0; i < _tab.Tabs.Count; i++)
            {
                BotTabSimple tab = _tab.Tabs[i];
                TradeData data = getSettings(tab);
                decimal lastMoney = data.lastVolume * tab.CandlesAll[tab.CandlesAll.Count - 1].Close * tab.Security.Lot;
                this.SendNewLogMessage("Active: " + data.SecName + ", rebalanceCount: " + data.rebalanceCount +
                ", Vol: " + data.initialVolume + " -> " + data.lastVolume +
                    ", Money: " + data.initialMoney + " -> " + lastMoney
                    , Logging.LogMessageType.Trade);
            }
        }

        private void Server_TestingStartEvent()
        {
            this.SendNewLogMessage("Server_TestingStartEvent", Logging.LogMessageType.Trade);
            tradeData.Clear();
            dateRebalance = new DateTime(1980, 1, 1);
        }

        private TradeData getSettings(BotTabSimple tab)
        {
            TradeData mySettings = null;

            for (int i = 0; i < tradeData.Count; i++)
            {
                if (tradeData[i].SecName == tab.Security.Name &&
                    tradeData[i].SecClass == tab.Security.NameClass)
                {
                    mySettings = tradeData[i];
                    break;
                }
            }

            if (mySettings == null)
            {
                mySettings = new TradeData();
                mySettings.SecName = tab.Security.Name;
                mySettings.SecClass = tab.Security.NameClass;
                tradeData.Add(mySettings);
            }
            return mySettings;
        }


        public override string GetNameStrategyType()
        {
            return "SDKBuyAndHoldScreener";
        }
        public override void ShowIndividualSettingsDialog()
        {

        }

        private void Screener_ParametrsChangeByUser()
        {
            volume.Volume.ValueDecimal = 100m / (MaxPositions.ValueInt + 0);
        }

        DateTime dateRebalance;

        // logic
        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }
            
            decimal lastPrice = candles[candles.Count - 1].Close;

            List<Position> openPositions = tab.PositionsOpenAll;
            if (openPositions == null)
                return;

            TimeSpan delayDate = TimeSpan.FromDays(DayDelay.ValueInt);

            if (openPositions.Count == 0)
            {
                if (Regime.ValueString == "On")
                    LogicOpenPosition(candles, tab);
            }
            else if (deltaRebalancePercent.ValueDecimal != 0 &&
                candles[candles.Count - 1].TimeStart - dateRebalance > delayDate)
            {
                dateRebalance = candles[candles.Count - 1].TimeStart;
                decimal allMoneyInTrade = 0;
                List<Position> allOpenPositions = _tab.PositionsOpenAll;
                decimal minVolume = 1000000000;
                decimal maxVolume = 0;
                for (int i = 0; i < allOpenPositions.Count; i++)
                {
                    Position position = allOpenPositions[i];
                    BotTabSimple tabPosition = _tab.GetTabWithThisPosition(position.Number);
                    decimal volumePos = position.OpenVolume * tabPosition.CandlesAll[tabPosition.CandlesAll.Count - 1].Close * tabPosition.Security.Lot;
                    allMoneyInTrade += volumePos;
                    minVolume = Math.Min(minVolume, volumePos);
                    maxVolume = Math.Max(maxVolume, volumePos);
                }

                for (int i = 0; i < allOpenPositions.Count; i++)
                {
                    Position position = allOpenPositions[i];
                    BotTabSimple tabPosition = _tab.GetTabWithThisPosition(position.Number);
                    decimal volumePos = position.OpenVolume * tabPosition.CandlesAll[tabPosition.CandlesAll.Count - 1].Close * tabPosition.Security.Lot;
                    decimal volumeTarget = allMoneyInTrade / allOpenPositions.Count;
                    decimal part = volumeTarget * deltaRebalancePercent.ValueDecimal / 100;
                    if (volumePos > volumeTarget + part)
                    {
                        //tab.CloseAtMarket(openPositions[0], openPositions[0].OpenVolume);
                        //tab.BuyAtMarket(volumeTarget / (lastPrice * tab.Security.Lot));
                        this.SendNewLogMessage("Rebalance by active: " + position.SecurityName, Logging.LogMessageType.Trade);
                        TradeData data = getSettings(tabPosition);
                        data.rebalanceCount++;
                        _tab.CloseAllPositionAtMarket();
                        break;
                    }
                }
            }
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            decimal lastPrice = candles[candles.Count - 1].Close;
            decimal vol = volume.GetVolume(tab);
            decimal moneyPos = vol * lastPrice * tab.Security.Lot;
            tab.BuyAtMarket(vol);
            TradeData data = getSettings(tab);
            if (data.initialVolume == 0m)
            {
                data.initialVolume = vol;
                data.initialMoney = moneyPos;
            }
            data.lastVolume = vol;
            data.lastMoney = moneyPos;
        }
    }
}