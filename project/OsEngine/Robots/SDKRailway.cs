using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Robots.Helpers;
using System;
using System.Collections.Generic;

/* Description
Trading robot for osengine

The trend robot-screener on ZigZag Channel and Volatility group.

Buy:
1. The candle is buried above the ZigZag Channel's inclined channel level.
2. Filter by volatility groups. All screener papers are divided into 3 groups. One of them is traded.

Exit for long: When the ZigZag Channel bottom line is broken

*/

namespace OsEngine.Robots.SDKRobots
{
    [Bot("SDKRailway")]
    public class SDKRailway : BotPanel
    {
        private BotTabScreener _tabScreener;

        // Basic settings
        private StrategyParameterString _regime;
        private StrategyParameterInt _icebergCount;
        private StrategyParameterInt _maxPositions;
        private StrategyParameterInt _clusterToTrade;
        private StrategyParameterInt _clustersLookBack;

        // Indicator settings
        private StrategyParameterInt _zigZagChannelLen;
        private StrategyParameterInt _smaFilterLen;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        // Volatility clusters
        private VolatilityStageClusters _volatilityStageClusters = new VolatilityStageClusters();
        private DateTime _lastTimeSetClusters;

        public SDKVolume volume;
        public SDKPositionsSupport support;

        public SDKRailway(string name, StartProgram startProgram) : base(name, startProgram)
        {
            // non trade periods
            _tradePeriodsSettings = new NonTradePeriods(name);

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1Start = new TimeOfDay() { Hour = 0, Minute = 0 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1End = new TimeOfDay() { Hour = 10, Minute = 05 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1OnOff = true;

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod2Start = new TimeOfDay() { Hour = 13, Minute = 54 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod2End = new TimeOfDay() { Hour = 14, Minute = 6 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod2OnOff = false;

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3Start = new TimeOfDay() { Hour = 18, Minute = 1 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3End = new TimeOfDay() { Hour = 23, Minute = 58 };
            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod3OnOff = true;

            _tradePeriodsSettings.TradeInSunday = false;
            _tradePeriodsSettings.TradeInSaturday = false;

            _tradePeriodsSettings.Load();

            // Source creation
            TabCreate(BotTabType.Screener);
            _tabScreener = TabsScreener[0];

            // Basic settings
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On" });
            _icebergCount = CreateParameter("Iceberg orders count", 1, 1, 3, 1);
            _maxPositions = CreateParameter("Max positions", 10, 0, 20, 1);
            _clusterToTrade = CreateParameter("Volatility cluster to trade", 1, 1, 3, 1);
            _clustersLookBack = CreateParameter("Volatility cluster lookBack", 150, 10, 300, 1);
            _zigZagChannelLen = CreateParameter("ZigZag channel length", 56, 0, 20, 1);
            _smaFilterLen = CreateParameter("SMA filter length", 150, 0, 200, 10);
            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            // Subscribe to the candle finished event
            _tabScreener.CandleFinishedEvent += _screenerTab_CandleFinishedEvent;

            // Create indicator ZizZagChannel
            _tabScreener.CreateCandleIndicator(1, "ZigZagChannel_indicator", new List<string>() { _zigZagChannelLen.ValueInt.ToString() }, "Prime");

            Description = OsLocalization.Description.DescriptionLabel323;

            this.DeleteEvent += AlgoStart4ScreenerRailway_DeleteEvent;

            volume = new SDKVolume(this);
            support = new SDKPositionsSupport(this);

            // Subscribe to receive events/commands from Telegram
            ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;
        }

        private void AlgoStart4ScreenerRailway_DeleteEvent()
        {
            _tradePeriodsSettings.Delete();
        } 

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
        }

        private string _lastRegime = BotTradeRegime.Off.ToString();
        private void TelegramCommandHandler(string botName, Command cmd)
        {
            if (botName != null && !_tabScreener.TabName.Equals(botName))
                return;

            if (cmd == Command.StopAllBots || cmd == Command.StopBot)
            {
                _lastRegime = _regime;
                _regime.ValueString = BotTradeRegime.Off.ToString();

                SendNewLogMessage($"Changed Bot {_tabScreener.TabName} Regime to {_regime.ValueString} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if ((cmd == Command.StartAllBots || cmd == Command.StartBot) &&
                _regime.ValueString == BotTradeRegime.Off.ToString())
            {
                if (_lastRegime != BotTradeRegime.Off.ToString())
                    _regime.ValueString = _lastRegime;
                else
                    _regime.ValueString = BotTradeRegime.On.ToString();

                //changing bot mode to its previous state or On
                SendNewLogMessage($"Changed bot {_tabScreener.TabName} mode to state {_regime.ValueString} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.CancelAllActiveOrders)
            {
                //Some logic for cancel all active orders
            }
            else if (cmd == Command.GetStatus)
            {
                List<Journal.Journal> journals = _tabScreener.GetJournals();

                int count = 0;
                decimal profit = 0;
                decimal inputs = 0;

                for (int j = 0; j < journals.Count; j++)
                {
                    Journal.Journal curJournal = journals[j];

                    for (int i2 = 0; i2 < curJournal.OpenPositions.Count; i2++)
                    {
                        Position position = curJournal.OpenPositions[i2];
                        count++;
                        profit += position.ProfitPortfolioAbs;
                        inputs += position.OpenVolume * position.EntryPrice * position.Lots;
                    }
                }

                SendNewLogMessage($"\nBot {_tabScreener.TabName} is {_regime.ValueString}.\n" +
                                  $"Server Status - {(_tabScreener.Tabs.Count > 0 ? _tabScreener.Tabs[0].ServerStatus : "Empty")}.\n" +
                                  $"Positions count {count}.\n" +
                                  $"Total invested {inputs.ToString("F2")}.\n" +
                                  $"Profit for all {profit.ToString("F2")}.\n"
                                  , LogMessageType.User);
            }
        }

        // logic

        private void _screenerTab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (_regime.ValueString == "Off")
            {
                return;
            }

            if (candles.Count < 50)
            {
                return;
            }

            if (_tradePeriodsSettings.CanTradeThisTime(tab.TimeServerCurrent) == false)
            {
                return;
            }

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions.Count == 0
                && _clusterToTrade.ValueInt != 0)
            {
                if (_lastTimeSetClusters == DateTime.MinValue
                 || _lastTimeSetClusters != candles[^1].TimeStart)
                {
                    _volatilityStageClusters.Calculate(_tabScreener.Tabs, _clustersLookBack.ValueInt);
                    _lastTimeSetClusters = candles[^1].TimeStart;
                }

                if (_clusterToTrade.ValueInt == 1)
                {
                    if (_volatilityStageClusters.ClusterOne.Find(source => source.Connector.SecurityName == tab.Connector.SecurityName) == null)
                    {
                        return;
                    }
                }
                else if (_clusterToTrade.ValueInt == 2)
                {
                    if (_volatilityStageClusters.ClusterTwo.Find(source => source.Connector.SecurityName == tab.Connector.SecurityName) == null)
                    {
                        return;
                    }
                }
                else if (_clusterToTrade.ValueInt == 3)
                {
                    if (_volatilityStageClusters.ClusterThree.Find(source => source.Connector.SecurityName == tab.Connector.SecurityName) == null)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

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

        // Opening logic
        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            if (_tabScreener.PositionsOpenAll.Count >= _maxPositions.ValueInt)
            {
                return;
            }

            Aindicator zigZag = (Aindicator)tab.Indicators[0];

            if (zigZag.ParametersDigit[0].Value != _zigZagChannelLen.ValueInt)
            {
                zigZag.ParametersDigit[0].Value = _zigZagChannelLen.ValueInt;
                zigZag.Save();
                zigZag.Reload();
            }

            if (zigZag.DataSeries[4].Values.Count == 0 ||
                zigZag.DataSeries[4].Last == 0 ||
                zigZag.DataSeries[5].Values.Count == 0 ||
                zigZag.DataSeries[5].Last == 0)
            {
                return;
            }

            decimal zigZagUpLine = zigZag.DataSeries[4].Last;
            decimal zigZagDownLine = zigZag.DataSeries[5].Last;
            decimal lastCandleClose = candles[candles.Count - 1].Close;

            if (lastCandleClose > zigZagUpLine
                && lastCandleClose > zigZagDownLine)
            {
                decimal smaValue = Sma(candles, _smaFilterLen, candles.Count - 1);
                decimal smaPrev = Sma(candles, _smaFilterLen, candles.Count - 2);

                if (smaValue > smaPrev)
                {
                    if (!support.CanOpenNewPosition(tab, candles, lastCandleClose, Side.Buy))
                        return;
                    decimal vol = volume.GetVolume(tab);
                    if (vol > 0)
                        tab.BuyAtIcebergMarket(vol, _icebergCount.ValueInt, 1000);
                }
            }
        }

        // Logic close position
        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab, Position position)
        {
            if (position.State != PositionStateType.Open
                          //||
                          //(position.CloseOrders != null
                          //&& position.CloseOrders.Count > 0)
                          )
            {
                return;
            }
            if (support.IsNeedClosePosition(tab, position))
            {
                SendNewLogMessage($"Close By Support {tab.Security.Name}", LogMessageType.Trade);
                tab.CloseAtMarket(position, position.OpenVolume, "support");
                return;
            }

            Aindicator zigZag = (Aindicator)tab.Indicators[0];

            if (zigZag.ParametersDigit[0].Value != _zigZagChannelLen.ValueInt)
            {
                zigZag.ParametersDigit[0].Value = _zigZagChannelLen.ValueInt;
                zigZag.Save();
                zigZag.Reload();
            }

            if (zigZag.DataSeries[5].Values.Count == 0 ||
                zigZag.DataSeries[5].Last == 0)
            {
                return;
            }

            decimal lastClose = candles[candles.Count - 1].Close;
            decimal zigZagDownLine = zigZag.DataSeries[5].Last;

            if (lastClose < zigZagDownLine)
            {
                tab.CloseAtIcebergMarket(position, position.OpenVolume, _icebergCount.ValueInt, 1000);
                return;
            }
        }

        // Method for calculating sma
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

    public class SecurityRatingData
    {
        public string SecurityName;

        public decimal Rsi;

        public decimal Volume;
    }
}