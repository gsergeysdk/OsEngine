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
Trading robot for osEngine

The trend robot-screener on Adaptive Price Channel and Volatility group.

Buy:
1. The candle closed above the upper line of the Price Channel
2. Filter by volatility groups. All screener papers are divided into 3 groups. One of them is traded.

Exit for long: When the Price Channel bottom line is broken

*/

namespace OsEngine.Robots.SDKRobots
{
    [Bot("SDKPriceChannel")]
    public class SDKPriceChannel : BotPanel
    {
        private BotTabScreener _screenerTab;

        // Basic settings
        private StrategyParameterString _regime;
        private StrategyParameterInt _icebergCount;
        private StrategyParameterInt _maxPositions;
        private StrategyParameterInt _clusterToTrade;
        private StrategyParameterInt _clustersLookBack;

        // Indicator settings
        private StrategyParameterInt _pcAdxLength;
        private StrategyParameterInt _pcRatio;
        private StrategyParameterBool _smaFilterIsOn;
        private StrategyParameterInt _smaFilterLen;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        // Volatility clusters
        private VolatilityStageClusters _volatilityStageClusters = new VolatilityStageClusters();
        private DateTime _lastTimeSetClusters;

        public SDKVolume volume;

        public SDKPriceChannel(string name, StartProgram startProgram) : base(name, startProgram)
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
            _screenerTab = TabsScreener[0];

            // Subscribe to the candle finished event
            _screenerTab.CandleFinishedEvent += _screenerTab_CandleFinishedEvent;

            // Basic settings
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On" });
            _icebergCount = CreateParameter("Iceberg orders count", 1, 1, 3, 1);
            _clusterToTrade = CreateParameter("Volatility cluster to trade", 2, 1, 3, 1);
            _clustersLookBack = CreateParameter("Volatility cluster lookBack", 100, 10, 300, 1);
            _maxPositions = CreateParameter("Max poses", 10, 1, 20, 1);
            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            // Indicator settings
            _pcAdxLength = CreateParameter("Pc adx length", 50, 5, 300, 1);
            _pcRatio = CreateParameter("Pc ratio", 840, 5, 2000, 1);
            _smaFilterIsOn = CreateParameter("Sma filter is on", true);
            _smaFilterLen = CreateParameter("Sma filter Len", 70, 100, 300, 10);

            // Create indicator PriceChannelAdaptive
            _screenerTab.CreateCandleIndicator(2,
                "PriceChannelAdaptive",
                new List<string>() { _pcAdxLength.ValueInt.ToString(), _pcRatio.ValueInt.ToString() },
                "Prime");

            // Subscribe to the indicator update event
            ParametrsChangeByUser += SmaScreener_ParametrsChangeByUser;

            Description = OsLocalization.Description.DescriptionLabel326;
            DeleteEvent += AlgoStart3ScreenerPriceChannel_DeleteEvent;

            volume = new SDKVolume(this);

            // Subscribe to receive events/commands from Telegram
            ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;
        }

        private void SmaScreener_ParametrsChangeByUser()
        {
            _screenerTab._indicators[0].Parameters = new List<string>() { _pcAdxLength.ValueInt.ToString(), _pcRatio.ValueInt.ToString() };
            _screenerTab.UpdateIndicatorsParameters();
        }

        private void AlgoStart3ScreenerPriceChannel_DeleteEvent()
        {
            _tradePeriodsSettings.Delete();
        }

        private string _lastRegime = BotTradeRegime.Off.ToString();
        private void TelegramCommandHandler(string botName, Command cmd)
        {
            if (botName != null && !_screenerTab.TabName.Equals(botName))
                return;

            if (cmd == Command.StopAllBots || cmd == Command.StopBot)
            {
                _lastRegime = _regime;
                _regime.ValueString = BotTradeRegime.Off.ToString();

                SendNewLogMessage($"Changed Bot {_screenerTab.TabName} Regime to {_regime.ValueString} " +
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
                SendNewLogMessage($"Changed bot {_screenerTab.TabName} mode to state {_regime.ValueString} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.CancelAllActiveOrders)
            {
                //Some logic for cancel all active orders
            }
            else if (cmd == Command.GetStatus)
            {
                List<Journal.Journal> journals = _screenerTab.GetJournals();

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

                SendNewLogMessage($"\nBot {_screenerTab.TabName} is {_regime.ValueString}.\n" +
                                  $"Server Status - {(_screenerTab.Tabs.Count > 0 ? _screenerTab.Tabs[0].ServerStatus : "Empty")}.\n" +
                                  $"Positions count {count}.\n" +
                                  $"Total invested {inputs.ToString("F2")}.\n" +
                                  $"Profit for all {profit.ToString("F2")}.\n"
                                  , LogMessageType.User);
            }
        }

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
        }

        // Logic
        private void _screenerTab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            // 1 If there is a position, then we close the trailing stop

            // 2 There is no pose. Open long if the last N candles we were above the moving average

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
                    _volatilityStageClusters.Calculate(_screenerTab.Tabs, _clustersLookBack.ValueInt);
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

            List<Position> positions = tab.PositionsOpenAll;

            if (positions.Count == 0) // Open position logic
            {
                int allPosesInAllTabs = this.PositionsCount;

                if (allPosesInAllTabs >= _maxPositions.ValueInt)
                {
                    return;
                }

                Aindicator priceChannel = (Aindicator)tab.Indicators[0];

                decimal pcUp = priceChannel.DataSeries[0].Values[priceChannel.DataSeries[0].Values.Count - 2];

                if (pcUp == 0)
                {
                    return;
                }

                decimal candleClose = candles[candles.Count - 1].Close;

                if (candleClose > pcUp)
                {

                    if (_smaFilterIsOn.ValueBool == true)
                    {
                        decimal smaValue = Sma(candles, _smaFilterLen.ValueInt, candles.Count - 1);
                        decimal smaPrev = Sma(candles, _smaFilterLen.ValueInt, candles.Count - 2);

                        if (smaValue < smaPrev)
                        {
                            return;
                        }
                    }
                    decimal vol = volume.GetVolume(tab);
                    if (vol > 0)
                        tab.BuyAtIcebergMarket(vol, _icebergCount.ValueInt, 1000);
                }
            }
            else // Close logic
            {
                Position pos = positions[0];

                if (pos.State != PositionStateType.Open)
                {
                    return;
                }

                Aindicator priceChannel = (Aindicator)tab.Indicators[0];

                decimal pcDown = priceChannel.DataSeries[1].Values[^2];

                if (pcDown == 0)
                {
                    return;
                }

                decimal lastClose = candles[^1].Close;

                if(lastClose <= pcDown)
                {
                    tab.CloseAtIcebergMarket(pos,pos.OpenVolume,_icebergCount.ValueInt,1000);
                }
            }
        }

        // Method for calculating Sma
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