/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Charts.CandleChart.Elements;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Counter-trend robot trading from the boundaries of a price range, without stop losses.
    /// The risk of hung trades is covered by options that the trader opens manually.
    ///
    /// The range is the highest High and the lowest Low of the previous Range length candles (standard PriceChannel
    /// of the previous candle). Standard ATR and EfficiencyRatio indicators are used as well, no own indicators.
    /// Entry is a false breakout: the candle went beyond a boundary and closed back inside the range.
    /// Above the upper boundary - sell, below the lower one - buy. The take target must be far enough (Min take distance).
    ///
    /// The take target is a level inside the range fixed at the moment of the entry: Take level 0.5 - the middle,
    /// 1 - the opposite boundary. It does not move after the price, but it is never closer than Min profit.
    ///
    /// Trades form a series. The first trade sets the direction, adds go only in the same direction,
    /// on a new false breakout of the already expanded range and not closer than the add step to the deepest entry.
    /// Closing only by take profit limit orders: each trade by its own take (Individual)
    /// or all trades of the series at one price (Basket).
    ///
    /// Every trade is a separate OsEngine position, so the series is restored from the journal
    /// after a restart and the robot needs no state file.
    ///
    /// Description of the parameters - in SDKCounterTrendRange.md.
    /// </summary>
    [Bot("SDKCounterTrendRange")]
    public class SDKCounterTrendRange : BotPanel
    {
        #region Constructor and fields

        private BotTabSimple _tab;

        // штатные индикаторы OsEngine: робот торгует по тем же значениям, что нарисованы на графике
        private Aindicator _priceChannel;

        private Aindicator _atr;

        private Aindicator _erIndicator;

        private LineHorisontal _erMaxLine;

        // Base
        private StrategyParameterString _regime;
        private StrategyParameterDecimal _volume;
        private StrategyParameterInt _maxAdditionalEntries;
        private StrategyParameterString _orderLifeType;
        private StrategyParameterString _testerForcedClose;

        // Range
        private StrategyParameterInt _rangeLength;
        private StrategyParameterInt _atrLength;
        private StrategyParameterDecimal _minTakeDistance;
        private StrategyParameterDecimal _minBreakout;

        // Entry
        private StrategyParameterInt _minCandlesBetweenEntries;
        private StrategyParameterDecimal _entryOffset;
        private StrategyParameterInt _entryOrderLife;
        private StrategyParameterString _addStepType;
        private StrategyParameterDecimal _addStep;
        private StrategyParameterDecimal _addStepMultiplier;

        // Exit
        private StrategyParameterString _closeMode;
        private StrategyParameterDecimal _takeLevel;
        private StrategyParameterString _minProfitType;
        private StrategyParameterDecimal _minProfit;

        // Filter
        private StrategyParameterString _trendFilter;
        private StrategyParameterInt _erLength;
        private StrategyParameterDecimal _erMax;

        // повторный запрос снятия той же заявки в реале - не чаще этого интервала
        private const int CancelRepeatSeconds = 30;

        private readonly object _locker = new object();

        // время закрытой свечи, по которой уже проверены сигналы входа
        private DateTime _lastSignalCandleTime = DateTime.MinValue;

        // номер заявки -> время последнего запроса на снятие (только реал)
        private readonly Dictionary<int, DateTime> _cancelRequests = new Dictionary<int, DateTime>();

        // номер позиции -> диапазон и ATR на сигнальной свече входа. Кэш, восстанавливается по свечам
        private readonly Dictionary<int, EntrySnapshot> _entrySnapshots = new Dictionary<int, EntrySnapshot>();

        private bool _mixedSidesWarned;

        private bool _gtcUnsupportedWarned;

        // период (неделя / месяц / квартал) последней обработанной свечи для Tester forced close
        private string _lastForcedClosePeriod;

        // номера позиций, которые закрываются по рынку в Tester forced close
        private readonly HashSet<int> _forcedClosePositions = new HashSet<int>();

        private bool _forcedCloseIgnoredWarned;

        private volatile bool _isDeleted;

        public SDKCounterTrendRange(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            _regime = CreateParameter("Regime", "Off",
                new[] { "Off", "On", "OnlyLong", "OnlyShort", "CloseOnly" }, "Base");
            _volume = CreateParameter("Volume per entry", 1m, 1m, 10m, 1m, "Base");
            _maxAdditionalEntries = CreateParameter("Max additional entries", 2, 0, 10, 1, "Base");
            _orderLifeType = CreateParameter("Order life type", "Tab", new[] { "Tab", "GTC" }, "Base");
            _testerForcedClose = CreateParameter("Tester forced close", "Off",
                new[] { "Off", "Week", "Month", "Quarter" }, "Base");

            _rangeLength = CreateParameter("Range length", 48, 12, 200, 12, "Range");
            _atrLength = CreateParameter("ATR length", 14, 5, 50, 5, "Range");
            _minTakeDistance = CreateParameter("Min take distance (ATR)", 4m, 0m, 20m, 1m, "Range");
            _minBreakout = CreateParameter("Min breakout (ATR)", 0m, 0m, 2m, 0.25m, "Range");

            _minCandlesBetweenEntries = CreateParameter("Min candles between entries", 3, 0, 20, 1, "Entry");
            _entryOffset = CreateParameter("Entry offset (ATR)", 0m, 0m, 1m, 0.1m, "Entry");
            _entryOrderLife = CreateParameter("Entry order life (candles)", 1, 0, 10, 1, "Entry");
            _addStepType = CreateParameter("Add step type", "Points", new[] { "Points", "Percent", "Atr" }, "Entry");
            _addStep = CreateParameter("Add step", 50m, 0m, 500m, 25m, "Entry");
            _addStepMultiplier = CreateParameter("Add step multiplier", 1.5m, 1m, 3m, 0.25m, "Entry");

            _closeMode = CreateParameter("Close mode", "Basket", new[] { "Basket", "Individual" }, "Exit");
            _takeLevel = CreateParameter("Take level", 0.5m, 0.25m, 1m, 0.25m, "Exit");
            _minProfitType = CreateParameter("Min profit type", "Atr", new[] { "Atr", "Points", "Percent" }, "Exit");
            _minProfit = CreateParameter("Min profit", 1m, 0m, 5m, 0.5m, "Exit");

            _trendFilter = CreateParameter("Trend filter", "Off", new[] { "Off", "On", "Directional" }, "Filter");
            _erLength = CreateParameter("ER length", 100, 10, 100, 10, "Filter");
            _erMax = CreateParameter("ER max", 0.15m, 0.1m, 0.6m, 0.05m, "Filter");

            _priceChannel = IndicatorsFactory.CreateIndicatorByName("PriceChannel", name + "PriceChannel", false);
            _priceChannel = (Aindicator)_tab.CreateCandleIndicator(_priceChannel, "Prime");

            _atr = IndicatorsFactory.CreateIndicatorByName("ATR", name + "Atr", false);
            _atr = (Aindicator)_tab.CreateCandleIndicator(_atr, "AtrArea");

            _erIndicator = IndicatorsFactory.CreateIndicatorByName("EfficiencyRatio", name + "Er", false);
            _erIndicator = (Aindicator)_tab.CreateCandleIndicator(_erIndicator, "ErArea");

            _erMaxLine = new LineHorisontal(name + "ErMax", "ErArea", false);
            _erMaxLine.Color = System.Drawing.Color.IndianRed;
            _erMaxLine.Label = "ER max";
            _tab.SetChartElement(_erMaxLine);

            ApplyIndicatorParameters();

            // заявки и тейки ведёт робот, штатное сопровождение OsEngine их бы снимало по таймауту
            _tab.ManualPositionSupport.DisableManualSupport();

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
            _tab.PositionOpeningSuccesEvent += _tab_PositionOpeningSuccesEvent;
            _tab.Connector.TestStartEvent += Connector_TestStartEvent;
            ParametrsChangeByUser += SDKCounterTrendRange_ParametrsChangeByUser;
            DeleteEvent += SDKCounterTrendRange_DeleteEvent;

            if (startProgram == StartProgram.IsOsTrader)
            {
                Thread worker = new Thread(WorkerThreadPlace);
                worker.Name = "SDKCounterTrendRange." + name;
                worker.IsBackground = true;
                worker.Start();
            }

            Description = "Контртрендовый робот от границ диапазона без стопов. " +
                "Диапазон - максимум и минимум за N прошлых свечей. Вход на ложном пробое: свеча вышла за границу " +
                "и закрылась обратно внутри. Тейк - уровень внутри диапазона, зафиксированный при входе (середина или противоположная граница). " +
                "Первая сделка задаёт направление серии, доборы - только в ту же сторону, на новом ложном пробое и глубже. " +
                "Закрытие только по профиту. Риск зависших сделок прикрывается опционами вручную.";
        }

        public override string GetNameStrategyType()
        {
            return "SDKCounterTrendRange";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        #endregion

        #region Event handlers

        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            try
            {
                lock (_locker)
                {
                    Process(true);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void _tab_PositionOpeningSuccesEvent(Position position)
        {
            try
            {
                // тейк ставим сразу, не дожидаясь закрытия свечи
                lock (_locker)
                {
                    Process(false);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void WorkerThreadPlace()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);

                    if (_isDeleted)
                    {
                        return;
                    }

                    lock (_locker)
                    {
                        Process(false);
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        private void SDKCounterTrendRange_ParametrsChangeByUser()
        {
            try
            {
                lock (_locker)
                {
                    _gtcUnsupportedWarned = false;
                    _entrySnapshots.Clear();
                    ApplyIndicatorParameters();
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Connector_TestStartEvent()
        {
            try
            {
                lock (_locker)
                {
                    _lastSignalCandleTime = DateTime.MinValue;
                    _cancelRequests.Clear();
                    _entrySnapshots.Clear();
                    _mixedSidesWarned = false;
                    _gtcUnsupportedWarned = false;
                    _lastForcedClosePeriod = null;
                    _forcedClosePositions.Clear();
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SDKCounterTrendRange_DeleteEvent()
        {
            _isDeleted = true;
        }

        #endregion

        #region Main logic

        /// <summary>
        /// One pass of the robot. Signals are checked only on a finished candle,
        /// orders are maintained on every pass.
        /// </summary>
        private void Process(bool isCandleFinished)
        {
            if (_isDeleted)
            {
                return;
            }

            if (_tab.Security == null
                || _tab.Security.PriceStep <= 0)
            {
                return;
            }

            List<Position> positions = _tab.PositionsOpenAll;

            if (positions == null)
            {
                return;
            }

            string regime = _regime.ValueString;

            if (regime == "Off")
            {
                CancelAllOrders(positions);
                return;
            }

            ModelValues model = GetModelValues();

            if (model == null)
            {
                return;
            }

            if (ProcessForcedClose(positions, model))
            {
                // идёт принудительное закрытие: тейки и новые входы - после него
                return;
            }

            CancelEntryOrders(positions, regime == "CloseOnly");

            MaintainTakes(positions, model);

            if (isCandleFinished == false
                || regime == "CloseOnly"
                || model.CandleTime <= _lastSignalCandleTime)
            {
                return;
            }

            _lastSignalCandleTime = model.CandleTime;

            TryEnter(positions, model, regime);
        }

        private void TryEnter(List<Position> positions, ModelValues model, string regime)
        {
            List<Position> series = new List<Position>();
            bool hasLong = false;
            bool hasShort = false;

            for (int i = 0; i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position.OpenActive)
                {
                    // заявка на вход ещё стоит - следующий вход только после её исполнения или снятия
                    return;
                }

                if (position.OpenVolume <= 0)
                {
                    continue;
                }

                series.Add(position);

                if (position.Direction == Side.Buy)
                {
                    hasLong = true;
                }
                else
                {
                    hasShort = true;
                }
            }

            if (hasLong && hasShort)
            {
                if (_mixedSidesWarned == false)
                {
                    _mixedSidesWarned = true;
                    SendLog("Открыты позиции в обе стороны. Новые входы запрещены, тейки сопровождаются. " +
                        "Закройте лишние позиции вручную", LogMessageType.Error);
                }
                return;
            }

            _mixedSidesWarned = false;

            if (series.Count == 0)
            {
                TryOpenFirstEntry(model, regime);
                return;
            }

            Side side = hasLong ? Side.Buy : Side.Sell;

            if (IsSideAllowed(side, regime) == false
                || series.Count >= 1 + _maxAdditionalEntries.ValueInt)
            {
                return;
            }

            DateTime lastEntryTime = DateTime.MinValue;
            Position firstEntry = series[0];
            decimal worstEntryPrice = series[0].EntryPrice;

            for (int i = 0; i < series.Count; i++)
            {
                if (series[i].TimeCreate > lastEntryTime)
                {
                    lastEntryTime = series[i].TimeCreate;
                }

                if (series[i].TimeCreate < firstEntry.TimeCreate)
                {
                    firstEntry = series[i];
                }

                if (side == Side.Buy && series[i].EntryPrice < worstEntryPrice
                    || side == Side.Sell && series[i].EntryPrice > worstEntryPrice)
                {
                    worstEntryPrice = series[i].EntryPrice;
                }
            }

            if (CandlesPassed(lastEntryTime, _tab.TimeServerCurrent) < _minCandlesBetweenEntries.ValueInt)
            {
                return;
            }

            int entryNumber = series.Count;
            decimal addStep = GetAddStep(entryNumber, firstEntry, worstEntryPrice, model);
            if (IsFalseBreakout(side, model) == false)
            {
                return;
            }

            if (side == Side.Buy && model.Close > worstEntryPrice - addStep
                || side == Side.Sell && model.Close < worstEntryPrice + addStep)
            {
                return;
            }

            if (IsTrendFilterBlocking("Add " + entryNumber + " " + side, side, model))
            {
                return;
            }

            OpenEntry(side, entryNumber, model);
        }

        private void TryOpenFirstEntry(ModelValues model, string regime)
        {
            Side[] sides = { Side.Buy, Side.Sell };

            for (int i = 0; i < sides.Length; i++)
            {
                Side side = sides[i];

                if (IsSideAllowed(side, regime) == false)
                {
                    continue;
                }

                if (IsFalseBreakout(side, model) == false
                    || IsTrendFilterBlocking("Entry " + side, side, model))
                {
                    continue;
                }

                OpenEntry(side, 0, model);
                return;
            }
        }

        /// <summary>
        /// False breakout on the last finished candle. Sell: High went above Upper at least by Min breakout * ATR
        /// and Close returned below Upper. Buy - the mirror at Lower.
        /// The take target must be not closer than Min take distance * ATR from Close.
        /// </summary>
        private bool IsFalseBreakout(Side side, ModelValues model)
        {
            decimal breakout = _minBreakout.ValueDecimal * model.Atr;
            bool isBreakout = side == Side.Buy
                ? model.Low < model.Lower - breakout && model.Close > model.Lower
                : model.High > model.Upper + breakout && model.Close < model.Upper;

            if (isBreakout == false)
            {
                return false;
            }

            decimal targetDistance = Math.Abs(GetRangeTarget(side, model.Upper, model.Lower) - model.Close);

            return targetDistance >= _minTakeDistance.ValueDecimal * model.Atr;
        }

        /// <summary>
        /// How far beyond the deepest entry of the series the price must go for add number entryNumber (1, 2, ...).
        /// Step * Multiplier ^ (entryNumber - 1). Points - in price units, Percent - of the deepest entry price,
        /// Atr - in ATR of the first trade signal candle, fixed for the whole series.
        /// </summary>
        private decimal GetAddStep(int entryNumber, Position firstEntry, decimal worstEntryPrice, ModelValues model)
        {
            decimal step = _addStep.ValueDecimal;

            if (_addStepType.ValueString == "Percent")
            {
                step = worstEntryPrice * step / 100;
            }
            else if (_addStepType.ValueString == "Atr")
            {
                step = step * GetEntrySnapshot(firstEntry, model).Atr;
            }

            for (int i = 1; i < entryNumber; i++)
            {
                step = step * _addStepMultiplier.ValueDecimal;
            }

            return step;
        }

        /// <summary>
        /// Trend filter for both the first entry and the adds. A signal cut off by the filter is written to the log.
        /// On - while ER is above ER max, no entries at all.
        /// Directional - while ER is above ER max, only false breakouts along the trend: buy if Close is higher
        /// than ER length candles ago, sell if it is lower. Counter-trend false breakouts in a trend are the ones
        /// that most often go deep against the trade.
        /// </summary>
        private bool IsTrendFilterBlocking(string signal, Side side, ModelValues model)
        {
            string mode = _trendFilter.ValueString;

            if (mode == "Off")
            {
                return false;
            }

            if (model.Er < 0)
            {
                SendLog(signal + ": сигнал пропущен фильтром тренда - для ER мало свечей (нужно больше " +
                    _erLength.ValueInt + ")", LogMessageType.User);
                return true;
            }

            if (model.Er <= _erMax.ValueDecimal)
            {
                return false;
            }

            if (mode == "Directional")
            {
                bool isAlongTrend = side == Side.Buy && model.TrendChange > 0
                    || side == Side.Sell && model.TrendChange < 0;

                if (isAlongTrend)
                {
                    return false;
                }

                SendLog(signal + ": сигнал пропущен фильтром тренда - ER " + Math.Round(model.Er, 4) +
                    " > ER max " + _erMax.ValueDecimal + " и вход против тренда (изменение цены за " + _erLength.ValueInt +
                    " свечей " + Math.Round(model.TrendChange, 8) + "). Свеча " + model.CandleTime.ToString("dd.MM.yyyy HH:mm"),
                    LogMessageType.User);
                return true;
            }

            SendLog(signal + ": сигнал пропущен фильтром тренда - ER " + Math.Round(model.Er, 4) +
                " > ER max " + _erMax.ValueDecimal + ". Свеча " + model.CandleTime.ToString("dd.MM.yyyy HH:mm"),
                LogMessageType.User);
            return true;
        }

        private void OpenEntry(Side side, int entryNumber, ModelValues model)
        {
            decimal volume = _volume.ValueDecimal;

            if (volume <= 0)
            {
                return;
            }

            decimal offset = _entryOffset.ValueDecimal * model.Atr;
            decimal priceStep = _tab.Security.PriceStep;
            string signal = entryNumber == 0 ? "Entry" : "Add " + entryNumber;
            Position position;
            decimal price;
            OrderTypeTime previousLifeType;
            bool isLifeOverridden = TryOverrideOrderLife(out previousLifeType);

            try
            {
                if (side == Side.Buy)
                {
                    price = RoundDown(model.Close - offset, priceStep);
                    position = _tab.BuyAtLimit(volume, price, signal);
                }
                else
                {
                    price = RoundUp(model.Close + offset, priceStep);
                    position = _tab.SellAtLimit(volume, price, signal);
                }
            }
            finally
            {
                RestoreOrderLife(isLifeOverridden, previousLifeType);
            }

            if (position != null)
            {
                decimal target = GetRangeTarget(side, model.Upper, model.Lower);

                SendLog(signal + " " + side + ": лимитка " + volume + " по " + price +
                    ". Диапазон " + model.Lower + " - " + model.Upper + ", цель тейка " + Math.Round(target, 8) +
                    ", ATR " + Math.Round(model.Atr, 8), LogMessageType.User);
            }
        }

        #endregion

        #region Orders maintenance

        /// <summary>
        /// Takes of all open trades. Individual - the range target fixed at the entry of each trade.
        /// Basket - one price for all trades of the side: the range target of the latest entry,
        /// but not closer than Min profit from the average entry price.
        /// </summary>
        private void MaintainTakes(List<Position> positions, ModelValues model)
        {
            Side[] sides = { Side.Buy, Side.Sell };

            for (int s = 0; s < sides.Length; s++)
            {
                Side side = sides[s];
                List<Position> group = new List<Position>();
                List<EntrySnapshot> snapshots = new List<EntrySnapshot>();
                decimal volumeSum = 0;
                decimal moneySum = 0;
                decimal atrSum = 0;
                Position latestEntry = null;
                EntrySnapshot latestSnapshot = null;

                for (int i = 0; i < positions.Count; i++)
                {
                    Position position = positions[i];

                    if (position.Direction != side
                        || position.OpenVolume <= 0
                        || position.OpenActive)
                    {
                        continue;
                    }

                    EntrySnapshot snapshot = GetEntrySnapshot(position, model);

                    group.Add(position);
                    snapshots.Add(snapshot);
                    volumeSum += position.OpenVolume;
                    moneySum += position.EntryPrice * position.OpenVolume;
                    atrSum += snapshot.Atr * position.OpenVolume;

                    if (latestEntry == null
                        || position.TimeCreate > latestEntry.TimeCreate)
                    {
                        latestEntry = position;
                        latestSnapshot = snapshot;
                    }
                }

                if (group.Count == 0)
                {
                    continue;
                }

                decimal basketPrice = 0;

                if (_closeMode.ValueString == "Basket")
                {
                    decimal target = GetRangeTarget(side, latestSnapshot.Upper, latestSnapshot.Lower);
                    basketPrice = GetTakePrice(side, moneySum / volumeSum, atrSum / volumeSum, target);
                }

                for (int i = 0; i < group.Count; i++)
                {
                    decimal takePrice = basketPrice;

                    if (takePrice == 0)
                    {
                        decimal target = GetRangeTarget(side, snapshots[i].Upper, snapshots[i].Lower);
                        takePrice = GetTakePrice(side, group[i].EntryPrice, snapshots[i].Atr, target);
                    }

                    PlaceOrMoveTake(group[i], takePrice);
                }
            }
        }

        /// <summary>
        /// Level inside the range counted from the boundary of the breakout: Take level 0.5 - the middle, 1 - the opposite boundary.
        /// </summary>
        private decimal GetRangeTarget(Side side, decimal upper, decimal lower)
        {
            decimal distance = (upper - lower) * _takeLevel.ValueDecimal;

            if (side == Side.Buy)
            {
                return lower + distance;
            }

            return upper - distance;
        }

        /// <summary>
        /// The range target, but not closer than Min profit from the entry (average entry in Basket).
        /// </summary>
        private decimal GetTakePrice(Side side, decimal entryPrice, decimal entryAtr, decimal target)
        {
            decimal profit = _minProfit.ValueDecimal;

            if (_minProfitType.ValueString == "Atr")
            {
                profit = profit * entryAtr;
            }
            else if (_minProfitType.ValueString == "Percent")
            {
                profit = entryPrice * profit / 100;
            }

            decimal priceStep = _tab.Security.PriceStep;

            if (side == Side.Buy)
            {
                return RoundUp(Math.Max(target, entryPrice + profit), priceStep);
            }

            return RoundDown(Math.Min(target, entryPrice - profit), priceStep);
        }

        /// <summary>
        /// Range and ATR on the signal candle of the trade: the last candle finished before the entry order was created.
        /// They are fixed for the whole life of the trade, so the take does not move after the price.
        /// If the history does not reach the entry - the current values.
        /// </summary>
        private EntrySnapshot GetEntrySnapshot(Position position, ModelValues model)
        {
            EntrySnapshot cached;

            if (_entrySnapshots.TryGetValue(position.Number, out cached))
            {
                return cached;
            }

            EntrySnapshot current = new EntrySnapshot();
            current.Upper = model.Upper;
            current.Lower = model.Lower;
            current.Atr = model.Atr;

            List<Candle> candles = _tab.CandlesAll;
            TimeSpan timeFrame = _tab.Connector.TimeFrameTimeSpan;
            DateTime entryTime = position.TimeCreate;

            if (candles == null
                || candles.Count == 0
                || timeFrame.TotalSeconds <= 0
                || candles[0].TimeStart.Add(timeFrame) > entryTime)
            {
                return current;
            }

            // последняя свеча, закрывшаяся не позже создания заявки на вход
            int low = 0;
            int high = candles.Count - 1;
            int index = -1;

            while (low <= high)
            {
                int middle = (low + high) / 2;

                if (candles[middle].TimeStart.Add(timeFrame) <= entryTime)
                {
                    index = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            decimal upper;
            decimal lower;
            decimal atr;

            if (index < 0
                || TryGetRange(index, out upper, out lower, out atr) == false)
            {
                return current;
            }

            EntrySnapshot snapshot = new EntrySnapshot();
            snapshot.Upper = upper;
            snapshot.Lower = lower;
            snapshot.Atr = atr;

            if (_entrySnapshots.Count > 1000)
            {
                _entrySnapshots.Clear();
            }

            _entrySnapshots[position.Number] = snapshot;

            return snapshot;
        }

        /// <summary>
        /// Puts a take limit order or moves it to a new price.
        /// The old order is cancelled first, the new one goes only after the cancellation is confirmed,
        /// so two takes of one trade never stand on the exchange at the same time.
        /// </summary>
        private void PlaceOrMoveTake(Position position, decimal takePrice)
        {
            Order activeOrder = null;

            for (int i = 0; position.CloseOrders != null && i < position.CloseOrders.Count; i++)
            {
                Order order = position.CloseOrders[i];

                if (order != null && IsOrderAlive(order))
                {
                    activeOrder = order;
                }
            }

            if (activeOrder != null)
            {
                if (activeOrder.State == OrderStateType.Pending
                    || activeOrder.State == OrderStateType.None)
                {
                    // биржа ещё не подтвердила заявку
                    return;
                }

                // цель зафиксирована при входе: тейк переставляется только при смене средней цены серии или параметров
                if (Math.Abs(activeOrder.Price - takePrice) < _tab.Security.PriceStep)
                {
                    return;
                }

                CancelOrder(activeOrder);

                if (position.CloseActive)
                {
                    // в реале снятие асинхронное: новый тейк - на следующем проходе
                    return;
                }
            }

            if (position.OpenVolume <= 0)
            {
                return;
            }

            OrderTypeTime previousLifeType;
            bool isLifeOverridden = TryOverrideOrderLife(out previousLifeType);

            try
            {
                _tab.CloseAtLimit(position, takePrice, position.OpenVolume, "Take");
            }
            finally
            {
                RestoreOrderLife(isLifeOverridden, previousLifeType);
            }
        }

        /// <summary>
        /// "Order life type" = GTC: the tab sends orders with the lifetime type from its position support settings,
        /// so for the moment of sending a robot order the type is switched to GTC and then switched back.
        /// The tab settings themselves are not changed, manual orders keep the type chosen by the trader.
        /// </summary>
        private bool TryOverrideOrderLife(out OrderTypeTime previousLifeType)
        {
            previousLifeType = _tab.ManualPositionSupport.OrderTypeTime;

            if (_orderLifeType.ValueString != "GTC"
                || previousLifeType == OrderTypeTime.GTC)
            {
                return false;
            }

            if (IsGtcSupported() == false)
            {
                if (_gtcUnsupportedWarned == false)
                {
                    _gtcUnsupportedWarned = true;
                    SendLog("Order life type = GTC: коннектор " + _tab.Connector.ServerType +
                        " не поддерживает GTC. Заявки уходят с типом из настроек сопровождения таба: " +
                        previousLifeType, LogMessageType.Error);
                }
                return false;
            }

            _tab.ManualPositionSupport.OrderTypeTime = OrderTypeTime.GTC;
            return true;
        }

        private void RestoreOrderLife(bool isLifeOverridden, OrderTypeTime previousLifeType)
        {
            if (isLifeOverridden)
            {
                _tab.ManualPositionSupport.OrderTypeTime = previousLifeType;
            }
        }

        /// <summary>
        /// Same rule as the tab uses for entry orders: no permission or no lifetime restrictions
        /// (the tester among them) - any type is allowed.
        /// </summary>
        private bool IsGtcSupported()
        {
            IServerPermission permission = ServerMaster.GetServerPermission(_tab.Connector.ServerType);

            if (permission == null
                || permission.OrdersLifeTimeRealization == null)
            {
                return true;
            }

            return permission.OrdersLifeTimeRealization.GtcIsReady;
        }

        /// <summary>
        /// Cancels entry orders that lived longer than "Entry order life", or all of them in CloseOnly.
        /// </summary>
        private void CancelEntryOrders(List<Position> positions, bool cancelAll)
        {
            int life = _entryOrderLife.ValueInt;

            if (cancelAll == false
                && life <= 0)
            {
                return;
            }

            if (StartProgram == StartProgram.IsTester
                    || StartProgram == StartProgram.IsOsOptimizer)
            {
                // в оптимизаторе событие закрытия свечи приходит роботу раньше, чем сервер проверяет заявки
                // на этой свече: без поправки заявка снималась бы, не увидев ни одной свечи, и сделок не было бы вовсе.
                // С поправкой число сделок совпадает с тестером
                life++;
            }

            DateTime now = _tab.TimeServerCurrent;

            for (int i = 0; i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position.OpenActive == false)
                {
                    continue;
                }

                if (cancelAll == false
                    && CandlesPassed(position.TimeCreate, now) < life)
                {
                    continue;
                }

                for (int j = 0; position.OpenOrders != null && j < position.OpenOrders.Count; j++)
                {
                    CancelOrder(position.OpenOrders[j]);
                }
            }
        }

        private void CancelAllOrders(List<Position> positions)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                Position position = positions[i];

                for (int j = 0; position.OpenOrders != null && j < position.OpenOrders.Count; j++)
                {
                    CancelOrder(position.OpenOrders[j]);
                }

                for (int j = 0; position.CloseOrders != null && j < position.CloseOrders.Count; j++)
                {
                    CancelOrder(position.CloseOrders[j]);
                }
            }
        }

        private void CancelOrder(Order order)
        {
            if (order == null)
            {
                return;
            }

            if (order.State != OrderStateType.Active
                && order.State != OrderStateType.Partial)
            {
                return;
            }

            if (StartProgram == StartProgram.IsOsTrader)
            {
                DateTime now = DateTime.Now;
                DateTime lastRequest;

                if (_cancelRequests.TryGetValue(order.NumberUser, out lastRequest)
                    && now - lastRequest < TimeSpan.FromSeconds(CancelRepeatSeconds))
                {
                    return;
                }

                if (_cancelRequests.Count > 1000)
                {
                    _cancelRequests.Clear();
                }

                _cancelRequests[order.NumberUser] = now;
            }

            _tab.CloseOrder(order);
        }

        private bool IsOrderAlive(Order order)
        {
            return order.State == OrderStateType.Active
                || order.State == OrderStateType.Partial
                || order.State == OrderStateType.Pending
                || order.State == OrderStateType.None;
        }

        #endregion

        #region Tester forced close

        /// <summary>
        /// Tester and optimizer only. On the first candle of a new week / month / quarter all trades are closed
        /// at market, like an option expiration: a hung series cannot drop months out of the test.
        /// True while the forced close is in progress - takes and entries wait for it.
        /// </summary>
        private bool ProcessForcedClose(List<Position> positions, ModelValues model)
        {
            string mode = _testerForcedClose.ValueString;

            if (mode == "Off")
            {
                _lastForcedClosePeriod = null;
                _forcedClosePositions.Clear();
                return false;
            }

            if (StartProgram == StartProgram.IsOsTrader)
            {
                if (_forcedCloseIgnoredWarned == false)
                {
                    _forcedCloseIgnoredWarned = true;
                    SendLog("Tester forced close = " + mode + " работает только в тестере и оптимизаторе. " +
                        "В реальной торговле параметр игнорируется", LogMessageType.System);
                }
                return false;
            }

            string period = GetForcedClosePeriod(model.CandleTime, mode);

            if (_lastForcedClosePeriod == null)
            {
                // первая свеча теста: закрывать ещё нечего
                _lastForcedClosePeriod = period;
            }
            else if (period != _lastForcedClosePeriod)
            {
                _lastForcedClosePeriod = period;
                _forcedClosePositions.Clear();

                for (int i = 0; i < positions.Count; i++)
                {
                    if (positions[i].OpenVolume > 0
                        || positions[i].OpenActive)
                    {
                        _forcedClosePositions.Add(positions[i].Number);
                    }
                }

                if (_forcedClosePositions.Count > 0)
                {
                    SendLog("Tester forced close: начался период " + period +
                        ", сделок к закрытию по рынку: " + _forcedClosePositions.Count, LogMessageType.User);
                }
            }

            if (_forcedClosePositions.Count == 0)
            {
                return false;
            }

            bool isInProgress = false;
            HashSet<int> stillOpen = new HashSet<int>();

            for (int i = 0; i < positions.Count; i++)
            {
                Position position = positions[i];

                if (_forcedClosePositions.Contains(position.Number) == false)
                {
                    continue;
                }

                if (position.OpenActive)
                {
                    for (int j = 0; position.OpenOrders != null && j < position.OpenOrders.Count; j++)
                    {
                        CancelOrder(position.OpenOrders[j]);
                    }

                    stillOpen.Add(position.Number);
                    isInProgress = true;
                    continue;
                }

                if (position.OpenVolume <= 0)
                {
                    continue;
                }

                stillOpen.Add(position.Number);
                isInProgress = true;

                if (HasAliveMarketCloseOrder(position))
                {
                    continue;
                }

                // в безопасном режиме тестер снимает тейк и сразу отправляет рыночную заявку
                _tab.CloseAtMarket(position, position.OpenVolume, "Forced close");
            }

            _forcedClosePositions.IntersectWith(stillOpen);

            return isInProgress;
        }

        private bool HasAliveMarketCloseOrder(Position position)
        {
            for (int i = 0; position.CloseOrders != null && i < position.CloseOrders.Count; i++)
            {
                Order order = position.CloseOrders[i];

                if (order != null
                    && order.TypeOrder == OrderPriceType.Market
                    && IsOrderAlive(order))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Week starts on Monday, month and quarter are calendar ones.
        /// </summary>
        private string GetForcedClosePeriod(DateTime time, string mode)
        {
            if (mode == "Week")
            {
                int daysFromMonday = ((int)time.DayOfWeek + 6) % 7;
                return "W" + time.Date.AddDays(-daysFromMonday).ToString("yyyy-MM-dd");
            }

            if (mode == "Quarter")
            {
                return time.Year + "-Q" + ((time.Month - 1) / 3 + 1);
            }

            return time.ToString("yyyy-MM");
        }

        #endregion

        #region Model

        private void ApplyIndicatorParameters()
        {
            if (_priceChannel != null)
            {
                // PriceChannel: [0] Length up, [1] Length down
                bool isChanged = SetIndicatorParameter(_priceChannel, 0, _rangeLength.ValueInt);
                isChanged |= SetIndicatorParameter(_priceChannel, 1, _rangeLength.ValueInt);
                ReloadIfChanged(_priceChannel, isChanged);
            }

            if (_atr != null)
            {
                // ATR: [0] Length, [1] Series type - нужен ATR в пунктах цены
                bool isChanged = SetIndicatorParameter(_atr, 0, _atrLength.ValueInt);
                IndicatorParameterString seriesType = (IndicatorParameterString)_atr.Parameters[1];

                if (seriesType.ValueString != "Absolute")
                {
                    seriesType.ValueString = "Absolute";
                    isChanged = true;
                }

                ReloadIfChanged(_atr, isChanged);
            }

            if (_erIndicator != null)
            {
                // EfficiencyRatio: [0] Length
                ReloadIfChanged(_erIndicator, SetIndicatorParameter(_erIndicator, 0, _erLength.ValueInt));
            }

            if (_erMaxLine != null)
            {
                _erMaxLine.Value = _erMax.ValueDecimal;
                _erMaxLine.Refresh();
            }
        }

        private void ReloadIfChanged(Aindicator indicator, bool isChanged)
        {
            if (isChanged)
            {
                indicator.Save();
                indicator.Reload();
            }
        }

        private bool SetIndicatorParameter(Aindicator indicator, int index, int value)
        {
            IndicatorParameterInt parameter = (IndicatorParameterInt)indicator.Parameters[index];

            if (parameter.ValueInt == value)
            {
                return false;
            }

            parameter.ValueInt = value;
            return true;
        }

        private bool SetIndicatorParameter(Aindicator indicator, int index, decimal value)
        {
            IndicatorParameterDecimal parameter = (IndicatorParameterDecimal)indicator.Parameters[index];

            if (parameter.ValueDecimal == value)
            {
                return false;
            }

            parameter.ValueDecimal = value;
            return true;
        }

        /// <summary>
        /// Values of the model on the last finished candle. Null while there is not enough history.
        /// </summary>
        private ModelValues GetModelValues()
        {
            List<Candle> candles = _tab.CandlesAll;

            if (candles == null
                || candles.Count == 0)
            {
                return null;
            }

            int index = candles.Count - 1;

            if (candles[index].State != CandleState.Finished)
            {
                index--;
            }

            int warmUp = Math.Max(_rangeLength.ValueInt, _atrLength.ValueInt) + 2;

            if (index < warmUp)
            {
                return null;
            }

            decimal upper;
            decimal lower;
            decimal atr;

            if (TryGetRange(index, out upper, out lower, out atr) == false)
            {
                return null;
            }

            ModelValues model = new ModelValues();
            model.CandleTime = candles[index].TimeStart;
            model.Close = candles[index].Close;
            model.High = candles[index].High;
            model.Low = candles[index].Low;
            model.Upper = upper;
            model.Lower = lower;
            model.Atr = atr;
            model.Er = GetEfficiencyRatio(index);

            if (index - _erLength.ValueInt >= 0)
            {
                model.TrendChange = candles[index].Close - candles[index - _erLength.ValueInt].Close;
            }

            return model;
        }

        /// <summary>
        /// Range for the candle index: PriceChannel of the previous candle, i.e. the previous Range length candles
        /// without the candle itself. Otherwise a candle could never go beyond its own channel. ATR - of the candle index.
        /// </summary>
        private bool TryGetRange(int index, out decimal upper, out decimal lower, out decimal atr)
        {
            upper = 0;
            lower = 0;
            atr = 0;

            List<decimal> upperValues = _priceChannel.DataSeries[0].Values;
            List<decimal> lowerValues = _priceChannel.DataSeries[1].Values;
            List<decimal> atrValues = _atr.DataSeries[0].Values;

            if (index < 1
                || upperValues.Count <= index
                || lowerValues.Count <= index
                || atrValues.Count <= index)
            {
                return false;
            }

            upper = upperValues[index - 1];
            lower = lowerValues[index - 1];
            atr = atrValues[index];

            // до прогрева PriceChannel рисует нули
            return atr > 0
                && lower > 0
                && upper > lower;
        }

        /// <summary>
        /// Kaufman efficiency ratio on the candle index, taken from the ER indicator on the chart:
        /// 1 - straight trend, 0 - noise. -1 if there are not enough candles.
        /// </summary>
        private decimal GetEfficiencyRatio(int index)
        {
            if (_erIndicator == null
                || _erLength.ValueInt < 1
                || index - _erLength.ValueInt <= 0)
            {
                return -1;
            }

            List<decimal> erValues = _erIndicator.DataSeries[0].Values;

            if (erValues.Count <= index)
            {
                return -1;
            }

            return erValues[index];
        }

        #endregion

        #region Helpers

        private bool IsSideAllowed(Side side, string regime)
        {
            if (side == Side.Buy)
            {
                return regime == "On" || regime == "OnlyLong";
            }

            return regime == "On" || regime == "OnlyShort";
        }

        private int CandlesPassed(DateTime from, DateTime to)
        {
            TimeSpan timeFrame = _tab.Connector.TimeFrameTimeSpan;

            if (timeFrame.TotalSeconds <= 0
                || to <= from)
            {
                return 0;
            }

            return (int)((to - from).TotalSeconds / timeFrame.TotalSeconds);
        }

        private decimal RoundDown(decimal value, decimal priceStep)
        {
            return Math.Floor(value / priceStep) * priceStep;
        }

        private decimal RoundUp(decimal value, decimal priceStep)
        {
            return Math.Ceiling(value / priceStep) * priceStep;
        }

        private void SendLog(string message, LogMessageType type)
        {
            if (StartProgram == StartProgram.IsOsOptimizer
                && type != LogMessageType.Error)
            {
                return;
            }

            SendNewLogMessage(message, type);
        }

        #endregion

        private class ModelValues
        {
            public DateTime CandleTime;

            public decimal Close;

            public decimal High;

            public decimal Low;

            public decimal Upper;

            public decimal Lower;

            public decimal Atr;

            public decimal Er;

            // изменение Close за ER length свечей: знак - направление тренда для Trend filter = Directional
            public decimal TrendChange;
        }

        private class EntrySnapshot
        {
            public decimal Upper;

            public decimal Lower;

            public decimal Atr;
        }
    }
}
