using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.Market.Servers.Tester;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;


namespace OsEngine.Robots.SoldiersScreener
{
    [Bot("SDKThreeSoldierScreener")]
    public class SDKThreeSoldierScreener : BotPanel
    {
        private BotTabScreener _tab;

        // settings
        public StrategyParameterString Regime;
        public StrategyParameterInt MaxPositions;
        public StrategyParameterDecimal ProcHeightTake;
        public StrategyParameterDecimal ProcHeightStop;
        public StrategyParameterDecimal TrailingStopRepcent;
        public StrategyParameterInt ExitAtBarCount;
        public StrategyParameterDecimal MaxStopLossPercent;
        public StrategyParameterDecimal Slippage;

        public StrategyParameterInt DaysVolatilityAdaptive;
        public StrategyParameterDecimal HeightSoldiersVolaPecrent;
        public StrategyParameterDecimal MinHeightOneSoldiersVolaPecrent;
        public StrategyParameterDecimal MaxHeightPatternPercent;

        public StrategyParameterBool SmaFilterIsOn;
        public StrategyParameterInt SmaFilterLen;

        private StrategyParameterTimeOfDay TimeStart;
        private StrategyParameterTimeOfDay TimeEnd;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        public SDKVolume volume;

        // volatility adaptation

        private List<SecuritiesTradeSettings> _tradeSettings = new List<SecuritiesTradeSettings>();

        public SDKThreeSoldierScreener(string name, StartProgram startProgram)
            : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);
            _tab = TabsScreener[0];

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
            _tab.CandleUpdateEvent += _tab_CandleUpdateEvent;
           
            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort" });
            MaxPositions = CreateParameter("Max positions", 5, 0, 20, 1);
            Slippage = CreateParameter("Slippage %", 0, 0, 20, 1m);
            TimeStart = CreateParameterTimeOfDay("Start Trade Time", 7, 35, 0, 0);
            TimeEnd = CreateParameterTimeOfDay("End Trade Time", 22, 25, 0, 0);
            ProcHeightTake = CreateParameter("Profit % from height of pattern", 50m, 0, 20, 1m);
            ProcHeightStop = CreateParameter("Stop % from height of pattern", 20m, 0, 20, 1m);
            TrailingStopRepcent = CreateParameter("Trailing stop %", 20m, 0, 20, 1m);
            ExitAtBarCount = CreateParameter("Exit bars count", 5, 0, 50, 1);
            DaysVolatilityAdaptive = CreateParameter("Days volatility adaptive", 1, 0, 20, 1);
            HeightSoldiersVolaPecrent = CreateParameter("Height soldiers volatility percent", 5, 0, 20, 1m);
            MinHeightOneSoldiersVolaPecrent = CreateParameter("Min height one soldier volatility percent", 1, 0, 20, 1m);
            MaxStopLossPercent = CreateParameter("Max stop loss percent", 5, 5, 20, 1m);
            MaxHeightPatternPercent = CreateParameter("Max percent of height pattern from price", 5, 5, 20, 1m);
            SmaFilterIsOn = CreateParameter("Sma filter is on", true);
            SmaFilterLen = CreateParameter("Sma filter Len", 100, 100, 300, 10);
            volume = new SDKVolume(this);

            _tradePeriodsSettings = new NonTradePeriods(name);
            _tradePeriodsSettings.Load();
            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;


            Description = "Trading robot Three Soldiers adaptive by volatility. " +
                "When forming a pattern of three growing / falling candles, " +
                "the entrance to the countertrend with a fixation on a profit or a stop";

            if (startProgram == StartProgram.IsOsTrader)
                LoadTradeSettings();
            else if (startProgram == StartProgram.IsTester)
            {
                List<IServer> servers = ServerMaster.GetServers();

                if (servers != null
                    && servers.Count > 0
                    && servers[0].ServerType == ServerType.Tester)
                {
                    TesterServer server = (TesterServer)servers[0];
                    server.TestingStartEvent += Server_TestingStartEvent;
                }
            }

            this.DeleteEvent += ThreeSoldierAdaptiveScreener_DeleteEvent;
            ParametrsChangeByUser += Screener_ParametrsChangeByUser;

            // Subscribe to receive events/commands from Telegram
            ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;
        }

        private void Server_TestingStartEvent()
        {
            _tradeSettings.Clear();
        }

        private void Screener_ParametrsChangeByUser()
        {
            _tab.UpdateIndicatorsParameters();
        }

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
        }

        public override string GetNameStrategyType()
        {
            return "SDKThreeSoldierScreener";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void SaveTradeSettings()
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(@"Engine\" + NameStrategyUniq + @"SettingsBot.txt", false)
                    )
                {
                    for(int i = 0;i < _tradeSettings.Count;i++)
                    {
                        writer.WriteLine(_tradeSettings[i].GetSaveString());
                    }

                    writer.Close();
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void LoadTradeSettings()
        {
            if (!File.Exists(@"Engine\" + NameStrategyUniq + @"SettingsBot.txt"))
            {
                return;
            }
            try
            {
                using (StreamReader reader = new StreamReader(@"Engine\" + NameStrategyUniq + @"SettingsBot.txt"))
                {
                    while(reader.EndOfStream == false)
                    {
                        string line = reader.ReadLine();

                        if(string.IsNullOrEmpty(line))
                        {
                            continue;
                        }

                        SecuritiesTradeSettings newSettings = new SecuritiesTradeSettings();
                        newSettings.LoadFromString(line);
                        _tradeSettings.Add(newSettings);
                    }

                    reader.Close();
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void ThreeSoldierAdaptiveScreener_DeleteEvent()
        {
            try
            {
                if (File.Exists(@"Engine\" + NameStrategyUniq + @"SettingsBot.txt"))
                {
                    File.Delete(@"Engine\" + NameStrategyUniq + @"SettingsBot.txt");
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private string _lastRegime = BotTradeRegime.Off.ToString();
        private void TelegramCommandHandler(string botName, Command cmd)
        {
            if (botName != null && !_tab.TabName.Equals(botName))
                return;

            if (cmd == Command.StopAllBots || cmd == Command.StopBot)
            {
                _lastRegime = Regime;
                Regime.ValueString = BotTradeRegime.Off.ToString();

                SendNewLogMessage($"Changed Bot {_tab.TabName} Regime to {Regime.ValueString} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.StartAllBots || cmd == Command.StartBot)
            {
                if (_lastRegime != BotTradeRegime.Off.ToString())
                    Regime.ValueString = _lastRegime;
                else
                    Regime.ValueString = BotTradeRegime.On.ToString();

                //changing bot mode to its previous state or On
                SendNewLogMessage($"Changed bot {_tab.TabName} mode to state {Regime.ValueString} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.CancelAllActiveOrders)
            {
                //Some logic for cancel all active orders
            }
            else if (cmd == Command.GetStatus)
            {
                List<Journal.Journal> journals = _tab.GetJournals();

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

                SendNewLogMessage($"\nBot {_tab.TabName} is {Regime.ValueString}.\n" +
                                  $"Server Status - {(_tab.Tabs.Count > 0 ? _tab.Tabs[0].ServerStatus : "Empty")}.\n" +
                                  $"Positions count {count}.\n" +
                                  $"Total invested {inputs.ToString("F2")}.\n" +
                                  $"Profit for all {profit.ToString("F2")}.\n"
                                  , LogMessageType.User);
            }
        }

        private void AdaptSoldiersHeight(List<Candle> candles, SecuritiesTradeSettings settings)
        {
            if (DaysVolatilityAdaptive.ValueInt <= 0
                || candles.Count < 2)
                return;

            // 1 рассчитываем движение от хая до лоя внутри N дней

            decimal minValueInDay = decimal.MaxValue;
            decimal maxValueInDay = decimal.MinValue;
            List<decimal> volaInDaysPercent = new List<decimal>();
            DateTime date = candles[candles.Count - 2].TimeStart.Date;
            int days = 0;

            for (int i = candles.Count - 2; i >= 0; i--)
            {
                Candle curCandle = candles[i];
                if (curCandle.TimeStart.Date < date)
                {
                    date = curCandle.TimeStart.Date;
                    days++;
                    decimal volaAbsToday = maxValueInDay - minValueInDay;
                    decimal volaPercentToday = volaAbsToday / (minValueInDay / 100);
                    volaInDaysPercent.Add(volaPercentToday);
                    minValueInDay = decimal.MaxValue;
                    maxValueInDay = decimal.MinValue;
                }

                if (days > DaysVolatilityAdaptive.ValueInt)
                    break;

                if (curCandle.High > maxValueInDay)
                    maxValueInDay = curCandle.High;
                if (curCandle.Low < minValueInDay)
                    minValueInDay = curCandle.Low;

                if (i == 0)
                {
                    days++;

                    decimal volaAbsToday = maxValueInDay - minValueInDay;
                    decimal volaPercentToday = volaAbsToday / (minValueInDay / 100);
                    volaInDaysPercent.Add(volaPercentToday);
                }
            }

            if (volaInDaysPercent.Count == 0)
                return;

            // 2 усредняем это движение. Нужна усреднённая волатильность. процент

            decimal volaPercentSma = 0;

            for (int i = 0; i < volaInDaysPercent.Count; i++)
            {
                volaPercentSma += volaInDaysPercent[i];
            }

            volaPercentSma = volaPercentSma / volaInDaysPercent.Count;

            // 3 считаем размер свечей с учётом этой волатильности

            decimal allSoldiersHeight = volaPercentSma * (HeightSoldiersVolaPecrent.ValueDecimal / 100);
            decimal oneSoldiersHeight = volaPercentSma * (MinHeightOneSoldiersVolaPecrent.ValueDecimal / 100);

            settings.HeightSoldiers = allSoldiersHeight;
            settings.MinHeightOneSoldier = oneSoldiersHeight;
            settings.LastUpdateTime = candles[candles.Count - 1].TimeStart;
        }

        // logic

        private SecuritiesTradeSettings getSettings(BotTabSimple tab)
        {
            SecuritiesTradeSettings mySettings = null;

            for (int i = 0; i < _tradeSettings.Count; i++)
            {
                if (_tradeSettings[i].SecName == tab.Security.Name &&
                    _tradeSettings[i].SecClass == tab.Security.NameClass)
                {
                    mySettings = _tradeSettings[i];
                    break;
                }
            }

            if (mySettings == null)
            {
                mySettings = new SecuritiesTradeSettings();
                mySettings.SecName = tab.Security.Name;
                mySettings.SecClass = tab.Security.NameClass;
                _tradeSettings.Add(mySettings);
            }
            return mySettings;
        }

        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            SecuritiesTradeSettings mySettings = getSettings(tab);

            if (_tradePeriodsSettings.CanTradeThisTime(tab.TimeServerCurrent) == false)
            {
                return;
            }

            if (mySettings == null)
                return;

            if(mySettings.LastUpdateTime.Date != candles[candles.Count-1].TimeStart.Date)
            {
                AdaptSoldiersHeight(candles, mySettings);
                if (tab.StartProgram == StartProgram.IsOsTrader)
                    SaveTradeSettings();
            }


            Logic(candles, tab, mySettings);
        }

        private void _tab_CandleUpdateEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (_tradePeriodsSettings.CanTradeThisTime(tab.TimeServerCurrent) == false)
            {
                return;
            }
            List<Position> openPositions = tab.PositionsOpenAll;
            if (openPositions != null && openPositions.Count != 0 && tab.StartProgram == StartProgram.IsOsTrader)
            {
                SecuritiesTradeSettings mySettings = getSettings(tab);
                if (mySettings == null)
                    return;
                LogicClosePosition(candles, tab, mySettings);
            }
        }

        private void Logic(List<Candle> candles,BotTabSimple tab, SecuritiesTradeSettings settings)
        {
            if (Regime.ValueString == "Off")
                return;

            if (candles.Count < 5)
                return;

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
                LogicOpenPosition(candles, tab, settings);
            if (openPositions != null && openPositions.Count != 0)
                LogicClosePosition(candles, tab, settings);
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab, SecuritiesTradeSettings settings)
        {
            if (_tab.PositionsOpenAll.Count >= MaxPositions.ValueInt || settings.HeightSoldiers <= 0m)
                return;

            if (candles[candles.Count - 3].TimeStart.Day != candles[candles.Count - 1].TimeStart.Day)
                return;

            if (candles[^1].TimeStart.DayOfWeek < DayOfWeek.Sunday ||
                candles[^1].TimeStart.DayOfWeek > DayOfWeek.Friday)
                return;

            if (TimeStart.Value > tab.TimeServerCurrent ||
                TimeEnd.Value < tab.TimeServerCurrent)
                return;

            decimal _lastPrice = candles[candles.Count - 1].Close;
            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions != null && openPositions.Count != 0 &&
                openPositions[openPositions.Count - 1].TimeOpen > candles[candles.Count - 3].TimeStart)
                return;

            if (Math.Abs(candles[candles.Count - 3].Open - candles[candles.Count - 1].Close)
                / (candles[candles.Count - 1].Close / 100) < settings.HeightSoldiers)
                return;
            if (Math.Abs(candles[candles.Count - 3].Open - candles[candles.Count - 1].Close)
                / (candles[candles.Count - 1].Close / 100) > MaxHeightPatternPercent.ValueDecimal)
                return;
            if (Math.Abs(candles[candles.Count - 3].Open - candles[candles.Count - 3].Close)
                / (candles[candles.Count - 3].Close / 100) < settings.MinHeightOneSoldier)
                return;
            if (Math.Abs(candles[candles.Count - 2].Open - candles[candles.Count - 2].Close)
                / (candles[candles.Count - 2].Close / 100) < settings.MinHeightOneSoldier)
                return;
            if (Math.Abs(candles[candles.Count - 1].Open - candles[candles.Count - 1].Close)
                / (candles[candles.Count - 1].Close / 100) < settings.MinHeightOneSoldier)
                return;

            //  long
            if (Regime.ValueString != "OnlyShort")
            {
                if (candles[candles.Count - 3].Open < candles[candles.Count - 3].Close
                    && candles[candles.Count - 2].Open < candles[candles.Count - 2].Close
                    && candles[candles.Count - 1].Open < candles[candles.Count - 1].Close
                    && candles[candles.Count - 3].Open < candles[candles.Count - 2].Open
                    && candles[candles.Count - 2].Open < candles[candles.Count - 1].Open)
                {
                    if (SmaFilterIsOn.ValueBool == true)
                    {
                        decimal smaValue = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 4); // before pattern
                        decimal smaPrev = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 5);  // before pattern - 1 bar
                        if (smaValue <= smaPrev || smaValue > candles[candles.Count - 4].Close)
                            return;
                    }
                    decimal stopLossPrice = GetStopLossPrice(Side.Buy, candles, tab);
                    if (stopLossPrice < _lastPrice - _lastPrice * (MaxStopLossPercent.ValueDecimal / 100) ||
                        stopLossPrice > _lastPrice)
                        return;
                    decimal vol = volume.GetVolume(tab);
                    if (vol > 0)
                        tab.BuyAtLimit(vol, _lastPrice + _lastPrice * (Slippage.ValueDecimal / 100));
                }
            }

            // Short
            if (Regime.ValueString != "OnlyLong")
            {
                if (candles[candles.Count - 3].Open > candles[candles.Count - 3].Close
                    && candles[candles.Count - 2].Open > candles[candles.Count - 2].Close
                    && candles[candles.Count - 1].Open > candles[candles.Count - 1].Close)
                {
                    if (SmaFilterIsOn.ValueBool == true)
                    {
                        decimal smaValue = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 4);
                        decimal smaPrev = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 5);
                        if (smaValue >= smaPrev || smaValue < candles[candles.Count - 4].Close)
                            return;
                    }
                    decimal stopLossPrice = GetStopLossPrice(Side.Sell, candles, tab);
                    if (stopLossPrice > _lastPrice + _lastPrice * (MaxStopLossPercent.ValueDecimal / 100))
                        return;
                    decimal vol = volume.GetVolume(tab);
                    if (vol > 0)
                        tab.SellAtLimit(vol, _lastPrice - _lastPrice * (Slippage.ValueDecimal / 100));
                }
            }
            return;
        }

        private decimal GetStopLossPrice(Side direction, List<Candle> candles, BotTabSimple tab)
        {
            decimal _lastPrice = candles[candles.Count - 1].Close;
            decimal priceStop = 0m;
            if (direction == Side.Buy)
            {
                if (ProcHeightStop.ValueDecimal != 0)
                {
                    decimal heightPattern =
                        Math.Abs(tab.CandlesAll[tab.CandlesAll.Count - 3].Open - tab.CandlesAll[tab.CandlesAll.Count - 1].Close);
                    priceStop = _lastPrice - (heightPattern * ProcHeightStop.ValueDecimal) / 100;
                }
                if (TrailingStopRepcent.ValueDecimal != 0)
                {
                    priceStop = Math.Max(_lastPrice - _lastPrice * (TrailingStopRepcent.ValueDecimal / 100), priceStop);
                }
            }
            else
            {
                if (ProcHeightStop.ValueDecimal != 0)
                {
                    decimal heightPattern = 
                        Math.Abs(tab.CandlesAll[tab.CandlesAll.Count - 1].Close - tab.CandlesAll[tab.CandlesAll.Count - 3].Open);
                    priceStop = _lastPrice + (heightPattern * ProcHeightStop.ValueDecimal) / 100;
                }
                if (TrailingStopRepcent.ValueDecimal != 0)
                {
                    priceStop = _lastPrice + _lastPrice * (TrailingStopRepcent.ValueDecimal / 100);
                }
            }
            return priceStop;
        }

        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab, SecuritiesTradeSettings settings)
        {
            decimal _lastPrice = candles[candles.Count - 1].Close;
            List<Position> openPositions = tab.PositionsOpenAll;
            for (int i = 0; openPositions != null && i < openPositions.Count; i++)
            {
                Position position = openPositions[i];
                if (position.State != PositionStateType.Open)
                    continue;

                if (ExitAtBarCount.ValueInt != 0)
                {
                    int bars = 0;
                    for (int j = candles.Count - 1; j >= 0; j--)
                    {
                        if (candles[j].TimeStart > position.TimeOpen)
                            bars++;
                        else
                            break;
                    }

                    if (bars > ExitAtBarCount.ValueInt)
                    {
                        tab.CloseAtMarket(position, position.OpenVolume);
                        continue;
                    }
                }

                if (position.Direction == Side.Buy)
                {
                    decimal heightPattern =
                        Math.Abs(tab.CandlesAll[tab.CandlesAll.Count - 4].Open - tab.CandlesAll[tab.CandlesAll.Count - 2].Close);

                    if (position.StopOrderPrice == 0)
                    {
                        decimal priceStop = _lastPrice - (heightPattern * ProcHeightStop.ValueDecimal) / 100;
                        decimal priceTake = _lastPrice + (heightPattern * ProcHeightTake.ValueDecimal) / 100;
                        if (ProcHeightStop.ValueDecimal != 0)
                            tab.CloseAtStop(position, priceStop, priceStop - priceStop * (Slippage.ValueDecimal / 100));
                        if (ProcHeightTake.ValueDecimal != 0)
                            tab.CloseAtProfit(position, priceTake, priceTake - priceTake * (Slippage.ValueDecimal / 100));
                    }
                    if (TrailingStopRepcent.ValueDecimal != 0)
                    {
                        decimal priceStop = _lastPrice - _lastPrice * (TrailingStopRepcent.ValueDecimal / 100);
                        if (priceStop > position.EntryPrice)
                            tab.CloseAtTrailingStop(position, priceStop, priceStop - priceStop * (Slippage.ValueDecimal / 100));
                    }
                }
                else
                {
                    decimal heightPattern = Math.Abs(tab.CandlesAll[tab.CandlesAll.Count - 2].Close - tab.CandlesAll[tab.CandlesAll.Count - 4].Open);
                    if (position.StopOrderPrice != 0)
                    {
                        decimal priceStop = _lastPrice + (heightPattern * ProcHeightStop.ValueDecimal) / 100;
                        decimal priceTake = _lastPrice - (heightPattern * ProcHeightTake.ValueDecimal) / 100;
                        if (ProcHeightStop.ValueDecimal != 0)
                            tab.CloseAtStop(position, priceStop, priceStop + priceStop * (Slippage.ValueDecimal / 100));
                        if (ProcHeightTake.ValueDecimal != 0)
                            tab.CloseAtProfit(position, priceTake, priceTake + priceTake * (Slippage.ValueDecimal / 100));
                    }
                    if (TrailingStopRepcent.ValueDecimal != 0)
                    {
                        decimal priceStop = _lastPrice + _lastPrice * (TrailingStopRepcent.ValueDecimal / 100);
                        if (priceStop < position.EntryPrice)
                            tab.CloseAtTrailingStop(position, priceStop, priceStop + priceStop * (Slippage.ValueDecimal / 100));
                    }
                }
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

    public class SecuritiesTradeSettings
    {
        public string SecName;

        public string SecClass;

        public decimal HeightSoldiers;
        public decimal MinHeightOneSoldier;
        public DateTime LastUpdateTime;

        public string GetSaveString()
        {
            string result = "";

            result += SecName + "%";
            result += SecClass + "%";
            result += HeightSoldiers + "%";
            result += MinHeightOneSoldier + "%";
            result += LastUpdateTime.ToString(CultureInfo.InvariantCulture) + "%";

            return result;
        }

        public void LoadFromString(string str)
        {
            string[] array = str.Split('%');

            SecName = array[0];
            SecClass = array[1];
            HeightSoldiers = array[2].ToDecimal();
            MinHeightOneSoldier = array[3].ToDecimal();
            LastUpdateTime = Convert.ToDateTime(array[4], CultureInfo.InvariantCulture);
        }
    }
}