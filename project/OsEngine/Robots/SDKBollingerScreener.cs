using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Logging;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Drawing;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

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

        private StrategyParameterDecimal minBollingerDev;
        private StrategyParameterDecimal maxBollingerDev;

        // Exit setting
        private StrategyParameterDecimal _trailStop;
        public StrategyParameterInt priceChannelLength;
        public StrategyParameterBool stopByBollinger;
        private StrategyParameterDecimal _takeProfit;

        public StrategyParameterBool SmaFilterIsOn;
        public StrategyParameterInt SmaFilterLen;

        public StrategyParameterBool trandFilterIsOn;
        private StrategyParameterInt trandPeriodFast;
        private StrategyParameterInt trandPeriodSlow;
        private StrategyParameterInt trandCounter;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

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
            _tabScreener.CreateCandleIndicator(3, "TrandPhaseDema", new List<string>() { "200", "600", "10" }, "Trand");

            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyClosePosition" });
            _maxPositions = CreateParameter("Max positions", 5, 0, 20, 1);

            // Indicator settings
            _bollingerLen = CreateParameter("Bollinger length", 50, 0, 20, 1);
            _bollingerDev = CreateParameter("Bollinger deviation", 2m, 0, 20, 1m);
            minBollingerDev = CreateParameter("Min Bollinger volitility %", 1m, 0, 1, 0.1m);
            maxBollingerDev = CreateParameter("Max Bollinger volitility %", 100m, 0, 100, 5m);

            // Exit setting
            _trailStop = CreateParameter("Trail stop %", 2.9m, 0, 20, 1m);
            priceChannelLength = CreateParameter("Price channel length", 10, 10, 80, 5);
            stopByBollinger = CreateParameter("Stop by Bollinger", true);
            _takeProfit = CreateParameter("Take profit %", 7m, 0, 20, 1m);

            SmaFilterIsOn = CreateParameter("Sma filter is on", true);
            SmaFilterLen = CreateParameter("Sma filter Len", 100, 100, 300, 10);

            trandFilterIsOn = CreateParameter("Trand filter is on", false);
            trandPeriodFast = CreateParameter("Trand Period Fast", 100, 50, 300, 50);
            trandPeriodSlow = CreateParameter("Trand Period Slow", 200, 100, 500, 50);
            trandCounter = CreateParameter("Trand Counter", 10, 10, 50, 10);

            volume = new SDKVolume(this);

            _tradePeriodsSettings = new NonTradePeriods(name);
            _tradePeriodsSettings.Load();
            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            // Subscribe to receive events/commands from Telegram
            ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;
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
            else if (cmd == Command.StartAllBots || cmd == Command.StartBot)
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

        private void _screenerTab_CandleUpdateEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (_regime.ValueString == "Off")
            {
                return;
            }

            if (_tradePeriodsSettings.CanTradeThisTime(tab.TimeServerCurrent) == false)
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

            if (_tradePeriodsSettings.CanTradeThisTime(tab.TimeServerCurrent) == false)
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

            decimal lastCandleClose = candles[^1].Close;
            decimal lastCandleOpen = candles[^1].Open;

            Aindicator bollinger = (Aindicator)tab.Indicators[0];

            if (bollinger.ParametersDigit[0].Value != _bollingerLen.ValueInt
                || bollinger.ParametersDigit[1].Value != _bollingerDev.ValueDecimal)
            {
                bollinger.ParametersDigit[0].Value = _bollingerLen.ValueInt;
                bollinger.ParametersDigit[1].Value = _bollingerDev.ValueDecimal;
                bollinger.Save();
                bollinger.Reload();
            }

            Aindicator trand = (Aindicator)tab.Indicators[2];

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

            if (bollinger.DataSeries[0].Values.Count == 0 ||
                bollinger.DataSeries[0].Last == 0)
            {
                return;
            }

            decimal trandUp = trand.DataSeries[0].Last;
            decimal trandDown = trand.DataSeries[1].Last;

            if (trandFilterIsOn && trandDown > 0)
                return;
            if (trandFilterIsOn && trandUp <= 0)
                return;

            decimal lastUpBollingerLine = bollinger.DataSeries[0].Last;
            //decimal prevUpBollingerLine = bollinger.DataSeries[0].Values[^2];
            decimal lastDownBollingerLine = bollinger.DataSeries[1].Last;
            //decimal prevDownBollingerLine = bollinger.DataSeries[1].Values[^2];

            if (lastUpBollingerLine < lastDownBollingerLine * (1m + minBollingerDev.ValueDecimal / 100m))
                return;
            if (lastUpBollingerLine > lastDownBollingerLine * (1m + maxBollingerDev.ValueDecimal / 100m))
                return;

            if (lastCandleClose > lastUpBollingerLine && lastCandleOpen < lastUpBollingerLine)
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
            if (position.State != PositionStateType.Open)
            {
                return;
            }

            Aindicator pc = (Aindicator)tab.Indicators[1];

            if (priceChannelLength.ValueInt != pc.ParametersDigit[0].Value ||
                priceChannelLength.ValueInt != pc.ParametersDigit[1].Value)
            {
                pc.ParametersDigit[0].Value = priceChannelLength.ValueInt;
                pc.ParametersDigit[1].Value = priceChannelLength.ValueInt;
                pc.Save();
                pc.Reload();
            }

            Aindicator bollinger = (Aindicator)tab.Indicators[0];
            decimal lastDownBollingerLine = bollinger.DataSeries[1].Last;

            decimal lastClose = candles[^1].Close;
            decimal priceChannelDown = 0;
            //decimal priceChannelTrail = 0;
            if (priceChannelLength.ValueInt > 1)
            {
                priceChannelDown = pc.DataSeries[1].Values[^2];
                priceChannelDown -= priceChannelDown * (0.5m / 100m); // -0.5 % ;
                //priceChannelDown = priceChannelDown > position.EntryPrice ? (position.EntryPrice + priceChannelDown) / 2m : priceChannelDown;
            }

            decimal stop = 0;

            if (stopByBollinger.ValueBool)
                stop = lastDownBollingerLine * 0.995m; // -0.5 % ;

            if (_trailStop.ValueDecimal != 0m)
                stop = Math.Max(priceChannelDown, Math.Max(stop, lastClose - lastClose * (_trailStop.ValueDecimal / 100)));
            else
                stop = Math.Max(priceChannelDown, stop);

            //if (position.Direction == Side.Buy)
            //{
            //    if (position.StopOrderPrice == 0m)
            //        stop = priceChannelDown;// - priceChannelDown * (0.5m / 100m); // -0.5 % 
            //    else
            //        stop = Math.Max(priceChannelTrail, lastClose - lastClose * (_trailStop.ValueDecimal / 100));
            //}


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