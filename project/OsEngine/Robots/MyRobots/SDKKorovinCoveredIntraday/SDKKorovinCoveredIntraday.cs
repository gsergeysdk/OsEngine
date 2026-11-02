/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.OsTrader.Grids;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Интрадей-часть стратегии Ильи Коровина «Прикрытый интрадей».
    /// Опционное прикрытие (покупка стреддла / стрэнгла) трейдер ведёт руками,
    /// робот только скальпирует фьючерс в боковике.
    ///
    /// По кнопке (от заданной или текущей цены) или по расписанию (от текущей цены) строятся две штатные сетки
    /// OsEngine типа MarketMaking: Buy ниже цены и Sell выше, с одинаковым числом линий.
    /// Шаг от линии к линии растёт геометрически, тейк-профит и объём у всех линий одинаковы,
    /// стопов нет. После тейка линия снова выставляет вход по той же цене, так что пока цена
    /// ходит в диапазоне, сделки повторяются.
    ///
    /// Повторное построение переносит открытые позиции: линия с позицией занимает место
    /// ближайшей новой линии, если попадает в её ячейку (половина шага в каждую сторону) -
    /// так исключается задвоение входа. Остальные позиции дорабатывают свои тейки
    /// в старой сетке в режиме CloseOnly, пустая старая сетка удаляется.
    /// Новых уровней на стороне строится не больше, чем предел позиций стороны минус уже
    /// открытые позиции этой стороны, так что позиция стороны не превышает предел × объём.
    /// Размер самой сетки задаёт отдельный параметр «число линий».
    ///
    /// Описание параметров - в SDKKorovinCoveredIntraday.md.
    /// </summary>
    [Bot("SDKKorovinCoveredIntraday")]
    public class SDKKorovinCoveredIntraday : BotPanel
    {
        #region Constructor and fields

        private BotTabSimple _tab;

        // Base
        private StrategyParameterString _regime;
        private StrategyParameterButton _buildButton;
        private StrategyParameterDecimal _buildPrice;
        private StrategyParameterString _autoBuildRegime;
        private StrategyParameterTimeOfDay _autoBuildTime;
        private StrategyParameterString _testerForcedClose;

        // Grid
        private StrategyParameterInt _linesCount;
        private StrategyParameterString _valueType;
        private StrategyParameterDecimal _firstStep;
        private StrategyParameterDecimal _stepMultiplier;
        private StrategyParameterDecimal _takeProfit;
        private StrategyParameterDecimal _volume;
        private StrategyParameterInt _maxEntryOrders;
        private StrategyParameterInt _maxPositions;

        // ожидание снятия заявок на вход перед пересборкой
        private const int EntryCancelTimeoutSeconds = 60;

        // пауза между сборкой новых сеток и их включением. За это время рабочий поток
        // старой сетки гарантированно отпускает прежний список линий, и перенесённую позицию
        // не обслуживают две сетки сразу
        private const int ActivationDelayMilliseconds = 1500;

        private readonly object _pumpLocker = new object();

        private RebuildStage _stage = RebuildStage.None;

        private DateTime _stageStartTime;

        private volatile bool _buildRequested;

        // цена, от которой строится текущая пересборка. 0 - от текущей цены
        private decimal _requestedAnchorPrice;

        // режим робота, уже применённый к сеткам. Сетки переключаются только при смене
        // режима, а не при загрузке: сохранённые режимы сеток уже ему соответствуют
        private string _appliedRegime;

        private volatile bool _entryOrdersLimitChanged;

        private DateTime _lastAutoBuildDate = DateTime.MinValue;

        // время сервера прошлого построения. По нему видно, начался ли новый
        // день / неделя / месяц для форсированного закрытия в тестере
        private DateTime _lastBuildServerTime = DateTime.MinValue;

        private readonly List<TradeGrid> _gridsToActivate = new List<TradeGrid>();

        private volatile bool _isDeleted;

        public SDKKorovinCoveredIntraday(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "CloseOnly" }, "Base");
            _buildButton = CreateParameterButton("Build grids", "Base");
            _buildButton.UserClickOnButtonEvent += BuildButton_UserClickOnButtonEvent;
            _buildPrice = CreateParameter("Build price (0 - current)", 0m, 0m, 1000000m, 1m, "Base");
            _autoBuildRegime = CreateParameter("Auto build", "Off",
                new[] { "Off", "Daily", "Weekly", "Monthly", "Quarterly" }, "Base");
            _autoBuildTime = CreateParameterTimeOfDay("Auto build time", 10, 5, 0, 0, "Base");
            _testerForcedClose = CreateParameter("Tester forced close", "Off",
                new[] { "Off", "Day", "Week", "Month" }, "Base");

            _linesCount = CreateParameter("Lines count per side", 10, 1, 50, 1, "Grid");
            _valueType = CreateParameter("Step and profit type", "Points", new[] { "Points", "Percent" }, "Grid");
            _firstStep = CreateParameter("First step", 100m, 10m, 1000m, 10m, "Grid");
            _stepMultiplier = CreateParameter("Step multiplier", 1.2m, 1m, 2m, 0.1m, "Grid");
            _takeProfit = CreateParameter("Take profit", 100m, 10m, 1000m, 10m, "Grid");
            _volume = CreateParameter("Volume per line", 1m, 1m, 10m, 1m, "Grid");
            _maxEntryOrders = CreateParameter("Max entry orders per side", 0, 0, 50, 1, "Grid");
            _maxPositions = CreateParameter("Max positions per side", 0, 0, 200, 5, "Grid");

            _appliedRegime = _regime.ValueString;

            _tab.Connector.TestStartEvent += Connector_TestStartEvent;
            ParametrsChangeByUser += SDKKorovinCoveredIntraday_ParametrsChangeByUser;

            if (startProgram == StartProgram.IsOsTrader)
            {
                Thread worker = new Thread(WorkerThreadPlace);
                worker.Name = "SDKKorovinCoveredIntraday." + name;
                worker.IsBackground = true;
                worker.Start();
            }
            else
            {
                // в тестере и оптимизаторе время течёт только вместе с данными
                _tab.ServerTimeChangeEvent += Tab_ServerTimeChangeEvent;
            }

            DeleteEvent += SDKKorovinCoveredIntraday_DeleteEvent;

            Description = "Интрадей-часть стратегии Ильи Коровина «Прикрытый интрадей». " +
                "По кнопке от заданной или текущей цены строятся две сетки лимиток: на покупку ниже цены и на продажу выше. " +
                "Шаг растёт геометрически, тейк и объём у всех линий одинаковы, стопов нет. " +
                "После тейка линия снова встаёт на вход по той же цене. " +
                "Повторное построение переносит открытые позиции без задвоения. " +
                "Опционное прикрытие ведётся вручную.";
        }

        public override string GetNameStrategyType()
        {
            return "SDKKorovinCoveredIntraday";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        #endregion

        #region Event handlers

        private void BuildButton_UserClickOnButtonEvent()
        {
            try
            {
                _buildRequested = true;
                string priceText = _buildPrice.ValueDecimal > 0
                    ? "от цены " + _buildPrice.ValueDecimal
                    : "от текущей цены";

                SendLog("Запрошено построение сеток " + priceText, LogMessageType.User);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SDKKorovinCoveredIntraday_ParametrsChangeByUser()
        {
            // только флаг: основной цикл держит _pumpLocker и при удалении сетки
            // может ждать UI-поток, так что брать блокировку из UI-потока нельзя
            _entryOrdersLimitChanged = true;
        }

        private void Tab_ServerTimeChangeEvent(DateTime time)
        {
            try
            {
                Pump();
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

                    Pump();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        private void Connector_TestStartEvent()
        {
            try
            {
                lock (_pumpLocker)
                {
                    _stage = RebuildStage.None;
                    _buildRequested = false;
                    _lastAutoBuildDate = DateTime.MinValue;
                    _lastBuildServerTime = DateTime.MinValue;
                    _requestedAnchorPrice = 0;
                    _appliedRegime = _regime.ValueString;
                    _gridsToActivate.Clear();

                    if (_tab.GridsMaster == null)
                    {
                        return;
                    }

                    TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

                    for (int i = 0; i < grids.Length; i++)
                    {
                        _tab.GridsMaster.DeleteAtNum(grids[i].Number);
                    }
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SDKKorovinCoveredIntraday_DeleteEvent()
        {
            _isDeleted = true;
        }

        #endregion

        #region Main loop

        private void Pump()
        {
            if (_isDeleted)
            {
                return;
            }

            if (_tab.GridsMaster == null)
            {
                return;
            }

            lock (_pumpLocker)
            {
                if (_stage == RebuildStage.WaitForcedClose)
                {
                    TryFinishForcedClose();
                    return;
                }

                if (_stage == RebuildStage.WaitEntryCancel)
                {
                    TryFinishEntryCancel();
                    return;
                }

                if (_stage == RebuildStage.WaitActivation)
                {
                    TryActivateNewGrids();
                    return;
                }

                if (_appliedRegime != _regime.ValueString)
                {
                    ApplyRegime(_regime.ValueString);
                    return;
                }

                if (_buildRequested)
                {
                    _buildRequested = false;

                    if (_regime.ValueString != "On")
                    {
                        SendLog("Regime = " + _regime.ValueString + ": построение сеток запрещено. " +
                            "Включите Regime = On", LogMessageType.Error);
                        return;
                    }

                    TryStartRebuild(_buildPrice.ValueDecimal);
                    return;
                }

                if (_regime.ValueString == "On"
                    && NeedAutoBuild())
                {
                    // авто-построение всегда от текущей цены
                    if (TryStartRebuild(0))
                    {
                        _lastAutoBuildDate = _tab.TimeServerCurrent.Date;
                    }
                    return;
                }

                if (_entryOrdersLimitChanged)
                {
                    _entryOrdersLimitChanged = false;
                    ApplyEntryOrdersLimit();
                }

                TryDeleteEmptyOldGrids();
            }
        }

        private bool NeedAutoBuild()
        {
            if (_autoBuildRegime.ValueString == "Off")
            {
                return false;
            }

            DateTime time = _tab.TimeServerCurrent;

            if (time == DateTime.MinValue)
            {
                return false;
            }

            DateTime periodStart = GetAutoBuildPeriodStart(time);

            // в этом дне / неделе / месяце / квартале уже строились
            if (_lastAutoBuildDate != DateTime.MinValue
                && GetAutoBuildPeriodStart(_lastAutoBuildDate) == periodStart)
            {
                return false;
            }

            // перезапуск терминала при уже работающих сетках: построение этого периода
            // уже было, если сейчас позже времени построения или идёт не первый день периода.
            // Повторно сетки не сдвигаем
            if (StartProgram == StartProgram.IsOsTrader
                && _lastAutoBuildDate == DateTime.MinValue
                && _tab.GridsMaster.TradeGrids.Count != 0
                && (time.TimeOfDay >= _autoBuildTime.TimeSpan
                    || periodStart != time.Date))
            {
                _lastAutoBuildDate = time.Date;
                SendLog("Сетки уже работают, авто-построение в текущем периоде пропущено", LogMessageType.System);
                return false;
            }

            if (time.TimeOfDay < _autoBuildTime.TimeSpan)
            {
                return false;
            }

            // строимся только от цены текущего дня, а не от вчерашнего закрытия
            List<Candle> candles = _tab.CandlesAll;

            if (candles == null
                || candles.Count == 0
                || candles[candles.Count - 1].TimeStart.Date != time.Date)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Начало периода авто-построения: день, неделя (с понедельника), месяц или квартал.
        /// Сетки строятся в первый торговый день периода, когда время дойдёт до Auto build time
        /// </summary>
        private DateTime GetAutoBuildPeriodStart(DateTime time)
        {
            string regime = _autoBuildRegime.ValueString;

            if (regime == "Weekly")
            {
                return GetWeekStart(time);
            }

            if (regime == "Monthly")
            {
                return new DateTime(time.Year, time.Month, 1);
            }

            if (regime == "Quarterly")
            {
                int firstMonthOfQuarter = (time.Month - 1) / 3 * 3 + 1;
                return new DateTime(time.Year, firstMonthOfQuarter, 1);
            }

            return time.Date;
        }

        #endregion

        #region Regime

        /// <summary>
        /// Режим рабочей сетки для режима робота:
        /// On - торговля, CloseOnly - входы сняты, тейки стоят, Off - сняты все заявки
        /// </summary>
        private TradeGridRegime GetWorkingGridRegime(string robotRegime)
        {
            if (robotRegime == "On")
            {
                return TradeGridRegime.On;
            }

            if (robotRegime == "CloseOnly")
            {
                return TradeGridRegime.CloseOnly;
            }

            return TradeGridRegime.OffAndCancelOrders;
        }

        /// <summary>
        /// Переключает сетки при смене режима робота. Рабочие сетки получают режим робота.
        /// Старые сетки (дорабатывают перенесённые позиции) в Off снимают тейки,
        /// а в On и CloseOnly возвращаются в CloseOnly - новых входов у них нет никогда.
        /// Сетки в CloseForced не трогаем: они закрываются
        /// </summary>
        private void ApplyRegime(string robotRegime)
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            int newestBuy;
            int newestSell;
            GetWorkingGridNumbers(grids, out newestBuy, out newestSell);

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid)
                    || grid.Regime == TradeGridRegime.CloseForced)
                {
                    continue;
                }

                TradeGridRegime newRegime;

                if (grid.Number == newestBuy
                    || grid.Number == newestSell)
                {
                    newRegime = GetWorkingGridRegime(robotRegime);
                }
                else if (robotRegime == "Off")
                {
                    newRegime = TradeGridRegime.OffAndCancelOrders;
                }
                else if (grid.Regime == TradeGridRegime.OffAndCancelOrders)
                {
                    newRegime = TradeGridRegime.CloseOnly;
                }
                else
                {
                    continue;
                }

                if (grid.Regime != newRegime)
                {
                    grid.Regime = newRegime;
                    grid.Save();
                }
            }

            SendLog("Regime: " + _appliedRegime + " -> " + robotRegime, LogMessageType.System);
            _appliedRegime = robotRegime;
        }

        #endregion

        #region Tester forced close

        /// <summary>
        /// Режим только для тестера и оптимизатора: при построении, если с прошлого построения
        /// начался новый день / неделя / месяц, все позиции закрываются по рынку,
        /// и сетки строятся с чистого листа. В реале параметр игнорируется
        /// </summary>
        private bool NeedTesterForcedClose(DateTime previousBuildTime, DateTime currentBuildTime)
        {
            string mode = _testerForcedClose.ValueString;

            if (mode == "Off")
            {
                return false;
            }

            if (StartProgram != StartProgram.IsTester
                && StartProgram != StartProgram.IsOsOptimizer)
            {
                SendLog("Tester forced close = " + mode + " работает только в тестере и оптимизаторе. " +
                    "В реале параметр игнорируется", LogMessageType.System);
                return false;
            }

            if (previousBuildTime == DateTime.MinValue
                || currentBuildTime == DateTime.MinValue)
            {
                return false;
            }

            if (mode == "Day")
            {
                return previousBuildTime.Date != currentBuildTime.Date;
            }

            if (mode == "Week")
            {
                return GetWeekStart(previousBuildTime) != GetWeekStart(currentBuildTime);
            }

            if (mode == "Month")
            {
                return previousBuildTime.Year != currentBuildTime.Year
                    || previousBuildTime.Month != currentBuildTime.Month;
            }

            return false;
        }

        private DateTime GetWeekStart(DateTime time)
        {
            // неделя начинается с понедельника
            int daysFromMonday = ((int)time.DayOfWeek + 6) % 7;
            return time.Date.AddDays(-daysFromMonday);
        }

        private void StartForcedClose()
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid))
                {
                    continue;
                }

                // в тестере сетка в CloseForced снимает тейки, закрывает позиции по рынку
                // и сама переходит в Off
                grid.CloseForcedRegimeOrderType = OrderPriceType.Market;

                if (grid.Regime != TradeGridRegime.CloseForced)
                {
                    grid.Regime = TradeGridRegime.CloseForced;
                    grid.Save();
                }
            }

            _stage = RebuildStage.WaitForcedClose;
            _stageStartTime = DateTime.Now;

            SendLog("Форсированное закрытие (" + _testerForcedClose.ValueString + "): " +
                "все позиции закрываются по рынку перед построением", LogMessageType.Signal);
        }

        private void TryFinishForcedClose()
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            bool waiting = false;

            for (int i = 0; i < grids.Length; i++)
            {
                if (IsGridDeleted(grids[i]) == false
                    && IsGridAlive(grids[i]))
                {
                    waiting = true;
                    break;
                }
            }

            if (waiting == false)
            {
                // сетки отработали - удаляем, новые построятся с чистого листа
                for (int i = 0; i < grids.Length; i++)
                {
                    if (IsGridDeleted(grids[i]) == false)
                    {
                        _tab.GridsMaster.DeleteAtNum(grids[i].Number);
                    }
                }

                // позиции вне сеток (ручные, потерянные сеткой) тоже закрываем
                List<Position> openPositions = _tab.PositionsOpenAll;

                for (int i = 0; openPositions != null && i < openPositions.Count; i++)
                {
                    Position position = openPositions[i];

                    if (position.CloseActive)
                    {
                        waiting = true;
                        continue;
                    }

                    if (position.OpenActive)
                    {
                        _tab.CloseAllOrderToPosition(position);
                        waiting = true;
                        continue;
                    }

                    if (position.OpenVolume != 0)
                    {
                        _tab.CloseAtMarket(position, position.OpenVolume);
                        waiting = true;
                    }
                }
            }

            if (waiting)
            {
                if (_stageStartTime.AddSeconds(EntryCancelTimeoutSeconds) < DateTime.Now)
                {
                    _stage = RebuildStage.None;
                    SendLog("Позиции не закрылись за " + EntryCancelTimeoutSeconds + " сек. " +
                        "Форсированное закрытие и построение отменены", LogMessageType.Error);
                }

                return;
            }

            SendLog("Форсированное закрытие завершено, позиций нет", LogMessageType.System);

            // дальше - обычное построение. Сеток нет, ждать снятия входов не нужно
            _stage = RebuildStage.WaitEntryCancel;
            _stageStartTime = DateTime.Now;
        }

        #endregion

        #region Rebuild

        /// <param name="requestedPrice">цена, от которой строить сетки. 0 - от текущей цены</param>
        private bool TryStartRebuild(decimal requestedPrice)
        {
            if (CheckSettings() == false)
            {
                return false;
            }

            if (requestedPrice < 0)
            {
                SendLog("Цена построения отрицательная, сетки не построены", LogMessageType.Error);
                return false;
            }

            if (requestedPrice == 0
                && GetAnchorPrice() <= 0)
            {
                SendLog("Нет текущей цены, сетки не построены", LogMessageType.Error);
                return false;
            }

            _requestedAnchorPrice = 0;

            if (requestedPrice > 0)
            {
                _requestedAnchorPrice = RoundToPriceStep(requestedPrice, _tab.Security.PriceStep);
            }

            DateTime previousBuildTime = _lastBuildServerTime;
            _lastBuildServerTime = _tab.TimeServerCurrent;

            if (NeedTesterForcedClose(previousBuildTime, _lastBuildServerTime))
            {
                StartForcedClose();
                return true;
            }

            // сетки, которые ещё работают, переводим в CloseOnly: заявки на вход снимаются,
            // тейки по открытым позициям остаются. Сетки в CloseForced не трогаем - они закрываются
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid)
                    || grid.Regime == TradeGridRegime.CloseForced)
                {
                    continue;
                }

                if (grid.Regime != TradeGridRegime.CloseOnly)
                {
                    grid.Regime = TradeGridRegime.CloseOnly;
                    grid.Save();
                }
            }

            _stage = RebuildStage.WaitEntryCancel;
            _stageStartTime = DateTime.Now;

            return true;
        }

        private void TryFinishEntryCancel()
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            bool haveEntryOrders = false;

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid)
                    || grid.Regime == TradeGridRegime.CloseForced)
                {
                    continue;
                }

                AttachPositionsFromJournal(grid);

                List<TradeGridLine> lines = grid.GridCreator.Lines;

                for (int j = 0; j < lines.Count; j++)
                {
                    if (lines[j].Position != null
                        && lines[j].Position.OpenActive)
                    {
                        haveEntryOrders = true;
                        break;
                    }
                }
            }

            if (haveEntryOrders)
            {
                if (_stageStartTime.AddSeconds(EntryCancelTimeoutSeconds) < DateTime.Now)
                {
                    _stage = RebuildStage.None;
                    SendLog("Заявки на вход не сняты за " + EntryCancelTimeoutSeconds + " сек. " +
                        "Построение отменено, сетки оставлены в режиме CloseOnly. Повторите построение",
                        LogMessageType.Error);
                }

                return;
            }

            decimal anchorPrice = _requestedAnchorPrice;

            if (anchorPrice <= 0)
            {
                anchorPrice = GetAnchorPrice();
            }

            if (anchorPrice <= 0)
            {
                _stage = RebuildStage.None;
                SendLog("Нет текущей цены, построение отменено. Сетки оставлены в режиме CloseOnly",
                    LogMessageType.Error);
                return;
            }

            _gridsToActivate.Clear();

            // этап переключаем до сборки: если сборка упадёт посередине, уже созданные сетки
            // с перенесёнными позициями всё равно включатся, а построение не начнётся заново
            // на каждом тике, плодя сетки
            _stage = RebuildStage.WaitActivation;
            _stageStartTime = DateTime.Now;

            string buyReport = BuildSide(Side.Buy, anchorPrice, grids);
            string sellReport = BuildSide(Side.Sell, anchorPrice, grids);

            string priceSource = _requestedAnchorPrice > 0 ? " (задана вручную)" : " (текущая)";

            SendLog("Сетки построены от цены " + anchorPrice + priceSource + ". " + buyReport + ". " + sellReport,
                LogMessageType.Signal);
        }

        private void TryActivateNewGrids()
        {
            if (StartProgram == StartProgram.IsOsTrader
                && _stageStartTime.AddMilliseconds(ActivationDelayMilliseconds) > DateTime.Now)
            {
                return;
            }

            for (int i = 0; i < _gridsToActivate.Count; i++)
            {
                if (IsGridDeleted(_gridsToActivate[i]))
                {
                    continue;
                }

                // режим робота могли сменить во время пересборки: новые сетки включаются
                // в уже применённом режиме, а смену следующий проход применит ко всем сеткам
                _gridsToActivate[i].Regime = GetWorkingGridRegime(_appliedRegime);
                _gridsToActivate[i].Save();
            }

            _gridsToActivate.Clear();
            _stage = RebuildStage.None;
        }

        private string BuildSide(Side side, decimal anchorPrice, TradeGrid[] oldGrids)
        {
            // 1 позиции, которые надо перенести. Берём из всех прежних сеток этой стороны

            List<TradeGrid> oldSideGrids = new List<TradeGrid>();
            List<TradeGridLine> carriedLines = new List<TradeGridLine>();

            for (int i = 0; i < oldGrids.Length; i++)
            {
                TradeGrid grid = oldGrids[i];

                // снимок сеток общий для обеих сторон: пустые старые сетки Buy
                // к моменту сборки Sell уже удалены
                if (IsGridDeleted(grid)
                    || grid.Regime == TradeGridRegime.CloseForced
                    || grid.GridCreator.GridSide != side)
                {
                    continue;
                }

                oldSideGrids.Add(grid);

                List<TradeGridLine> lines = grid.GridCreator.Lines;

                for (int j = 0; j < lines.Count; j++)
                {
                    if (lines[j].Position != null
                        && lines[j].Position.OpenVolume != 0)
                    {
                        carriedLines.Add(lines[j]);
                    }
                }
            }

            // 2 новая сетка. Создаётся выключенной, включается после паузы

            TradeGrid newGrid = CreateGrid(side, anchorPrice);
            List<TradeGridLine> slots = newGrid.GridCreator.Lines;

            // 3 проверка на задвоение: позиция, попавшая в ячейку новой линии, занимает её место

            TradeGridLine[] occupants = new TradeGridLine[slots.Count];

            for (int i = 0; i < carriedLines.Count; i++)
            {
                TradeGridLine carried = carriedLines[i];

                int slotIndex = FindSlot(slots, carried.PriceEnter, anchorPrice, side);

                if (slotIndex < 0)
                {
                    continue;
                }

                TradeGridLine current = occupants[slotIndex];

                if (current == null
                    || Math.Abs(carried.PriceEnter - slots[slotIndex].PriceEnter)
                    < Math.Abs(current.PriceEnter - slots[slotIndex].PriceEnter))
                {
                    occupants[slotIndex] = carried;
                }
            }

            // 4 лимит позиций стороны. Сетка - это «число линий» ячеек: занятые перенесёнными
            // позициями плюс свободные. Свободных уровней при этом не больше, чем общий предел
            // позиций стороны минус уже открытые (и перенесённые, и оставшиеся в старых сетках).
            // Иначе каждое пересоздание добавляет полный набор входов, и при движении
            // в одну сторону позиция растёт без предела. Лишние уровни срезаются с дальнего края

            int freeSlotsAllowed = Math.Max(0, GetMaxPositions() - carriedLines.Count);
            int freeSlotsUsed = 0;

            List<TradeGridLine> newLines = new List<TradeGridLine>();
            HashSet<TradeGridLine> movedLines = new HashSet<TradeGridLine>();

            for (int i = 0; i < slots.Count; i++)
            {
                if (occupants[i] != null)
                {
                    newLines.Add(occupants[i]);
                    movedLines.Add(occupants[i]);
                }
                else if (freeSlotsUsed < freeSlotsAllowed)
                {
                    newLines.Add(slots[i]);
                    freeSlotsUsed++;
                }
            }

            // списки линий подменяем целиком, а не правим на месте:
            // рабочий поток сетки в этот момент может перебирать прежний список
            newGrid.GridCreator.Lines = newLines;
            newGrid.MaxOpenOrdersInMarket = GetMaxEntryOrders(newLines.Count);
            newGrid.MaxCloseOrdersInMarket = newLines.Count;
            newGrid.Save();
            newGrid.FullRePaintGrid();

            _gridsToActivate.Add(newGrid);

            // 5 прежние сетки: оставляем только неперенесённые позиции, пустые удаляем

            int leftInOldGrids = 0;

            for (int i = 0; i < oldSideGrids.Count; i++)
            {
                TradeGrid oldGrid = oldSideGrids[i];
                List<TradeGridLine> oldLines = oldGrid.GridCreator.Lines;
                List<TradeGridLine> keepLines = new List<TradeGridLine>();

                for (int j = 0; j < oldLines.Count; j++)
                {
                    if (movedLines.Contains(oldLines[j]) == false
                        && IsLineAlive(oldLines[j]))
                    {
                        keepLines.Add(oldLines[j]);
                    }
                }

                if (keepLines.Count == 0)
                {
                    oldGrid.GridCreator.Lines = keepLines;
                    _tab.GridsMaster.DeleteAtNum(oldGrid.Number);
                    continue;
                }

                oldGrid.GridCreator.Lines = keepLines;
                oldGrid.Save();
                oldGrid.FullRePaintGrid();
                leftInOldGrids += keepLines.Count;
            }

            return side + ": открытых позиций " + carriedLines.Count + " из " + GetMaxPositions()
                + ", новых уровней " + freeSlotsUsed
                + ", линий в сетке " + newLines.Count
                + ", перенесено позиций " + movedLines.Count
                + ", дорабатывают в старых сетках " + leftInOldGrids;
        }

        private TradeGrid CreateGrid(Side side, decimal anchorPrice)
        {
            decimal priceStep = _tab.Security.PriceStep;
            decimal step = _firstStep.ValueDecimal;
            decimal profit = _takeProfit.ValueDecimal;

            if (_valueType.ValueString == "Percent")
            {
                step = anchorPrice * step / 100;
                profit = anchorPrice * profit / 100;
            }

            step = RoundToPriceStep(step, priceStep);
            profit = RoundToPriceStep(profit, priceStep);

            TradeGrid grid = _tab.GridsMaster.CreateNewTradeGrid();
            grid.GridType = TradeGridPrimeType.MarketMaking;
            grid.OpenOrdersMakerOnly = true;
            grid.MaxDistanceToOrdersPercent = 0;

            TradeGridCreator creator = grid.GridCreator;
            creator.GridSide = side;
            creator.FirstPrice = side == Side.Buy ? anchorPrice - step : anchorPrice + step;
            creator.LineCountStart = _linesCount.ValueInt;
            creator.TypeStep = TradeGridValueType.Absolute;
            creator.LineStep = step;
            creator.StepMultiplicator = _stepMultiplier.ValueDecimal;
            creator.TypeProfit = TradeGridValueType.Absolute;
            creator.ProfitStep = profit;
            creator.ProfitMultiplicator = 1;
            creator.TypeVolume = TradeGridVolumeType.Contracts;
            creator.StartVolume = _volume.ValueDecimal;
            creator.MartingaleMultiplicator = 1;
            creator.CreateNewGrid(_tab, TradeGridPrimeType.MarketMaking);

            return grid;
        }

        /// <summary>
        /// Индекс линии новой сетки, в ячейку которой попадает цена входа перенесённой позиции, или -1.
        /// Ячейка - от середины до соседней линии с одной стороны до середины с другой.
        /// Внутренняя граница первой линии - середина до текущей цены,
        /// внешняя граница последней - половина последнего шага за ней.
        /// </summary>
        private int FindSlot(List<TradeGridLine> slots, decimal price, decimal anchorPrice, Side side)
        {
            if (slots.Count == 0)
            {
                return -1;
            }

            // расстояние от текущей цены вглубь сетки: у Buy вниз, у Sell вверх
            decimal distance = DistanceFromAnchor(price, anchorPrice, side);
            decimal firstDistance = DistanceFromAnchor(slots[0].PriceEnter, anchorPrice, side);
            decimal lastDistance = DistanceFromAnchor(slots[slots.Count - 1].PriceEnter, anchorPrice, side);

            decimal lastHalfGap = firstDistance / 2;

            if (slots.Count > 1)
            {
                lastHalfGap = (lastDistance
                    - DistanceFromAnchor(slots[slots.Count - 2].PriceEnter, anchorPrice, side)) / 2;
            }

            if (distance <= firstDistance / 2
                || distance > lastDistance + lastHalfGap)
            {
                return -1;
            }

            int result = 0;
            decimal bestGap = decimal.MaxValue;

            for (int i = 0; i < slots.Count; i++)
            {
                decimal gap = Math.Abs(distance - DistanceFromAnchor(slots[i].PriceEnter, anchorPrice, side));

                if (gap < bestGap)
                {
                    bestGap = gap;
                    result = i;
                }
            }

            return result;
        }

        private decimal DistanceFromAnchor(decimal price, decimal anchorPrice, Side side)
        {
            if (side == Side.Buy)
            {
                return anchorPrice - price;
            }

            return price - anchorPrice;
        }

        #endregion

        #region Old grids cleanup

        private void TryDeleteEmptyOldGrids()
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            if (grids.Length <= 1)
            {
                return;
            }

            int newestBuy;
            int newestSell;
            GetWorkingGridNumbers(grids, out newestBuy, out newestSell);

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid)
                    || grid.Number == newestBuy
                    || grid.Number == newestSell)
                {
                    continue;
                }

                // старую сетку, которую трейдер вручную снова включил, не трогаем
                if (grid.Regime == TradeGridRegime.On)
                {
                    continue;
                }

                if (IsGridAlive(grid))
                {
                    continue;
                }

                _tab.GridsMaster.DeleteAtNum(grid.Number);
                SendLog("Старая сетка " + grid.Number + " отработала все позиции и удалена", LogMessageType.System);
            }
        }

        /// <summary>
        /// Рабочая сетка стороны - самая новая. Остальные дорабатывают перенесённые позиции.
        /// Номер -1 - сетки этой стороны нет
        /// </summary>
        private void GetWorkingGridNumbers(TradeGrid[] grids, out int newestBuy, out int newestSell)
        {
            newestBuy = -1;
            newestSell = -1;

            for (int i = 0; i < grids.Length; i++)
            {
                if (IsGridDeleted(grids[i]))
                {
                    continue;
                }

                if (grids[i].GridCreator.GridSide == Side.Buy)
                {
                    newestBuy = Math.Max(newestBuy, grids[i].Number);
                }
                else
                {
                    newestSell = Math.Max(newestSell, grids[i].Number);
                }
            }
        }

        #endregion

        #region Limits

        /// <summary>
        /// Предел открытых позиций стороны по всем сеткам. 0 - равен числу линий
        /// </summary>
        private int GetMaxPositions()
        {
            if (_maxPositions.ValueInt <= 0)
            {
                return _linesCount.ValueInt;
            }

            return _maxPositions.ValueInt;
        }

        /// <summary>
        /// Сколько заявок на вход сетка держит на бирже. Сетка ставит их на ближайшие
        /// к цене свободные линии и после исполнения сдвигает окно к следующей линии
        /// </summary>
        private int GetMaxEntryOrders(int linesCount)
        {
            if (_maxEntryOrders.ValueInt <= 0)
            {
                return linesCount;
            }

            return _maxEntryOrders.ValueInt;
        }

        /// <summary>
        /// Новый лимит действует на рабочие сетки сразу, без перестроения.
        /// Лишние заявки сетка снимает сама, недостающие выставляет
        /// </summary>
        private void ApplyEntryOrdersLimit()
        {
            TradeGrid[] grids = _tab.GridsMaster.TradeGrids.ToArray();

            int newestBuy;
            int newestSell;
            GetWorkingGridNumbers(grids, out newestBuy, out newestSell);

            for (int i = 0; i < grids.Length; i++)
            {
                TradeGrid grid = grids[i];

                if (IsGridDeleted(grid)
                    || (grid.Number != newestBuy && grid.Number != newestSell))
                {
                    continue;
                }

                int limit = GetMaxEntryOrders(grid.GridCreator.Lines.Count);

                if (grid.MaxOpenOrdersInMarket == limit)
                {
                    continue;
                }

                grid.MaxOpenOrdersInMarket = limit;
                grid.Save();
                grid.RePaintGrid();
                SendLog("Сетка " + grid.Number + ": заявок на вход в рынке не больше " + limit, LogMessageType.System);
            }
        }

        #endregion

        #region Grid state

        /// <summary>
        /// TradeGrid.Delete обнуляет вложенные объекты, а снимок списка сеток
        /// ещё может держать ссылку на удалённую сетку
        /// </summary>
        private bool IsGridDeleted(TradeGrid grid)
        {
            return grid == null
                || grid.GridCreator == null;
        }

        private bool IsGridAlive(TradeGrid grid)
        {
            List<TradeGridLine> lines = grid.GridCreator.Lines;

            for (int i = 0; i < lines.Count; i++)
            {
                if (IsLineAlive(lines[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsLineAlive(TradeGridLine line)
        {
            Position position = line.Position;

            if (position == null)
            {
                // после перезапуска номер позиции уже загружен, а сама позиция
                // ещё не найдена сеткой в журнале. Считаем линию живой
                return line.PositionNum > 0;
            }

            return position.OpenVolume != 0
                || position.OpenActive
                || position.CloseActive;
        }

        /// <summary>
        /// Сетка находит свои позиции в журнале только в рабочих режимах.
        /// Перед переносом делаем это сами, иначе позиция выключенной сетки потеряется.
        /// </summary>
        private void AttachPositionsFromJournal(TradeGrid grid)
        {
            List<TradeGridLine> lines = grid.GridCreator.Lines;
            List<Position> positions = null;

            for (int i = 0; i < lines.Count; i++)
            {
                TradeGridLine line = lines[i];

                if (line.Position != null
                    || line.PositionNum <= 0)
                {
                    continue;
                }

                if (positions == null)
                {
                    positions = _tab.PositionsAll;
                }

                for (int j = 0; positions != null && j < positions.Count; j++)
                {
                    if (positions[j].Number == line.PositionNum)
                    {
                        line.Position = positions[j];
                        break;
                    }
                }
            }
        }

        #endregion

        #region Helpers

        private bool CheckSettings()
        {
            if (_tab.Security == null
                || _tab.Security.PriceStep <= 0)
            {
                SendLog("Инструмент не подключён, сетки не построены", LogMessageType.Error);
                return false;
            }

            if (StartProgram == StartProgram.IsOsTrader
                && (_tab.IsConnected == false || _tab.IsReadyToTrade == false))
            {
                SendLog("Нет подключения к бирже, сетки не построены", LogMessageType.Error);
                return false;
            }

            if (_linesCount.ValueInt <= 0
                || _firstStep.ValueDecimal <= 0
                || _takeProfit.ValueDecimal <= 0
                || _volume.ValueDecimal <= 0)
            {
                SendLog("Число линий, шаг, тейк и объём должны быть больше нуля, сетки не построены",
                    LogMessageType.Error);
                return false;
            }

            if (_stepMultiplier.ValueDecimal < 1)
            {
                SendLog("Множитель шага меньше 1: шаг должен расти от линии к линии, сетки не построены",
                    LogMessageType.Error);
                return false;
            }

            return true;
        }

        private decimal GetAnchorPrice()
        {
            List<Candle> candles = _tab.CandlesAll;

            if (candles == null
                || candles.Count == 0)
            {
                return 0;
            }

            return candles[candles.Count - 1].Close;
        }

        private decimal RoundToPriceStep(decimal value, decimal priceStep)
        {
            decimal result = Math.Round(value / priceStep, MidpointRounding.AwayFromZero) * priceStep;

            if (result < priceStep)
            {
                result = priceStep;
            }

            return result;
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

        private enum RebuildStage
        {
            None,
            WaitForcedClose,
            WaitEntryCancel,
            WaitActivation
        }
    }
}
