/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.Market.Servers.Optimizer;
using OsEngine.Market.Servers.Tester;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Wiki;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Ребалансировочный робот портфеля по принципам Ильи Коровина.
    /// Три класса активов: акции из скринера, золото и денежная позиция.
    /// Робот приводит фактические веса к целевым, то есть продаёт подорожавшее
    /// и покупает подешевевшее. Поверх этого работает лестница состояний рынка:
    /// чем глубже уже случившаяся просадка индекса, тем выше целевая доля акций.
    /// Только лонг, без плеча, без стопов, без прогнозов.
    ///
    /// Табы: Screener_stocks, GOLD, LQDT, Index. Все на одном интрадей таймфрейме,
    /// рекомендуется час. Описание параметров - в SDKKorovinPortfolio.md.
    /// </summary>
    [Bot("SDKKorovinPortfolio")]
    public class SDKKorovinPortfolio : BotPanel
    {
        #region Constructor and fields

        private BotTabScreener _tabStocks;

        private BotTabSimple _tabGold;

        private BotTabSimple _tabLqdt;

        private BotTabIndex _tabIndex;

        // Base
        private StrategyParameterString _regime;
        private StrategyParameterString _schedule;
        private StrategyParameterInt _scheduleDay;
        private StrategyParameterTimeOfDay _rebalanceTime;
        private StrategyParameterInt _intervalDays;
        private StrategyParameterInt _ladderMinDays;
        private StrategyParameterString _warmupPolicy;
        private StrategyParameterInt _warmupMinDays;

        // Allocation
        private StrategyParameterDecimal _stocksBasePercent;
        private StrategyParameterDecimal _goldBasePercent;
        private StrategyParameterDecimal _cashBasePercent;
        private StrategyParameterDecimal _stocksMaxPercent;
        private StrategyParameterDecimal _goldMinPercent;
        private StrategyParameterDecimal _cashMinPercent;
        private StrategyParameterDecimal _stocksMinPercent;
        private StrategyParameterString _reserveOrder;

        // Market
        private StrategyParameterDecimal _ddStartPercent;
        private StrategyParameterDecimal _ddFullPercent;
        private StrategyParameterDecimal _depthCurve;
        private StrategyParameterDecimal _speedStartPercent;
        private StrategyParameterDecimal _speedFullPercent;
        private StrategyParameterDecimal _speedWeight;
        private StrategyParameterDecimal _euphoriaDevStartPercent;
        private StrategyParameterDecimal _euphoriaDevFullPercent;
        private StrategyParameterDecimal _euphoriaWeight;
        private StrategyParameterDecimal _stepUp;
        private StrategyParameterDecimal _stepDown;
        private StrategyParameterDecimal _minKStep;

        private StrategyParameterString _indexDividendAdjust;
        private StrategyParameterDecimal _peakDecayPercentPerYear;
        private StrategyParameterInt _longSmaPeriod;
        private StrategyParameterDecimal _hysteresis;
        private StrategyParameterInt _exitDelayDays;
        private StrategyParameterDecimal _exitRecoveryPercent;
        private StrategyParameterInt _staleDays;
        private StrategyParameterString _staleAction;
        private StrategyParameterInt _speedWindow;
        private StrategyParameterInt _speedHoldDays;

        // Stocks
        private StrategyParameterString _stockTilt;
        private StrategyParameterInt _tiltLookback;
        private StrategyParameterDecimal _tiltStrength;
        private StrategyParameterDecimal _tiltMin;
        private StrategyParameterDecimal _tiltMax;
        private StrategyParameterString _idioGuard;
        private StrategyParameterInt _idioLookback;
        private StrategyParameterDecimal _idioThresholdPercent;

        // Execution
        private StrategyParameterString _reopenMode;
        private StrategyParameterString _portfolioValueMode;
        private StrategyParameterDecimal _bandAbsPercent;
        private StrategyParameterDecimal _bandRelPercent;
        private StrategyParameterDecimal _hardBandMult;
        private StrategyParameterDecimal _minTradeMoney;
        private StrategyParameterString _tradeTo;
        private StrategyParameterDecimal _cashInflowTriggerPercent;
        private StrategyParameterInt _pendingBuysTtlBars;
        private StrategyParameterInt _staleBarsLimit;

        private StrategyParameterInt _tradeToleranceBars;
        private StrategyParameterInt _syncToleranceBars;
        private StrategyParameterDecimal _dividendTaxPercent;
        private StrategyParameterInt _dividendHoldDays;

        private StrategyParameterString _dividendGapNeutral;
        private StrategyParameterString _depthCheck;
        private StrategyParameterDecimal _maxSlippagePercent;
        private StrategyParameterDecimal _cashBufferPercent;
        private StrategyParameterString _freezeNearRecordDate;
        private StrategyParameterInt _freezeDays;

        private KorovinDailySeries _indexSeries = new KorovinDailySeries();

        private Dictionary<string, KorovinDailySeries> _securitySeries
            = new Dictionary<string, KorovinDailySeries>();

        private List<KorovinDailySeries> _basketSeries = new List<KorovinDailySeries>();

        private List<string> _basketTickers = new List<string>();

        private KorovinTotalReturnIndex _totalReturnIndex = new KorovinTotalReturnIndex();

        private KorovinMarketLadder _ladder = new KorovinMarketLadder();

        private KorovinDividendCache _dividends = new KorovinDividendCache();

        /// <summary>
        /// Прочиталась ли база дивидендов по всей корзине на этом баре. Когда нет,
        /// ряд total return не достраивается: дивидендный гэп впечатался бы в него навсегда
        /// </summary>
        private bool _dividendsReliable = true;

        /// <summary>
        /// Когда последний раз сообщали о придержанном ряде
        /// </summary>
        private DateTime _dividendsHoldReported = DateTime.MinValue;

        private DateTime _formulaGapSince = DateTime.MinValue;

        private bool _formulaGapReported;

        /// <summary>
        /// Когда последний раз сообщали о недоступном стакане
        /// </summary>
        private DateTime _depthProblemReported = DateTime.MinValue;

        /// <summary>
        /// Когда последний раз сообщали о неудачной записи кэша истории индекса
        /// </summary>
        private DateTime _indexCacheErrorDate = DateTime.MinValue;

        /// <summary>
        /// Кэш истории индекса не пишется прямо сейчас
        /// </summary>
        private bool _indexCacheBroken;

        /// <summary>
        /// Стакан недоступен прямо сейчас. Нужно, чтобы сообщить о возврате к норме
        /// </summary>
        private bool _depthProblemActive = false;

        /// <summary>
        /// Продажи прошлой ребалансировки, ожидающие проверки на исполнение
        /// </summary>
        private KorovinRebalanceRollback _sellsToVerify;

        private KorovinBotState _state = new KorovinBotState();

        private List<string> _frozenSecurities = new List<string>();

        private DateTime _lastBarTime = DateTime.MinValue;

        private int _barsAfterPendingBuys;

        /// <summary>
        /// Продажи акций и золота, финансирующие отложенный план покупок.
        ///
        /// Хранится отдельно от денежной ноги по той же причине, по которой их разделяет
        /// VerifyPreviousSells: последствия у провалов разные. Не исполнились продажи акций -
        /// портфель не изменился и ребалансировка откатывается целиком; не исполнилась
        /// денежная нога - откатывается только ступень лестницы. В объединённом счёте
        /// исполнившаяся денежная нога замаскировала бы провал акций
        /// </summary>
        private List<KorovinWaitOrder> _pendingStockOrders;

        /// <summary>
        /// Продажа денежной позиции, финансирующая отложенный план покупок
        /// </summary>
        private List<KorovinWaitOrder> _pendingLqdtOrders;

        /// <summary>
        /// Когда план встал в ожидание. Нужно только для журнала: по этой отметке видно,
        /// сколько заняло ожидание и не выродился ли быстрый путь в ожидание свечи
        /// </summary>
        private DateTime _pendingSince = DateTime.MinValue;

        /// <summary>
        /// Свободные деньги брокера ДО отправки продаж: точка отсчёта, от которой видно,
        /// что выручка зачислена. -1 означает, что брокер денежную позицию не отдаёт
        /// и проверка по деньгам пропускается
        /// </summary>
        private decimal _pendingCashAtPlan = -1;

        /// <summary>
        /// План покупок ждёт исполнения продаж. Читается из потока коннектора без лока -
        /// это только фильтр, чтобы не входить в лок на каждом тике серверного времени;
        /// настоящая проверка идёт под локом
        /// </summary>
        private volatile bool _waitingForSells;

        private TimeSpan _barLength = TimeSpan.Zero;

        private bool _warmupMessageSent;

        private bool _profitMarketChecked;

        private bool _parametersChecked;

        private bool _journalRestored;

        private bool _ladderRestored;

        private DateTime _cashReserveMessageDate = DateTime.MinValue;

        private StrategyParameterButton _forceRebalanceButton;

        /// <summary>
        /// Неторговые периоды и дни. Штатный механизм OsEngine с собственным окном настройки
        /// </summary>
        private NonTradePeriods _nonTradePeriods;

        private StrategyParameterButton _nonTradePeriodsButton;

        /// <summary>
        /// Обновление базы дивидендов. Файлы лежат в Wiki\Dividends рядом с терминалом и сами
        /// по себе не обновляются: их переписывает внешний DividendsUpdater. Роботу дивиденды
        /// нужны трижды - для дивидендной коррекции индекса, для total return доходности бумаг
        /// и для нейтрализации дивидендного гэпа в триггерах, поэтому в реальной торговле
        /// свежесть базы робот проверяет сам, а не надеется, что её обновят снаружи
        /// </summary>
        private StrategyParameterString _autoUpdateDividends;

        private StrategyParameterTimeOfDay _dividendsUpdateCheckTime;

        private StrategyParameterInt _dividendsMaxAgeDays;

        private StrategyParameterButton _updateDividendsButton;

        private DateTime _lastDividendsCheckDate = DateTime.MinValue;

        private bool _dividendsUpdating;

        /// <summary>
        /// Запрошена ручная ребалансировка. Выполняется на ближайшем расчёте в обход
        /// расписания и месячного лимита. Сбрасывается сразу после того, как повод определён
        /// </summary>
        private bool _forceRebalance;

        /// <summary>
        /// Состояние оповещений. Ошибка в канал шлётся один раз на вход в проблемное состояние,
        /// иначе канал заваливается повторами и его перестают читать. Долгие состояния
        /// напоминают о себе не чаще раза в неделю, выход из них отмечается обычной записью
        /// </summary>
        private DateTime _syncProblemSince = DateTime.MinValue;

        private DateTime _syncErrorSent = DateTime.MinValue;

        private readonly Dictionary<string, DateTime> _staleSince = new Dictionary<string, DateTime>();

        private readonly Dictionary<string, DateTime> _staleErrorSent = new Dictionary<string, DateTime>();

        /// <summary>
        /// Сколько расчётных дней подряд источник молчит. Считаем именно расчёты, а не календарь:
        /// расчёт идёт только в торговые дни, поэтому выходные и праздники не создают
        /// ложных срабатываний, а календарный порог в сутки ловил бы каждый понедельник
        /// </summary>
        private readonly Dictionary<string, int> _staleDayCount = new Dictionary<string, int>();

        private readonly Dictionary<string, DateTime> _staleLastDay = new Dictionary<string, DateTime>();

        private const int StaleAlertDays = 2;

        /// <summary>
        /// Сколько отказов закрытия ПОДРЯД терпеть, прежде чем прекратить повторы.
        /// Штатное аварийное дозакрытие OsEngine выключается из кода принудительно,
        /// см DisableDoubleExit
        /// </summary>
        private const int CloseFailsInRowLimit = 10;

        private bool _noAssetsErrorSent;

        private DateTime _warmupSince = DateTime.MinValue;

        private bool _warmupErrorSent;

        private bool _negativeCashErrorSent;

        private bool _reopenWarningSent;

        private const int AlertRepeatDays = 7;

        /// <summary>
        /// Счётчик отказов заявок. Увеличивается из потоков коннектора
        /// (PositionOpeningFail / PositionClosingFail), обнуляется главным циклом,
        /// поэтому меняется через Interlocked
        /// </summary>
        private int _failedOrders;

        private List<string> _noPriceReported = new List<string>();

        /// <summary>
        /// Бумаги, по которым уже сказано, что целевой вес меньше одного лота: имя -> дата.
        /// Нужен только чтобы не повторять запись на каждом баре
        /// </summary>
        private readonly Dictionary<string, DateTime> _lotTooBigReported
            = new Dictionary<string, DateTime>();

        private DateTime _syncProblemDate = DateTime.MinValue;

        private DateTime _staleWarnDate = DateTime.MinValue;

        private bool _indexCacheLoaded;

        private bool _isWarmup;

        private DateTime _lastProcessedDate = DateTime.MinValue;

        /// <summary>
        /// Поводы, о пустом срабатывании которых уже сказано сегодня: повод -> дата.
        /// Нужен только чтобы не повторять запись на каждом баре; на поведение не влияет
        /// </summary>
        private readonly Dictionary<string, DateTime> _emptyReasonDate
            = new Dictionary<string, DateTime>();


        private decimal _lastPortfolioValue;

        private decimal _lastInvestedAtCost;

        private decimal _lastPositionsValue;

        private decimal _lastCashRaw;

        private decimal _lastReserveUp;

        private decimal _lastReserveDown;


        private object _locker = new object();

        public SDKKorovinPortfolio(string name, StartProgram startProgram)
            : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);
            _tabStocks = TabsScreener[0];

            TabCreate(BotTabType.Simple);
            _tabGold = TabsSimple[0];

            TabCreate(BotTabType.Simple);
            _tabLqdt = TabsSimple[1];

            TabCreate(BotTabType.Index);
            _tabIndex = TabsIndex[0];

            CreateParameters();

            _tabStocks.CandlesSyncFinishedEvent += TabStocks_CandlesSyncFinishedEvent;
            _tabStocks.CandleFinishedEvent += TabStocks_CandleFinishedEvent;
            _tabStocks.TestStartEvent += TabStocks_TestStartEvent;

            _tabStocks.PositionOpeningFailEvent += Screener_PositionOpeningFailEvent;
            _tabStocks.PositionClosingFailEvent += Screener_PositionClosingFailEvent;

            // золото и денежная позиция живут на своих табах: под оптимизатором решение
            // должно пересчитываться и когда свежая свеча пришла по ним, иначе барьер
            // синхронизации откладывает его до следующей свечи скринера
            _tabGold.CandleFinishedEvent += Tab_CandleFinishedEvent;
            _tabLqdt.CandleFinishedEvent += Tab_CandleFinishedEvent;

            // табы скринера, поднятые из сохранённого набора, создаются внутри TabCreate -
            // событие по ним успевает пройти до этой подписки, поэтому существующие
            // обрабатываются отдельным проходом ниже
            _tabStocks.NewTabCreateEvent += Screener_NewTabCreateEvent;

            DisableDoubleExit();

            // насос для отложенного плана покупок. Серверное время идёт непрерывно
            // и не зависит от ликвидности инструмента, а событие портфеля приходит ровно
            // тогда, когда брокер пересчитал свободные деньги - вместе они заменяют
            // ожидание фиксированной паузой
            _tabLqdt.ServerTimeChangeEvent += Lqdt_ServerTimeChangeEvent;
            _tabLqdt.PortfolioOnExchangeChangedEvent += Lqdt_PortfolioChangedEvent;

            _tabGold.PositionOpeningFailEvent += Tab_PositionOpeningFailEvent;
            _tabGold.PositionClosingFailEvent += Gold_PositionClosingFailEvent;
            _tabLqdt.PositionOpeningFailEvent += Tab_PositionOpeningFailEvent;
            _tabLqdt.PositionClosingFailEvent += Lqdt_PositionClosingFailEvent;

            // ошибки базы дивидендов WikiMaster пишет в лог сервера, а не робота:
            // без этого трейдер не увидел бы, что ряд total return придержан
            _dividends.LogMessageEvent += Dividends_LogMessageEvent;

            LoadState();

            DeleteEvent += SDKKorovinPortfolio_DeleteEvent;

            CreateNonTradePeriods(name);

            Description = "Ребалансировщик портфеля по принципам Ильи Коровина. " +
                "Акции, золото и денежная позиция приводятся к целевым весам, " +
                "а доля акций поднимается ступенями по факту уже случившейся просадки рынка. " +
                "Только лонг, без плеча и стопов. Табы: Screener_stocks, GOLD, LQDT, Index. " +
                "Все табы на одном интрадей таймфрейме, рекомендуется час.";
        }

        private void CreateParameters()
        {
            _regime = CreateParameter("Regime", "Off",
                new[] { "Off", "On", "OnlyRebalanceNoNewMoney", "OnlyClosePosition" }, "Base");
            _schedule = CreateParameter("Schedule", "Interval",
                new[] { "Monthly", "Weekly", "Interval" }, "Base");
            _scheduleDay = CreateParameter("Schedule day", 1, 1, 28, 1, "Base");
            _rebalanceTime = CreateParameterTimeOfDay("Rebalance time", 11, 30, 0, 0, "Base");
            _intervalDays = CreateParameter("Interval days", 50, 1, 200, 1, "Base");
            _ladderMinDays = CreateParameter("Ladder min days", 1, 0, 90, 1, "Base");
            _warmupPolicy = CreateParameter("Warmup policy", "NoTrade",
                new[] { "NoTrade", "BaseWeightsOnly", "UseAvailable" }, "Base");
            _warmupMinDays = CreateParameter("Warmup min days", 120, 20, 500, 10, "Base");

            _forceRebalanceButton = CreateParameterButton("Force rebalance now", "Base");
            _forceRebalanceButton.UserClickOnButtonEvent += ForceRebalanceButton_UserClickOnButtonEvent;

            _nonTradePeriodsButton = CreateParameterButton("Non trade periods", "Base");
            _nonTradePeriodsButton.UserClickOnButtonEvent += NonTradePeriodsButton_UserClickOnButtonEvent;

            _stocksBasePercent = CreateParameter("Stocks base percent", 50m, 0m, 100m, 5m, "Allocation");
            _goldBasePercent = CreateParameter("Gold base percent", 25m, 0m, 100m, 5m, "Allocation");
            _cashBasePercent = CreateParameter("Cash base percent", 25m, 0m, 100m, 5m, "Allocation");
            _stocksMaxPercent = CreateParameter("Stocks max percent", 90m, 0m, 100m, 5m, "Allocation");
            _goldMinPercent = CreateParameter("Gold min percent", 5m, 0m, 100m, 5m, "Allocation");
            _cashMinPercent = CreateParameter("Cash min percent", 5m, 0m, 100m, 5m, "Allocation");
            _stocksMinPercent = CreateParameter("Stocks min percent", 20m, 0m, 100m, 5m, "Allocation");
            _reserveOrder = CreateParameter("Reserve order", "CashFirst",
                new[] { "CashFirst", "GoldFirst", "Proportional" }, "Allocation");

            _ddStartPercent = CreateParameter("DD start percent", 12m, 1m, 40m, 1m, "Market");
            _ddFullPercent = CreateParameter("DD full percent", 35m, 5m, 90m, 1m, "Market");
            _depthCurve = CreateParameter("Depth curve", 1.0m, 0.3m, 4m, 0.1m, "Market");
            _speedStartPercent = CreateParameter("Speed start percent", 5m, 1m, 30m, 1m, "Market");
            _speedFullPercent = CreateParameter("Speed full percent", 15m, 2m, 50m, 1m, "Market");
            _speedWeight = CreateParameter("Speed weight", 0.15m, 0m, 1m, 0.05m, "Market");
            _euphoriaDevStartPercent = CreateParameter("Euphoria dev start percent", 10m, 1m, 50m, 1m, "Market");
            _euphoriaDevFullPercent = CreateParameter("Euphoria dev full percent", 25m, 2m, 80m, 1m, "Market");
            _euphoriaWeight = CreateParameter("Euphoria weight", 0m, 0m, 1m, 0.05m, "Market");
            _stepUp = CreateParameter("Step up", 0.1m, 0.01m, 1m, 0.01m, "Market");
            _stepDown = CreateParameter("Step down", 0.2m, 0.01m, 1m, 0.01m, "Market");
            _minKStep = CreateParameter("Min k step", 0.05m, 0.01m, 0.5m, 0.01m, "Market");

            _indexDividendAdjust = CreateParameter("Index dividend adjust", "On",
                new[] { "On", "Off" }, "Market");
            _peakDecayPercentPerYear = CreateParameter("Peak decay percent per year", 15m, 0m, 100m, 5m, "Market");
            _longSmaPeriod = CreateParameter("Long sma period", 200, 20, 500, 10, "Market");
            _hysteresis = CreateParameter("Hysteresis", 2m, 0m, 20m, 1m, "Market");
            _exitDelayDays = CreateParameter("Exit delay days", 25, 0, 250, 5, "Market");
            _exitRecoveryPercent = CreateParameter("Exit recovery percent", 20m, 0m, 60m, 1m, "Market");
            _staleDays = CreateParameter("Stale days", 120, 10, 500, 10, "Market");
            _staleAction = CreateParameter("Stale action", "LowerLevel",
                new[] { "LowerLevel", "KeepLevel" }, "Market");
            _speedWindow = CreateParameter("Speed window", 5, 2, 40, 1, "Market");
            _speedHoldDays = CreateParameter("Speed hold days", 10, 0, 100, 5, "Market");

            _stockTilt = CreateParameter("Stock tilt", "Off", new[] { "Off", "On" }, "Stocks");
            _tiltLookback = CreateParameter("Tilt lookback", 120, 10, 500, 10, "Stocks");
            _tiltStrength = CreateParameter("Tilt strength", 1m, 0m, 5m, 0.1m, "Stocks");
            _tiltMin = CreateParameter("Tilt min", 0.7m, 0.1m, 1m, 0.05m, "Stocks");
            _tiltMax = CreateParameter("Tilt max", 1.3m, 1m, 3m, 0.05m, "Stocks");
            _idioGuard = CreateParameter("Idio guard", "On", new[] { "On", "Off" }, "Stocks");
            _idioLookback = CreateParameter("Idio lookback", 60, 10, 400, 10, "Stocks");
            _idioThresholdPercent = CreateParameter("Idio threshold percent", 30m, 5m, 90m, 5m, "Stocks");

            _reopenMode = CreateParameter("Reopen mode", "Off", new[] { "Off", "On" }, "Execution");
            _portfolioValueMode = CreateParameter("Portfolio value mode", "Auto",
                new[] { "Auto", "CashAndRealized", "FullPortfolioValue" }, "Execution");
            _bandAbsPercent = CreateParameter("Band abs percent", 2m, 0m, 20m, 0.5m, "Execution");
            _bandRelPercent = CreateParameter("Band rel percent", 30m, 0m, 100m, 5m, "Execution");
            _hardBandMult = CreateParameter("Hard band mult", 2m, 1m, 10m, 0.5m, "Execution");
            _minTradeMoney = CreateParameter("Min trade money", 5000m, 0m, 1000000m, 1000m, "Execution");
            _tradeTo = CreateParameter("Trade to", "Target", new[] { "Target", "BandEdge" }, "Execution");
            _cashInflowTriggerPercent = CreateParameter("Cash inflow trigger percent", 1m, 0m, 50m, 0.5m, "Execution");
            _pendingBuysTtlBars = CreateParameter("Pending buys ttl bars", 3, 1, 50, 1, "Execution");
            _staleBarsLimit = CreateParameter("Stale bars limit", 5, 1, 100, 1, "Execution");
            _tradeToleranceBars = CreateParameter("Trade tolerance bars", 1, 0, 10, 1, "Execution");
            _syncToleranceBars = CreateParameter("Sync tolerance bars", 3, 0, 20, 1, "Execution");
            _dividendTaxPercent = CreateParameter("Dividend tax percent", 13m, 0m, 50m, 1m, "Execution");
            _dividendHoldDays = CreateParameter("Dividend hold days", 90, 10, 400, 10, "Execution");
            _dividendGapNeutral = CreateParameter("Dividend gap neutral", "On",
                new[] { "On", "Off" }, "Execution");
            _depthCheck = CreateParameter("Depth check", "On", new[] { "On", "Off" }, "Execution");
            _maxSlippagePercent = CreateParameter("Max slippage percent", 0.3m, 0.01m, 5m, 0.05m, "Execution");
            _cashBufferPercent = CreateParameter("Cash buffer percent", 0.3m, 0m, 5m, 0.1m, "Execution");
            _freezeNearRecordDate = CreateParameter("Freeze near record date", "Off",
                new[] { "Off", "On" }, "Execution");
            _freezeDays = CreateParameter("Freeze days", 5, 1, 60, 1, "Execution");

            _autoUpdateDividends = CreateParameter("Auto update dividends", "On",
                new[] { "On", "Off" }, "Update");

            // время проверки должно попадать в торговое окно робота (неторговые периоды
            // отсекают всё до 10:00 и после 19:00) и стоять раньше времени ребалансировки:
            // обновление идёт внешним процессом и занимает время, а решение принимается в 11:30
            _dividendsUpdateCheckTime = CreateParameterTimeOfDay("Dividends update check time",
                10, 30, 0, 0, "Update");
            _dividendsMaxAgeDays = CreateParameter("Dividends max age days", 5, 1, 60, 1, "Update");

            _updateDividendsButton = CreateParameterButton("Start update dividends", "Update");
            _updateDividendsButton.UserClickOnButtonEvent += UpdateDividendsButton_UserClickOnButtonEvent;
        }

        public override string GetNameStrategyType()
        {
            return "SDKKorovinPortfolio";
        }

        public override void ShowIndividualSettingsDialog()
        {
            // отдельного окна нет, всё в панели параметров
        }

        /// <summary>
        /// По умолчанию выходные исключены. На МосБирже часть бумаг торгуется в субботу
        /// и воскресенье, но это не полноценная сессия: ликвидности мало, диапазон движения
        /// ограничен. Для ребалансировщика такие свечи вредны дважды - они искажают дневной ряд
        /// индекса, по которому считается просадка, и рассинхронизируют табы, потому что
        /// торгуется в выходные не весь список бумаг
        /// </summary>
        private void CreateNonTradePeriods(string name)
        {
            _nonTradePeriods = new NonTradePeriods(name);

            // Портфель считается только когда все три класса активов торгуются одновременно.
            // На МосБирже расписания разъезжаются: акции идут с утренней сессии 07:00
            // и до вечерней 24:00, золото GLDRUB_TOM - только 09:00-18:00 (на истории),
            // денежный фонд как акции. На утренних и вечерних барах золота просто нет,
            // и его последняя свеча оказывается вчерашней, а в понедельник - пятничной.
            // Отсюда и поток сообщений "источник отстаёт", и перекошенный дневной ряд:
            // закрытие дня по акциям бралось бы в 23:00, а по золоту в 18:00
            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod1Start = new TimeOfDay() { Hour = 0, Minute = 0 };
            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod1End = new TimeOfDay() { Hour = 10, Minute = 0 };
            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod1OnOff = true;

            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod2Start = new TimeOfDay() { Hour = 19, Minute = 0 };
            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod2End = new TimeOfDay() { Hour = 23, Minute = 59 };
            _nonTradePeriods.NonTradePeriodGeneral.NonTradePeriod2OnOff = true;

            // выходная сессия МосБиржи - не полноценные торги: ликвидности мало, диапазон
            // движения ограничен, и торгуется не весь список бумаг
            _nonTradePeriods.TradeInSaturday = false;
            _nonTradePeriods.TradeInSunday = false;

            _nonTradePeriods.Load();
        }

        /// <summary>
        /// Последний ли это бар торгового окна: следующего бара, на котором можно было бы
        /// исполнить отложенные покупки, сегодня уже не будет
        /// </summary>
        private bool IsLastBarOfWindow(DateTime barTime)
        {
            if (_nonTradePeriods == null
                || _barLength <= TimeSpan.Zero)
            {
                return false;
            }

            DateTime next = barTime.Add(_barLength);

            if (next.Date != barTime.Date)
            {
                return true;
            }

            return _nonTradePeriods.CanTradeThisTime(next) == false;
        }

        /// <summary>
        /// Попадает ли момент в торговое окно. Тем же фильтром отсеиваются свечи при построении
        /// дневных рядов: иначе закрытием дня становилась бы вечерняя сессия акций, которой
        /// нет ни у золота, ни в момент принятия решения
        /// </summary>
        private bool IsTradeTime(DateTime time)
        {
            if (_nonTradePeriods == null)
            {
                return true;
            }

            return _nonTradePeriods.CanTradeThisTime(time);
        }

        private void NonTradePeriodsButton_UserClickOnButtonEvent()
        {
            try
            {
                _nonTradePeriods.ShowDialog();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SDKKorovinPortfolio_DeleteEvent()
        {
            try
            {
                if (_nonTradePeriods != null)
                {
                    _nonTradePeriods.Delete();
                }

                _state.Delete(GetStateFilePath());

                if (System.IO.File.Exists(GetIndexCacheFilePath()))
                {
                    System.IO.File.Delete(GetIndexCacheFilePath());
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// В оптимизаторе логику запускает завершение свечи по любому табу скринера,
        /// а не событие сервера.
        ///
        /// Причина в ядре OsEngine: BotTabScreener подписывается на EndNextMinuteWithCandlesEvent
        /// только под тестером, а под оптимизатором CandlesSyncFinishedEvent у скринера
        /// не срабатывает вовсе. Штатный обходной приём (CONTEXT_REBALANCER §2.4) - подписаться
        /// на это событие сервера самому, но оно взводится лишь когда модельное время точно
        /// совпало с моментом последней свечи, а OptimizerServer при часовом таймфрейме шагает
        /// по 5 минут против 1 минуты у TesterServer. Половина совпадений теряется, робот
        /// получает меньше точек принятия решения, и прогон в оптимизаторе расходится
        /// с прогоном в тестере: 563 сделки против 683 при одних и тех же параметрах.
        ///
        /// Завершение свечи приходит независимо от шага времени сервера. Лишние вызовы
        /// безопасны: _lastBarTime отсекает повтор внутри бара, _lastProcessedDate - внутри дня,
        /// а барьер синхронизации не даст принять решение, пока не подтянутся все табы
        /// </summary>
        private void TabStocks_CandleFinishedEvent(List<Candle> candles, BotTabSimple source)
        {
            try
            {
                if (source == null
                    || source.Connector == null
                    || source.Connector.ServerType != ServerType.Optimizer)
                {
                    return;
                }

                // ждём, пока подтянутся все табы: иначе портфель считался бы по смеси -
                // одна бумага по новой свече, остальные по предыдущей. Именно это делал
                // CandlesSyncFinishedEvent, которого под оптимизатором нет
                if (AllStockTabsSynchronized() == false)
                {
                    return;
                }

                TabStocks_CandlesSyncFinishedEvent(_tabStocks.Tabs);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Все ли табы скринера достаточно свежие, чтобы считать оптимизаторный расчёт
        /// синхронным. Точное совпадение времени последней свечи слишком строго: реальные
        /// пропуски у отдельных бумаг (PMSBP, LSNGP) держали бы расчёт заблокированным на весь
        /// период их молчания - робот стоял бы там, где в тестере он спокойно продолжал бы
        /// работать через CandlesSyncFinishedEvent.
        ///
        /// Поэтому используются те же пороги, что и для торговли отдельной бумагой:
        /// таб, отставший не больше чем на Trade tolerance bars, считается синхронным;
        /// таб, отставший больше чем на Stale bars limit, уже помечен неторгуемым в другом
        /// месте и не обязан участвовать в синхронизации вовсе. Требование остаётся только
        /// для промежуточного случая - отстал заметно, но ещё не признан протухшим.
        ///
        /// Длина бара считается здесь же, через GetBarLength, а не берётся из _barLength:
        /// под оптимизатором это единственный путь в Process, и если бы длина бара
        /// бралась из поля, выставляемого только внутри Process, получился бы замкнутый
        /// круг - вход недостижим, пока не выполнен код, который недостижим без входа
        /// </summary>
        private bool AllStockTabsSynchronized()
        {
            if (_tabStocks == null
                || _tabStocks.Tabs == null
                || _tabStocks.Tabs.Count == 0)
            {
                return false;
            }

            TimeSpan barLength = GetBarLength(_tabStocks.Tabs);

            if (barLength <= TimeSpan.Zero)
            {
                return false;
            }

            List<DateTime> times = new List<DateTime>();
            DateTime maxTime = DateTime.MinValue;

            for (int i = 0; i < _tabStocks.Tabs.Count; i++)
            {
                BotTabSimple tab = _tabStocks.Tabs[i];

                if (tab == null
                    || tab.CandlesFinishedOnly == null
                    || tab.CandlesFinishedOnly.Count == 0)
                {
                    continue;
                }

                DateTime last = tab.CandlesFinishedOnly[tab.CandlesFinishedOnly.Count - 1].TimeStart;

                times.Add(last);

                if (last > maxTime)
                {
                    maxTime = last;
                }
            }

            if (times.Count == 0)
            {
                return false;
            }

            TimeSpan tolerance = TimeSpan.FromTicks(barLength.Ticks * _tradeToleranceBars.ValueInt);
            TimeSpan staleLimit = TimeSpan.FromTicks(barLength.Ticks * _staleBarsLimit.ValueInt);

            for (int i = 0; i < times.Count; i++)
            {
                TimeSpan lag = maxTime - times[i];

                if (lag <= tolerance)
                {
                    continue;
                }

                if (lag > staleLimit)
                {
                    // бумага уже протухла и не торгуется - ждать её незачем
                    continue;
                }

                return false;
            }

            return true;
        }

        private void Tab_CandleFinishedEvent(List<Candle> candles)
        {
            try
            {
                if (StartProgram != StartProgram.IsOsOptimizer
                    || _tabStocks == null
                    || AllStockTabsSynchronized() == false)
                {
                    return;
                }

                TabStocks_CandlesSyncFinishedEvent(_tabStocks.Tabs);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Screener_PositionOpeningFailEvent(Position position, BotTabSimple tab)
        {
            Tab_PositionOpeningFailEvent(position);
        }

        private void Lqdt_ServerTimeChangeEvent(DateTime time)
        {
            TryFinishPendingBuys();
        }

        private void Lqdt_PortfolioChangedEvent(Portfolio portfolio)
        {
            TryFinishPendingBuys();
        }

        /// <summary>
        /// Досрочно исполнить план покупок, если продажи под него уже отработали.
        ///
        /// Раньше план всегда ждал следующего бара. На часовом таймфрейме это означало
        /// час между расчётом и покупкой: цены за это время уходили, денег переставало
        /// хватать, и заявка последнего в списке актива отклонялась брокером. Ждать надо
        /// не бар, а зачисления выручки - а о нём говорят сами события коннектора.
        ///
        /// Флаг проверяется до лока: событие серверного времени приходит постоянно, и входить
        /// ради него в общий лок робота, когда ждать нечего, незачем
        /// </summary>
        private void TryFinishPendingBuys()
        {
            if (_waitingForSells == false
                || StartProgram != StartProgram.IsOsTrader)
            {
                return;
            }

            // не lock, а попытка без ожидания. Лок общий с главным циклом, а тот держит его
            // долго на тяжёлых участках - пересборка ряда индекса, проход лестницы по истории,
            // восстановление из журнала. Поток серверного времени один на всё подключение:
            // заблокировав его, робот притормозил бы рассылку времени остальным роботам.
            // Терять тут нечего - следующее событие придёт через секунду
            bool locked = false;

            try
            {
                Monitor.TryEnter(_locker, 0, ref locked);

                if (locked == false)
                {
                    return;
                }

                if (_waitingForSells == false)
                {
                    return;
                }

                if (_regime.ValueString == "Off"
                    || _regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }

                // Торговое окно проверяется по текущему времени, а не по времени бара.
                // План живёт до Pending buys ttl bars, и продажи могут добраться до Done
                // сильно позже своего бара - тогда покупки ушли бы в вечернюю сессию либо
                // в клиринг, то есть ровно туда, куда робот сознательно не ходит.
                // Барьер данных дублировать не нужно: план составлен на этом же баре,
                // и устареть сильнее, чем при его составлении, данные не успели
                DateTime now = _tabLqdt == null ? DateTime.MinValue : _tabLqdt.TimeServerCurrent;

                if (now != DateTime.MinValue
                    && _nonTradePeriods != null
                    && _nonTradePeriods.CanTradeThisTime(now) == false)
                {
                    return;
                }

                TryExecutePendingBuys(_lastBarTime);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
            finally
            {
                if (locked)
                {
                    Monitor.Exit(_locker);
                }
            }
        }

        private void Screener_PositionClosingFailEvent(Position position, BotTabSimple tab)
        {
            Tab_PositionClosingFailEvent(position, tab);
        }

        private void Gold_PositionClosingFailEvent(Position position)
        {
            Tab_PositionClosingFailEvent(position, _tabGold);
        }

        private void Lqdt_PositionClosingFailEvent(Position position)
        {
            Tab_PositionClosingFailEvent(position, _tabLqdt);
        }

        /// <summary>
        /// Заявка на открытие не прошла. Робот не пересчитывает портфель тут же:
        /// недобранное подхватит ближайшая ребалансировка по отклонению весов,
        /// но факт отказа обязан быть виден в логе
        /// </summary>
        private void Tab_PositionOpeningFailEvent(Position position)
        {
            try
            {
                Interlocked.Increment(ref _failedOrders);

                SendNewLogMessage("Не удалось открыть позицию по "
                    + (position != null ? position.SecurityName : "?")
                    + ". Объём будет добран следующей ребалансировкой", LogMessageType.Error);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Заявка на закрытие не прошла, позиция осталась в состоянии ClosingFail.
        ///
        /// По документации OsEngine это не тупик, а приглашение повторить закрытие:
        /// для ClosingFail в таблице состояний стоит «можно повторить», а рекомендованная
        /// реакция на PositionClosingFailEvent - принудительный CloseAtMarket.
        /// 
        /// Аварийное дозакрытие платформы выключено в коде, поскольку его логика не соответствует
        /// логике роьота, см DisableDoubleExit
        ///
        /// Повторить закрытие обязательно: продажа берёт только позиции Open, поэтому
        /// зависшая в ClosingFail позиция остаётся видимой в капитале, но неуправляемой -
        /// перевес заморозился бы в портфеле до ручного вмешательства
        /// </summary>
        private void Tab_PositionClosingFailEvent(Position position, BotTabSimple tab)
        {
            try
            {
                Interlocked.Increment(ref _failedOrders);

                string name = position != null ? position.SecurityName : "?";
                string number = position != null ? position.Number.ToString() : "?";

                SendNewLogMessage("Не удалось закрыть позицию по " + name + ", номер " + number
                    + ". Позиция в состоянии ClosingFail и до восстановления не управляется",
                    LogMessageType.Error);

                if (position == null
                    || tab == null
                    || position.OpenVolume <= 0)
                {
                    return;
                }

                // ядро могло уже отправить своё аварийное дозакрытие: второй заявкой
                // мы продали бы вдвое больше нужного
                if (position.CloseActive)
                {
                    SendNewLogMessage("Закрытие " + name + " номер " + number
                        + " уже перевыставлено сопровождением позиции, повтор не нужен",
                        LogMessageType.Error);
                    return;
                }

                // предохранитель от гонки заявок: если закрытие не проходит раз за разом,
                // долбить биржу бессмысленно. Позицию подхватит восстановление на следующем
                // баре и вернёт в обычный оборот ребалансировок
                int failsInRow = GetCloseFailsInRow(position);

                if (failsInRow >= CloseFailsInRowLimit)
                {
                    SendNewLogMessage("Закрытие " + name + " номер " + number + " не проходит: "
                        + failsInRow + " отказов подряд. Повторы прекращены, "
                        + "позиция вернётся в работу восстановлением. Требуется вмешательство",
                        LogMessageType.Error);
                    return;
                }

                // Объём берём из самой неисполненной заявки, а не из позиции целиком.
                // Робот держит всю бумагу в одной совокупной позиции и срезает перевес
                // частичным закрытием, поэтому OpenVolume здесь - это всё владение, а продать
                // нужно только недоисполненный остаток. Закрытие на OpenVolume вынесло бы
                // позицию под ноль вместо того, чтобы поправить вес
                decimal volume = GetFailedCloseVolume(position);

                if (volume <= 0)
                {
                    return;
                }

                SendNewLogMessage("Повторная отправка заявки на закрытие " + name + " номер "
                    + number + ", объём " + volume + " из " + position.OpenVolume
                    + " открытых, по рынку", LogMessageType.Error);

                tab.CloseAtMarket(position, volume);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Screener_NewTabCreateEvent(BotTabSimple tab)
        {
            try
            {
                DisableDoubleExit(tab);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Выключить штатное аварийное дозакрытие OsEngine (Double exit) на всех вкладках.
        ///
        /// Механизм платформы закрывает позицию на весь OpenVolume: он рассчитан на стратегии,
        /// которые выходят целиком. Этот робот держит всю бумагу в одной совокупной позиции
        /// и срезает перевес частичным закрытием, поэтому дозакрытие на весь объём продало бы
        /// всё владение вместо правки веса. Включённым его оставлять нельзя - он прямо
        /// противоречит логике робота.
        ///
        /// Свой повтор закрытия робот делает сам, в обработчике отказа, и на правильном
        /// объёме - недоисполненном остатке заявки.
        ///
        /// Снятие зависшей заявки по таймауту (SecondToClose) не трогаем: оно объём
        /// не искажает и полезно
        /// </summary>
        private void DisableDoubleExit()
        {
            DisableDoubleExit(_tabGold);
            DisableDoubleExit(_tabLqdt);

            if (_tabStocks == null
                || _tabStocks.Tabs == null)
            {
                return;
            }

            for (int i = 0; i < _tabStocks.Tabs.Count; i++)
            {
                DisableDoubleExit(_tabStocks.Tabs[i]);
            }
        }

        /// <summary>
        /// Каждая бумага скринера живёт на своей вкладке со своими настройками сопровождения:
        /// при создании вкладки копируются коннектор, таймфрейм и комиссии, но не сопровождение,
        /// поэтому выставлять приходится на каждой
        /// </summary>
        private void DisableDoubleExit(BotTabSimple tab)
        {
            if (tab == null
                || tab.ManualPositionSupport == null)
            {
                return;
            }

            tab.ManualPositionSupport.DoubleExitIsOn = false;
        }

        /// <summary>
        /// Сколько осталось дозакрыть после неудачной заявки: сколько просили минус сколько
        /// успело исполниться. Частичное исполнение здесь обычное дело - заявка могла набрать
        /// часть объёма и отвалиться, и повторять нужно ровно недобранное.
        ///
        /// Ограничение сверху по OpenVolume - страховка от рассинхрона: закрыть больше,
        /// чем открыто, нельзя ни при каких данных заявки
        /// </summary>
        /// <summary>
        /// Сколько заявок на закрытие подряд закончились ничем.
        ///
        /// Считаем хвост списка, а не его длину. Position.CloseOrders пополняется при каждом
        /// закрытии и не чистится никогда, а этот робот держит всю бумагу в одной совокупной
        /// позиции годами и срезает перевес частичными продажами. По длине списка предохранитель
        /// сработал бы от обычной работы: десяток успешных частичных продаж набирается
        /// за год-полтора, и повторы отключились бы именно у самых старых позиций - там,
        /// где перевесов больше всего.
        ///
        /// Отказом считаем и Fail, и Cancel. В ClosingFail позиция попадает обоими путями
        /// (Position.SetOrder: отдельные ветки на Fail и на Cancel, обе при CloseActive == false
        /// и ненулевом OpenVolume), и PositionClosingFailEvent поднимается в обоих случаях.
        /// Для этого робота путь через отмену не экзотика: платформенное дозакрытие Double exit
        /// выключено намеренно, а снятие зависшей заявки по таймауту (SecondToClose) оставлено
        /// включённым, и на неликвиде рыночная заявка без контрагента уходит именно в отмену.
        /// Считая только Fail, предохранитель на этом пути не наступал бы никогда.
        ///
        /// Частично исполненная заявка отказом не считается: что-то продать удалось, значит
        /// цепочка прервана. Успешное закрытие обнуляет счёт, как и должно быть у счётчика
        /// отказов подряд
        /// </summary>
        private static int GetCloseFailsInRow(Position position)
        {
            if (position == null
                || position.CloseOrders == null)
            {
                return 0;
            }

            int count = 0;

            for (int i = position.CloseOrders.Count - 1; i >= 0; i--)
            {
                Order order = position.CloseOrders[i];

                if (order == null)
                {
                    continue;
                }

                if (order.State != OrderStateType.Fail
                    && order.State != OrderStateType.Cancel)
                {
                    break;
                }

                if (order.VolumeExecute > 0)
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private decimal GetFailedCloseVolume(Position position)
        {
            if (position == null
                || position.CloseOrders == null
                || position.CloseOrders.Count == 0)
            {
                return 0;
            }

            Order lastClose = position.CloseOrders[position.CloseOrders.Count - 1];

            if (lastClose == null)
            {
                return 0;
            }

            decimal volume = lastClose.Volume - lastClose.VolumeExecute;

            if (volume > position.OpenVolume)
            {
                volume = position.OpenVolume;
            }

            return volume;
        }

        private void TabStocks_TestStartEvent()
        {
            try
            {
                lock (_locker)
                {
                    ResetForNewRun();
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Робот живёт между прогонами тестера в одном экземпляре. Без сброса он считает,
        /// что бары нового прогона уже в прошлом, и не делает ничего
        /// </summary>
        private void ResetForNewRun()
        {
            _lastBarTime = DateTime.MinValue;
            _lastProcessedDate = DateTime.MinValue;
            _emptyReasonDate.Clear();
            _barLength = TimeSpan.Zero;
            _syncProblemDate = DateTime.MinValue;
            _staleWarnDate = DateTime.MinValue;
            _syncProblemSince = DateTime.MinValue;
            _syncErrorSent = DateTime.MinValue;
            _staleSince.Clear();
            _staleErrorSent.Clear();
            _staleDayCount.Clear();
            _staleLastDay.Clear();
            _noAssetsErrorSent = false;
            _formulaGapSince = DateTime.MinValue;
            _formulaGapReported = false;
            _warmupSince = DateTime.MinValue;
            _warmupErrorSent = false;
            _negativeCashErrorSent = false;
            _reopenWarningSent = false;
            _cashReserveMessageDate = DateTime.MinValue;
            _lastDividendsCheckDate = DateTime.MinValue;
            _forceRebalance = false;
            _ladderRestored = false;
            _noPriceReported.Clear();
            _lotTooBigReported.Clear();
            _barsAfterPendingBuys = 0;
            _pendingStockOrders = null;
            _pendingLqdtOrders = null;
            _pendingCashAtPlan = -1;
            _pendingSince = DateTime.MinValue;
            _waitingForSells = false;
            _warmupMessageSent = false;
            _indexCacheLoaded = false;
            _isWarmup = false;

            _indexSeries.Clear();
            _securitySeries.Clear();
            _basketSeries = new List<KorovinDailySeries>();
            _basketTickers = new List<string>();
            _frozenSecurities.Clear();

            _totalReturnIndex.Clear();
            _totalReturnIndex.SetHistory(null, null);
            _ladder.Reset();
            _dividends.Clear();
            _dividendsReliable = true;
            _dividendsHoldReported = DateTime.MinValue;
            _depthProblemReported = DateTime.MinValue;
            _depthProblemActive = false;
            _indexCacheErrorDate = DateTime.MinValue;
            _indexCacheBroken = false;
            _sellsToVerify = null;
            _state.Clear();

            SendNewLogMessage("Новый прогон: накопленные ряды, лестница и состояние сброшены",
                LogMessageType.System);
        }

        private void TabStocks_CandlesSyncFinishedEvent(List<BotTabSimple> tabs)
        {
            try
            {
                lock (_locker)
                {
                    Process(tabs);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        #endregion

        #region Main logic

        private void Process(List<BotTabSimple> stockTabs)
        {
            if (_regime.ValueString == "Off")
            {
                return;
            }

            if (stockTabs == null
                || stockTabs.Count == 0)
            {
                return;
            }

            DateTime barTime = DateTime.MinValue;

            for (int i = 0; i < stockTabs.Count; i++)
            {
                DateTime tabTime = GetTabLastBarTime(stockTabs[i]);

                if (tabTime > barTime)
                {
                    barTime = tabTime;
                }
            }

            if (barTime == DateTime.MinValue)
            {
                return;
            }

            if (_lastBarTime != DateTime.MinValue
                && barTime < _lastBarTime.AddDays(-1))
            {
                // события старта теста могло и не быть: время пошло назад, это новый прогон
                ResetForNewRun();
            }

            if (barTime <= _lastBarTime)
            {
                return;
            }

            // Свечи неторгового времени не должны попасть даже в ряды: выходная сессия
            // с её тонким рынком исказила бы дневной ряд индекса, по которому считается
            // просадка, а частичный состав торгуемых бумаг - рассинхронизировал бы табы
            if (_nonTradePeriods != null
                && _nonTradePeriods.CanTradeThisTime(barTime) == false)
            {
                return;
            }

            _lastBarTime = barTime;

            UpdateBarLength(stockTabs);

            CheckDividendsUpdate(barTime);

            // страховка: табы скринера поднимает фоновый поток платформы уже после
            // конструктора, и хотя на их создание робот подписан, полагаться только
            // на событие для настройки, влияющей на объём заявок, не стоит
            DisableDoubleExit();

            RestoreLastRebalanceFromJournal(stockTabs);

            VerifyPreviousSells(barTime);

            FixPartialClosedPositions(stockTabs);

            UpdateSeries(stockTabs, barTime);

            if (_regime.ValueString == "OnlyClosePosition"
                && _state.PendingBuys.Count > 0)
            {
                SendNewLogMessage("Режим OnlyClosePosition: отложенный план покупок отменён",
                    LogMessageType.System);
                ClearPendingBuys();
                SaveState();
            }

            string syncProblem = GetSyncProblem(barTime);

            if (_state.PendingBuys.Count > 0)
            {
                _barsAfterPendingBuys++;

                // барьер данных распространяется и на отложенный план: он был составлен
                // на прошлом баре, но исполняется сейчас, и по устаревшим ценам отправлять
                // заявки нельзя. Если данные не подтянутся, план протухнет по TTL - это лучше,
                // чем купить вслепую
                if (string.IsNullOrEmpty(syncProblem) == false)
                {
                    ReportSyncProblem(syncProblem, barTime);
                    return;
                }

                string expireReason = GetPendingBuysExpireReason(barTime);

                if (string.IsNullOrEmpty(expireReason) == false)
                {
                    // деньги от продаж остались нераспределёнными: это незапланированная
                    // денежная позиция, о которой трейдер должен узнать сразу
                    SendExecutionProblem("План покупок отменён: " + expireReason
                        + ". Деньги остались нераспределёнными до следующей ребалансировки");
                    ClearPendingBuys();
                    SaveState();
                }
                else
                {
                    ExecutePendingBuys(stockTabs, barTime);
                    return;
                }
            }

            if (_regime.ValueString == "OnlyClosePosition")
            {
                return;
            }

            if (string.IsNullOrEmpty(syncProblem) == false)
            {
                ReportSyncProblem(syncProblem, barTime);
                return;
            }

            if (_syncProblemSince != DateTime.MinValue)
            {
                SendNewLogMessage("Источники синхронны, работа возобновлена (простой с "
                    + _syncProblemSince.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + ")",
                    LogMessageType.System);

                _syncProblemSince = DateTime.MinValue;
                _syncErrorSent = DateTime.MinValue;
            }

            // ручной запрос обходит расписание и «раз в день», но не барьер синхронизации,
            // не торговое окно и не прогрев: без данных решение принимать не на чем
            if (_forceRebalance == false)
            {
                if (_state.LastRebalanceDate.Date == barTime.Date
                    || _lastProcessedDate.Date == barTime.Date)
                {
                    return;
                }

                if (IsRebalanceTimeCome(barTime) == false)
                {
                    return;
                }
            }

            RunRebalance(stockTabs, barTime);
        }

        /// <summary>
        /// Сказать один раз за день, что повод сработал, а торговать нечем.
        ///
        /// Триггер и исполнение меряют портфель по-разному, и расхождений три: триггер
        /// Deviation считает отклонение с учётом начисленного дивиденда, а полосу в исполнении
        /// меряют по сырой стоимости; исполнение пропускает замороженные стоп-листом бумаги;
        /// исполнение пропускает бумаги без свежих данных. Триггер ни одного из трёх
        /// не различает, поэтому повод возвращается на каждом баре.
        ///
        /// Гасить его при этом НЕЛЬЗЯ, хотя со стороны это и выглядит холостой прокруткой.
        /// Возврат повода работает как механизм повтора: шаг лестницы, для которого сейчас
        /// все дельты внутри полосы, исполняется на одном из следующих баров, когда цены
        /// сдвинутся. Попытка гасить повод до конца дня стоила 6 ребалансировок по LoadChange,
        /// 12 по Deviation и 7,8% результата на десятилетнем прогоне. Поэтому здесь только
        /// запись в лог - чтобы происходящее было видно, а поведение не менялось.
        ///
        /// Уровень записи обычный, не ошибка: в основном это штатное ожидание исполнимого
        /// момента, и поднимать тревогу на каждый такой случай значило бы кричать «волки»
        /// </summary>
        private void ReportEmptyReason(string reason, DateTime barTime)
        {
            DateTime date;

            if (_emptyReasonDate.TryGetValue(reason, out date)
                && date.Date == barTime.Date)
            {
                return;
            }

            _emptyReasonDate[reason] = barTime.Date;

            SendNewLogMessage("Повод " + reason + " сработал, но торговать нечем: отклонения "
                + "внутри полосы либо бумаги заморожены или без свежих данных. Повод вернётся "
                + "на следующем баре", LogMessageType.System);
        }

        private void RunRebalance(List<BotTabSimple> stockTabs, DateTime barTime)
        {
            _isWarmup = UpdateLadder(barTime);

            if (_isWarmup
                && _warmupPolicy.ValueString == "NoTrade")
            {
                // без модели рынка позиции не набираем: иначе после докачки истории
                // пришлось бы разворачивать веса обратно
                return;
            }

            List<KorovinAsset> assets = BuildAssets(stockTabs, barTime);

            if (assets.Count == 0)
            {
                if (_noAssetsErrorSent == false)
                {
                    _noAssetsErrorSent = true;

                    SendExecutionProblem("Нет ни одного инструмента с данными: портфелем "
                        + "никто не управляет. Проверьте состав скринера и подключение источников");
                }

                return;
            }

            if (_noAssetsErrorSent)
            {
                _noAssetsErrorSent = false;

                SendNewLogMessage("Инструменты снова доступны, управление портфелем возобновлено",
                    LogMessageType.System);
            }

            CheckMarketDepthAvailability(assets, barTime);

            decimal equity;
            decimal cash;

            EvaluatePortfolio(assets, out equity, out cash);

            if (equity <= 0)
            {
                SendNewLogMessage("Стоимость портфеля равна нулю, ребалансировка пропущена",
                    LogMessageType.Error);
                return;
            }

            UpdateFrozenSecurities(assets, barTime);
            UpdatePendingDividends(assets, barTime);

            BuildTargetWeights(assets, equity);
            CalculateDeltas(assets, equity);

            string reason = CheckTriggers(assets, equity, cash, barTime);

            bool force = _forceRebalance;

            if (force)
            {
                // флаг сбрасывается здесь, а не после сделок: иначе запрос, по которому
                // выравнивать нечего, висел бы до первого настоящего повода
                _forceRebalance = false;
                reason = "Manual";
            }

            if (string.IsNullOrEmpty(reason))
            {
                return;
            }

            ExecuteRebalance(assets, equity, cash, reason, barTime);
        }

        #endregion

        #region Series and market state

        private void UpdateSeries(List<BotTabSimple> stockTabs, DateTime barTime)
        {
            for (int i = 0; i < stockTabs.Count; i++)
            {
                UpdateSecuritySeries(stockTabs[i], barTime);
            }

            UpdateSecuritySeries(_tabGold, barTime);
            UpdateSecuritySeries(_tabLqdt, barTime);

            if (_tabIndex != null
                && _tabIndex.Candles != null
                && _tabIndex.Candles.Count > 0)
            {
                if (_indexCacheLoaded == false
                    && StartProgram == StartProgram.IsOsTrader)
                {
                    _indexCacheLoaded = true;

                    List<DateTime> cacheDates = new List<DateTime>();
                    List<decimal> cacheValues = new List<decimal>();

                    string cacheError = KorovinBotState.LoadIndexCache(GetIndexCacheFilePath(),
                        cacheDates, cacheValues);

                    if (string.IsNullOrEmpty(cacheError) == false)
                    {
                        SendExecutionProblem("Не удалось прочитать кэш истории индекса: "
                            + cacheError + ". История начинается заново с того, что отдал "
                            + "коннектор: если этого мало, робот уйдёт в прогрев");
                    }

                    // сохранённый ряд уже очищен от дивидендных гэпов, поэтому он идёт
                    // историей total return ряда, а не подмешивается в цены
                    _totalReturnIndex.SetHistory(cacheDates, cacheValues);

                    if (cacheDates.Count > 0)
                    {
                        SendNewLogMessage("Из кэша поднято дней истории индекса: " + cacheDates.Count,
                            LogMessageType.System);
                    }
                }

                _indexSeries.Update(_tabIndex.Candles, barTime, IsTradeTime);
            }

            UpdateBasketSeries(barTime);
        }

        private void UpdateSecuritySeries(BotTabSimple tab, DateTime barTime)
        {
            if (tab == null
                || tab.Security == null
                || string.IsNullOrWhiteSpace(tab.Security.Name))
            {
                return;
            }

            if (tab.CandlesFinishedOnly == null
                || tab.CandlesFinishedOnly.Count == 0)
            {
                return;
            }

            KorovinDailySeries series;

            if (_securitySeries.TryGetValue(tab.Security.Name, out series) == false)
            {
                series = new KorovinDailySeries();
                _securitySeries.Add(tab.Security.Name, series);
            }

            series.Update(tab.CandlesFinishedOnly, barTime, IsTradeTime);
        }

        private void UpdateBasketSeries(DateTime barTime)
        {
            // признак считается заново каждый бар: вчерашняя ошибка чтения не должна
            // держать ряд после того, как база снова читается
            _dividendsReliable = true;

            if (_tabIndex == null
                || _tabIndex.Tabs == null)
            {
                return;
            }

            if (_basketSeries.Count != _tabIndex.Tabs.Count)
            {
                _basketSeries = new List<KorovinDailySeries>();
                _basketTickers = new List<string>();

                for (int i = 0; i < _tabIndex.Tabs.Count; i++)
                {
                    _basketSeries.Add(new KorovinDailySeries());
                    _basketTickers.Add("");
                }

                _totalReturnIndex.Clear();

                // ряд будет построен заново, поэтому пик и дно от прежнего ряда держать
                // нельзя: просадка считалась бы по одной шкале, а цена шла бы по другой
                _ladder.Reset();
            }

            for (int i = 0; i < _tabIndex.Tabs.Count; i++)
            {
                Market.Connectors.ConnectorCandles connector = _tabIndex.Tabs[i];

                if (connector == null
                    || connector.Security == null
                    || string.IsNullOrWhiteSpace(connector.Security.Name))
                {
                    continue;
                }

                _basketTickers[i] = connector.Security.Name;

                List<Candle> candles = connector.Candles(true);

                if (candles == null
                    || candles.Count == 0)
                {
                    continue;
                }

                _basketSeries[i].Update(candles, barTime, IsTradeTime);

                if (_dividends.Load(connector.Security.Name, barTime) == false)
                {
                    _dividendsReliable = false;
                }
            }
        }

        /// <summary>
        /// Сообщить о неудачной записи кэша истории индекса.
        ///
        /// Кэш пишется каждый бар, поэтому сообщение throttled по дню: без этого полный диск
        /// или снятые права залили бы канал ошибок. Но молчать нельзя совсем - пока запись
        /// не проходит, история не пополняется, и после перезапуска ряд окажется короче
        /// на всё время сбоя
        /// </summary>
        private void ReportIndexCacheSave(string error, DateTime barTime)
        {
            if (string.IsNullOrEmpty(error))
            {
                if (_indexCacheBroken)
                {
                    _indexCacheBroken = false;

                    SendNewLogMessage("Кэш истории индекса снова пишется",
                        LogMessageType.System);
                }

                return;
            }

            _indexCacheBroken = true;

            if (_indexCacheErrorDate.Date == barTime.Date)
            {
                return;
            }

            _indexCacheErrorDate = barTime;

            SendExecutionProblem("Не удалось сохранить кэш истории индекса: " + error
                + ". Пока запись не проходит, история не пополняется: после перезапуска "
                + "робота ряд будет короче на всё время сбоя, а недостающий кусок склейка "
                + "сошьёт молча, как будто рынок за это время не двигался");
        }

        /// <summary>
        /// Бумага индексной вкладки, которой нет в формуле индекса, всё равно попадает
        /// в расчёт дивидендного гэпа корзины - с весом 1 по умолчанию. Отсюда две ошибки
        /// сразу: настоящие отсечки занижаются, а по бумагам вне формулы появляются гэпы,
        /// которых индекс не переживал. Причина почти всегда одна - Sec count автоформулы
        /// меньше числа инструментов вкладки.
        ///
        /// Сообщение разовое: пока состав не поправят, повторять его на каждом баре незачем.
        /// Первый бар с расхождением пропускается - автоформула перестраивается уже на ходу,
        /// пока подключаются бумаги, и на первом баре состав формулы может быть ещё старым.
        /// Ошибка настройки никуда не денется и подтвердится на следующем баре
        /// </summary>
        private void CheckFormulaCoversBasket(DateTime barTime)
        {
            // при Off корзина и множители в ряду не участвуют вовсе, а пустая формула -
            // это ненастроенная вкладка, а не ошибка состава
            if (_indexDividendAdjust.ValueString != "On"
                || _tabIndex == null
                || _tabIndex.Tabs == null
                || _tabIndex.Tabs.Count == 0
                || _totalReturnIndex.FormulaIsEmpty)
            {
                return;
            }

            string missing = "";

            for (int i = 0; i < _tabIndex.Tabs.Count; i++)
            {
                if (_totalReturnIndex.HasFormulaIndex(i))
                {
                    continue;
                }

                Market.Connectors.ConnectorCandles connector = _tabIndex.Tabs[i];

                string name = connector != null
                    && connector.Security != null
                    && string.IsNullOrWhiteSpace(connector.Security.Name) == false
                    ? connector.Security.Name
                    : "A" + i;

                missing += name + " ";
            }

            if (string.IsNullOrEmpty(missing))
            {
                _formulaGapSince = DateTime.MinValue;
                _formulaGapReported = false;
                return;
            }

            if (_formulaGapSince == DateTime.MinValue)
            {
                _formulaGapSince = barTime;
                return;
            }

            if (_formulaGapReported)
            {
                return;
            }

            _formulaGapReported = true;

            SendExecutionProblem("В формулу индекса не вошли бумаги вкладки: " + missing
                + "(всего инструментов во вкладке " + _tabIndex.Tabs.Count + "). В расчёт "
                + "дивидендного гэпа они всё равно попадают, с весом 1, поэтому ряд total "
                + "return считается неверно. Поднимите Sec count автоформулы до числа "
                + "инструментов вкладки либо выключите Index dividend adjust");
        }

        private bool UpdateLadder(DateTime barTime)
        {
            _totalReturnIndex.DividendAdjust = _indexDividendAdjust.ValueString == "On";
            _totalReturnIndex.SetFormula(_tabIndex != null ? _tabIndex.UserFormula : null);
            CheckFormulaCoversBasket(barTime);
            _totalReturnIndex.Rebuild(_indexSeries, _basketSeries, _basketTickers, _dividends,
                _dividendsReliable);

            if (_dividendsReliable == false
                && _dividendsHoldReported.Date != barTime.Date)
            {
                _dividendsHoldReported = barTime;

                SendExecutionProblem("Ряд total return не достраивается: база дивидендов "
                    + "прочитана не полностью. Лестница работает на прежнем ряде, ряд догонит "
                    + "после успешного чтения");
            }

            if (StartProgram == StartProgram.IsOsTrader
                && _totalReturnIndex.Values.Count > 0)
            {
                ReportIndexCacheSave(KorovinBotState.SaveIndexCache(GetIndexCacheFilePath(),
                    _totalReturnIndex.Dates, _totalReturnIndex.Values), barTime);
            }

            int requiredDays = _longSmaPeriod.ValueInt;

            int haveDays = _totalReturnIndex.Values.Count;

            if (haveDays < requiredDays)
            {
                bool useAvailable = _warmupPolicy.ValueString == "UseAvailable"
                    && haveDays >= _warmupMinDays.ValueInt;

                if (useAvailable == false)
                {
                    _ladder.Reset();

                    if (_warmupMessageSent == false)
                    {
                        string action = _warmupPolicy.ValueString == "NoTrade"
                            ? "Торговли нет, робот ждёт историю"
                            : "Работа по базовым весам, лестница отключена";

                        SendNewLogMessage("Прогрев: дней истории индекса " + haveDays +
                            " из " + requiredDays + ". " + action, LogMessageType.System);
                        _warmupMessageSent = true;
                    }

                    if (_warmupSince == DateTime.MinValue)
                    {
                        _warmupSince = barTime;
                    }

                    // история должна была накопиться давным-давно. Раз её нет, коннектор
                    // её не отдаёт, и робот с политикой NoTrade не начнёт торговать вообще
                    if (_warmupErrorSent == false
                        && _warmupPolicy.ValueString == "NoTrade"
                        && (barTime - _warmupSince).TotalDays > _warmupMinDays.ValueInt)
                    {
                        _warmupErrorSent = true;

                        SendExecutionProblem("Прогрев идёт " + (int)(barTime - _warmupSince).TotalDays
                            + " дней, истории индекса " + haveDays + " из " + requiredDays
                            + ". При политике NoTrade торговля так и не начнётся: "
                            + "история индекса не докачивается");
                    }

                    return true;
                }
            }

            _warmupMessageSent = false;
            _warmupSince = DateTime.MinValue;
            _warmupErrorSent = false;

            ApplyLadderSettings();

            // формула индексной вкладки, состав корзины или флаг дивидендной коррекции
            // поменялись на ходу: ряд пересчитан по другим правилам, и накопленные пик, дно
            // и загрузка относятся к ряду, которого больше нет
            if (_totalReturnIndex.SeriesRebuilt)
            {
                _totalReturnIndex.SeriesRebuilt = false;

                _ladder.Reset();

                SendNewLogMessage("Ряд индекса пересобран по новым правилам (формула, состав "
                    + "корзины или Index dividend adjust): лестница сброшена, состояние рынка "
                    + "считается заново", LogMessageType.Error);
            }

            // сначала прогон по истории, потом снимок: Process при первом вызове проходит
            // весь ряд с нуля и перезаписал бы восстановленные загрузку, пик и дно
            _ladder.Process(_totalReturnIndex.Dates, _totalReturnIndex.Values,
                _totalReturnIndex.CurrentValue, barTime);

            if (RestoreLadderFromState())
            {
                // снимок лёг поверх реконструкции: шаг по текущему дню надо посчитать
                // уже от него. Историю этот вызов не трогает, она пройдена
                _ladder.Process(_totalReturnIndex.Dates, _totalReturnIndex.Values,
                    _totalReturnIndex.CurrentValue, barTime);
            }

            return false;
        }

        private void ApplyLadderSettings()
        {
            _ladder.PeakDecayPercentPerYear = _peakDecayPercentPerYear.ValueDecimal;
            _ladder.LongSmaPeriod = _longSmaPeriod.ValueInt;

            _ladder.Hysteresis = _hysteresis.ValueDecimal;
            _ladder.ExitDelayDays = _exitDelayDays.ValueInt;
            _ladder.ExitRecoveryPercent = _exitRecoveryPercent.ValueDecimal;
            _ladder.StaleDays = _staleDays.ValueInt;
            _ladder.StaleLowersLevel = _staleAction.ValueString == "LowerLevel";
            _ladder.SpeedWindow = _speedWindow.ValueInt;
            _ladder.SpeedHoldDays = _speedHoldDays.ValueInt;

            _ladder.DdStartPercent = _ddStartPercent.ValueDecimal;
            _ladder.DdFullPercent = _ddFullPercent.ValueDecimal;
            _ladder.DepthCurve = _depthCurve.ValueDecimal;
            _ladder.SpeedStartPercent = _speedStartPercent.ValueDecimal;
            _ladder.SpeedFullPercent = _speedFullPercent.ValueDecimal;
            _ladder.SpeedWeight = _speedWeight.ValueDecimal;
            _ladder.EuphoriaDevStartPercent = _euphoriaDevStartPercent.ValueDecimal;
            _ladder.EuphoriaDevFullPercent = _euphoriaDevFullPercent.ValueDecimal;
            _ladder.EuphoriaWeight = _euphoriaWeight.ValueDecimal;
            _ladder.StepUp = _stepUp.ValueDecimal;
            _ladder.StepDown = _stepDown.ValueDecimal;
            _ladder.MinKStep = _minKStep.ValueDecimal;
        }

        #endregion

        #region Portfolio

        private List<KorovinAsset> BuildAssets(List<BotTabSimple> stockTabs, DateTime barTime)
        {
            List<KorovinAsset> result = new List<KorovinAsset>();

            for (int i = 0; i < stockTabs.Count; i++)
            {
                KorovinAsset asset = CreateAsset(stockTabs[i], KorovinAssetType.Stock, barTime);

                if (asset != null)
                {
                    result.Add(asset);
                }
            }

            KorovinAsset gold = CreateAsset(_tabGold, KorovinAssetType.Gold, barTime);

            if (gold != null)
            {
                result.Add(gold);
            }

            KorovinAsset lqdt = CreateAsset(_tabLqdt, KorovinAssetType.Lqdt, barTime);

            if (lqdt != null)
            {
                result.Add(lqdt);
            }

            return result;
        }

        private KorovinAsset CreateAsset(BotTabSimple tab, KorovinAssetType type, DateTime barTime)
        {
            if (tab == null
                || tab.Security == null
                || string.IsNullOrWhiteSpace(tab.Security.Name))
            {
                return null;
            }

            bool hasCandles = tab.CandlesFinishedOnly != null
                && tab.CandlesFinishedOnly.Count > 0
                && tab.CandlesFinishedOnly[tab.CandlesFinishedOnly.Count - 1].Close > 0;

            if (hasCandles == false)
            {
                // без цены торговать нельзя, но и молча выбросить инструмент нельзя:
                // его позиция входит в стоимость счёта у брокера, и если не вычесть её из
                // капитала, она превратится в фантомные свободные деньги
                return CreateAssetWithoutPrice(tab, type);
            }

            Candle lastCandle = tab.CandlesFinishedOnly[tab.CandlesFinishedOnly.Count - 1];

            KorovinAsset asset = new KorovinAsset();

            asset.Tab = tab;
            asset.Type = type;
            asset.Name = tab.Security.Name;
            asset.Price = lastCandle.Close;
            asset.Lot = tab.Security.Lot > 0 ? tab.Security.Lot : 1m;
            asset.LastCandleTime = lastCandle.TimeStart;

            List<Position> positions = tab.PositionsOpenAll;

            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position == null
                    || position.Direction != Side.Buy)
                {
                    continue;
                }

                asset.Volume += position.OpenVolume;
                asset.InvestedAtCost += position.EntryPrice * position.OpenVolume * asset.Lot;
            }

            asset.Value = asset.Price * asset.Volume * asset.Lot;

            // бумага без свежих данных не торгуется. Лимит задан в барах, поэтому и меряем
            // в барах: в календарных днях он означал бы совсем другое на разных таймфреймах
            TimeSpan lag = barTime - lastCandle.TimeStart;

            // Точное совпадение времени слишком жёстко для реала: событие скринера приходит
            // через пару секунд после первой завершённой свечи, и бумага, не успевшая её
            // построить, отстаёт сразу на целый бар. Решение принимается раз в день, поэтому
            // такая бумага просто выпадала бы из торговли - систематически одни и те же
            // менее ликвидные. Допуск задаётся в барах: цена часовой давности при полосе
            // в проценты приемлема, а бумага остаётся управляемой
            if (_barLength > TimeSpan.Zero
                && _tradeToleranceBars.ValueInt > 0)
            {
                asset.IsTradable = lag <= TimeSpan.FromTicks(_barLength.Ticks * _tradeToleranceBars.ValueInt);
            }
            else
            {
                asset.IsTradable = lastCandle.TimeStart == barTime;
            }

            if (_barLength > TimeSpan.Zero)
            {
                asset.PriceIsStale = lag > TimeSpan.FromTicks(_barLength.Ticks * _staleBarsLimit.ValueInt);
            }
            else
            {
                asset.PriceIsStale = lag.TotalDays > _staleBarsLimit.ValueInt;
            }

            if (type == KorovinAssetType.Stock)
            {
                // золото и денежную позицию ведёт WarnIfStale под понятными именами,
                // иначе один и тот же инструмент считался бы дважды
                TrackStaleSource(asset.Name, asset.PriceIsStale, lastCandle.TimeStart, barTime);
            }

            return asset;
        }

        /// <summary>
        /// Инструмент без свечей: позиция оценивается по цене входа и помечается неторгуемой.
        /// Если позиции нет, инструмент можно пропустить - он ни на что не влияет
        /// </summary>
        private KorovinAsset CreateAssetWithoutPrice(BotTabSimple tab, KorovinAssetType type)
        {
            decimal lot = tab.Security.Lot > 0 ? tab.Security.Lot : 1m;

            decimal volume = 0;
            decimal invested = 0;

            List<Position> positions = tab.PositionsOpenAll;

            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position == null
                    || position.Direction != Side.Buy)
                {
                    continue;
                }

                volume += position.OpenVolume;
                invested += position.EntryPrice * position.OpenVolume * lot;
            }

            if (volume <= 0)
            {
                return null;
            }

            KorovinAsset asset = new KorovinAsset();

            asset.Tab = tab;
            asset.Type = type;
            asset.Name = tab.Security.Name;
            asset.Lot = lot;
            asset.Volume = volume;
            asset.InvestedAtCost = invested;
            asset.Price = volume > 0 ? invested / (volume * lot) : 0;
            asset.Value = invested;
            asset.IsTradable = false;
            asset.PriceIsStale = true;
            asset.LastCandleTime = DateTime.MinValue;

            if (_noPriceReported.Contains(asset.Name) == false)
            {
                _noPriceReported.Add(asset.Name);

                SendNewLogMessage("По инструменту " + asset.Name + " нет свечей. Позиция "
                    + Math.Round(invested, 0) + " учтена по цене входа и исключена из торговли",
                    LogMessageType.Error);
            }

            return asset;
        }

        private void EvaluatePortfolio(List<KorovinAsset> assets, out decimal equity, out decimal cash)
        {
            decimal positionsValue = 0;
            decimal investedAtCost = 0;

            for (int i = 0; i < assets.Count; i++)
            {
                positionsValue += assets[i].Value;
                investedAtCost += assets[i].InvestedAtCost;
            }

            CheckProfitMarketSetting();
            CheckParameters();

            decimal portfolioValue = GetPortfolioValue();

            _lastPortfolioValue = portfolioValue;
            _lastInvestedAtCost = investedAtCost;
            _lastPositionsValue = positionsValue;

            if (IsFullPortfolioValueMode())
            {
                equity = portfolioValue;
                cash = portfolioValue - positionsValue;
            }
            else
            {
                cash = portfolioValue - investedAtCost;
                equity = cash + positionsValue;
            }

            _lastCashRaw = cash;

            if (cash < 0)
            {
                // Робот считает позиции иначе, чем брокер: либо часть позиций ему не видна,
                // либо портфель отдаёт не ту величину. Из всех расхождений это самое опасное
                if (_negativeCashErrorSent == false)
                {
                    _negativeCashErrorSent = true;

                    SendExecutionProblem("Перевложение: свободных денег " + Math.Round(cash)
                        + ". Стоимость известных роботу позиций больше стоимости портфеля - "
                        + "проверьте, все ли позиции счёта видны роботу");
                }

                // на покупки таких денег нет, но в оценке портфеля перевложение учтено
                cash = 0;
            }
            else if (_negativeCashErrorSent)
            {
                _negativeCashErrorSent = false;

                SendNewLogMessage("Перевложение устранено, свободные деньги снова положительные",
                    LogMessageType.System);
            }
        }

        /// <summary>
        /// В тестере расчёт портфеля можно выключить. Тогда ValueCurrent навсегда остаётся
        /// стартовым депозитом: прибыль не реинвестируется, и ребалансировщик,
        /// который считает доли от стоимости портфеля, показывает бессмысленный результат
        /// </summary>
        private void CheckProfitMarketSetting()
        {
            if (_profitMarketChecked)
            {
                return;
            }

            _profitMarketChecked = true;

            if (StartProgram != StartProgram.IsTester)
            {
                return;
            }

            try
            {
                if (_tabLqdt == null
                    || _tabLqdt.Connector == null)
                {
                    return;
                }

                TesterServer server = _tabLqdt.Connector.MyServer as TesterServer;

                if (server == null
                    || server.ProfitMarketIsOn)
                {
                    return;
                }

                SendNewLogMessage("В настройках тестера выключен расчёт портфеля. "
                    + "Стоимость портфеля не будет меняться, прибыль не реинвестируется, "
                    + "и результат ребалансировщика будет некорректным. "
                    + "Включите опцию расчёта портфеля", LogMessageType.Error);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Проверка параметров на противоречия. Выполняется один раз при первой работе:
        /// такие ошибки иначе видны только по итогам прогона
        /// </summary>
        private void CheckParameters()
        {
            if (_parametersChecked)
            {
                return;
            }

            _parametersChecked = true;

            List<string> problems = new List<string>();

            decimal stocksBase = _stocksBasePercent.ValueDecimal;
            decimal goldBase = _goldBasePercent.ValueDecimal;
            decimal cashBase = _cashBasePercent.ValueDecimal;

            if (stocksBase + goldBase + cashBase != 100m)
            {
                problems.Add("сумма базовых долей не равна 100, веса будут нормализованы");
            }

            if (cashBase < _cashMinPercent.ValueDecimal)
            {
                problems.Add("Cash base percent меньше Cash min percent");
            }

            if (goldBase < _goldMinPercent.ValueDecimal)
            {
                problems.Add("Gold base percent меньше Gold min percent");
            }

            if (stocksBase > _stocksMaxPercent.ValueDecimal)
            {
                problems.Add("Stocks base percent больше Stocks max percent");
            }

            if (stocksBase < _stocksMinPercent.ValueDecimal)
            {
                problems.Add("Stocks base percent меньше Stocks min percent");
            }

            decimal reserve = (cashBase - _cashMinPercent.ValueDecimal)
                + (goldBase - _goldMinPercent.ValueDecimal);

            decimal roomToMax = _stocksMaxPercent.ValueDecimal - stocksBase;

            if (reserve <= 0)
            {
                problems.Add("резерв под докупку равен нулю: лестница работать не будет");
            }

            if (roomToMax <= 0)
            {
                problems.Add("Stocks max percent не оставляет места под докупку");
            }

            if (_ddStartPercent.ValueDecimal >= _ddFullPercent.ValueDecimal)
            {
                problems.Add("DD start percent не меньше DD full percent");
            }


            if (_speedStartPercent.ValueDecimal >= _speedFullPercent.ValueDecimal)
            {
                problems.Add("Speed start percent не меньше Speed full percent");
            }

            if (_euphoriaDevStartPercent.ValueDecimal >= _euphoriaDevFullPercent.ValueDecimal)
            {
                problems.Add("Euphoria dev start percent не меньше Euphoria dev full percent");
            }

            if (_minKStep.ValueDecimal > _stepUp.ValueDecimal)
            {
                problems.Add("Min k step больше Step up: докупка не сможет сработать");
            }

            if (reserve < roomToMax)
            {
                SendNewLogMessage("Резерв под докупку " + Math.Round(reserve, 1)
                    + " п.п. меньше, чем разрешает Stocks max percent ("
                    + Math.Round(roomToMax, 1) + " п.п.). Полная загрузка даст "
                    + Math.Round(stocksBase + reserve, 1) + "% акций", LogMessageType.System);
            }

            for (int i = 0; i < problems.Count; i++)
            {
                SendNewLogMessage("Параметры: " + problems[i], LogMessageType.Error);
            }

            WarnAboutTesterDividendModel();
        }

        /// <summary>
        /// В тестере дивиденды зачисляются как OpenVolume x цена x доходность, без размера лота
        /// (TesterServer.ProcessDividendForPosition). Объём в OsEngine считается в лотах, а деньги
        /// позиции - это объём x цена x лот, поэтому при лоте больше единицы в портфель приходит
        /// ровно 1/лот от дивиденда. При лоте 1000 это практически ноль.
        ///
        /// В журнале этого не видно: тестер рисует синтетическую позицию TICKER_divs, её прибыль
        /// считается через ProfitPortfolioAbs уже с лотом, а денег в портфель эта позиция
        /// не добавляет - BotTabSimple пропускает AddProfit для имён на _divs. То есть
        /// статистика показывает дивиденды, которых портфель не получал.
        ///
        /// Для робота, живущего с дивидендов, это ломает весь прогон, а не только веса:
        /// доход просто не реинвестируется. Поэтому лоты в тестере надо держать равными 1 -
        /// округление по лоту стоит меньше процента, а потеря дивидендов десятки.
        /// В реальной торговле расхождения нет - там дивиденды приходят от брокера
        /// </summary>
        private void WarnAboutTesterDividendModel()
        {
            if (StartProgram == StartProgram.IsOsTrader)
            {
                return;
            }

            List<BotTabSimple> tabs = new List<BotTabSimple>();

            if (_tabStocks != null
                && _tabStocks.Tabs != null)
            {
                tabs.AddRange(_tabStocks.Tabs);
            }

            string bad = "";

            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i] == null
                    || tabs[i].Security == null
                    || tabs[i].Security.Lot <= 1)
                {
                    continue;
                }

                bad += tabs[i].Security.Name + " (лот " + tabs[i].Security.Lot + ") ";
            }

            if (bad.Length == 0)
            {
                return;
            }

            SendNewLogMessage("Прогон недостоверен: тестер зачисляет в портфель только 1/лот "
                + "дивиденда, хотя в журнале он показан полным. Доход по этим бумагам "
                + "не реинвестируется, и итог прогона занижен. Ставьте лот = 1 в настройках "
                + "бумаги в тестере: округление по лоту стоит меньше процента, потеря "
                + "дивидендов - десятки. Бумаги с лотом больше 1: " + bad,
                LogMessageType.Error);
        }

        private bool IsFullPortfolioValueMode()
        {
            if (_portfolioValueMode.ValueString == "FullPortfolioValue")
            {
                return true;
            }

            if (_portfolioValueMode.ValueString == "CashAndRealized")
            {
                return false;
            }

            return StartProgram == StartProgram.IsOsTrader;
        }

        /// <summary>
        /// Свободные рубли по данным брокера или -1, если их не определить.
        ///
        /// Это не то же самое, что cash из EvaluatePortfolio. Тот считается как
        /// ValueCurrent - стоимость позиций, а ValueCurrent у большинства коннекторов -
        /// полная стоимость счёта: у TInvest это TotalAmountPortfolio, у Алора
        /// portfolioLiquidationValue. От продажи бумаги такая величина не меняется вовсе -
        /// бумага меняется на деньги той же стоимости. Значит вычисленный cash растёт
        /// в тот момент, когда уменьшилась позиция в табе, а не когда пришли деньги,
        /// и проверять им зачисление бессмысленно: она повторяет проверку по состоянию заявки.
        ///
        /// Настоящие свободные деньги лежат отдельной денежной позицией портфеля. У TInvest
        /// это позиция валюты rub, и в неё уже заложены заблокированные средства
        /// (valuePortfolio - blockRub). Если такой позиции нет - у коннектора её может
        /// не быть вовсе, - возвращаем -1, и проверка по деньгам просто пропускается:
        /// блокировать из-за отсутствия данных план нельзя
        /// </summary>
        private decimal GetBrokerFreeMoney()
        {
            try
            {
                Portfolio portfolio = GetTradePortfolio();

                if (portfolio == null)
                {
                    return -1;
                }

                List<PositionOnBoard> poses = portfolio.GetPositionOnBoard();

                if (poses == null)
                {
                    return -1;
                }

                for (int i = 0; i < poses.Count; i++)
                {
                    if (poses[i] == null
                        || string.IsNullOrEmpty(poses[i].SecurityNameCode))
                    {
                        continue;
                    }

                    if (poses[i].SecurityNameCode.Equals("rub",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return poses[i].ValueCurrent;
                    }
                }

                return -1;
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);

                return -1;
            }
        }

        private Portfolio GetTradePortfolio()
        {
            if (_tabLqdt != null
                && _tabLqdt.Portfolio != null)
            {
                return _tabLqdt.Portfolio;
            }

            if (_tabGold != null
                && _tabGold.Portfolio != null)
            {
                return _tabGold.Portfolio;
            }

            if (_tabStocks != null
                && _tabStocks.Tabs != null
                && _tabStocks.Tabs.Count > 0)
            {
                return _tabStocks.Tabs[0].Portfolio;
            }

            return null;
        }

        private decimal GetPortfolioValue()
        {
            Portfolio portfolio = GetTradePortfolio();

            if (portfolio == null)
            {
                return 0;
            }

            if (portfolio.ValueCurrent != 0)
            {
                return portfolio.ValueCurrent;
            }

            return portfolio.ValueBegin;
        }

        #endregion

        #region Target weights

        private void BuildTargetWeights(List<KorovinAsset> assets, decimal equity)
        {
            decimal stocksBase = _stocksBasePercent.ValueDecimal;
            decimal goldBase = _goldBasePercent.ValueDecimal;
            decimal cashBase = _cashBasePercent.ValueDecimal;

            decimal sum = stocksBase + goldBase + cashBase;

            if (sum <= 0)
            {
                return;
            }

            if (sum != 100m)
            {
                stocksBase = stocksBase / sum * 100m;
                goldBase = goldBase / sum * 100m;
                cashBase = cashBase / sum * 100m;
            }

            decimal cashRoom = cashBase - _cashMinPercent.ValueDecimal;
            decimal goldRoom = goldBase - _goldMinPercent.ValueDecimal;

            if (cashRoom < 0)
            {
                cashRoom = 0;
            }

            if (goldRoom < 0)
            {
                goldRoom = 0;
            }

            decimal shift = GetShiftPercent(stocksBase, cashRoom, goldRoom);

            decimal fromCash;
            decimal fromGold;

            SplitShift(shift, cashRoom, goldRoom, out fromCash, out fromGold);

            decimal stocksWeight = stocksBase + fromCash + fromGold;
            decimal goldWeight = goldBase - fromGold;
            decimal cashWeight = cashBase - fromCash;

            if (stocksWeight > _stocksMaxPercent.ValueDecimal)
            {
                cashWeight += stocksWeight - _stocksMaxPercent.ValueDecimal;
                stocksWeight = _stocksMaxPercent.ValueDecimal;
            }

            if (stocksWeight < 0)
            {
                cashWeight += stocksWeight;
                stocksWeight = 0;
            }

            DistributeStockWeights(assets, stocksWeight, equity);

            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].Type == KorovinAssetType.Gold)
                {
                    assets[i].TargetWeight = goldWeight;
                }
                else if (assets[i].Type == KorovinAssetType.Lqdt)
                {
                    assets[i].TargetWeight = cashWeight;
                }
            }
        }

        /// <summary>
        /// Сдвиг доли акций в процентных пунктах - доля от физически доступного резерва,
        /// поэтому параметры не могут потребовать больше, чем есть
        /// </summary>
        private decimal GetShiftPercent(decimal stocksBase, decimal cashRoom, decimal goldRoom)
        {
            decimal reserveUp = cashRoom + goldRoom;
            decimal roomToMax = _stocksMaxPercent.ValueDecimal - stocksBase;

            if (reserveUp > roomToMax)
            {
                reserveUp = roomToMax;
            }

            if (reserveUp < 0)
            {
                reserveUp = 0;
            }

            decimal reserveDown = stocksBase - _stocksMinPercent.ValueDecimal;

            if (reserveDown < 0)
            {
                reserveDown = 0;
            }

            _lastReserveUp = reserveUp;
            _lastReserveDown = reserveDown;

            decimal load = GetEffectiveLoad();

            if (load >= 0)
            {
                return load * reserveUp;
            }

            return load * reserveDown;
        }

        /// <summary>
        /// Откуда берём деньги под сдвиг: сначала кэш, сначала золото или пропорционально
        /// </summary>
        private void SplitShift(decimal shift, decimal cashRoom, decimal goldRoom,
            out decimal fromCash, out decimal fromGold)
        {
            fromCash = 0;
            fromGold = 0;

            if (shift <= 0)
            {
                // избыток акций возвращаем в кэш
                fromCash = shift;
                return;
            }

            if (_reserveOrder.ValueString == "GoldFirst")
            {
                fromGold = shift > goldRoom ? goldRoom : shift;
                decimal rest = shift - fromGold;
                fromCash = rest > cashRoom ? cashRoom : rest;
                return;
            }

            if (_reserveOrder.ValueString == "Proportional")
            {
                decimal total = cashRoom + goldRoom;

                if (total <= 0)
                {
                    return;
                }

                fromCash = shift * cashRoom / total;
                fromGold = shift * goldRoom / total;
                return;
            }

            fromCash = shift > cashRoom ? cashRoom : shift;
            decimal restCash = shift - fromCash;
            fromGold = restCash > goldRoom ? goldRoom : restCash;
        }

        private void DistributeStockWeights(List<KorovinAsset> assets, decimal stocksWeight, decimal equity)
        {
            List<KorovinAsset> stocks = new List<KorovinAsset>();

            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].Type == KorovinAssetType.Stock
                    && assets[i].Price > 0)
                {
                    stocks.Add(assets[i]);
                }
            }

            if (stocks.Count == 0)
            {
                return;
            }

            decimal[] shares = new decimal[stocks.Count];
            decimal sharesSum = 0;

            for (int i = 0; i < stocks.Count; i++)
            {
                shares[i] = 1m / stocks.Count;

                if (_stockTilt.ValueString == "On")
                {
                    shares[i] = shares[i] * GetTilt(stocks, i);
                }

                sharesSum += shares[i];
            }

            if (sharesSum <= 0)
            {
                return;
            }

            for (int i = 0; i < stocks.Count; i++)
            {
                stocks[i].TargetWeight = stocksWeight * shares[i] / sharesSum;
            }

            // заморозка: докупать нельзя, вес фиксируется на фактическом
            decimal released = 0;
            decimal freeWeight = 0;

            for (int i = 0; i < stocks.Count; i++)
            {
                if (IsFrozen(stocks[i].Name) == false)
                {
                    freeWeight += stocks[i].TargetWeight;
                    continue;
                }

                decimal factWeight = equity > 0 ? stocks[i].Value / equity * 100m : 0;

                if (factWeight < stocks[i].TargetWeight)
                {
                    released += stocks[i].TargetWeight - factWeight;
                    stocks[i].TargetWeight = factWeight;
                }

                stocks[i].IsFrozen = true;
            }

            if (released <= 0
                || freeWeight <= 0)
            {
                return;
            }

            for (int i = 0; i < stocks.Count; i++)
            {
                if (stocks[i].IsFrozen)
                {
                    continue;
                }

                stocks[i].TargetWeight += released * stocks[i].TargetWeight / freeWeight;
            }
        }

        private decimal GetTilt(List<KorovinAsset> stocks, int index)
        {
            List<decimal> returns = new List<decimal>();

            for (int i = 0; i < stocks.Count; i++)
            {
                returns.Add(GetTotalReturn(stocks[i].Name, _tiltLookback.ValueInt));
            }

            decimal median = GetMedian(returns);

            decimal tilt = 1m + _tiltStrength.ValueDecimal * (median - returns[index]) / 100m;

            if (tilt < _tiltMin.ValueDecimal)
            {
                tilt = _tiltMin.ValueDecimal;
            }

            if (tilt > _tiltMax.ValueDecimal)
            {
                tilt = _tiltMax.ValueDecimal;
            }

            return tilt;
        }

        private decimal GetMedian(List<decimal> values)
        {
            if (values == null
                || values.Count == 0)
            {
                return 0;
            }

            List<decimal> sorted = new List<decimal>(values);
            sorted.Sort();

            int middle = sorted.Count / 2;

            if (sorted.Count % 2 == 1)
            {
                return sorted[middle];
            }

            return (sorted[middle - 1] + sorted[middle]) / 2m;
        }

        /// <summary>
        /// Доходность бумаги за период с учётом дивидендов, в процентах
        /// </summary>
        private decimal GetTotalReturn(string securityName, int lookbackDays)
        {
            KorovinDailySeries series;

            if (_securitySeries.TryGetValue(securityName, out series) == false
                || series.Count < 2)
            {
                return 0;
            }

            int fromIndex = series.Count - 1 - lookbackDays;

            if (fromIndex < 0)
            {
                fromIndex = 0;
            }

            decimal startPrice = series.Closes[fromIndex];

            if (startPrice <= 0)
            {
                return 0;
            }

            decimal endPrice = series.CurrentPrice > 0 ? series.CurrentPrice : series.Closes[series.Count - 1];

            _dividends.Load(securityName, series.CurrentDate);

            decimal dividends = _dividends.GetAmountInPeriod(securityName,
                series.Dates[fromIndex], series.CurrentDate);

            return (endPrice + dividends) / startPrice * 100m - 100m;
        }

        #endregion

        #region Frozen securities and dividends

        private void UpdateFrozenSecurities(List<KorovinAsset> assets, DateTime barTime)
        {
            if (_idioGuard.ValueString == "Off")
            {
                _frozenSecurities.Clear();
                return;
            }

            if (_totalReturnIndex.Values == null
                || _totalReturnIndex.Values.Count < _idioLookback.ValueInt)
            {
                // без истории индекса сравнивать не с чем, ложные заморозки не нужны
                return;
            }

            decimal indexReturn = GetIndexReturn(_idioLookback.ValueInt);

            for (int i = 0; i < assets.Count; i++)
            {
                KorovinAsset asset = assets[i];

                if (asset.Type != KorovinAssetType.Stock)
                {
                    continue;
                }

                decimal securityReturn = GetTotalReturn(asset.Name, _idioLookback.ValueInt);
                decimal lag = indexReturn - securityReturn;

                bool wasFrozen = IsFrozen(asset.Name);

                if (wasFrozen == false
                    && lag >= _idioThresholdPercent.ValueDecimal)
                {
                    _frozenSecurities.Add(asset.Name);
                    SendNewLogMessage("Бумага " + asset.Name + " отстала от индекса на " +
                        Math.Round(lag, 1) + " п.п. Докупка запрещена", LogMessageType.System);
                }
                else if (wasFrozen
                    && lag < _idioThresholdPercent.ValueDecimal / 2m)
                {
                    _frozenSecurities.Remove(asset.Name);
                    SendNewLogMessage("Бумага " + asset.Name + " вернулась к индексу, запрет докупки снят",
                        LogMessageType.System);
                }
            }
        }

        /// <summary>
        /// Сколько бумаг было в позиции на указанную дату. Открытые позиции берутся по текущему
        /// остатку, закрытые - по максимальному объёму, если на ту дату они ещё жили
        /// </summary>
        private decimal GetVolumeAtDate(BotTabSimple tab, DateTime date)
        {
            if (tab == null)
            {
                return 0;
            }

            decimal volume = 0;

            volume += GetVolumeAtDate(tab.PositionsOpenAll, date);
            volume += GetVolumeAtDate(tab.PositionsCloseAll, date);

            return volume;
        }

        /// <summary>
        /// Сколько бумаг было в позициях на указанную дату.
        ///
        /// Считается по сделкам позиции, а не по её текущему остатку: внутри одной позиции
        /// могли быть частичные продажи и докупки, и после отсечки остаток уже не равен тому,
        /// что было в день закрытия реестра. Дивиденд платят по факту владения на дату,
        /// поэтому объём восстанавливается сложением сделок до этой даты.
        ///
        /// Если сделок не видно (позиция поднята из журнала без них), берётся прежняя
        /// приблизительная оценка - лучше приблизительно, чем ноль
        /// </summary>
        private decimal GetVolumeAtDate(List<Position> positions, DateTime date)
        {
            decimal volume = 0;

            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position == null
                    || position.Direction != Side.Buy
                    || position.TimeOpen.Date > date.Date)
                {
                    continue;
                }

                List<MyTrade> trades = position.MyTrades;

                if (trades == null
                    || trades.Count == 0)
                {
                    // сделок нет: для открытой позиции остаётся текущий остаток,
                    // для закрытой - её максимальный объём, если на ту дату она ещё жила
                    if (position.State == PositionStateType.Open)
                    {
                        volume += position.OpenVolume;
                    }
                    else if (position.TimeClose.Date > date.Date)
                    {
                        volume += position.MaxVolume;
                    }

                    continue;
                }

                decimal atDate = 0;

                for (int t = 0; t < trades.Count; t++)
                {
                    MyTrade trade = trades[t];

                    if (trade == null
                        || trade.Time.Date > date.Date)
                    {
                        continue;
                    }

                    atDate += trade.Side == Side.Buy ? trade.Volume : -trade.Volume;
                }

                if (atDate > 0)
                {
                    volume += atDate;
                }
            }

            return volume;
        }

        private bool IsFrozen(string securityName)
        {
            return _frozenSecurities.Contains(securityName);
        }

        private decimal GetIndexReturn(int lookbackDays)
        {
            List<decimal> values = _totalReturnIndex.Values;

            if (values == null
                || values.Count < 2)
            {
                return 0;
            }

            int fromIndex = values.Count - 1 - lookbackDays;

            if (fromIndex < 0)
            {
                fromIndex = 0;
            }

            decimal startValue = values[fromIndex];

            if (startValue <= 0)
            {
                return 0;
            }

            decimal endValue = _totalReturnIndex.CurrentValue > 0
                ? _totalReturnIndex.CurrentValue
                : values[values.Count - 1];

            return endValue / startValue * 100m - 100m;
        }

        /// <summary>
        /// Дивиденды, начисленные после последней плановой ребалансировки.
        /// Нужны, чтобы отсечка не выглядела ни просадкой, ни поводом для внеплановой ребалансировки
        /// </summary>
        private void UpdatePendingDividends(List<KorovinAsset> assets, DateTime barTime)
        {
            DateTime from = _state.LastScheduledDate;

            if (from == DateTime.MinValue
                || (barTime.Date - from).TotalDays > _dividendHoldDays.ValueInt)
            {
                from = barTime.Date.AddDays(-_dividendHoldDays.ValueInt);
            }

            Dictionary<string, decimal> fresh = new Dictionary<string, decimal>();

            for (int i = 0; i < assets.Count; i++)
            {
                KorovinAsset asset = assets[i];

                if (asset.Type != KorovinAssetType.Stock
                    || asset.Volume <= 0)
                {
                    continue;
                }

                _dividends.Load(asset.Name, barTime);

                List<KeyValuePair<DateTime, decimal>> payments =
                    _dividends.GetPaymentsInPeriod(asset.Name, from, barTime.Date);

                if (payments.Count == 0)
                {
                    continue;
                }

                decimal money = 0;

                for (int p = 0; p < payments.Count; p++)
                {
                    // объём берётся на дату фиксации права, а не текущий: купленное после
                    // отсечки дивиденда не даёт, проданное после отсечки его не отменяет
                    decimal volumeAtRecord = GetVolumeAtDate(asset.Tab, payments[p].Key);

                    if (volumeAtRecord <= 0)
                    {
                        continue;
                    }

                    money += payments[p].Value * volumeAtRecord * asset.Lot
                        * (100m - _dividendTaxPercent.ValueDecimal) / 100m;
                }

                if (money <= 0)
                {
                    continue;
                }

                // один и тот же тикер может стоять в скринере дважды: складываем,
                // а не падаем на дубликате ключа
                decimal already;

                if (fresh.TryGetValue(asset.Name, out already))
                {
                    fresh[asset.Name] = already + money;
                }
                else
                {
                    fresh.Add(asset.Name, money);
                }

                asset.PendingDividend = money;

                // видно, что нейтрализация действительно работает и на какую сумму
                decimal known;

                if (_state.PendingDividends.TryGetValue(asset.Name, out known) == false
                    || Math.Abs(known - money) > 1m)
                {
                    SendNewLogMessage("Дивиденд " + asset.Name + ": " + Math.Round(money)
                        + " руб. после налога, выплат в окне " + payments.Count
                        + ". Гэп компенсируется в триггерах: "
                        + _dividendGapNeutral.ValueString, LogMessageType.System);
                }
            }

            _state.PendingDividends = fresh;
        }

        /// <summary>
        /// Проверить свежесть базы дивидендов и при необходимости запустить обновление.
        ///
        /// Только в реальной торговле: в тестере и оптимизаторе история дивидендов лежит
        /// на диске готовой и меняться не должна, иначе прогоны перестанут быть воспроизводимыми.
        /// Проверка идёт раз в день после указанного времени - файлы переписывает внешний
        /// процесс, дёргать его на каждом баре незачем
        /// </summary>
        private void CheckDividendsUpdate(DateTime barTime)
        {
            try
            {
                if (StartProgram != StartProgram.IsOsTrader
                    || _autoUpdateDividends.ValueString == "Off")
                {
                    return;
                }

                if (_lastDividendsCheckDate.Date == barTime.Date)
                {
                    return;
                }

                TimeSpan checkTime = new TimeSpan(_dividendsUpdateCheckTime.Value.Hour,
                    _dividendsUpdateCheckTime.Value.Minute, 0);

                if (barTime.TimeOfDay < checkTime)
                {
                    return;
                }

                // отметку ставим до запуска: если обновление сорвётся, повторять его
                // в тот же день не нужно - следующая попытка будет завтра
                _lastDividendsCheckDate = barTime;

                if (IsDividendsBaseStale(barTime) == false)
                {
                    return;
                }

                StartDividendsUpdate("база дивидендов не обновлялась дольше "
                    + _dividendsMaxAgeDays.ValueInt + " дн.");
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Запустить внешний DividendsUpdater. Процесс долгий и ждёт своего завершения,
        /// поэтому уходит в отдельный поток: главный цикл робота стоять на нём не должен.
        ///
        /// После обновления сбрасывается кэш дивидендов - иначе робот продолжил бы работать
        /// с тем, что загрузил раньше, и обновление осталось бы без эффекта до перезапуска.
        /// Пересобирать ряды руками не нужно: total return индекса строится заново на каждом
        /// баре, поэтому на ближайшем расчёте дивиденды подтянутся из обновлённых файлов
        /// </summary>
        private void StartDividendsUpdate(string reason)
        {
            if (_dividendsUpdating)
            {
                SendNewLogMessage("Обновление базы дивидендов уже идёт", LogMessageType.System);
                return;
            }

            _dividendsUpdating = true;

            SendNewLogMessage("Запуск обновления базы дивидендов: " + reason, LogMessageType.System);

            Task.Run(() =>
            {
                try
                {
                    DateTime startTime = DateTime.Now;

                    WikiMaster.UpdateDividendsBase();

                    // убеждаемся, что запуск действительно что-то записал: WikiMaster ничего
                    // не возвращает, а сам updater завершается кодом 0 даже когда не разобрал ни
                    // одной бумаги. Единственный надёжный признак работы - файлы, переписанные
                    // за время запуска. Отметку не ставим - завтра попробуем снова
                    if (GetDividendsLastWriteTime() < startTime)
                    {
                        SendNewLogMessage("Обновление базы дивидендов отработало, но не переписало "
                            + "ни одного файла: база осталась прежней, повтор завтра. Смотрите лог "
                            + "OsEngine и вывод DividendsUpdater", LogMessageType.Error);
                        return;
                    }

                    // всё, что трогает общее состояние, делаем под тем же локом, под которым
                    // работает главный цикл. Сброс кэша иначе мог бы прийтись на середину
                    // расчёта, а запись состояния - совпасть с сохранением из ребалансировки:
                    // KorovinBotState.Save пишет через общий временный файл <state>.txt.tmp,
                    // и две одновременные записи мешают друг другу вплоть до File.Delete
                    // одного потока между File.Copy другого. Главный цикл держит _locker
                    // на весь Process, поэтому одного лока здесь достаточно.
                    // Файловый ввод-вывод внутри лока стоит миллисекунды и случается
                    // не чаще раза в сутки
                    lock (_locker)
                    {
                        _dividends.Clear();

                        // помним не время файлов, а время своего запроса: только по нему видно,
                        // что базу спрашивали, даже если новых объявлений по бумагам не было
                        _state.LastDividendsUpdateDate = DateTime.Now;
                        SaveState();
                    }

                    SendNewLogMessage("Обновление базы дивидендов завершено, кэш сброшен",
                        LogMessageType.System);
                }
                catch (Exception error)
                {
                    SendNewLogMessage("Ошибка обновления базы дивидендов: " + error,
                        LogMessageType.Error);
                }
                finally
                {
                    _dividendsUpdating = false;
                }
            });
        }

        /// <summary>
        /// Давно ли спрашивали базу. Смотрим на собственную отметку последнего запроса,
        /// а не на время файлов: файл тикера переписывается, только когда по нему вообще
        /// что-то нашлось, а отметка каталога в Windows меняется лишь при создании,
        /// удалении или переименовании записей. Если объявлений не было, ни то, ни другое
        /// не сдвинется, и база выглядела бы вечно устаревшей.
        ///
        /// Отметка живёт в файле состояния и переживает перезапуск терминала.
        /// Не спрашивали ни разу - считаем базу устаревшей
        /// </summary>
        private bool IsDividendsBaseStale(DateTime barTime)
        {
            DateTime lastUpdate = _state.LastDividendsUpdateDate;

            if (lastUpdate == DateTime.MinValue)
            {
                return true;
            }

            return (barTime - lastUpdate).TotalDays > _dividendsMaxAgeDays.ValueInt;
        }

        /// <summary>
        /// Время самого свежего файла базы. Нужно ровно для одного - убедиться, что запуск
        /// updater'а что-то записал. Как мера возраста базы не годится, см. IsDividendsBaseStale
        /// </summary>
        private DateTime GetDividendsLastWriteTime()
        {
            try
            {
                string path = GetDividendsBasePath();

                if (Directory.Exists(path) == false)
                {
                    return DateTime.MinValue;
                }

                string[] files = Directory.GetFiles(path, "*.md");

                DateTime lastWrite = DateTime.MinValue;

                for (int i = 0; i < files.Length; i++)
                {
                    DateTime writeTime = File.GetLastWriteTime(files[i]);

                    if (writeTime > lastWrite)
                    {
                        lastWrite = writeTime;
                    }
                }

                return lastWrite;
            }
            catch (Exception error)
            {
                SendNewLogMessage("Не удалось прочитать отметки файлов базы дивидендов: " + error,
                    LogMessageType.Error);

                // прочитать не смогли - считаем, что запись прошла: иначе робот будет дёргать
                // внешний процесс каждый день из-за проблемы с чтением каталога
                return DateTime.MaxValue;
            }
        }

        private string GetDividendsBasePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wiki", "Dividends");
        }

        /// <summary>
        /// Ручной запуск обновления. Нужен, когда ждать ежедневной проверки некогда -
        /// например, робота подняли после долгого простоя перед самой ребалансировкой
        /// </summary>
        private void UpdateDividendsButton_UserClickOnButtonEvent()
        {
            try
            {
                if (StartProgram != StartProgram.IsOsTrader)
                {
                    SendNewLogMessage("Обновление базы дивидендов доступно только в реальной "
                        + "торговле: в тестере и оптимизаторе история должна оставаться неизменной",
                        LogMessageType.Error);
                    return;
                }

                StartDividendsUpdate("ручной запрос");
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        #endregion

        #region Triggers

        private bool IsRebalanceTimeCome(DateTime barTime)
        {
            TimeSpan barTimeOfDay = barTime.TimeOfDay;
            TimeSpan target = new TimeSpan(_rebalanceTime.Value.Hour, _rebalanceTime.Value.Minute, 0);

            return barTimeOfDay >= target;
        }

        private string CheckTriggers(List<KorovinAsset> assets, decimal equity, decimal cash, DateTime barTime)
        {
            if (IsScheduledDay(barTime))
            {
                return "Scheduled";
            }

            string ladderReason = GetLadderReason();

            if (string.IsNullOrEmpty(ladderReason) == false)
            {
                return ladderReason;
            }

            bool gapNeutral = _dividendGapNeutral.ValueString == "On";

            decimal effectiveCash = cash;

            if (gapNeutral)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    effectiveCash -= assets[i].PendingDividend;
                }
            }

            if (effectiveCash > equity * _cashInflowTriggerPercent.ValueDecimal / 100m)
            {
                return "CashInflow";
            }

            for (int i = 0; i < assets.Count; i++)
            {
                KorovinAsset asset = assets[i];

                if (asset.Type == KorovinAssetType.Lqdt)
                {
                    // денежная позиция - остаток, её вес отдельно не выравнивается,
                    // но провал ниже неснижаемого уровня чинить надо
                    decimal minValue = equity * _cashMinPercent.ValueDecimal / 100m;

                    if (asset.Value < minValue - _minTradeMoney.ValueDecimal)
                    {
                        return "CashReserve";
                    }

                    continue;
                }

                // при Off дивидендный гэп не компенсируется: просевшая после отсечки бумага
                // выглядит недовешенной и подбирается как обычное отклонение веса
                decimal effectiveValue = gapNeutral
                    ? asset.Value + asset.PendingDividend
                    : asset.Value;

                decimal delta = asset.TargetMoney - effectiveValue;

                if (Math.Abs(delta) > asset.Band
                    && Math.Abs(delta) > _minTradeMoney.ValueDecimal)
                {
                    return "Deviation";
                }
            }

            return "";
        }

        /// <summary>
        /// Сдвинулась ли лестница. Проверяется отдельно от общего триггера: в день расписания
        /// CheckTriggers вернёт Scheduled раньше, и ступень лестницы могла бы потеряться
        /// </summary>
        private string GetLadderReason()
        {
            if (Math.Abs(GetEffectiveLoad() - _state.LastLoad) > 0.0001m)
            {
                return "LoadChange";
            }

            return "";
        }

        /// <summary>
        /// Не слишком ли рано для новой ступени. Ladder min days задаёт паузу между
        /// ребалансировками по лестнице: без неё дёрганый рынок гоняет портфель каждый день
        /// </summary>
        private bool IsLadderOnHold()
        {
            if (_ladderMinDays.ValueInt <= 0
                || _state.LastLadderDate == DateTime.MinValue
                || _lastBarTime == DateTime.MinValue)
            {
                return false;
            }

            return (_lastBarTime.Date - _state.LastLadderDate).TotalDays < _ladderMinDays.ValueInt;
        }

        /// <summary>
        /// Загрузка, по которой считаются целевые веса. Пока пауза между ступенями не вышла,
        /// робот работает по прежней загрузке: иначе ограничение было бы бессмысленным -
        /// новая загрузка всё равно применилась бы первой же ребалансировкой по другому поводу
        /// </summary>
        private decimal GetEffectiveLoad()
        {
            if (IsLadderOnHold())
            {
                return _state.LastLoad;
            }

            return _ladder.KApplied;
        }

        /// <summary>
        /// Наступил ли день плановой ребалансировки.
        ///
        /// Monthly и Weekly привязаны к явному дню: Schedule day - это число месяца или номер
        /// дня недели, и плановая идёт ровно в этот день, один раз в период. Если день пропущен
        /// (выходной, нет данных, робот был выключен), плановая проходит в первый следующий
        /// день периода - иначе она потерялась бы целиком.
        ///
        /// Interval отсчитывает дни от ЛЮБОЙ последней ребалансировки, включая лестничную:
        /// если робот только что перетряхнул портфель по лестнице, плановая поверх неё не нужна
        /// </summary>
        private bool IsScheduledDay(DateTime barTime)
        {
            DateTime today = barTime.Date;

            if (_schedule.ValueString == "Interval")
            {
                DateTime last = _state.LastRebalanceDate;

                if (last == DateTime.MinValue)
                {
                    return true;
                }

                return (today - last).TotalDays >= _intervalDays.ValueInt;
            }

            DateTime lastScheduled = _state.LastScheduledDate;

            if (_schedule.ValueString == "Weekly")
            {
                int targetDay = _scheduleDay.ValueInt;

                if (targetDay > 7)
                {
                    targetDay = 7;
                }

                // в DayOfWeek воскресенье это 0, а у нас 7: неделя человеческая
                int currentDay = (int)today.DayOfWeek;

                if (currentDay == 0)
                {
                    currentDay = 7;
                }

                if (lastScheduled == DateTime.MinValue)
                {
                    return currentDay >= targetDay;
                }

                if (GetWeekKey(lastScheduled) == GetWeekKey(today))
                {
                    return false;
                }

                return currentDay >= targetDay;
            }

            // Monthly
            if (lastScheduled == DateTime.MinValue)
            {
                return today.Day >= _scheduleDay.ValueInt;
            }

            if (lastScheduled.Year == today.Year
                && lastScheduled.Month == today.Month)
            {
                return false;
            }

            return today.Day >= _scheduleDay.ValueInt;
        }

        /// <summary>
        /// Номер недели, к которой относится день. Нужен, чтобы отличить "уже была на этой
        /// неделе" от "новая неделя началась"
        /// </summary>
        private static int GetWeekKey(DateTime date)
        {
            int day = (int)date.DayOfWeek;

            if (day == 0)
            {
                day = 7;
            }

            DateTime monday = date.Date.AddDays(1 - day);

            return (int)(monday - new DateTime(2000, 1, 3)).TotalDays / 7;
        }




        /// <summary>
        /// Запомнить отметки «повод отработан» до того, как их перепишет регистрация.
        /// Снимок делается всегда: он нужен и для полного отката по продажам акций,
        /// и для частичного, когда не сработала продажа денежной позиции
        /// </summary>
        private KorovinRebalanceRollback CreateRollbackPoint(DateTime barTime, string reason,
            List<Order> sellOrders)
        {
            KorovinRebalanceRollback rollback = new KorovinRebalanceRollback();

            rollback.BarTime = barTime;
            rollback.Reason = reason;
            rollback.SellOrders = sellOrders ?? new List<Order>();
            rollback.LastRebalanceDate = _state.LastRebalanceDate;
            rollback.LastScheduledDate = _state.LastScheduledDate;
            rollback.LastLadderDate = _state.LastLadderDate;
            rollback.LastLoad = _state.LastLoad;

            return rollback;
        }

        /// <summary>
        /// Проверить, исполнилась ли хоть одна продажа прошлой ребалансировки, и если нет -
        /// откатить её.
        ///
        /// Деньги от продаж засчитываются в момент отправки: CloseAtMarket возвращает
        /// управление сразу, а дожидаться исполнения внутри бара нельзя - на этом построена
        /// вся двухфазная схема (продажи сейчас, покупки следующим баром). Поэтому
        /// немедленная проверка sellsMoney меньше либо равно нулю ловит только «заявка
        /// не ушла»: отказ брокера приходит уже после отправки и под неё не попадает.
        ///
        /// Без отката ребалансировка засчитывалась бы при неизменившемся портфеле. Для мелкой
        /// ступени лестницы это потеря насовсем: LastLoad уже равен новой загрузке, поэтому
        /// LoadChange не сработает, а Deviation не увидит ничего - дельты мелкой ступени лежат
        /// внутри полосы допуска, ради чего лестничный повод и заведён отдельно. Ступень
        /// пропала бы до следующего движения лестницы.
        ///
        /// Откат безопасен, даже если зависшая заявка исполнится позже: продажи считаются
        /// от текущих весов, а не от запомненного плана, поэтому лишней продажи не будет -
        /// ближайшая ребалансировка просто увидит веса уже верными.
        ///
        /// Проверка живёт в памяти: после перезапуска терминала она теряется, и поведение
        /// возвращается к прежнему. Это осознанный размен - хранить в состоянии ссылки
        /// на заявки нечем, а цена потери одна ступень
        /// </summary>
        private void VerifyPreviousSells(DateTime barTime)
        {
            if (_sellsToVerify == null)
            {
                return;
            }

            // исполнение приходит асинхронно: на том же баре его ещё может не быть
            if (barTime <= _sellsToVerify.BarTime)
            {
                return;
            }

            KorovinRebalanceRollback rollback = _sellsToVerify;
            _sellsToVerify = null;

            // продажи акций и продажа денежной позиции проверяются раздельно. Слить их
            // в один счёт нельзя: исполнившиеся продажи акций замаскировали бы провал
            // денежной ноги, а последствия у них разные - там не изменился портфель,
            // здесь не хватило денег на покупки
            if (rollback.SellOrders.Count > 0
                && GetExecutedVolume(rollback.SellOrders) <= 0)
            {
                RollbackWholeRebalance(rollback);
                return;
            }

            if (rollback.LqdtOrders.Count > 0
                && GetExecutedVolume(rollback.LqdtOrders) <= 0)
            {
                RollbackLadderStep(rollback, "заявки на продажу денежной позиции "
                    + "не исполнились");
            }
        }

        private static decimal GetExecutedVolume(List<Order> orders)
        {
            decimal executed = 0;

            for (int i = 0; i < orders.Count; i++)
            {
                Order order = orders[i];

                if (order == null)
                {
                    continue;
                }

                executed += order.VolumeExecute;
            }

            return executed;
        }

        /// <summary>
        /// Продажи акций не исполнились ни по одной бумаге: портфель не изменился,
        /// и ребалансировка не должна считаться состоявшейся вовсе
        /// </summary>
        /// <summary>
        /// Сколько денег реально принесла продажа денежной позиции
        /// </summary>
        private decimal GetLqdtExecutedMoney(KorovinRebalanceRollback rollback)
        {
            if (rollback == null
                || rollback.LqdtOrders == null
                || rollback.LqdtOrders.Count == 0)
            {
                return 0;
            }

            decimal lot = _tabLqdt == null || _tabLqdt.Security == null
                ? 1m
                : _tabLqdt.Security.Lot;

            if (lot <= 0)
            {
                lot = 1m;
            }

            decimal money = 0;

            for (int i = 0; i < rollback.LqdtOrders.Count; i++)
            {
                Order order = rollback.LqdtOrders[i];

                if (order == null)
                {
                    continue;
                }

                money += order.VolumeExecute * order.PriceReal * lot;
            }

            return money;
        }

        private void RollbackWholeRebalance(KorovinRebalanceRollback rollback)
        {
            _state.LastRebalanceDate = rollback.LastRebalanceDate;
            _state.LastScheduledDate = rollback.LastScheduledDate;
            _state.LastLadderDate = rollback.LastLadderDate;
            _state.LastLoad = rollback.LastLoad;

            // день той ребалансировки закрываем, как это делает немедленный откат: иначе
            // повод вернулся бы на следующем же баре и ошибка повторялась бы до вечера
            _lastProcessedDate = rollback.BarTime.Date;

            // без этого откат обещает больше, чем делает: отложенный план пережил бы его
            // и на следующем баре докупил бы под ребалансировку, которой больше нет
            ClearPendingBuys();

            SaveState();

            // Списки продаж разведены как раз потому, что денежная нога могла пройти, когда
            // акции не прошли. Сказать в этом случае «портфель не изменился» значит
            // противоречить соседнему сообщению об откате ступени лестницы
            decimal lqdtMoney = GetLqdtExecutedMoney(rollback);

            string tail = lqdtMoney > 0
                ? "Позиции по акциям и золоту не изменились, но денежная позиция продана на "
                    + Math.Round(lqdtMoney) + ": эти деньги остались на счёте и будут "
                    + "размещены следующей ребалансировкой. Повод "
                : "Портфель не изменился, повод ";

            SendExecutionProblem("Ребалансировка "
                + rollback.BarTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                + " откачена: заявки на продажу акций ушли, но не исполнилось ни одной. "
                + tail + rollback.Reason + " снова считается неотработанным "
                + "и вернётся на следующий торговый день");
        }

        /// <summary>
        /// Продажи акций прошли, а денежная позиция не продалась: портфель изменился,
        /// но денег на покупки не хватило и загрузка до цели не доведена.
        ///
        /// Откатывать ребалансировку целиком нельзя - продажи-то состоялись. Возвращаются
        /// только лестничные отметки, чтобы повод LoadChange сработал заново.
        ///
        /// LastLadderDate возвращается обязательно, иначе откат бесполезен: пока идёт пауза
        /// Ladder min days, GetEffectiveLoad отдаёт сам LastLoad, он же сравнивается с собой,
        /// и лестничный повод не сработает никогда, а целевые веса будут считаться
        /// по старой загрузке.
        ///
        /// Сам по себе перевес денежной позиции ничем не ловится: у неё проверяется только
        /// провал ниже неснижаемого остатка, потолка нет. А Deviation по акциям тут почти
        /// бесполезен - недобор размазан по всем целям покупки, то есть поделён на число
        /// бумаг, и на полосе в 30% от целевой позиции остаётся невидимым
        /// </summary>
        private void RollbackLadderStep(KorovinRebalanceRollback rollback, string cause)
        {
            if (_state.LastLoad == rollback.LastLoad
                && _state.LastLadderDate == rollback.LastLadderDate)
            {
                // уже откачено немедленной проверкой
                return;
            }

            _state.LastLoad = rollback.LastLoad;
            _state.LastLadderDate = rollback.LastLadderDate;

            SaveState();

            SendExecutionProblem("Ступень лестницы не засчитана: " + cause + ". Продажи акций "
                + "прошли и портфель изменился, но денег на покупки не хватило, и загрузка "
                + "до цели не доведена. Загрузка возвращена к прежней, повод LoadChange "
                + "вернётся на следующий торговый день");
        }

        private void RegisterRebalance(DateTime barTime, string reason)
        {
            _state.LastRebalanceDate = barTime.Date;

            if (reason == "LoadChange"
                || reason == "LevelChange")
            {
                _state.LastLadderDate = barTime.Date;
            }

            _state.LastLevel = _ladder.Level;
            _state.LastLoad = GetEffectiveLoad();
            _state.LadderSaved = true;
            _state.LadderPeak = _ladder.Peak;
            _state.LadderTrough = _ladder.Trough;
            _state.LadderTroughDate = _ladder.TroughDate;
            _state.LadderLevelEnterDate = _ladder.LevelEnterDate;

            if (reason == "Scheduled")
            {
                _state.LastScheduledDate = barTime.Date;
            }
        }

        #endregion

        #region Orders

        /// <summary>
        /// Полоса допуска по каждому активу.
        ///
        /// Band abs задан в процентах от капитала и означает допуск для КЛАССА активов, а не
        /// для отдельной бумаги. Золото и денежная позиция - это классы целиком, а вот акции
        /// делят свой класс на N бумаг, поэтому и полоса каждой бумаги делится на N.
        ///
        /// Без деления полоса получалась несоразмерной размеру позиции: при 13 бумагах и доле
        /// акций 50 % цель на бумагу составляет 3,85 % капитала, а полоса 2 % капитала - это
        /// 52 % от неё. Бумагу трогали, только если она уезжала на половину собственного веса,
        /// и внутриклассовая ребалансировка - то, ради чего портфель и собран, - почти
        /// не работала: в прогоне половина ребалансировок не касалась акций вовсе, а в среднем
        /// торговалось 0,8 бумаги из 13. Заодно параметр был немонотонен по числу бумаг:
        /// переход с 8 на 13 молча ужесточал стратегию в 1,6 раза
        /// </summary>
        private void CalculateDeltas(List<KorovinAsset> assets, decimal equity)
        {
            int stockCount = 0;

            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].Type == KorovinAssetType.Stock)
                {
                    stockCount++;
                }
            }

            if (stockCount < 1)
            {
                stockCount = 1;
            }

            for (int i = 0; i < assets.Count; i++)
            {
                KorovinAsset asset = assets[i];

                asset.TargetMoney = equity * asset.TargetWeight / 100m;
                asset.Delta = asset.TargetMoney - asset.Value;

                decimal bandAbs = equity * _bandAbsPercent.ValueDecimal / 100m;

                if (asset.Type == KorovinAssetType.Stock)
                {
                    // доля класса акций, приходящаяся на одну бумагу
                    bandAbs = bandAbs / stockCount;
                }

                decimal bandRel = asset.TargetMoney * _bandRelPercent.ValueDecimal / 100m;

                asset.Band = bandAbs > bandRel ? bandAbs : bandRel;
            }
        }

        private void ExecuteRebalance(List<KorovinAsset> assets, decimal equity, decimal cash,
            string reason, DateTime barTime)
        {
            // точка отсчёта для проверки зачисления - ДО отправки продаж. Заявка по ликвидной
            // бумаге исполняется за десятки миллисекунд, а денежная позиция портфеля
            // обновляется своим потоком: замерь её после отправки - и выручка уже успевших
            // продаж попадёт и в точку отсчёта, и в ожидаемую сумму, то есть будет учтена
            // дважды. Порог станет недостижимым, и быстрый путь молча выродится в ожидание свечи
            decimal brokerCashAtPlan = GetBrokerFreeMoney();

            bool reopen = IsReopenAllowed();
            bool onlyBuys = reason == "CashInflow" || _regime.ValueString == "OnlyRebalanceNoNewMoney";
            reopen = reopen && onlyBuys == false;

            List<KorovinAsset> buys = new List<KorovinAsset>();
            List<KorovinAsset> sells = new List<KorovinAsset>();

            decimal lqdtValue = 0;

            for (int i = 0; i < assets.Count; i++)
            {
                KorovinAsset asset = assets[i];

                if (asset.Type == KorovinAssetType.Lqdt)
                {
                    lqdtValue = asset.Value;
                    continue;
                }

                if (asset.IsTradable == false)
                {
                    continue;
                }

                asset.SellForCashReserve = false;
                asset.SellMoneyLimit = 0;

                bool insideBand = Math.Abs(asset.Delta) <= asset.Band
                    || Math.Abs(asset.Delta) < _minTradeMoney.ValueDecimal;

                if (insideBand)
                {
                    // денежная позиция провалилась ниже неснижаемого остатка. Восполнить её
                    // можно только продажей подорожавшего, поэтому здесь полоса не помеха:
                    // иначе повод возвращался бы каждый бар, а сделок бы не было
                    bool fixCashReserve = reason == "CashReserve"
                        && asset.Delta < 0
                        && Math.Abs(asset.Delta) >= _minTradeMoney.ValueDecimal;

                    if (fixCashReserve == false)
                    {
                        continue;
                    }

                    // такая продажа идёт не ради покупок, а ради самого остатка: ниже её
                    // пришлось бы отсеять и по полосе, и по нехватке денег на покупки
                    asset.SellForCashReserve = true;
                }

                if (asset.Delta > 0)
                {
                    if (asset.IsFrozen)
                    {
                        continue;
                    }

                    // Отсев до подсчёта needBuy - именно поэтому он здесь, а не в исполнении.
                    // Ниже needBuy определяет, сколько продать денежной позиции: покупка,
                    // которой не хватает на лот, утянула бы в план деньги, которые тут же
                    // вернутся в LQDT парковкой. На боевом счёте так и вышло - LSNGP и DOMRF
                    // не набрали лота, под них было продано LQDT на 4885, и следующей же
                    // заявкой те же деньги ушли обратно в LQDT
                    decimal buyMoney = reopen ? asset.TargetMoney : GetTradeMoney(asset);

                    decimal minBuyMoney = GetMinBuyMoney(asset);

                    if (minBuyMoney > 0
                        && buyMoney < minBuyMoney)
                    {
                        if (asset.TargetMoney < minBuyMoney)
                        {
                            ReportLotTooBig(asset, minBuyMoney, barTime);
                        }

                        continue;
                    }

                    buys.Add(asset);
                }
                else
                {
                    sells.Add(asset);
                }
            }

            if (buys.Count == 0
                && sells.Count == 0)
            {
                if (reason == "Manual")
                {
                    SendNewLogMessage("Внеплановая ребалансировка: все веса в пределах полосы, "
                        + "сделок не требуется", LogMessageType.System);
                }

                if (reason == "Scheduled")
                {
                    // плановая состоялась: портфель проверен, отклонений нет. Без отметки
                    // повод Scheduled возвращался бы каждый день и подавлял все остальные
                    _state.LastScheduledDate = barTime.Date;
                    SaveState();
                }

                if (reason == "CashReserve")
                {
                    // продать нечего: перевесов на сумму сделки нет. День закрываем, иначе
                    // повод возвращался бы на каждом баре и робот молча пересчитывал портфель
                    _lastProcessedDate = barTime.Date;

                    if (_cashReserveMessageDate.Date != barTime.Date)
                    {
                        _cashReserveMessageDate = barTime;

                        SendNewLogMessage("Денежная позиция ниже неснижаемого остатка, но "
                            + "продавать нечего: перевесов на сумму сделки нет",
                            LogMessageType.System);
                    }
                }
                else
                {
                    ReportEmptyReason(reason, barTime);
                }

                return;
            }

            decimal needBuy = 0;

            for (int i = 0; i < buys.Count; i++)
            {
                needBuy += GetTradeMoney(buys[i]);
            }

            // из денежной позиции доступно только то, что выше неснижаемого остатка
            decimal lqdtMin = equity * _cashMinPercent.ValueDecimal / 100m;
            decimal lqdtFree = lqdtValue - lqdtMin;

            if (lqdtFree < 0)
            {
                lqdtFree = 0;
            }

            decimal available = cash + lqdtFree;

            // сначала деньги, потом продажи
            List<KorovinAsset> sellsToDo = new List<KorovinAsset>();

            if (reopen)
            {
                // переоткрытие: каждый затронутый инструмент закрывается целиком
                // и покупается заново на целевую сумму. Без закрытия недовесов
                // покупка на целевую сумму легла бы поверх старой позиции
                needBuy = 0;

                for (int i = 0; i < buys.Count; i++)
                {
                    needBuy += buys[i].TargetMoney;

                    if (buys[i].Volume > 0)
                    {
                        sellsToDo.Add(buys[i]);
                    }
                }

                for (int i = 0; i < sells.Count; i++)
                {
                    needBuy += sells[i].TargetMoney;

                    if (sells[i].Volume > 0)
                    {
                        sellsToDo.Add(sells[i]);
                    }
                }
            }
            else if (onlyBuys == false)
            {
                sells.Sort((first, second) => Math.Abs(second.Delta).CompareTo(Math.Abs(first.Delta)));

                decimal deficit = needBuy - available;

                // сколько не хватает денежной позиции. Продажа ради остатка берёт ровно
                // эту сумму: срезать под нож весь перевес ради недостачи в пару тысяч -
                // это лишний оборот, лишняя комиссия и переполненная денежная позиция,
                // которую назавтра придётся распродавать обратно по поводу Deviation
                decimal cashReserveNeed = GetCashReserveNeed(assets, equity, reason);

                // раскладываем недостачу по перевешенным активам заранее: сколько взять
                // с каждого. Список идёт по тем же индексам, что и sells
                List<decimal> reserveMoney = new List<decimal>();

                if (cashReserveNeed > 0)
                {
                    List<decimal> availableForReserve = new List<decimal>();

                    for (int i = 0; i < sells.Count; i++)
                    {
                        availableForReserve.Add(sells[i].SellForCashReserve
                            ? GetTradeMoney(sells[i])
                            : 0);
                    }

                    reserveMoney = SplitCashReserveNeed(availableForReserve, cashReserveNeed);
                }

                for (int i = 0; i < sells.Count; i++)
                {
                    bool isHard = Math.Abs(sells[i].Delta) > sells[i].Band * _hardBandMult.ValueDecimal;

                    if (sells[i].SellForCashReserve)
                    {
                        decimal take = i < reserveMoney.Count ? reserveMoney[i] : 0;

                        if (take <= 0)
                        {
                            // недостача уже покрыта предыдущими продажами: остальные
                            // перевесы внутри полосы трогать незачем
                            sells[i].SellForCashReserve = false;
                            continue;
                        }

                        if (take < GetTradeMoney(sells[i]))
                        {
                            sells[i].SellMoneyLimit = take;
                        }

                        sellsToDo.Add(sells[i]);
                        deficit -= take;
                        continue;
                    }

                    // перевес внутри полосы жёстким не бывает, а дефицита при пустых покупках
                    // нет: без отдельной ветки такая продажа не прошла бы никогда
                    if (isHard == false
                        && deficit <= 0)
                    {
                        continue;
                    }

                    sellsToDo.Add(sells[i]);
                    deficit -= GetTradeMoney(sells[i]);
                }
            }

            if (sellsToDo.Count == 0
                && buys.Count == 0)
            {
                if (reason == "CashReserve")
                {
                    // продавать нечего или продажи запрещены режимом. День закрываем,
                    // иначе повод возвращался бы на каждом баре
                    _lastProcessedDate = barTime.Date;

                    if (_cashReserveMessageDate.Date != barTime.Date)
                    {
                        _cashReserveMessageDate = barTime;

                        SendNewLogMessage("Денежная позиция ниже неснижаемого остатка, "
                            + "но восполнить её нечем", LogMessageType.System);
                    }
                }
                else
                {
                    ReportEmptyReason(reason, barTime);
                }

                return;
            }

            Interlocked.Exchange(ref _failedOrders, 0);

            LogDecision(assets, equity, cash, reason, sellsToDo, buys, barTime, reopen);

            List<Order> sellOrders = new List<Order>();

            decimal sellsMoney = ExecuteSells(sellsToDo, reopen, sellOrders);

            // Регистрация объявляет сигнал отработанным: сдвигает дату последней
            // ребалансировки и запоминает новую загрузку. Делать её до продаж нельзя -
            // если ни одна заявка не ушла (отказ брокера, пустой стакан, зависшая позиция),
            // портфель не изменился, а повод больше не вернулся бы: LastLoad зафиксировал бы
            // текущую загрузку, и LoadChange перестал бы срабатывать. Частичное исполнение
            // засчитывается - остаток проявится как отклонение веса и будет добран следующей
            if (sellsToDo.Count > 0
                && sellsMoney <= 0)
            {
                // день закрываем: иначе повод возвращался бы на каждом баре и ошибка
                // повторялась бы до вечера. Следующая попытка - завтра
                _lastProcessedDate = barTime.Date;

                SendExecutionProblem("Ребалансировка не состоялась: продажи требовались, но "
                    + "не ушли ни по одному инструменту. Портфель не изменился, повод "
                    + "вернётся на следующий торговый день");

                return;
            }

            KorovinRebalanceRollback rollback = CreateRollbackPoint(barTime, reason, sellOrders);

            RegisterRebalance(barTime, reason);

            // повод CashReserve означает, что денег на счёте меньше неснижаемого остатка.
            // Раздать выручку по недовесам значило бы оставить остаток пустым и вернуться
            // сюда завтра, поэтому здесь продажи не финансируют покупки
            bool cashReserveOnly = reason == "CashReserve" && reopen == false;

            decimal moneyFromLqdt = cashReserveOnly
                ? 0
                : needBuy - cash - sellsMoney;

            decimal lqdtSent = 0;

            // в режиме докупки без продаж денежная позиция тоже не распродаётся:
            // покупки идут только на реально свободные деньги
            if (moneyFromLqdt > 0
                && _regime.ValueString != "OnlyRebalanceNoNewMoney")
            {
                decimal lqdtPlanned;

                lqdtSent = SellLqdt(assets, moneyFromLqdt, reopen, lqdtFree,
                    rollback.LqdtOrders, out lqdtPlanned);

                // заявки не создались вовсе - это видно сразу, ждать бара незачем.
                // Сравниваем с тем, что робот собирался продать, а не с запрошенной суммой:
                // урезание по свободному остатку и по стакану - штатное ограничение,
                // а не отказ, и денег после него всё равно меньше
                if (lqdtPlanned - lqdtSent > _minTradeMoney.ValueDecimal)
                {
                    RollbackLadderStep(rollback, "заявки на продажу денежной позиции "
                        + "не созданы");

                    rollback.LqdtOrders.Clear();
                }
            }

            if (rollback.SellOrders.Count > 0
                || rollback.LqdtOrders.Count > 0)
            {
                _sellsToVerify = rollback;
            }

            Dictionary<string, decimal> plan = new Dictionary<string, decimal>();

            if (cashReserveOnly)
            {
                KorovinAsset lqdtAsset = GetAsset(assets, KorovinAssetType.Lqdt);

                if (lqdtAsset != null
                    && sellsMoney > 0)
                {
                    // выручка вернётся в денежную позицию, как только продажи будут
                    // зачислены. Объём пересчитывается по реальному кэшу на тот момент
                    plan.Add(lqdtAsset.Name, sellsMoney);
                }

                SetBuysPlan(plan, barTime, reason, assets,
                    BuildWaitOrders(rollback.SellOrders, assets),
                    BuildWaitOrders(rollback.LqdtOrders, assets), brokerCashAtPlan);
                return;
            }

            for (int i = 0; i < buys.Count; i++)
            {
                decimal money = GetTradeMoney(buys[i]);

                if (reopen)
                {
                    money = buys[i].TargetMoney;
                }

                if (money <= 0)
                {
                    continue;
                }

                if (plan.ContainsKey(buys[i].Name) == false)
                {
                    plan.Add(buys[i].Name, money);
                }
            }

            // в Reopen продажи закрывают позицию целиком, поэтому докупить нужно и то,
            // что продавалось только ради приведения к целевому весу
            if (reopen)
            {
                for (int i = 0; i < sellsToDo.Count; i++)
                {
                    if (sellsToDo[i].TargetMoney <= 0)
                    {
                        continue;
                    }

                    if (plan.ContainsKey(sellsToDo[i].Name) == false)
                    {
                        plan.Add(sellsToDo[i].Name, sellsToDo[i].TargetMoney);
                    }
                }
            }

            SetBuysPlan(plan, barTime, reason, assets,
                BuildWaitOrders(rollback.SellOrders, assets),
                BuildWaitOrders(rollback.LqdtOrders, assets), brokerCashAtPlan);
        }

        /// <summary>
        /// Заявки, от которых зависят деньги под план покупок, вместе с размером лота:
        /// ждём мы денег, а объём заявки задан в лотах
        /// </summary>
        private List<KorovinWaitOrder> BuildWaitOrders(List<Order> orders,
            List<KorovinAsset> assets)
        {
            List<KorovinWaitOrder> result = new List<KorovinWaitOrder>();

            for (int i = 0; orders != null && i < orders.Count; i++)
            {
                Order order = orders[i];

                if (order == null)
                {
                    continue;
                }

                KorovinWaitOrder wait = new KorovinWaitOrder();
                wait.Order = order;

                KorovinAsset asset = GetAsset(assets, order.SecurityNameCode);

                if (asset != null
                    && asset.Lot > 0)
                {
                    wait.Lot = asset.Lot;
                }

                result.Add(wait);
            }

            return result;
        }

        /// <summary>
        /// Сколько денег реально принесли эти заявки: объём исполнения на среднюю цену сделки
        /// </summary>
        private static decimal GetExecutedMoney(List<KorovinWaitOrder> orders)
        {
            decimal money = 0;

            for (int i = 0; orders != null && i < orders.Count; i++)
            {
                KorovinWaitOrder wait = orders[i];

                if (wait == null
                    || wait.Order == null)
                {
                    continue;
                }

                money += wait.Order.VolumeExecute * wait.Order.PriceReal * wait.Lot;
            }

            return money;
        }

        private static int CountOrders(List<KorovinWaitOrder> orders)
        {
            return orders == null ? 0 : orders.Count;
        }

        /// <summary>
        /// Исполнить план покупок: сразу, если деньги на счёте уже есть, иначе - как только
        /// отработают продажи, которые его финансируют.
        ///
        /// Раньше план безусловно откладывался на следующую свечу. Это была не задержка ради
        /// цены, а способ дождаться зачисления выручки, и цену она стоила несоразмерную:
        /// на часовом таймфрейме между расчётом плана и покупкой проходил час, рынок за это
        /// время уходил, суммы плана переставали сходиться с ценами, и заявка последнего
        /// в списке актива отклонялась брокером по недостатку средств. Веса при этом тоже
        /// считались по часовой давности оценке портфеля.
        ///
        /// Пустой план означает, что покупать нечего: свободные деньги уходят в LQDT
        /// </summary>
        private void SetBuysPlan(Dictionary<string, decimal> plan, DateTime barTime,
            string reason, List<KorovinAsset> assets, List<KorovinWaitOrder> stockOrders,
            List<KorovinWaitOrder> lqdtOrders, decimal brokerCashAtPlan)
        {
            if (plan.Count == 0)
            {
                ParkRestInLqdt(assets, 0);
                SaveState();
                return;
            }

            // продажи не потребовались - деньги на счёте уже лежат, ждать нечего
            if (CountOrders(stockOrders) == 0
                && CountOrders(lqdtOrders) == 0)
            {
                ExecuteBuysNow(assets, plan);
                return;
            }

            // следующего бара в окне может не быть: продажи уже прошли, а покупки назавтра
            // будут аннулированы как относящиеся к другому торговому дню. Тогда исполняем
            // здесь же, не дожидаясь зачисления: неизрасходованный остаток паркуется
            if (IsLastBarOfWindow(barTime))
            {
                SendNewLogMessage("Окно торговли заканчивается: план покупок исполняется "
                    + "в этом же баре, а не откладывается", LogMessageType.System);

                ExecuteBuysNow(assets, plan);
                return;
            }

            _state.PendingBuys = plan;
            _state.PendingBuysTime = barTime;
            _state.PendingReason = reason;
            _barsAfterPendingBuys = 0;

            _pendingStockOrders = stockOrders;
            _pendingLqdtOrders = lqdtOrders;
            _pendingCashAtPlan = brokerCashAtPlan;
            _pendingSince = DateTime.Now;
            _waitingForSells = true;

            SaveState();

            // первая проверка сразу: заявка могла исполниться мгновенно. Дальше проверку
            // ведут события коннектора, а если они не придут - следующий бар
            TryExecutePendingBuys(barTime);
        }

        private void ExecuteBuysNow(List<KorovinAsset> assets, Dictionary<string, decimal> plan)
        {
            decimal spent = ExecuteBuysPlan(assets, plan);

            ParkRestInLqdt(assets, spent);

            SaveState();
        }

        /// <summary>
        /// Исполнить отложенный план, если продажи под него отработали и выручка зачислена.
        ///
        /// Условий два, и оба обязательны. Терминальное состояние заявки говорит, что сделка
        /// состоялась, но свободные деньги в портфеле обновляются своим потоком и на мгновение
        /// отстают - купить по неполному кэшу значит ужать план на ровном месте. Поэтому
        /// второе условие сравнивает деньги с тем, что должно было прийти по реально
        /// исполненному объёму. Именно эту задержку и пытался переждать вслепую Thread.Sleep
        /// </summary>
        private bool TryExecutePendingBuys(DateTime barTime)
        {
            if (_waitingForSells == false
                || _state.PendingBuys.Count == 0)
            {
                return false;
            }

            int waitCount = CountOrders(_pendingStockOrders) + CountOrders(_pendingLqdtOrders);

            if (waitCount == 0)
            {
                return false;
            }

            if (AreOrdersFinished(_pendingStockOrders) == false
                || AreOrdersFinished(_pendingLqdtOrders) == false)
            {
                return false;
            }

            decimal stockMoney = GetExecutedMoney(_pendingStockOrders);
            decimal arrived = stockMoney + GetExecutedMoney(_pendingLqdtOrders);

            if (arrived <= 0)
            {
                // продажи отработали, но не исполнились: финансировать покупки нечем
                SendExecutionProblem("План покупок отменён: продажи не исполнились ни по "
                    + "одному инструменту, покупать не на что");

                ClearPendingBuys();
                SaveState();

                return false;
            }

            if (CountOrders(_pendingStockOrders) > 0
                && stockMoney <= 0)
            {
                // акции не продались, прошла только денежная нога. Ребалансировку откатит
                // VerifyPreviousSells на следующем баре - покупать под неё нечего
                SendExecutionProblem("План покупок отменён: продажи акций не исполнились "
                    + "ни по одной бумаге, ребалансировка будет откачена");

                ClearPendingBuys();
                SaveState();

                return false;
            }

            // Проверка по деньгам работает только там, где брокер отдаёт денежную позицию.
            // Точка отсчёта тоже должна быть от него: смешивать её с вычисленным cash нельзя,
            // это разные величины
            decimal brokerCash = GetBrokerFreeMoney();

            if (brokerCash >= 0
                && _pendingCashAtPlan >= 0)
            {
                // допуск на комиссию и на разницу между средней ценой сделки и списанием
                decimal tolerance = arrived / 100m;

                if (brokerCash + tolerance < _pendingCashAtPlan + arrived)
                {
                    return false;
                }
            }

            List<BotTabSimple> stockTabs = _tabStocks == null ? null : _tabStocks.Tabs;

            if (stockTabs == null
                || stockTabs.Count == 0)
            {
                return false;
            }

            List<KorovinAsset> assets = BuildAssets(stockTabs, barTime);

            Dictionary<string, decimal> plan = new Dictionary<string, decimal>(_state.PendingBuys);

            // Без этой записи успешное срабатывание быстрого пути неотличимо от молчаливого
            // отката к ожиданию свечи: и то и другое выглядит одинаково - план исполнился.
            // Бэктестом эту правку не проверить, так что журнал - единственный способ увидеть,
            // что она работает
            string waited = _pendingSince == DateTime.MinValue
                ? "?"
                : Math.Round((DateTime.Now - _pendingSince).TotalSeconds, 1)
                    .ToString(CultureInfo.InvariantCulture);

            SendNewLogMessage("Продажи исполнены за " + waited + " с, план покупок пошёл сразу: "
                + plan.Count + " заявок, выручка " + Math.Round(arrived), LogMessageType.System);

            // план списываем ДО отправки заявок и сразу пишем на диск: если процесс упадёт
            // между этими действиями, план не повторится после перезапуска
            ClearPendingBuys();
            SaveState();

            ExecuteBuysNow(assets, plan);

            return true;
        }

        /// <summary>
        /// Все ли заявки отработали. Partial не считается: объём ещё добирается
        /// </summary>
        private static bool AreOrdersFinished(List<KorovinWaitOrder> orders)
        {
            for (int i = 0; orders != null && i < orders.Count; i++)
            {
                KorovinWaitOrder wait = orders[i];

                if (wait == null
                    || wait.Order == null)
                {
                    continue;
                }

                if (wait.Order.State != OrderStateType.Done
                    && wait.Order.State != OrderStateType.Fail
                    && wait.Order.State != OrderStateType.Cancel
                    && wait.Order.State != OrderStateType.LostAfterActive)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Сколько денег не хватает денежной позиции, чтобы повод CashReserve был закрыт.
        ///
        /// Доводим до целевого веса, но не ниже неснижаемого остатка. Именно до цели, а не
        /// впритык к минимуму: восстановление ровно до порога оставило бы позицию на самом
        /// краю, и следующее же движение рынка вернуло бы повод.
        ///
        /// Для остальных поводов ограничения нет - там продажи финансируют покупки,
        /// и объём считается по дельте до цели, как обычно
        /// </summary>
        private decimal GetCashReserveNeed(List<KorovinAsset> assets, decimal equity, string reason)
        {
            if (reason != "CashReserve")
            {
                return 0;
            }

            KorovinAsset lqdt = GetAsset(assets, KorovinAssetType.Lqdt);

            if (lqdt == null)
            {
                return 0;
            }

            return GetCashReserveNeed(lqdt.TargetMoney, lqdt.Value, equity,
                _cashMinPercent.ValueDecimal);
        }

        /// <summary>
        /// Сколько денег не хватает денежной позиции. Доводим до целевого веса, но не ниже
        /// неснижаемого остатка: восстановление впритык к порогу оставило бы позицию на самом
        /// краю, и следующее же движение рынка вернуло бы повод
        /// </summary>
        public static decimal GetCashReserveNeed(decimal lqdtTargetMoney, decimal lqdtValue,
            decimal equity, decimal cashMinPercent)
        {
            decimal minValue = equity * cashMinPercent / 100m;

            decimal target = lqdtTargetMoney > minValue ? lqdtTargetMoney : minValue;

            decimal need = target - lqdtValue;

            return need > 0 ? need : 0;
        }

        /// <summary>
        /// Разложить недостачу денежной позиции по перевешенным активам: сколько взять
        /// с каждого. Активы перебираются в заданном порядке (у робота - по убыванию
        /// перевеса), пока недостача не покрыта; объём последнего режется по остатку,
        /// остальные получают ноль и не продаются вовсе.
        ///
        /// Смысл в том, чтобы не срезать перевес до цели ради небольшой недостачи. Полоса
        /// допуска одной бумаги измеряется сотнями тысяч, и продажа «до цели» ради нехватки
        /// в пару тысяч давала бы лишний оборот, лишнюю комиссию и переполненную денежную
        /// позицию, которую назавтра пришлось бы распродавать обратно
        /// </summary>
        public static List<decimal> SplitCashReserveNeed(List<decimal> availableMoney, decimal need)
        {
            List<decimal> result = new List<decimal>();

            if (availableMoney == null)
            {
                return result;
            }

            for (int i = 0; i < availableMoney.Count; i++)
            {
                decimal available = availableMoney[i];

                if (need <= 0
                    || available <= 0)
                {
                    result.Add(0);
                    continue;
                }

                decimal take = available > need ? need : available;

                result.Add(take);
                need -= take;
            }

            return result;
        }

        private decimal GetTradeMoney(KorovinAsset asset)
        {
            decimal money = Math.Abs(asset.Delta);

            if (_tradeTo.ValueString == "BandEdge")
            {
                money = money - asset.Band;
            }

            if (money < 0)
            {
                money = 0;
            }

            return money;
        }

        private decimal ExecuteSells(List<KorovinAsset> sells, bool reopen,
            List<Order> sentOrders)
        {
            decimal moneySum = 0;

            for (int i = 0; i < sells.Count; i++)
            {
                KorovinAsset asset = sells[i];

                decimal money = GetTradeMoney(asset);

                if (reopen)
                {
                    money = asset.Value;
                }
                else if (asset.SellMoneyLimit > 0
                    && money > asset.SellMoneyLimit)
                {
                    // продажа ради денежного остатка: берём только недостающее
                    money = asset.SellMoneyLimit;
                }

                decimal volume = CalculateVolume(asset, money, GetExecutionPrice(asset, false));

                if (volume <= 0)
                {
                    continue;
                }

                if (volume > asset.Volume
                    || reopen)
                {
                    volume = asset.Volume;
                }

                if (IsFreezeNearRecordDate(asset))
                {
                    SendNewLogMessage("Продажа " + asset.Name +
                        " отложена: близка дивидендная отсечка", LogMessageType.System);
                    continue;
                }

                moneySum += ClosePartOfPosition(asset, volume, sentOrders);
            }

            return moneySum;
        }

        private decimal ClosePartOfPosition(KorovinAsset asset, decimal volume,
            List<Order> sentOrders)
        {
            volume = LimitVolumeByDepth(asset, volume, false);

            if (volume <= 0)
            {
                return 0;
            }

            List<Position> positions = asset.Tab.PositionsOpenAll;

            // деньги от продажи считаем по той же цене, по которой уйдёт заявка: завышенная
            // выручка раздувает план покупок, а расплачивается за это последний в плане актив
            decimal sellPrice = GetExecutionPrice(asset, false);

            decimal rest = volume;
            decimal money = 0;
            int notSent = 0;

            for (int i = 0; positions != null && i < positions.Count && rest > 0; i++)
            {
                Position position = positions[i];

                if (position == null
                    || position.Direction != Side.Buy
                    || position.State != PositionStateType.Open
                    || position.OpenVolume <= 0)
                {
                    continue;
                }

                decimal volumeToClose = rest > position.OpenVolume ? position.OpenVolume : rest;

                int ordersBefore = position.CloseOrders == null
                    ? 0
                    : position.CloseOrders.Count;

                asset.Tab.CloseAtMarket(position, volumeToClose);

                // BotTabSimple добавляет заявку в CloseOrders синхронно, до отправки
                // в коннектор, поэтому новая заявка - последняя в списке. Если заявка
                // не создалась, список не вырастет: CloseAtMarket молча выходит при обрыве
                // связи, неготовности к торговле и нулевом BestAsk - то есть уже после
                // того, как проверка стакана прошла.
                //
                // Пока такая продажа считалась отправленной, обе страховки молчали: деньги
                // засчитывались, поэтому немедленная проверка sellsMoney не срабатывала,
                // а отложенная не взводилась - взводить её было не на чем, заявок нет.
                // Ребалансировка засчитывалась при нетронутом портфеле - ровно тот случай,
                // ради которого обе страховки и сделаны
                bool orderCreated = position.CloseOrders != null
                    && position.CloseOrders.Count > ordersBefore;

                if (orderCreated == false)
                {
                    notSent++;
                    continue;
                }

                if (sentOrders != null)
                {
                    sentOrders.Add(position.CloseOrders[position.CloseOrders.Count - 1]);
                }

                rest -= volumeToClose;
                money += volumeToClose * sellPrice * asset.Lot;
            }

            if (notSent > 0)
            {
                SendExecutionProblem("Продажа " + asset.Name + ": заявка не создана по "
                    + notSent + " позициям. Обычно это обрыв связи, неготовность коннектора "
                    + "к торговле или отсутствие цены. Деньги по этим позициям не засчитаны, "
                    + "ребалансировка откатится, если не ушло вообще ничего");
            }

            return money;
        }

        /// <summary>
        /// Продать денежную позицию под покупки. Возвращает деньги, по которым заявки
        /// действительно созданы, и через planned - сколько робот собирался продать.
        /// Разница между ними и есть провал фондирующей ноги: продажи акций уже прошли,
        /// а денег на покупки не будет
        /// </summary>
        private decimal SellLqdt(List<KorovinAsset> assets, decimal money, bool reopen,
            decimal lqdtFree, List<Order> sentOrders, out decimal planned)
        {
            planned = 0;

            KorovinAsset lqdt = GetAsset(assets, KorovinAssetType.Lqdt);

            if (lqdt == null
                || lqdt.Volume <= 0
                || lqdtFree <= 0)
            {
                return 0;
            }

            // ниже неснижаемого остатка денежную позицию не распродаём
            if (money > lqdtFree)
            {
                money = lqdtFree;
            }

            // В Reopen позиция закрывается целиком - иначе это частичное закрытие, при котором
            // тестер не вызывает AddProfit, и весь доход денежной позиции пропадает из эквити.
            // Неснижаемый остаток от этого не страдает: целевые веса его уже учитывают
            // (резерв считается от Cash base - Cash min), а излишек вернётся в LQDT парковкой
            decimal lqdtSellPrice = GetExecutionPrice(lqdt, false);

            decimal volume = reopen
                ? lqdt.Volume
                : CalculateVolume(lqdt, money, lqdtSellPrice);

            if (volume > lqdt.Volume)
            {
                volume = lqdt.Volume;
            }

            if (volume <= 0)
            {
                return 0;
            }

            planned = volume * lqdtSellPrice * lqdt.Lot;

            return ClosePartOfPosition(lqdt, volume, sentOrders);
        }

        private void ExecutePendingBuys(List<BotTabSimple> stockTabs, DateTime barTime)
        {
            // сюда попадают только планы, которые не дождались зачисления по событиям.
            // Отметка в журнале нужна, чтобы молчаливая деградация быстрого пути к ожиданию
            // свечи была видна: без неё оба исхода выглядят одинаково.
            // В тестере событийный путь выключен намеренно, там сообщать не о чём
            if (StartProgram == StartProgram.IsOsTrader)
            {
                SendNewLogMessage("План покупок исполняется на следующем баре: "
                    + "зачисление выручки по событиям не подтвердилось", LogMessageType.System);
            }

            List<KorovinAsset> assets = BuildAssets(stockTabs, barTime);

            decimal equity;
            decimal cash;

            EvaluatePortfolio(assets, out equity, out cash);

            Dictionary<string, decimal> plan = new Dictionary<string, decimal>(_state.PendingBuys);

            // план списываем ДО отправки заявок и сразу пишем на диск: если процесс упадёт
            // между этими действиями, план не повторится после перезапуска. Недобранное
            // доберётся ближайшей ребалансировкой по отклонению весов
            ClearPendingBuys();
            SaveState();

            decimal spent = ExecuteBuysPlan(assets, plan);
            ParkRestInLqdt(assets, spent);

            SaveState();
        }

        private decimal ExecuteBuysPlan(List<KorovinAsset> assets, Dictionary<string, decimal> plan)
        {
            decimal equity;
            decimal cash;

            EvaluatePortfolio(assets, out equity, out cash);

            List<KorovinAsset> targets = new List<KorovinAsset>();
            List<decimal> moneyList = new List<decimal>();
            List<string> skipped = new List<string>();

            foreach (KeyValuePair<string, decimal> pair in plan)
            {
                KorovinAsset asset = GetAsset(assets, pair.Key);

                if (asset == null
                    || asset.IsTradable == false)
                {
                    skipped.Add(pair.Key);
                    continue;
                }

                targets.Add(asset);
                moneyList.Add(pair.Value);
            }

            // деньги под план уже освобождены продажами, и если купить не на что, они просто
            // лягут на счёт мёртвым грузом. Молчать об этом нельзя: особенно когда пропущена
            // денежная позиция - там повод CashReserve вернётся завтра, а причина будет неясна
            if (skipped.Count > 0)
            {
                SendExecutionProblem("Покупка не выполнена по " + string.Join(", ", skipped)
                    + ": инструмент не торгуется. Деньги остались нераспределёнными "
                    + "до следующей ребалансировки");
            }

            if (targets.Count == 0)
            {
                return 0;
            }

            decimal planSum = 0;

            for (int i = 0; i < moneyList.Count; i++)
            {
                planSum += moneyList[i];
            }

            decimal availableCash = cash;
            decimal spent = 0;

            // на покупки идёт не весь остаток: часть держим под комиссию и уход цены
            decimal cashForPlan = ApplyCashBuffer(availableCash);

            if (planSum > availableCash
                && planSum > 0)
            {
                // денег меньше, чем расписано: обычно продажи ушли не полным объёмом
                // или расчёты ещё не пришли. Покупки ужимаются пропорционально.
                //
                // Сообщение привязано к полному остатку, а не к остатку за вычетом запаса:
                // ужатие на сам запас - штатная работа, а не нехватка денег, и сообщать
                // о нём каждую ребалансировку значило бы залить канал ошибок
                SendExecutionProblem("Денег меньше плана покупок: нужно "
                    + Math.Round(planSum) + ", доступно " + Math.Round(availableCash)
                    + ". Покупки ужаты пропорционально, остаток добирается следующей ребалансировкой");
            }

            for (int i = 0; i < targets.Count; i++)
            {
                decimal money = moneyList[i];

                if (planSum > cashForPlan
                    && planSum > 0)
                {
                    money = money * cashForPlan / planSum;
                }

                if (money < _minTradeMoney.ValueDecimal)
                {
                    continue;
                }

                decimal buyPrice = GetExecutionPrice(targets[i], true);

                decimal volume = CalculateVolume(targets[i], money, buyPrice);

                if (volume <= 0)
                {
                    continue;
                }

                volume = LimitVolumeByDepth(targets[i], volume, true);

                if (volume <= 0)
                {
                    continue;
                }

                OpenOrAddPosition(targets[i], volume);

                spent += volume * buyPrice * targets[i].Lot;
            }

            return spent;
        }

        private void ParkRestInLqdt(List<KorovinAsset> assets, decimal alreadySpent)
        {
            KorovinAsset lqdt = GetAsset(assets, KorovinAssetType.Lqdt);

            if (lqdt == null)
            {
                return;
            }

            decimal equity;
            decimal cash;

            EvaluatePortfolio(assets, out equity, out cash);

            // заявки этого бара портфель ещё не видит, их деньги уже расписаны
            cash = cash - alreadySpent;

            // парковка идёт последней и забирает весь остаток, поэтому без запаса именно
            // она и упиралась бы в недостаток средств
            cash = ApplyCashBuffer(cash);

            if (cash < _minTradeMoney.ValueDecimal)
            {
                return;
            }

            // проверка стоит после подсчёта денег: сообщать не о чем, пока парковать нечего
            if (lqdt.IsTradable == false)
            {
                SendExecutionProblem("Свободные деньги " + Math.Round(cash)
                    + " не размещены: денежная позиция не торгуется. Останутся на счёте "
                    + "до следующей ребалансировки");
                return;
            }

            decimal volume = CalculateVolume(lqdt, cash, GetExecutionPrice(lqdt, true));

            if (volume <= 0)
            {
                return;
            }

            OpenOrAddPosition(lqdt, volume);
        }

        /// <summary>
        /// Недобор объёма трейдер должен увидеть сразу, а не найти потом в логе, поэтому
        /// в реальной торговле такие сообщения идут ошибкой - в канал ошибок.
        /// В тестере и оптимизаторе это обычная запись: канал ошибок там некому читать,
        /// а сообщений на прогоне в десять лет были бы тысячи
        /// </summary>
        private void Dividends_LogMessageEvent(string message)
        {
            SendExecutionProblem(message);
        }

        private void SendExecutionProblem(string message)
        {
            SendNewLogMessage(message, StartProgram == StartProgram.IsOsTrader
                ? LogMessageType.Error
                : LogMessageType.System);
        }

        /// <summary>
        /// Проверить, доходят ли до робота стаканы, и если нет - назвать причину.
        ///
        /// При Depth check = On робот не отправляет рыночную заявку без стакана: оценить
        /// проскальзывание не на чем. Стакан при этом приходит роботу не сам собой - у каждого
        /// сервера OsEngine есть параметр "Использовать полный стакан" (Use Full Market Depth),
        /// и при выключенном параметре AServer.TrySendMarketDepthEvent выходит сразу, событие
        /// стакана не поднимается, и BotTabSimple.MarketDepth остаётся null навсегда. Подсказка
        /// у самой галки советует ставить false, так что выключают её охотно.
        ///
        /// Лучшие цены при этом продолжают идти: TrySendBidAsk вызывается отдельно и этот
        /// параметр не проверяет. Отсюда точный признак - стакана нет, а PriceBestBid
        /// и PriceBestAsk не нулевые. Отличаем этот случай от полного отсутствия данных,
        /// чтобы не гонять человека проверять подключение, когда дело в одной галке
        /// </summary>
        private void CheckMarketDepthAvailability(List<KorovinAsset> assets, DateTime barTime)
        {
            if (StartProgram != StartProgram.IsOsTrader
                || _depthCheck.ValueString != "On")
            {
                return;
            }

            int withDepth = 0;
            int withoutDepth = 0;
            int withoutDepthButWithPrices = 0;

            for (int i = 0; i < assets.Count; i++)
            {
                BotTabSimple tab = assets[i] == null ? null : assets[i].Tab;

                if (tab == null)
                {
                    continue;
                }

                MarketDepth depth = tab.MarketDepth;

                bool hasLevels = depth != null
                    && ((depth.Bids != null && depth.Bids.Count > 0)
                        || (depth.Asks != null && depth.Asks.Count > 0));

                if (hasLevels)
                {
                    withDepth++;
                    continue;
                }

                withoutDepth++;

                if (tab.PriceBestBid > 0
                    || tab.PriceBestAsk > 0)
                {
                    withoutDepthButWithPrices++;
                }
            }

            if (withoutDepth == 0)
            {
                if (_depthProblemActive)
                {
                    _depthProblemActive = false;

                    SendNewLogMessage("Стаканы снова приходят, заявки отправляются как обычно",
                        LogMessageType.System);
                }

                return;
            }

            _depthProblemActive = true;

            if (_depthProblemReported.Date == barTime.Date)
            {
                return;
            }

            _depthProblemReported = barTime;

            if (withDepth == 0
                && withoutDepthButWithPrices > 0)
            {
                SendExecutionProblem("Заявки не отправляются: при Depth check = On робот "
                    + "не шлёт рыночную заявку без стакана, а стаканов нет ни по одной из "
                    + withoutDepth + " бумаг - при том что лучшие цены приходят. Так выглядит "
                    + "выключенный параметр сервера \"Использовать полный стакан\" "
                    + "(Use Full Market Depth). Включите его в настройках подключения "
                    + "или поставьте Depth check = Off, и тогда объём заявки не будет "
                    + "ограничиваться стаканом");

                return;
            }

            if (withDepth == 0)
            {
                SendExecutionProblem("Заявки не отправляются: стаканов нет ни по одной из "
                    + withoutDepth + " бумаг, и лучших цен тоже нет. Проверьте подключение "
                    + "и подписку на данные, а заодно параметр сервера \"Использовать "
                    + "полный стакан\"");

                return;
            }

            SendExecutionProblem("Стакан есть по " + withDepth + " бумагам из "
                + (withDepth + withoutDepth) + ", по остальным его нет: при Depth check = On "
                + "заявки по этим бумагам отправлены не будут, и их веса останутся "
                + "отклонёнными. Проверьте подписку на данные по ним");
        }

        /// <summary>
        /// Ограничить объём рыночной заявки тем, что стоит в стакане в пределах допустимого
        /// отклонения цены. Робот идёт по уровням от лучшей цены, пока цена не ушла дальше
        /// Max slippage percent, и складывает объём. Если нужного объёма там нет, берётся
        /// только доступный: остаток попадёт в отклонение весов и будет добран следующей
        /// ребалансировкой. В тестере стакана нет, там проверка не работает
        /// </summary>
        private decimal LimitVolumeByDepth(KorovinAsset asset, decimal volume, bool isBuy)
        {
            if (_depthCheck.ValueString != "On"
                || volume <= 0)
            {
                return volume;
            }

            try
            {
                MarketDepth depth = asset.Tab.MarketDepth;

                List<MarketDepthLevel> levels = depth == null
                    ? null
                    : (isBuy ? depth.Asks : depth.Bids);

                if (levels == null
                    || levels.Count == 0)
                {
                    // В тестере и оптимизаторе стакана нет по определению - там проверка
                    // просто не работает. В реальной торговле пустой стакан означает, что
                    // доступного объёма нет: слать рыночную заявку вслепую нельзя
                    if (StartProgram != StartProgram.IsOsTrader)
                    {
                        return volume;
                    }

                    SendExecutionProblem((isBuy ? "Покупка " : "Продажа ") + asset.Name
                        + " не отправлена: стакан пуст или недоступен, оценить проскальзывание "
                        + "не на чем");

                    return 0;
                }

                decimal bestPrice = (decimal)levels[0].Price;

                if (bestPrice <= 0)
                {
                    return volume;
                }

                decimal slippage = _maxSlippagePercent.ValueDecimal;

                decimal available = RoundVolume(asset,
                    GetVolumeInSlippage(levels, isBuy, slippage));

                if (available <= 0)
                {
                    SendExecutionProblem((isBuy ? "Покупка " : "Продажа ") + asset.Name
                        + " не отправлена: в пределах " + slippage + "% от цены " + bestPrice
                        + " в стакане нет объёма. Вес останется отклонённым до следующей ребалансировки");

                    return 0;
                }

                if (volume <= available)
                {
                    return volume;
                }

                SendExecutionProblem((isBuy ? "Покупка " : "Продажа ") + asset.Name
                    + " урезана по стакану: " + Math.Round(volume, 4) + " -> "
                    + Math.Round(available, 4) + " (в пределах " + slippage + "% от " + bestPrice
                    + "), недобор " + Math.Round(volume - available, 4)
                    + ". Остаток добирается следующей ребалансировкой");

                return available;
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return volume;
            }
        }

        private void OpenOrAddPosition(KorovinAsset asset, decimal volume)
        {
            List<Position> positions = asset.Tab.PositionsOpenAll;

            Position position = null;

            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                if (positions[i] != null
                    && positions[i].Direction == Side.Buy
                    && positions[i].State == PositionStateType.Open)
                {
                    position = positions[i];
                    break;
                }
            }

            if (position == null)
            {
                for (int i = 0; positions != null && i < positions.Count; i++)
                {
                    if (positions[i] != null
                        && positions[i].Direction == Side.Buy
                        && positions[i].State == PositionStateType.Closing
                        && positions[i].OpenVolume > 0
                        && positions[i].CloseActive == false)
                    {
                        positions[i].State = PositionStateType.Open;
                        position = positions[i];
                        break;
                    }
                }
            }

            if (position == null)
            {
                asset.Tab.BuyAtMarket(volume);
            }
            else
            {
                asset.Tab.BuyAtMarketToPosition(position, volume);
            }
        }

        /// <summary>
        /// Цена, по которой заявка исполнится на самом деле: аск при покупке, бид при продаже.
        ///
        /// Объём считался по цене последней свечи, а маркет-ордер уходит по другой стороне
        /// стакана. Разница мелкая - десятые доли процента, - но знак у неё постоянный:
        /// покупка всегда дороже, продажа всегда дешевле. Пока заявок много, ошибка копится
        /// по списку, и последний актив плана получает её целиком. На боевом счёте это
        /// выглядело как отказ брокера по недостатку средств на последней бумаге при
        /// формально сходящемся плане.
        ///
        /// В тестере и оптимизаторе стакана нет, там остаётся цена бара - как и было
        /// </summary>
        private decimal GetExecutionPrice(KorovinAsset asset, bool isBuy)
        {
            if (StartProgram != StartProgram.IsOsTrader
                || asset == null
                || asset.Tab == null)
            {
                return asset == null ? 0 : asset.Price;
            }

            try
            {
                MarketDepth depth = asset.Tab.MarketDepth;

                List<MarketDepthLevel> levels = depth == null
                    ? null
                    : (isBuy ? depth.Asks : depth.Bids);

                if (levels == null
                    || levels.Count == 0
                    || levels[0] == null)
                {
                    return asset.Price;
                }

                decimal bestPrice = (decimal)levels[0].Price;

                if (bestPrice <= 0)
                {
                    return asset.Price;
                }

                return bestPrice;
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);

                return asset.Price;
            }
        }

        /// <summary>
        /// Оставить неприкосновенный запас денег.
        ///
        /// Даже посчитанный по стакану объём не гарантирует, что заявка пройдёт: между
        /// чтением стакана и исполнением цена успевает уйти, а комиссия списывается сверх
        /// суммы сделки и в план не входит вовсе. Когда покупка расписана ровно на весь
        /// свободный остаток, этого достаточно для отказа по недостатку средств.
        ///
        /// Запас не теряется: он лежит на счёте и уходит в денежную позицию следующей
        /// ребалансировкой
        /// </summary>
        private decimal ApplyCashBuffer(decimal money)
        {
            if (money <= 0)
            {
                return 0;
            }

            money = money - money * _cashBufferPercent.ValueDecimal / 100m;

            return money > 0 ? money : 0;
        }

        /// <summary>
        /// Во сколько обойдётся минимальная покупка - один лот, а для инструментов с дробным
        /// объёмом минимальный шаг объёма.
        ///
        /// Объём округляется вниз (RoundVolume), поэтому денег меньше этой суммы хватает
        /// ровно на ноль лотов. Планировать такую покупку нельзя: под план продаётся денежная
        /// позиция, покупка не проходит, и следующим же шагом парковка возвращает те же деньги
        /// обратно в LQDT. Портфель не меняется, а две комиссии списаны
        /// </summary>
        private decimal GetMinBuyMoney(KorovinAsset asset)
        {
            if (asset == null
                || asset.Lot <= 0)
            {
                return 0;
            }

            decimal price = GetExecutionPrice(asset, true);

            if (price <= 0)
            {
                price = asset.Price;
            }

            if (price <= 0)
            {
                return 0;
            }

            int decimals = asset.Tab == null || asset.Tab.Security == null
                ? 0
                : asset.Tab.Security.DecimalsVolume;

            if (decimals < 0)
            {
                decimals = 0;
            }

            decimal step = 1m;

            for (int i = 0; i < decimals; i++)
            {
                step = step / 10m;
            }

            return step * price * asset.Lot;
        }

        /// <summary>
        /// Сказать раз в день, что бумагу нельзя купить в принципе.
        ///
        /// Недобор меньше лота - обычное дело: вес почти выровнен, остаток подождёт роста
        /// портфеля. А вот когда меньше лота оказывается вся ЦЕЛЬ, бумага не войдёт
        /// в портфель никогда, сколько ни ребалансируй, и это уже вопрос состава портфеля,
        /// а не текущего бара
        /// </summary>
        private void ReportLotTooBig(KorovinAsset asset, decimal minBuyMoney, DateTime barTime)
        {
            DateTime date;

            if (_lotTooBigReported.TryGetValue(asset.Name, out date)
                && date.Date == barTime.Date)
            {
                return;
            }

            _lotTooBigReported[asset.Name] = barTime.Date;

            SendExecutionProblem("Целевой вес " + asset.Name + " меньше одного лота: цель "
                + Math.Round(asset.TargetMoney) + ", лот стоит " + Math.Round(minBuyMoney)
                + ". Бумага не может быть куплена при текущем размере портфеля - исключите её "
                + "из состава или увеличьте капитал");
        }

        private decimal CalculateVolume(KorovinAsset asset, decimal money, decimal price)
        {
            if (price <= 0
                || money <= 0)
            {
                return 0;
            }

            decimal volume = money / (price * asset.Lot);

            return RoundVolume(asset, volume);
        }

        /// <summary>
        /// Сколько объёма стоит в стакане в пределах допустимого отклонения от лучшей цены.
        /// Вынесено отдельно, чтобы логику можно было проверить без биржи
        /// </summary>
        public static decimal GetVolumeInSlippage(List<MarketDepthLevel> levels, bool isBuy,
            decimal slippagePercent)
        {
            if (levels == null
                || levels.Count == 0
                || slippagePercent < 0)
            {
                return 0;
            }

            decimal bestPrice = 0;

            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] != null
                    && levels[i].Price > 0)
                {
                    bestPrice = (decimal)levels[i].Price;
                    break;
                }
            }

            if (bestPrice <= 0)
            {
                return 0;
            }

            decimal limitPrice = isBuy
                ? bestPrice * (1m + slippagePercent / 100m)
                : bestPrice * (1m - slippagePercent / 100m);

            decimal available = 0;

            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] == null)
                {
                    continue;
                }

                decimal price = (decimal)levels[i].Price;

                if (price <= 0)
                {
                    continue;
                }

                if (isBuy && price > limitPrice)
                {
                    break;
                }

                if (isBuy == false && price < limitPrice)
                {
                    break;
                }

                available += (decimal)(isBuy ? levels[i].Ask : levels[i].Bid);
            }

            return available;
        }

        /// <summary>
        /// Округление объёма вниз по шагу инструмента
        /// </summary>
        private decimal RoundVolume(KorovinAsset asset, decimal volume)
        {
            if (volume <= 0)
            {
                return 0;
            }

            int decimals = asset.Tab.Security.DecimalsVolume;

            if (decimals < 0)
            {
                decimals = 0;
            }

            decimal multiplier = 1m;

            for (int i = 0; i < decimals; i++)
            {
                multiplier = multiplier * 10m;
            }

            return Math.Floor(volume * multiplier) / multiplier;
        }

        private bool IsReopenAllowed()
        {
            if (_reopenMode.ValueString != "On")
            {
                return false;
            }

            if (StartProgram == StartProgram.IsOsTrader)
            {
                // проверка вызывается на каждой ребалансировке, поэтому сообщение - один раз
                if (_reopenWarningSent == false)
                {
                    _reopenWarningSent = true;

                    SendExecutionProblem("В настройках включён Reopen mode - тестовый режим, "
                        + "запрещённый в реальной торговле. Используется обычный режим, "
                        + "но конфигурация отличается от протестированной");
                }

                return false;
            }

            return true;
        }

        private bool IsFreezeNearRecordDate(KorovinAsset asset)
        {
            if (_freezeNearRecordDate.ValueString != "On"
                || asset.Type != KorovinAssetType.Stock)
            {
                return false;
            }

            DateTime nextRecordDate = _dividends.GetNextRecordDate(asset.Name, asset.LastCandleTime);

            if (nextRecordDate == DateTime.MinValue)
            {
                return false;
            }

            double daysLeft = (nextRecordDate - asset.LastCandleTime.Date).TotalDays;

            return daysLeft >= 0
                && daysLeft <= _freezeDays.ValueInt;
        }

        /// <summary>
        /// После частичного закрытия OsEngine оставляет позицию в состоянии Closing.
        /// Её надо вернуть в Open, иначе тестер перестаёт начислять по ней дивиденды
        /// </summary>
        private void FixPartialClosedPositions(List<BotTabSimple> stockTabs)
        {
            for (int i = 0; i < stockTabs.Count; i++)
            {
                FixPartialClosedPositions(stockTabs[i]);
            }

            FixPartialClosedPositions(_tabGold);
            FixPartialClosedPositions(_tabLqdt);
        }

        private void FixPartialClosedPositions(BotTabSimple tab)
        {
            if (tab == null)
            {
                return;
            }

            List<Position> positions = tab.PositionsOpenAll;

            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position == null
                    || position.OpenVolume <= 0
                    || position.CloseActive)
                {
                    continue;
                }

                if (position.State == PositionStateType.Closing)
                {
                    position.State = PositionStateType.Open;
                    continue;
                }

                // ClosingFail остаётся после неудачного закрытия и делает позицию
                // неуправляемой: продажа берёт только Open, а из PositionsOpenAll такая
                // позиция не исчезает - её объём продолжает считаться в капитале. Без
                // возврата в работу перевес завис бы в портфеле навсегда. Повтор закрытия
                // рынком уже сделан обработчиком отказа; здесь позиция возвращается обычной
                // логике, чтобы ближайшая ребалансировка попробовала снова
                if (position.State == PositionStateType.ClosingFail)
                {
                    SendExecutionProblem("Позиция " + position.SecurityName + " номер "
                        + position.Number + " висела в ClosingFail и возвращена в работу. "
                        + "Перевес будет срезан ближайшей ребалансировкой");

                    position.State = PositionStateType.Open;
                }
            }
        }

        #endregion

        #region Service

        private KorovinAsset GetAsset(List<KorovinAsset> assets, KorovinAssetType type)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].Type == type)
                {
                    return assets[i];
                }
            }

            return null;
        }

        private KorovinAsset GetAsset(List<KorovinAsset> assets, string name)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                if (assets[i].Name == name)
                {
                    return assets[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Длительность бара по двум последним свечам. Нужна, чтобы мерить возраст
        /// отложенного плана и рассинхронизацию источников в единицах времени
        /// </summary>
        private void UpdateBarLength(List<BotTabSimple> stockTabs)
        {
            TimeSpan length = GetBarLength(stockTabs);

            if (length > TimeSpan.Zero)
            {
                _barLength = length;
            }
        }

        /// <summary>
        /// Длительность бара по двум последним свечам табов скринера, без побочных эффектов.
        /// Вынесена отдельно от UpdateBarLength, потому что AllStockTabsSynchronized нужна
        /// длина бара ДО того, как отработает Process (внутри которого раньше и только
        /// вычислялся _barLength) - иначе под оптимизатором получался замкнутый круг:
        /// вход в Process гейтился синхронностью табов, а синхронность требовала уже
        /// посчитанный _barLength, который выставляется только внутри самого Process
        /// </summary>
        private TimeSpan GetBarLength(List<BotTabSimple> stockTabs)
        {
            for (int i = 0; i < stockTabs.Count; i++)
            {
                BotTabSimple tab = stockTabs[i];

                if (tab == null
                    || tab.CandlesFinishedOnly == null
                    || tab.CandlesFinishedOnly.Count < 2)
                {
                    continue;
                }

                int last = tab.CandlesFinishedOnly.Count - 1;

                TimeSpan length = tab.CandlesFinishedOnly[last].TimeStart
                    - tab.CandlesFinishedOnly[last - 1].TimeStart;

                // разрыв через ночь или выходные - не длина бара: на первом баре дня
                // это давало бы 16 часов и делало почти любую бумагу устаревшей
                if (length > TimeSpan.Zero
                    && length <= TimeSpan.FromHours(12))
                {
                    return length;
                }
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// Сообщить о рассинхроне источников: раз в день обычной записью, а если данные
        /// не идут несколько дней подряд - ошибкой, потому что робот всё это время стоит
        /// </summary>
        private void ReportSyncProblem(string syncProblem, DateTime barTime)
        {
            if (_syncProblemSince == DateTime.MinValue)
            {
                _syncProblemSince = barTime;
            }

            if (_syncProblemDate.Date != barTime.Date)
            {
                _syncProblemDate = barTime;

                SendNewLogMessage("Источники не синхронизированы, решение отложено: "
                    + syncProblem, LogMessageType.System);
            }

            int days = (int)(barTime - _syncProblemSince).TotalDays;

            if (days >= 1
                && (_syncErrorSent == DateTime.MinValue
                    || (barTime - _syncErrorSent).TotalDays >= AlertRepeatDays))
            {
                _syncErrorSent = barTime;

                SendExecutionProblem("Робот не торгует " + (days + 1) + "-й день: " + syncProblem);
            }
        }

        /// <summary>
        /// Барьер синхронизации. Жёстко блокирует решение только при устаревшем индексе:
        /// он задаёт загрузку лестницы, и считать её по вчерашней цене нельзя.
        /// Отставание золота или денежной позиции решение не блокирует - такой инструмент
        /// всё равно помечается неторгуемым в CreateAsset, - но пишется в лог,
        /// потому что его стоимость входит в капитал по последней известной цене.
        /// Возвращает описание проблемы либо пустую строку
        /// </summary>
        private string GetSyncProblem(DateTime barTime)
        {
            TimeSpan tolerance = TimeSpan.FromDays(1);

            if (_barLength > TimeSpan.Zero)
            {
                tolerance = TimeSpan.FromTicks(_barLength.Ticks * (_syncToleranceBars.ValueInt + 1));
            }

            // вся аналитика строится на дневных закрытиях, поэтому внутридневной лаг источника
            // безопасен: индекс из двух десятков бумаг регулярно отстаёт на несколько часов
            // просто потому, что не по всем были сделки. Блокировать надо застой на сутки и больше
            if (tolerance < TimeSpan.FromDays(1))
            {
                tolerance = TimeSpan.FromDays(1);
            }

            WarnIfStale(barTime, tolerance, GetTabLastBarTime(_tabGold), "золото");
            WarnIfStale(barTime, tolerance, GetTabLastBarTime(_tabLqdt), "денежная позиция");

            DateTime indexTime = DateTime.MinValue;

            if (_tabIndex != null
                && _tabIndex.Candles != null
                && _tabIndex.Candles.Count > 0)
            {
                indexTime = _tabIndex.Candles[_tabIndex.Candles.Count - 1].TimeStart;
            }

            // индекса нет вовсе - это прогрев, им занимается Warmup policy
            if (indexTime == DateTime.MinValue)
            {
                return "";
            }

            if (barTime - indexTime > tolerance)
            {
                return "индекс отстаёт, последняя свеча " + indexTime.ToString("dd.MM.yyyy HH:mm",
                    CultureInfo.InvariantCulture) + " при баре " + barTime.ToString("dd.MM.yyyy HH:mm",
                    CultureInfo.InvariantCulture);
            }

            return "";
        }

        private void WarnIfStale(DateTime barTime, TimeSpan tolerance, DateTime sourceTime, string name)
        {
            TrackStaleSource(name, sourceTime != DateTime.MinValue
                && barTime - sourceTime > tolerance, sourceTime, barTime);
        }

        /// <summary>
        /// Учёт источников без свежих данных. Инструмент, по которому данные встали,
        /// не торгуется - позицией по нему никто не управляет.
        ///
        /// В канал ошибок идёт только застой на несколько торговых дней. Stale bars limit
        /// меряется в барах и на часовом таймфрейме срабатывает от обычной внутридневной паузы
        /// в сделках, а календарный порог ловил бы каждый понедельник - за выходные любой
        /// источник отстаёт больше чем на сутки. Поэтому считаются расчётные дни: они идут
        /// только по торговым. Ошибка шлётся один раз на вход в состояние и повторяется
        /// не чаще раза в неделю; выход отмечается только тогда, когда про вход успели сообщить
        /// </summary>
        private void TrackStaleSource(string name, bool isStale, DateTime sourceTime, DateTime barTime)
        {
            if (isStale == false)
            {
                if (_staleErrorSent.ContainsKey(name))
                {
                    SendNewLogMessage("Источник " + name + " снова отдаёт данные, "
                        + "инструмент вернулся в торговлю", LogMessageType.System);
                }

                _staleSince.Remove(name);
                _staleErrorSent.Remove(name);
                _staleDayCount.Remove(name);
                _staleLastDay.Remove(name);

                return;
            }

            if (_staleSince.ContainsKey(name) == false)
            {
                _staleSince.Add(name, barTime);
            }

            DateTime lastDay;

            if (_staleLastDay.TryGetValue(name, out lastDay) == false)
            {
                _staleLastDay.Add(name, barTime.Date);
                _staleDayCount.Add(name, 1);
            }
            else if (lastDay != barTime.Date)
            {
                _staleLastDay[name] = barTime.Date;
                _staleDayCount[name] = _staleDayCount[name] + 1;
            }

            if (_staleWarnDate.Date != barTime.Date)
            {
                _staleWarnDate = barTime;

                SendNewLogMessage("Источник отстаёт: " + name + ", последняя свеча "
                    + sourceTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                    + ". Инструмент не торгуется, в капитале учтён по последней цене",
                    LogMessageType.System);
            }


            // внутридневная пауза в сделках - обычное дело. Тревожит источник, который молчит
            // несколько торговых дней подряд
            if (_staleDayCount[name] < StaleAlertDays)
            {
                return;
            }

            DateTime lastSent;

            if (_staleErrorSent.TryGetValue(name, out lastSent) == false)
            {
                _staleErrorSent.Add(name, barTime);

                SendExecutionProblem("Источник " + name + " молчит " + _staleDayCount[name]
                    + "-й торговый день, последняя свеча "
                    + sourceTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                    + ". Инструмент не торгуется, позиция по нему не управляется");
            }
            else if ((barTime - lastSent).TotalDays >= AlertRepeatDays)
            {
                _staleErrorSent[name] = barTime;

                SendExecutionProblem("Источник " + name + " молчит уже " + _staleDayCount[name]
                    + " торговых дней. Инструмент не торгуется, позиция по нему не управляется");
            }
        }

        private DateTime GetTabLastBarTime(BotTabSimple tab)
        {
            if (tab == null
                || tab.CandlesFinishedOnly == null
                || tab.CandlesFinishedOnly.Count == 0)
            {
                return DateTime.MinValue;
            }

            return tab.CandlesFinishedOnly[tab.CandlesFinishedOnly.Count - 1].TimeStart;
        }

        /// <summary>
        /// Причина отмены отложенного плана покупок. Проверяется не только счётчик баров,
        /// который живёт в памяти и обнуляется при перезапуске, но и время самого плана:
        /// иначе после рестарта старый план исполнился бы на первой же свече
        /// </summary>
        private string GetPendingBuysExpireReason(DateTime barTime)
        {
            if (_state.PendingBuysTime == DateTime.MinValue)
            {
                return "у плана нет времени составления";
            }

            if (barTime.Date != _state.PendingBuysTime.Date)
            {
                return "план составлен " + _state.PendingBuysTime.ToString("dd.MM.yyyy HH:mm",
                    CultureInfo.InvariantCulture) + ", сегодня другой торговый день";
            }

            if (_barsAfterPendingBuys > _pendingBuysTtlBars.ValueInt)
            {
                return "прошло баров: " + _barsAfterPendingBuys;
            }

            if (_barLength > TimeSpan.Zero)
            {
                TimeSpan maxAge = TimeSpan.FromTicks(_barLength.Ticks * (_pendingBuysTtlBars.ValueInt + 1));

                if (barTime - _state.PendingBuysTime > maxAge)
                {
                    return "план старше " + Math.Round((barTime - _state.PendingBuysTime).TotalMinutes, 0)
                        + " минут при допустимых " + Math.Round(maxAge.TotalMinutes, 0);
                }
            }

            return "";
        }

        private void ClearPendingBuys()
        {
            _state.PendingBuys = new Dictionary<string, decimal>();
            _state.PendingBuysTime = DateTime.MinValue;
            _state.PendingReason = "";
            _barsAfterPendingBuys = 0;

            _pendingStockOrders = null;
            _pendingLqdtOrders = null;
            _pendingCashAtPlan = -1;
            _pendingSince = DateTime.MinValue;
            _waitingForSells = false;
        }

        private void LogDecision(List<KorovinAsset> assets, decimal equity, decimal cash,
            string reason, List<KorovinAsset> sells, List<KorovinAsset> buys, DateTime barTime, bool reopen)
        {
            if (StartProgram == StartProgram.IsOsOptimizer)
            {
                return;
            }

            string levelSince = _ladder.LevelEnterDate == DateTime.MinValue
                ? "-"
                : _ladder.LevelEnterDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

            string loadText = ". Загрузка: " + Math.Round(_ladder.KApplied, 3)
                + " (цель " + Math.Round(_ladder.KTarget, 3)
                + " = глубина " + Math.Round(_ladder.KDepth, 3)
                + " + скорость " + Math.Round(_ladder.KSpeed, 3)
                + " - перегрев " + Math.Round(_ladder.KEuphoria, 3) + ")"
                + ", резерв " + Math.Round(_lastReserveUp, 1) + " п.п.";

            string message = "Ребалансировка " + barTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                + ". Повод: " + reason
                + ". Уровень: " + _ladder.GetLevelName() + " (" + _ladder.Level + ") с " + levelSince
                + loadText
                + ". Индекс: " + Math.Round(_totalReturnIndex.CurrentValue, 2)
                + ", пик " + Math.Round(_ladder.Peak, 2)
                + ", дно " + Math.Round(_ladder.Trough, 2)
                + ". Просадка индекса: " + Math.Round(_ladder.Drawdown, 1) + "%"
                + ". Рост от дна: " + Math.Round(_ladder.Recovery, 1) + "%"
                + (_failedOrders > 0 ? ". Отказов заявок с прошлой ребалансировки: " + _failedOrders : "")
                + ". Портфель: " + Math.Round(equity, 0)
                + ". Деньги: " + Math.Round(_lastCashRaw, 0)
                + ". ValueCurrent: " + Math.Round(_lastPortfolioValue, 0)
                + ", вложено по входу: " + Math.Round(_lastInvestedAtCost, 0)
                + ", позиции по рынку: " + Math.Round(_lastPositionsValue, 0);

            if (reopen)
            {
                // в этом режиме инструмент закрывается целиком и покупается на целевую сумму,
                // поэтому дельта ничего не говорит: показываем обе реальные суммы
                List<KorovinAsset> touched = new List<KorovinAsset>();

                for (int i = 0; i < sells.Count; i++)
                {
                    touched.Add(sells[i]);
                }

                for (int i = 0; i < buys.Count; i++)
                {
                    if (touched.Contains(buys[i]) == false)
                    {
                        touched.Add(buys[i]);
                    }
                }

                for (int i = 0; i < touched.Count; i++)
                {
                    KorovinAsset asset = touched[i];

                    message += Environment.NewLine + "  переоткрытие " + asset.Name
                        + ": закрыть " + Math.Round(asset.Value, 0)
                        + ", купить " + Math.Round(asset.TargetMoney, 0)
                        + " (вес " + Math.Round(equity > 0 ? asset.Value / equity * 100m : 0, 1)
                        + "% при цели " + Math.Round(asset.TargetWeight, 1) + "%)";
                }
            }
            else
            {
                for (int i = 0; i < sells.Count; i++)
                {
                    message += Environment.NewLine + "  продажа " + sells[i].Name
                        + " на " + Math.Round(GetTradeMoney(sells[i]), 0)
                        + " (вес " + Math.Round(equity > 0 ? sells[i].Value / equity * 100m : 0, 1)
                        + "% при цели " + Math.Round(sells[i].TargetWeight, 1) + "%)";
                }

                for (int i = 0; i < buys.Count; i++)
                {
                    message += Environment.NewLine + "  покупка " + buys[i].Name
                        + " на " + Math.Round(GetTradeMoney(buys[i]), 0)
                        + " (вес " + Math.Round(equity > 0 ? buys[i].Value / equity * 100m : 0, 1)
                        + "% при цели " + Math.Round(buys[i].TargetWeight, 1) + "%)";
                }
            }

            SendNewLogMessage(message, LogMessageType.System);
        }

        private string GetStateFilePath()
        {
            return @"Engine\" + NameStrategyUniq + "KorovinState.txt";
        }

        private string GetIndexCacheFilePath()
        {
            return @"Engine\" + NameStrategyUniq + "KorovinIndexCache.txt";
        }

        private void LoadState()
        {
            if (StartProgram != StartProgram.IsOsTrader)
            {
                return;
            }

            _state.LogMessageEvent += State_LogMessageEvent;
            _state.Load(GetStateFilePath());
        }

        /// <summary>
        /// Ручная ребалансировка в обход расписания. Сама торговля
        /// отсюда не запускается: флаг подхватит ближайший расчёт в потоке робота,
        /// когда придут свежие свечи и источники окажутся синхронны
        /// </summary>
        private void ForceRebalanceButton_UserClickOnButtonEvent()
        {
            try
            {
                if (_forceRebalance)
                {
                    SendNewLogMessage("Внеплановая ребалансировка уже запрошена и ждёт ближайшего расчёта",
                        LogMessageType.System);
                    return;
                }

                // в этих режимах главный цикл до ребалансировки не доходит: запрос просто
                // повис бы и сработал потом сам собой, когда режим переключат
                if (_regime.ValueString == "Off"
                    || _regime.ValueString == "OnlyClosePosition")
                {
                    SendNewLogMessage("Внеплановая ребалансировка невозможна: режим "
                        + _regime.ValueString, LogMessageType.Error);
                    return;
                }

                if (AskUserToRebalance() == false)
                {
                    return;
                }

                _forceRebalance = true;

                SendNewLogMessage("Запрошена внеплановая ребалансировка. Будет выполнена "
                    + "на ближайшем расчёте в обход расписания", LogMessageType.System);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Окно подтверждения. Клик приходит из потока интерфейса, но кнопку можно нажать
        /// и через MCP, поэтому диалог на всякий случай поднимается через диспетчер
        /// </summary>
        private bool AskUserToRebalance()
        {
            bool accepted = false;

            Action show = () =>
            {
                AcceptDialogUi dialog = new AcceptDialogUi(
                    "Вы действительно хотите провести внеплановую ребалансировку?");

                dialog.ShowDialog();

                accepted = dialog.UserAcceptAction;
            };

            if (MainWindow.GetDispatcher.CheckAccess())
            {
                show();
            }
            else
            {
                MainWindow.GetDispatcher.Invoke(show);
            }

            return accepted;
        }

        /// <summary>
        /// Однократное восстановление лестницы после перезапуска. Прогон автомата по истории
        /// не воспроизводит внутридневные шаги, поэтому доверяем сохранённому снимку.
        /// Вызывается ПОСЛЕ прогона: иначе снимок был бы затёрт им же.
        /// Возвращает true, если снимок применён
        /// </summary>
        private bool RestoreLadderFromState()
        {
            if (_ladderRestored
                || StartProgram != StartProgram.IsOsTrader)
            {
                return false;
            }

            _ladderRestored = true;

            if (_state.LadderSaved == false)
            {
                return false;
            }

            decimal reconstructed = _ladder.KApplied;

            _ladder.RestoreState(_state.LastLoad, _state.LastLevel, _state.LadderPeak,
                _state.LadderTrough, _state.LadderTroughDate, _state.LadderLevelEnterDate);

            _ladder.RestoreLevelEnterIndex(_totalReturnIndex.Dates);

            SendNewLogMessage("Лестница восстановлена из состояния: загрузка "
                + Math.Round(_state.LastLoad, 3) + ", уровень " + _state.LastLevel
                + " (прогон по истории дал бы " + Math.Round(reconstructed, 3) + ")",
                LogMessageType.System);

            return true;
        }

        private void State_LogMessageEvent(string message)
        {
            SendNewLogMessage(message, LogMessageType.Error);
        }

        /// <summary>
        /// Если состояние потеряно, дату последней ребалансировки видно по журналу сделок:
        /// иначе робот счёл бы, что не торговал никогда, и мог бы сразу выполнить плановую
        /// </summary>
        private void RestoreLastRebalanceFromJournal(List<BotTabSimple> stockTabs)
        {
            if (_journalRestored
                || StartProgram != StartProgram.IsOsTrader)
            {
                return;
            }

            _journalRestored = true;

            if (_state.LastRebalanceDate != DateTime.MinValue)
            {
                return;
            }

            DateTime last = DateTime.MinValue;

            List<BotTabSimple> tabs = new List<BotTabSimple>(stockTabs);
            tabs.Add(_tabGold);
            tabs.Add(_tabLqdt);

            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i] == null)
                {
                    continue;
                }

                last = MaxPositionTime(tabs[i].PositionsOpenAll, last);
                last = MaxPositionTime(tabs[i].PositionsCloseAll, last);
            }

            if (last == DateTime.MinValue)
            {
                return;
            }

            _state.LastRebalanceDate = last.Date;
            _state.LastScheduledDate = last.Date;

            // вместе с файлом состояния потерян отложенный план покупок и вся лестница
            SendExecutionProblem("Файл состояния отсутствовал. Дата последней ребалансировки "
                + "поднята из журнала сделок: "
                + last.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                + ". Отложенный план покупок и накопленное состояние лестницы потеряны");

            SaveState();
        }

        private DateTime MaxPositionTime(List<Position> positions, DateTime current)
        {
            for (int i = 0; positions != null && i < positions.Count; i++)
            {
                Position position = positions[i];

                if (position == null)
                {
                    continue;
                }

                if (position.TimeOpen > current)
                {
                    current = position.TimeOpen;
                }

                if (position.TimeClose > current)
                {
                    current = position.TimeClose;
                }
            }

            return current;
        }

        private void SaveState()
        {
            if (StartProgram != StartProgram.IsOsTrader)
            {
                return;
            }

            _state.Save(GetStateFilePath());
        }

        #endregion
    }

    /// <summary>
    /// Класс актива портфеля
    /// </summary>
    public enum KorovinAssetType
    {
        Stock,
        Gold,
        Lqdt
    }

    /// <summary>
    /// Заявка на продажу, исполнения которой ждёт план покупок, вместе с размером лота:
    /// без него по заявке не посчитать выручку
    /// </summary>
    public class KorovinWaitOrder
    {
        public Order Order;

        /// <summary>
        /// Размер лота инструмента. Объём заявки задан в лотах, а ждём мы денег: без лота
        /// сложение объёмов по разным инструментам смысла не имеет - лот денежной позиции
        /// стоит около полутора рублей, лот акции тысячи, и доля по лотам целиком
        /// определялась бы денежной ногой
        /// </summary>
        public decimal Lot = 1m;
    }

    /// <summary>
    /// Снимок отметок «повод отработан», снятый до регистрации ребалансировки.
    /// Нужен для отложенного отката: исполнение заявок известно только на следующем баре
    /// </summary>
    public class KorovinRebalanceRollback
    {
        public DateTime BarTime;

        public string Reason;

        public List<Order> SellOrders = new List<Order>();

        /// <summary>
        /// Заявки на продажу денежной позиции. Отдельно от акций: провал этой ноги
        /// откатывает только ступень лестницы, а не всю ребалансировку
        /// </summary>
        public List<Order> LqdtOrders = new List<Order>();

        public DateTime LastRebalanceDate;

        public DateTime LastScheduledDate;

        public DateTime LastLadderDate;

        public decimal LastLoad;
    }

    /// <summary>
    /// Инструмент портфеля со всеми расчётными величинами одной ребалансировки
    /// </summary>
    public class KorovinAsset
    {
        public BotTabSimple Tab;

        public KorovinAssetType Type;

        public string Name;

        public decimal Price;

        public decimal Lot = 1m;

        public decimal Volume;

        public decimal Value;

        public decimal InvestedAtCost;

        public decimal TargetWeight;

        public decimal TargetMoney;

        public decimal Delta;

        public decimal Band;

        public decimal PendingDividend;

        public bool IsFrozen;

        /// <summary>
        /// Продаётся ради восстановления неснижаемого денежного остатка, а не ради покупок.
        /// Такая продажа проходит и при перевесе внутри полосы, и когда покупать нечего
        /// </summary>
        public bool SellForCashReserve;

        /// <summary>
        /// Потолок суммы продажи, руб. Ноль означает «без ограничения» - продаётся полная
        /// дельта до цели. Ограничение нужно продаже ради денежного остатка: там надо взять
        /// ровно недостающее, а не срезать перевес целиком
        /// </summary>
        public decimal SellMoneyLimit;

        public bool IsTradable = true;

        /// <summary>
        /// Цена взята не из свечи, а из позиции: инструмент оценён приблизительно
        /// </summary>
        public bool PriceIsStale;

        public DateTime LastCandleTime;
    }
}
