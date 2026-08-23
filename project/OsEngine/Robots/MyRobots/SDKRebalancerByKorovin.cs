/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Logging;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Wiki;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace OsEngine.Robots.MyRobots
{
    [Bot("SDKRebalancerByKorovin")]
    public class SDKRebalancerByKorovin : BotPanel
    {
        #region Fields

        private const decimal LiveBuyCashBufferRate = 0.0025m;

        private readonly BotTabSimple _tabMoneyMarket;
        private readonly BotTabSimple _tabGold;
        private readonly BotTabIndex _tabMarketIndex;
        private readonly BotTabScreener _tabStocks;

        private readonly StrategyParameterString _regime;
        private readonly StrategyParameterString _tradeMode;
        private readonly StrategyParameterString _positionExecutionMode;
        private readonly StrategyParameterString _cashStorageMode;
        private readonly StrategyParameterTimeOfDay _rebalanceTime;
        private readonly StrategyParameterInt _indexHistoryLookback;
        private readonly StrategyParameterInt _assetHistoryLookback;
        private readonly StrategyParameterInt _breadthSmaLength;
        private readonly StrategyParameterString _stockSettingsMode;
        private readonly StrategyParameterDecimal _defaultStockMaxNavWeight;
        private readonly StrategyParameterDecimal _calmCashWeight;
        private readonly StrategyParameterDecimal _minimumCashWeight;
        private readonly StrategyParameterDecimal _hardCashReserveWeight;
        private readonly StrategyParameterDecimal _calmEquityWeight;
        private readonly StrategyParameterDecimal _calmGoldWeight;
        private readonly StrategyParameterDecimal _panicDrawdownStart;
        private readonly StrategyParameterDecimal _panicDrawdownFull;
        private readonly StrategyParameterDecimal _panicBreadthStart;
        private readonly StrategyParameterDecimal _panicBreadthFull;
        private readonly StrategyParameterDecimal _panicDrawdownWeight;
        private readonly StrategyParameterDecimal _panicBreadthWeight;
        private readonly StrategyParameterDecimal _equityDrawdownFull;
        private readonly StrategyParameterDecimal _goldDrawdownFull;
        private readonly StrategyParameterDecimal _relativeClassTilt;
        private readonly StrategyParameterDecimal _goldPanicTilt;
        private readonly StrategyParameterDecimal _equityRiskMinimum;
        private readonly StrategyParameterDecimal _equityRiskMaximum;
        private readonly StrategyParameterDecimal _stockDrawdownFull;
        private readonly StrategyParameterDecimal _stockRelativeOffset;
        private readonly StrategyParameterDecimal _stockRelativeRange;
        private readonly StrategyParameterDecimal _stockAbsoluteWeight;
        private readonly StrategyParameterDecimal _stockRelativeWeight;
        private readonly StrategyParameterDecimal _stockCheapnessLambda;
        private readonly StrategyParameterDecimal _rebalanceNavBand;
        private readonly StrategyParameterDecimal _rebalanceRelativeBand;
        private readonly StrategyParameterDecimal _rebalanceRate;

        private readonly object _stateLocker = new object();
        private readonly object _stockSettingsLocker = new object();
        private readonly List<KorovinStockUserSetting> _stockSettings = new List<KorovinStockUserSetting>();
        private readonly HashSet<BotTabSimple> _executionEventTabs = new HashSet<BotTabSimple>();
        private readonly List<PendingBuyOrder> _pendingBuyOrders = new List<PendingBuyOrder>();
        private readonly List<PendingReopenOrder> _pendingReopenOrders = new List<PendingReopenOrder>();
        private readonly HashSet<string> _dailyLogKeys = new HashSet<string>();
        private readonly Dictionary<BotTabSimple, DailyPriceCache> _dailyPriceCaches =
            new Dictionary<BotTabSimple, DailyPriceCache>();
        private readonly DailyPriceCache _marketIndexDailyPriceCache = new DailyPriceCache();
        private readonly Dictionary<string, DividendHistoryCache> _dividendHistoryCaches =
            new Dictionary<string, DividendHistoryCache>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<BotTabSimple, StockSignalCache> _stockSignalCaches =
            new Dictionary<BotTabSimple, StockSignalCache>();

        private WindowsFormsHost _hostStockSettings;
        private DataGridView _gridStockSettings;
        private DateTime _lastRebalanceDate = DateTime.MinValue;
        private DateTime _dailyLogDate = DateTime.MinValue;
        private RebalanceSourceBlocker _rebalanceSourceBlocker;
        private bool _rebalanceInProgress;
        private bool _pendingBuyExecutionInProgress;
        private bool _pendingBuyRetryRequested;
        private bool _pendingReopenExecutionInProgress;
        private bool _reopenClosuresSubmitted;
        private KorovinRebalanceOrder _pendingMoneyMarketFundingSell;
        private bool _moneyMarketFundingSellSubmitted;
        private decimal _moneyMarketFundingVolumeBefore;
        private decimal _moneyMarketFundingExpectedVolume;
        private DateTime _moneyMarketFundingLastAttemptTime = DateTime.MinValue;
        private bool _liveReopenModeWarningLogged;
        private bool _portfolioStateLogPending;
        private DateTime _portfolioStateLogDate = DateTime.MinValue;
        private bool _allScreenerModeLogged;
        private volatile bool _isDeleted;
        private bool _tableIsUpdating;

        #endregion

        #region Constructor

        public SDKRebalancerByKorovin(string name, StartProgram startProgram) : base(name, startProgram)
        {
            _regime = CreateParameter("Regime", "Off", new string[] { "Off", "On" }, "Base");
            _tradeMode = CreateParameter(
                "Trade mode",
                "Rebalance",
                new string[] { "Rebalance", "Buy and hold" },
                "Base");
            _positionExecutionMode = CreateParameter(
                "Position execution mode",
                "Modify",
                new string[] { "Modify", "Reopen" },
                "Base");
            _cashStorageMode = CreateParameter(
                "Cash storage mode",
                "Money market",
                new string[] { "Free cash", "Money market" },
                "Base");
            _rebalanceTime = CreateParameterTimeOfDay("Daily rebalance time", 12, 0, 0, 0, "Base");
            _indexHistoryLookback = CreateParameter(
                "Index history lookback",
                252,
                50,
                1000,
                1,
                "History");
            _assetHistoryLookback = CreateParameter(
                "Asset history lookback",
                252,
                50,
                1000,
                1,
                "History");
            _breadthSmaLength = CreateParameter("Breadth SMA length", 200, 20, 1000, 1, "History");

            string defaultStockSettingsMode = startProgram == StartProgram.IsOsOptimizer
                || IsOptimizerBotName(name)
                ? "All screener"
                : "Table";
            _stockSettingsMode = CreateParameter(
                "Stock settings mode",
                defaultStockSettingsMode,
                new string[] { "Table", "All screener" },
                "Stock universe");
            _defaultStockMaxNavWeight = CreateParameter(
                "Default stock max NAV weight",
                0.10m,
                0.01m,
                1m,
                0.01m,
                "Stock universe");

            _calmCashWeight = CreateParameter("Calm cash weight", 0.30m, 0m, 1m, 0.01m, "Allocation");
            _minimumCashWeight = CreateParameter("Minimum cash weight", 0.10m, 0m, 1m, 0.01m, "Allocation");
            _hardCashReserveWeight = CreateParameter("Hard cash reserve", 0.05m, 0m, 1m, 0.01m, "Allocation");
            _calmEquityWeight = CreateParameter("Calm equity weight", 0.50m, 0m, 1m, 0.01m, "Allocation");
            _calmGoldWeight = CreateParameter("Calm gold weight", 0.20m, 0m, 1m, 0.01m, "Allocation");

            _panicDrawdownStart = CreateParameter("Panic DD start", 0.10m, 0m, 1m, 0.01m, "Panic");
            _panicDrawdownFull = CreateParameter("Panic DD full", 0.40m, 0m, 1m, 0.01m, "Panic");
            _panicBreadthStart = CreateParameter("Panic breadth start", 0.50m, 0m, 1m, 0.01m, "Panic");
            _panicBreadthFull = CreateParameter("Panic breadth full", 0.90m, 0m, 1m, 0.01m, "Panic");
            _panicDrawdownWeight = CreateParameter("Panic DD weight", 0.70m, 0m, 1m, 0.01m, "Panic");
            _panicBreadthWeight = CreateParameter("Panic breadth weight", 0.30m, 0m, 1m, 0.01m, "Panic");

            _equityDrawdownFull = CreateParameter("Equity DD full", 0.35m, 0.01m, 1m, 0.01m, "Class tilt");
            _goldDrawdownFull = CreateParameter("Gold DD full", 0.25m, 0.01m, 1m, 0.01m, "Class tilt");
            _relativeClassTilt = CreateParameter("Relative class tilt", 0.15m, 0m, 1m, 0.01m, "Class tilt");
            _goldPanicTilt = CreateParameter("Gold panic tilt", 0m, -1m, 1m, 0.01m, "Class tilt");
            _equityRiskMinimum = CreateParameter("Equity risk minimum", 0.55m, 0m, 1m, 0.01m, "Class tilt");
            _equityRiskMaximum = CreateParameter("Equity risk maximum", 0.85m, 0m, 1m, 0.01m, "Class tilt");

            _stockDrawdownFull = CreateParameter("Stock DD full", 0.40m, 0.01m, 1m, 0.01m, "Stock tilt");
            _stockRelativeOffset = CreateParameter("Stock relative DD offset", 0.10m, 0m, 1m, 0.01m, "Stock tilt");
            _stockRelativeRange = CreateParameter("Stock relative range", 0.30m, 0.01m, 1m, 0.01m, "Stock tilt");
            _stockAbsoluteWeight = CreateParameter("Stock absolute weight", 0.70m, 0m, 1m, 0.01m, "Stock tilt");
            _stockRelativeWeight = CreateParameter("Stock relative weight", 0.30m, 0m, 1m, 0.01m, "Stock tilt");
            _stockCheapnessLambda = CreateParameter("Stock cheapness lambda", 0.70m, 0m, 5m, 0.01m, "Stock tilt");

            _rebalanceNavBand = CreateParameter("Rebalance NAV band", 0.0025m, 0m, 1m, 0.0005m, "Rebalance");
            _rebalanceRelativeBand = CreateParameter("Rebalance relative band", 0.20m, 0m, 1m, 0.01m, "Rebalance");
            _rebalanceRate = CreateParameter("Rebalance rate", 0.50m, 0m, 1m, 0.01m, "Rebalance");

            _tabMoneyMarket = TabCreate<BotTabSimple>();
            _tabGold = TabCreate<BotTabSimple>();
            _tabMarketIndex = TabCreate<BotTabIndex>();
            _tabStocks = TabCreate<BotTabScreener>();

            if (UseAllScreenerStockSettings() == false)
            {
                LoadStockSettings();
                CreateStockSettingsTable();
            }

            _tabMoneyMarket.CandleFinishedEvent += TabSimple_CandleFinishedEvent;
            _tabGold.CandleFinishedEvent += TabSimple_CandleFinishedEvent;
            _tabMarketIndex.SpreadChangeEvent += TabMarketIndex_SpreadChangeEvent;
            _tabStocks.CandleFinishedEvent += TabStocks_CandleFinishedEvent;
            _tabStocks.CandlesSyncFinishedEvent += TabStocks_CandlesSyncFinishedEvent;
            DeleteEvent += SDKRebalancerByKorovin_DeleteEvent;

            SubscribeExecutionEvents(_tabMoneyMarket);
            SubscribeExecutionEvents(_tabGold);
            RefreshStockSettingsFromTabs();

            if (StartProgram == StartProgram.IsOsTrader)
            {
                Thread schedulerThread = new Thread(SchedulerThreadArea);
                schedulerThread.IsBackground = true;
                schedulerThread.Name = NameStrategyUniq + " daily rebalance scheduler";
                schedulerThread.Start();
            }

            Description = OsLocalization.ConvertToLocString(
                "Eng:Daily long only portfolio rebalancer for money market, gold and stocks. " +
                "Dividend gaps are removed from stock signals and cash reserve is always preserved._" +
                "Ru:Дневной портфельный ребалансировщик без шортов для денежного рынка, золота и акций. " +
                "Дивидендные гэпы исключаются из сигналов, а денежный резерв всегда сохраняется._");
        }

        #endregion

        #region Events and scheduler

        private void TabSimple_CandleFinishedEvent(List<Candle> candles)
        {
            try
            {
                TryRunScheduledRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void TabMarketIndex_SpreadChangeEvent(List<Candle> candles)
        {
            try
            {
                TryRunScheduledRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void TabStocks_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            try
            {
                AddStockSettingFromTab(tab);
                SubscribeExecutionEvents(tab);
                TryRunScheduledRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void TabStocks_CandlesSyncFinishedEvent(List<BotTabSimple> tabs)
        {
            try
            {
                RefreshStockSettingsFromTabs();
                TryRunScheduledRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Tab_PositionClosingSuccesEvent(Position position)
        {
            try
            {
                TryExecutePendingReopens();
                TryExecutePendingBuys();
                TryLogPortfolioStateAfterRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Tab_MyTradeEvent(MyTrade trade)
        {
            try
            {
                TryExecutePendingReopens();
                TryExecutePendingBuys();
                TryLogPortfolioStateAfterRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void Tab_OrderUpdateEvent(Order order)
        {
            try
            {
                RestoreCompletedPartialClosures();

                if (order != null && order.State == OrderStateType.Done)
                {
                    TryExecutePendingReopens();
                    TryExecutePendingBuys();
                }

                TryLogPortfolioStateAfterRebalance();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SchedulerThreadArea()
        {
            while (_isDeleted == false)
            {
                try
                {
                    TryRunScheduledRebalance();
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }

                Thread.Sleep(5000);
            }
        }

        private void TryRunScheduledRebalance()
        {
            if (_isDeleted)
            {
                return;
            }

            RestoreCompletedPartialClosures();
            TryExecutePendingReopens();
            TryExecutePendingBuys();
            TryLogPortfolioStateAfterRebalance();

            if (HasPendingRegularBuy())
            {
                return;
            }

            if (_regime.ValueString == "Off")
            {
                return;
            }

            DateTime serverTime = TimeServer;

            if (serverTime == DateTime.MinValue
                || serverTime.TimeOfDay < _rebalanceTime.Value.TimeSpan)
            {
                return;
            }

            lock (_stateLocker)
            {
                if (_rebalanceInProgress
                    || _pendingReopenOrders.Count > 0
                    || SDKRebalancerByKorovinCalculator.ShouldRunDaily(
                        serverTime,
                        _rebalanceTime.Value.TimeSpan,
                        _lastRebalanceDate) == false)
                {
                    return;
                }

                if (_rebalanceSourceBlocker != null)
                {
                    long currentVersion = GetRebalanceSourceVersion(_rebalanceSourceBlocker.Kind, _rebalanceSourceBlocker.Tab);

                    if (currentVersion == _rebalanceSourceBlocker.Version)
                    {
                        return;
                    }

                    _rebalanceSourceBlocker = null;
                }

                _rebalanceInProgress = true;
            }

            bool completed = false;

            try
            {
                completed = ExecuteDailyRebalance(serverTime);
            }
            finally
            {
                lock (_stateLocker)
                {
                    if (completed)
                    {
                        _lastRebalanceDate = serverTime.Date;
                        _rebalanceSourceBlocker = null;
                    }

                    _rebalanceInProgress = false;
                }
            }
        }

        private void SDKRebalancerByKorovin_DeleteEvent()
        {
            try
            {
                _isDeleted = true;
                _tabMoneyMarket.CandleFinishedEvent -= TabSimple_CandleFinishedEvent;
                _tabGold.CandleFinishedEvent -= TabSimple_CandleFinishedEvent;
                _tabMarketIndex.SpreadChangeEvent -= TabMarketIndex_SpreadChangeEvent;
                _tabStocks.CandleFinishedEvent -= TabStocks_CandleFinishedEvent;
                _tabStocks.CandlesSyncFinishedEvent -= TabStocks_CandlesSyncFinishedEvent;
                DeleteEvent -= SDKRebalancerByKorovin_DeleteEvent;

                List<BotTabSimple> executionTabs;

                lock (_stateLocker)
                {
                    executionTabs = new List<BotTabSimple>(_executionEventTabs);
                    _executionEventTabs.Clear();
                    _pendingBuyOrders.Clear();
                    _pendingReopenOrders.Clear();
                    _pendingMoneyMarketFundingSell = null;
                    ResetMoneyMarketFundingSubmission();
                    _pendingBuyRetryRequested = false;
                }

                for (int index = 0; index < executionTabs.Count; index++)
                {
                    executionTabs[index].PositionClosingSuccesEvent -= Tab_PositionClosingSuccesEvent;
                    executionTabs[index].MyTradeEvent -= Tab_MyTradeEvent;
                    executionTabs[index].OrderUpdateEvent -= Tab_OrderUpdateEvent;
                }

                DisposeStockSettingsTable();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SubscribeExecutionEvents(BotTabSimple tab)
        {
            if (tab == null)
            {
                return;
            }

            lock (_stateLocker)
            {
                if (_isDeleted || _executionEventTabs.Contains(tab))
                {
                    return;
                }

                tab.PositionClosingSuccesEvent += Tab_PositionClosingSuccesEvent;
                tab.MyTradeEvent += Tab_MyTradeEvent;
                tab.OrderUpdateEvent += Tab_OrderUpdateEvent;
                _executionEventTabs.Add(tab);
            }
        }

        private void RestoreCompletedPartialClosures()
        {
            List<BotTabSimple> tabs;

            lock (_stateLocker)
            {
                tabs = new List<BotTabSimple>(_executionEventTabs);
            }

            for (int index = 0; index < tabs.Count; index++)
            {
                RestoreCompletedPartialClosures(tabs[index]);
            }
        }

        private void RestoreCompletedPartialClosures(BotTabSimple tab)
        {
            if (HasPendingReopen(tab))
            {
                return;
            }

            List<Position> positions = tab.PositionsOpenAll;

            for (int index = 0; positions != null && index < positions.Count; index++)
            {
                Position position = positions[index];

                if (position != null
                    && position.State == PositionStateType.Closing
                    && position.OpenVolume > 0m
                    && position.CloseActive == false)
                {
                    position.State = PositionStateType.Open;
                }
            }
        }

        #endregion

        #region Rebalance calculation

        private bool ExecuteDailyRebalance(DateTime serverTime)
        {
            KorovinCalculationParameters parameters = BuildCalculationParameters();

            if (ValidateParameters(parameters) == false)
            {
                return false;
            }

            RefreshStockSettingsFromTabs();
            RuntimeSnapshot snapshot = BuildRuntimeSnapshot(serverTime, parameters);

            if (snapshot == null)
            {
                return false;
            }

            List<KorovinEnabledStockHistory> breadthStocks = new List<KorovinEnabledStockHistory>();

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];

                if (stock.Setting.Enabled == false)
                {
                    continue;
                }

                KorovinEnabledStockHistory history = new KorovinEnabledStockHistory();
                history.Name = stock.Name;
                history.IsEnabled = true;
                history.SignalHistory = stock.SignalValues;
                breadthStocks.Add(history);
            }

            KorovinMarketPanicResult panic = SDKRebalancerByKorovinCalculator.CalculatePanic(
                snapshot.IndexValues,
                breadthStocks,
                parameters);

            if (panic.IsValid == false)
            {
                SendDailyRebalanceLog(
                    "Rebalance skipped. Market or breadth history is not ready",
                    LogMessageType.System);
                return false;
            }

            KorovinAllocationResult allocation = SDKRebalancerByKorovinCalculator.CalculateAllocation(
                panic,
                snapshot.GoldValues,
                parameters);

            if (allocation.IsValid == false)
            {
                SendDailyRebalanceLog(
                    "Rebalance skipped. Gold history or allocation parameters are not ready",
                    LogMessageType.System);
                return false;
            }

            List<KorovinStockAllocationInput> stockAllocationInputs = new List<KorovinStockAllocationInput>();

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];

                if (stock.Setting.Enabled == false)
                {
                    continue;
                }

                stock.Score = SDKRebalancerByKorovinCalculator.CalculateStockScore(
                    stock.SignalValues,
                    panic.MarketDrawdown,
                    parameters);

                if (stock.Score.IsValid == false)
                {
                    SendDailyRebalanceLog(
                        "Rebalance skipped. Stock history is not ready for " + stock.Name,
                        LogMessageType.System);
                    return false;
                }

                KorovinStockAllocationInput input = new KorovinStockAllocationInput();
                input.Name = stock.Name;
                input.BaseWeight = stock.Setting.BaseWeight;
                input.Multiplier = stock.Score.Multiplier;
                input.Cap = stock.Setting.MaxNavWeight;
                stockAllocationInputs.Add(input);
            }

            KorovinCappedAllocationResult cappedAllocation =
                SDKRebalancerByKorovinCalculator.AllocateStocksWithCaps(
                    stockAllocationInputs,
                    allocation.TargetEquityWeight);

            if (cappedAllocation.IsValid == false)
            {
                SendDailyRebalanceLog(
                    "Rebalance skipped. Stock cap allocation is invalid",
                    LogMessageType.System);
                return false;
            }

            Dictionary<string, decimal> targetStockWeights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < cappedAllocation.StockWeights.Count; index++)
            {
                targetStockWeights[cappedAllocation.StockWeights[index].Name] = cappedAllocation.StockWeights[index].Weight;
            }

            List<KorovinRebalanceAssetInput> riskAssets = new List<KorovinRebalanceAssetInput>();
            KorovinRebalanceAssetInput goldAsset = new KorovinRebalanceAssetInput();
            goldAsset.Name = snapshot.Gold.Name;
            goldAsset.CurrentWeight = snapshot.Gold.CurrentWeight;
            goldAsset.TargetWeight = allocation.TargetGoldWeight;
            goldAsset.RebalanceWeight = snapshot.Gold.CurrentWeight;
            goldAsset.Reason = "Gold rebalance";
            riskAssets.Add(goldAsset);

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];
                decimal targetWeight = 0m;
                targetStockWeights.TryGetValue(stock.Name, out targetWeight);
                stock.TargetWeight = targetWeight;
                stock.RebalanceWeight = SDKRebalancerByKorovinCalculator.CalculateDividendNeutralRebalanceWeight(
                    stock.IsExDividendDate,
                    stock.PreviousWeight,
                    stock.AdjustedReturn,
                    stock.CurrentWeight);

                KorovinRebalanceAssetInput asset = new KorovinRebalanceAssetInput();
                asset.Name = stock.Name;
                asset.CurrentWeight = stock.CurrentWeight;
                asset.TargetWeight = targetWeight;
                asset.RebalanceWeight = stock.RebalanceWeight;
                asset.Reason = stock.Setting.Enabled ? "Stock rebalance" : "Disabled stock exit";
                riskAssets.Add(asset);
            }

            KorovinRebalancePlanInput planInput = new KorovinRebalancePlanInput();
            planInput.NetAssetValue = snapshot.NetAssetValue;
            planInput.FreeCash = snapshot.FreeCash;
            planInput.EffectiveTargetCashWeight = allocation.TargetCashWeight + cappedAllocation.CashShortfallWeight;
            planInput.MoneyMarketName = snapshot.MoneyMarket.Name;
            planInput.MoneyMarketRebalanceWeight = snapshot.MoneyMarket.CurrentWeight;
            planInput.RiskAssets = riskAssets;

            KorovinRebalancePlanResult plan = SDKRebalancerByKorovinCalculator.BuildRebalancePlan(planInput, parameters);

            if (plan.IsValid == false)
            {
                SendDailyRebalanceLog(
                    "Rebalance skipped. Rebalance plan is invalid",
                    LogMessageType.System);
                return false;
            }

            PrepareMoneyMarketFundingPlan(plan, snapshot);
            LogCalculation(snapshot, panic, allocation, cappedAllocation, plan);
            MarkPortfolioStateLogPending(serverTime.Date);
            ExecutePlan(plan, snapshot, serverTime.Date);
            TryLogPortfolioStateAfterRebalance();
            return true;
        }

        private KorovinCalculationParameters BuildCalculationParameters()
        {
            KorovinCalculationParameters result = new KorovinCalculationParameters();
            result.IndexHistoryLookback = _indexHistoryLookback.ValueInt;
            result.AssetHistoryLookback = _assetHistoryLookback.ValueInt;
            result.BreadthSmaLength = _breadthSmaLength.ValueInt;
            result.PanicDrawdownStart = _panicDrawdownStart.ValueDecimal;
            result.PanicDrawdownRange = _panicDrawdownFull.ValueDecimal - _panicDrawdownStart.ValueDecimal;
            result.PanicBreadthStart = _panicBreadthStart.ValueDecimal;
            result.PanicBreadthRange = _panicBreadthFull.ValueDecimal - _panicBreadthStart.ValueDecimal;
            result.PanicDrawdownWeight = _panicDrawdownWeight.ValueDecimal;
            result.PanicBreadthWeight = _panicBreadthWeight.ValueDecimal;
            result.BaseCashWeight = _calmCashWeight.ValueDecimal;
            result.PanicCashReduction = _calmCashWeight.ValueDecimal - _minimumCashWeight.ValueDecimal;
            result.MinimumCashWeight = _minimumCashWeight.ValueDecimal;
            result.EquityCheapnessDrawdown = _equityDrawdownFull.ValueDecimal;
            result.GoldCheapnessDrawdown = _goldDrawdownFull.ValueDecimal;
            decimal calmRiskWeight = _calmEquityWeight.ValueDecimal + _calmGoldWeight.ValueDecimal;
            result.BaseEquityRiskShare = calmRiskWeight > 0m
                ? _calmEquityWeight.ValueDecimal / calmRiskWeight
                : 0m;
            result.RelativeCheapnessRiskAdjustment = _relativeClassTilt.ValueDecimal;
            result.GoldPanicTilt = _goldPanicTilt.ValueDecimal;
            result.MinimumEquityRiskShare = _equityRiskMinimum.ValueDecimal;
            result.MaximumEquityRiskShare = _equityRiskMaximum.ValueDecimal;
            result.StockAbsoluteCheapnessDrawdown = _stockDrawdownFull.ValueDecimal;
            result.StockRelativeDrawdownOffset = _stockRelativeOffset.ValueDecimal;
            result.StockRelativeCheapnessRange = _stockRelativeRange.ValueDecimal;
            result.StockAbsoluteCheapnessWeight = _stockAbsoluteWeight.ValueDecimal;
            result.StockRelativeCheapnessWeight = _stockRelativeWeight.ValueDecimal;
            result.StockMultiplierExponent = _stockCheapnessLambda.ValueDecimal;
            result.HardCashReserveWeight = _hardCashReserveWeight.ValueDecimal;
            result.MinimumRebalanceZone = _rebalanceNavBand.ValueDecimal;
            result.RelativeRebalanceZone = _rebalanceRelativeBand.ValueDecimal;
            result.RebalanceRate = _rebalanceRate.ValueDecimal;
            result.AllowSellOrders = _tradeMode.ValueString != "Buy and hold";
            result.SweepFreeCashToMoneyMarket = StoreFreeCashInMoneyMarket();
            return result;
        }

        private bool StoreFreeCashInMoneyMarket()
        {
            return _cashStorageMode.ValueString == "Money market";
        }

        private bool ValidateParameters(KorovinCalculationParameters parameters)
        {
            if (parameters.PanicDrawdownRange <= 0m
                || parameters.PanicBreadthRange <= 0m
                || parameters.BaseCashWeight < parameters.MinimumCashWeight
                || parameters.HardCashReserveWeight > parameters.MinimumCashWeight
                || parameters.GoldPanicTilt < -1m
                || parameters.GoldPanicTilt > 1m
                || parameters.MinimumEquityRiskShare > parameters.MaximumEquityRiskShare
                || parameters.BaseEquityRiskShare <= 0m
                || Math.Abs(parameters.BaseCashWeight
                    + _calmEquityWeight.ValueDecimal
                    + _calmGoldWeight.ValueDecimal - 1m) > 0.0001m
                || Math.Abs(parameters.PanicDrawdownWeight + parameters.PanicBreadthWeight - 1m) > 0.0001m
                || Math.Abs(parameters.StockAbsoluteCheapnessWeight + parameters.StockRelativeCheapnessWeight - 1m) > 0.0001m)
            {
                SendDailyRebalanceLog(
                    "Rebalance skipped. Strategy parameters are inconsistent",
                    LogMessageType.Error);
                return false;
            }

            return true;
        }

        #endregion

        #region Runtime snapshot

        private bool ValidateRuntimeSources(
            DateTime serverTime,
            Dictionary<string, KorovinStockUserSetting> settings)
        {
            if (_tabMoneyMarket.Security == null || _tabMoneyMarket.Portfolio == null
                || _tabMoneyMarket.CandlesFinishedOnly == null)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, _tabMoneyMarket);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Configure all four data sources",
                    LogMessageType.System);
                return false;
            }

            if (_tabGold.Security == null || _tabGold.Portfolio == null
                || _tabGold.CandlesFinishedOnly == null)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, _tabGold);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Configure all four data sources",
                    LogMessageType.System);
                return false;
            }

            if (_tabMarketIndex.Candles == null)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.MarketIndex, null);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Market index has no calculated candles"
                    + " indexSources " + (_tabMarketIndex.Tabs == null ? 0 : _tabMarketIndex.Tabs.Count)
                    + " calculationDepth " + _tabMarketIndex.CalculationDepth
                    + ". Check index sources and formula",
                    LogMessageType.System);
                return false;
            }

            if (_tabStocks.Tabs == null)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.StockUniverse, null);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Configure all four data sources",
                    LogMessageType.System);
                return false;
            }

            if (IsSimpleSourceCurrent(_tabMoneyMarket, serverTime.Date) == false)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, _tabMoneyMarket);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Money market source is not ready",
                    LogMessageType.System);
                return false;
            }

            if (IsSimpleSourceCurrent(_tabGold, serverTime.Date) == false)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, _tabGold);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Gold source is not ready",
                    LogMessageType.System);
                return false;
            }

            DateTime latestMarketIndexDate = GetLatestCandleDate(
                _tabMarketIndex.Candles,
                serverTime.Date);
            bool marketIndexUsesDailySources = MarketIndexUsesDailySources();

            if (SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                serverTime.Date,
                latestMarketIndexDate,
                marketIndexUsesDailySources) == false)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.MarketIndex, null);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Market index has no usable candle"
                    + " serverDate " + FormatLogDate(serverTime.Date)
                    + " latestIndexDate " + FormatLogDate(latestMarketIndexDate)
                    + " sourceTimeFrame " + (marketIndexUsesDailySources ? "Day" : "Intraday")
                    + " indexCandles " + _tabMarketIndex.Candles.Count
                    + " indexSources " + (_tabMarketIndex.Tabs == null ? 0 : _tabMarketIndex.Tabs.Count)
                    + " calculationDepth " + _tabMarketIndex.CalculationDepth
                    + ". Check index source synchronization",
                    LogMessageType.System);
                return false;
            }

            HashSet<string> availableStocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < _tabStocks.Tabs.Count; index++)
            {
                BotTabSimple tab = _tabStocks.Tabs[index];

                if (tab == null || tab.Security == null)
                {
                    continue;
                }

                availableStocks.Add(tab.Security.Name);
                KorovinStockUserSetting setting;

                if (settings.TryGetValue(tab.Security.Name, out setting) == false
                    || setting.Enabled == false && GetOpenLongVolume(tab) <= 0m)
                {
                    continue;
                }

                if (IsSimpleSourceCurrent(tab, serverTime.Date) == false)
                {
                    SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, tab);
                    SendDailyRebalanceLog(
                        "Rebalance postponed. Stock source is not ready for " + setting.Ticker,
                        LogMessageType.System);
                    return false;
                }
            }

            foreach (KeyValuePair<string, KorovinStockUserSetting> pair in settings)
            {
                if (pair.Value.Enabled && availableStocks.Contains(pair.Key) == false)
                {
                    SetRebalanceSourceBlocker(RebalanceSourceKind.StockUniverse, null);
                    SendDailyRebalanceLog(
                        "Rebalance postponed. Enabled stock has no screener source " + pair.Key,
                        LogMessageType.Error);
                    return false;
                }
            }

            return true;
        }

        private bool IsSimpleSourceCurrent(BotTabSimple tab, DateTime currentDate)
        {
            if (tab == null || tab.Security == null || tab.CandlesFinishedOnly == null
                || tab.CandlesFinishedOnly.Count == 0)
            {
                return false;
            }

            return tab.TimeServerCurrent != DateTime.MinValue
                && tab.TimeServerCurrent.Date == currentDate.Date
                && GetMarketPrice(tab) > 0m;
        }

        private static DateTime GetLatestCandleDate(List<Candle> candles, DateTime currentDate)
        {
            for (int index = candles == null ? -1 : candles.Count - 1; index >= 0; index--)
            {
                Candle candle = candles[index];

                if (candle == null || candle.Close <= 0m || candle.TimeStart.Date > currentDate.Date)
                {
                    continue;
                }

                return candle.TimeStart.Date;
            }

            return DateTime.MinValue;
        }

        private bool MarketIndexUsesDailySources()
        {
            if (_tabMarketIndex.Tabs == null || _tabMarketIndex.Tabs.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < _tabMarketIndex.Tabs.Count; index++)
            {
                if (_tabMarketIndex.Tabs[index] == null
                    || _tabMarketIndex.Tabs[index].TimeFrame != TimeFrame.Day)
                {
                    return false;
                }
            }

            return true;
        }

        private void SetRebalanceSourceBlocker(RebalanceSourceKind kind, BotTabSimple tab)
        {
            RebalanceSourceBlocker blocker = new RebalanceSourceBlocker();
            blocker.Kind = kind;
            blocker.Tab = tab;
            blocker.Version = GetRebalanceSourceVersion(kind, tab);

            lock (_stateLocker)
            {
                _rebalanceSourceBlocker = blocker;
            }
        }

        private long GetRebalanceSourceVersion(RebalanceSourceKind kind, BotTabSimple tab)
        {
            long sourceVersion;

            if (kind == RebalanceSourceKind.MarketIndex)
            {
                sourceVersion = GetCandleSourceVersion(_tabMarketIndex.Candles, DateTime.MinValue, 0m);
                return CombineSourceVersion(sourceVersion, GetStockUniverseVersion());
            }

            if (kind == RebalanceSourceKind.StockUniverse)
            {
                return GetStockUniverseVersion();
            }

            if (tab == null)
            {
                return GetStockUniverseVersion();
            }

            List<Candle> candles = tab.CandlesFinishedOnly;
            decimal marketPrice = GetMarketPrice(tab);
            sourceVersion = GetCandleSourceVersion(candles, tab.TimeServerCurrent.Date, marketPrice);
            string securityName = tab.Security == null ? string.Empty : tab.Security.Name;
            sourceVersion = CombineSourceVersion(
                sourceVersion,
                StringComparer.OrdinalIgnoreCase.GetHashCode(securityName));
            return CombineSourceVersion(sourceVersion, GetStockUniverseVersion());
        }

        private long GetStockUniverseVersion()
        {
            long version = 17L;
            List<BotTabSimple> tabs = _tabStocks.Tabs;
            version = CombineSourceVersion(version, tabs == null ? -1 : tabs.Count);

            for (int index = 0; tabs != null && index < tabs.Count; index++)
            {
                string securityName = tabs[index] == null || tabs[index].Security == null
                    ? string.Empty
                    : tabs[index].Security.Name;
                version = CombineSourceVersion(
                    version,
                    StringComparer.OrdinalIgnoreCase.GetHashCode(securityName));
            }

            Dictionary<string, KorovinStockUserSetting> settings = GetStockSettingsCopy();

            foreach (KeyValuePair<string, KorovinStockUserSetting> pair in settings)
            {
                version = CombineSourceVersion(
                    version,
                    StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Key));
                version = CombineSourceVersion(version, pair.Value.Enabled ? 1L : 0L);
                version = CombineSourceVersion(version, pair.Value.BaseWeight.GetHashCode());
                version = CombineSourceVersion(version, pair.Value.MaxNavWeight.GetHashCode());
            }

            version = CombineSourceVersion(version, _indexHistoryLookback.ValueInt);
            version = CombineSourceVersion(version, _assetHistoryLookback.ValueInt);
            version = CombineSourceVersion(version, _breadthSmaLength.ValueInt);
            version = CombineSourceVersion(
                version,
                StringComparer.OrdinalIgnoreCase.GetHashCode(_positionExecutionMode.ValueString));
            version = CombineSourceVersion(
                version,
                StringComparer.OrdinalIgnoreCase.GetHashCode(_cashStorageMode.ValueString));

            return version;
        }

        private static long GetCandleSourceVersion(
            List<Candle> candles,
            DateTime sourceDate,
            decimal marketPrice)
        {
            long version = sourceDate.Date.Ticks;
            int count = candles == null ? -1 : candles.Count;
            version = CombineSourceVersion(version, count);
            version = CombineSourceVersion(version, candles == null ? 0L : candles.GetHashCode());

            if (count > 0)
            {
                Candle lastCandle = candles[count - 1];

                if (lastCandle != null)
                {
                    version = CombineSourceVersion(version, lastCandle.TimeStart.Ticks);
                    version = CombineSourceVersion(version, lastCandle.Close.GetHashCode());
                }
            }

            return CombineSourceVersion(version, marketPrice.GetHashCode());
        }

        private static long CombineSourceVersion(long current, long value)
        {
            unchecked
            {
                return current * 397L ^ value;
            }
        }

        private RuntimeSnapshot BuildRuntimeSnapshot(DateTime serverTime, KorovinCalculationParameters parameters)
        {
            Dictionary<string, KorovinStockUserSetting> settings = GetStockSettingsCopy();

            if (ValidateRuntimeSources(serverTime, settings) == false)
            {
                return null;
            }

            RuntimeSnapshot snapshot = new RuntimeSnapshot();
            snapshot.MoneyMarket = BuildRuntimeAsset(_tabMoneyMarket, serverTime);
            snapshot.Gold = BuildRuntimeAsset(_tabGold, serverTime);

            if (snapshot.MoneyMarket == null || snapshot.Gold == null)
            {
                SetRebalanceSourceBlocker(
                    RebalanceSourceKind.SimpleTab,
                    snapshot.MoneyMarket == null ? _tabMoneyMarket : _tabGold);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Money market or gold source is not ready",
                    LogMessageType.System);
                return null;
            }

            UpdateDailyPrices(_marketIndexDailyPriceCache, _tabMarketIndex.Candles, serverTime.Date, 0m);

            int marketIndexDailyPointCount = _marketIndexDailyPriceCache.Prices.Count;
            DateTime firstMarketIndexDailyDate = marketIndexDailyPointCount == 0
                ? DateTime.MinValue
                : _marketIndexDailyPriceCache.Prices[0].Date.Date;
            DateTime latestMarketIndexDailyDate = marketIndexDailyPointCount == 0
                ? DateTime.MinValue
                : _marketIndexDailyPriceCache.Prices[marketIndexDailyPointCount - 1].Date.Date;

            if (marketIndexDailyPointCount < parameters.IndexHistoryLookback)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.MarketIndex, null);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Market index daily history is too short"
                    + " dailyPoints " + marketIndexDailyPointCount
                    + " requiredDailyPoints " + parameters.IndexHistoryLookback
                    + " firstDailyDate " + FormatLogDate(firstMarketIndexDailyDate)
                    + " latestDailyDate " + FormatLogDate(latestMarketIndexDailyDate)
                    + " serverDate " + FormatLogDate(serverTime.Date)
                    + " indexCandles " + _tabMarketIndex.Candles.Count
                    + " indexSources " + (_tabMarketIndex.Tabs == null ? 0 : _tabMarketIndex.Tabs.Count)
                    + " calculationDepth " + _tabMarketIndex.CalculationDepth
                    + ". Increase index Calculation depth or reduce Index history lookback",
                    LogMessageType.System);
                return null;
            }

            bool marketIndexUsesDailySources = MarketIndexUsesDailySources();

            if (SDKRebalancerByKorovinCalculator.IsMarketIndexDateReady(
                serverTime.Date,
                latestMarketIndexDailyDate,
                marketIndexUsesDailySources) == false)
            {
                SetRebalanceSourceBlocker(RebalanceSourceKind.MarketIndex, null);
                SendDailyRebalanceLog(
                    "Rebalance postponed. Market index daily history has no usable point"
                    + " serverDate " + FormatLogDate(serverTime.Date)
                    + " latestDailyDate " + FormatLogDate(latestMarketIndexDailyDate)
                    + " sourceTimeFrame " + (marketIndexUsesDailySources ? "Day" : "Intraday")
                    + " dailyPoints " + marketIndexDailyPointCount
                    + " indexCandles " + _tabMarketIndex.Candles.Count
                    + " indexSources " + (_tabMarketIndex.Tabs == null ? 0 : _tabMarketIndex.Tabs.Count)
                    + " calculationDepth " + _tabMarketIndex.CalculationDepth
                    + ". Check index source timestamps",
                    LogMessageType.System);
                return null;
            }

            snapshot.IndexValues = _marketIndexDailyPriceCache.Values;
            snapshot.GoldValues = snapshot.Gold.PriceValues;

            for (int index = 0; index < _tabStocks.Tabs.Count; index++)
            {
                BotTabSimple tab = _tabStocks.Tabs[index];

                if (tab == null || tab.Security == null)
                {
                    continue;
                }

                KorovinStockUserSetting setting;

                if (settings.TryGetValue(tab.Security.Name, out setting) == false)
                {
                    continue;
                }

                RuntimeAsset asset = BuildRuntimeAsset(tab, serverTime);

                if (asset == null)
                {
                    if (setting.Enabled || GetOpenLongVolume(tab) > 0m)
                    {
                        SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, tab);
                        SendDailyRebalanceLog(
                            "Rebalance postponed. Stock source is not ready for " + setting.Ticker,
                            LogMessageType.System);
                        return null;
                    }

                    continue;
                }

                RuntimeStock stock = new RuntimeStock();
                stock.Tab = tab;
                stock.Name = asset.Name;
                stock.Price = asset.Price;
                stock.PreviousPrice = asset.PreviousPrice;
                stock.Volume = asset.Volume;
                stock.Lot = asset.Lot;
                stock.MarketValue = asset.MarketValue;
                stock.DailyPriceCache = asset.DailyPriceCache;
                stock.Setting = setting;

                if (setting.Enabled)
                {
                    if (setting.BaseWeight <= 0m || setting.MaxNavWeight <= 0m || setting.MaxNavWeight > 1m)
                    {
                        SetRebalanceSourceBlocker(RebalanceSourceKind.StockUniverse, null);
                        SendDailyRebalanceLog(
                            "Rebalance postponed. Invalid stock weights for " + stock.Name,
                            LogMessageType.Error);
                        return null;
                    }

                    StockSignalCache signalCache = UpdateStockSignals(stock.Tab, stock.Name, stock.DailyPriceCache);
                    stock.SignalPoints = signalCache.Points;
                    stock.SignalValues = signalCache.Values;

                    if (stock.SignalPoints.Count < Math.Max(
                        parameters.AssetHistoryLookback,
                        parameters.BreadthSmaLength))
                    {
                        SetRebalanceSourceBlocker(RebalanceSourceKind.SimpleTab, tab);
                        SendDailyRebalanceLog(
                            "Rebalance postponed. Insufficient adjusted history for " + stock.Name,
                            LogMessageType.System);
                        return null;
                    }

                    KorovinSignalPoint lastSignal = stock.SignalPoints[stock.SignalPoints.Count - 1];
                    stock.IsExDividendDate = lastSignal.DividendAmount > 0m;
                    stock.AdjustedReturn = lastSignal.AdjustedReturn;

                    if (stock.IsExDividendDate)
                    {
                        LogDividendEvent(stock);
                    }
                }

                snapshot.Stocks.Add(stock);
            }

            foreach (KeyValuePair<string, KorovinStockUserSetting> pair in settings)
            {
                if (pair.Value.Enabled && FindStock(snapshot.Stocks, pair.Key) == null)
                {
                    SetRebalanceSourceBlocker(RebalanceSourceKind.StockUniverse, null);
                    SendDailyRebalanceLog(
                        "Rebalance postponed. Enabled stock has no screener source " + pair.Key,
                        LogMessageType.Error);
                    return null;
                }
            }

            KorovinPortfolioValuationResult valuation = GetPortfolioValuation();

            if (valuation.IsValid == false)
            {
                SendDailyRebalanceLog(
                    "Rebalance postponed. Portfolio NAV is zero",
                    LogMessageType.System);
                return null;
            }

            decimal netAssetValue = valuation.NetAssetValue;
            snapshot.EngineBalance = valuation.EngineBalance;
            snapshot.OpenPositionsProfit = valuation.OpenPositionsProfit;
            snapshot.NetAssetValue = netAssetValue;
            snapshot.FreeCash = valuation.FreeCash;
            decimal previousNetAssetValue = snapshot.FreeCash
                + snapshot.MoneyMarket.Volume * snapshot.MoneyMarket.PreviousPrice * snapshot.MoneyMarket.Lot
                + snapshot.Gold.Volume * snapshot.Gold.PreviousPrice * snapshot.Gold.Lot;

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];
                previousNetAssetValue += stock.Volume * stock.PreviousPrice * stock.Lot;
            }

            if (previousNetAssetValue <= 0m)
            {
                previousNetAssetValue = netAssetValue;
            }

            snapshot.MoneyMarket.CurrentWeight = snapshot.MoneyMarket.MarketValue / netAssetValue;
            snapshot.Gold.CurrentWeight = snapshot.Gold.MarketValue / netAssetValue;

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];
                stock.CurrentWeight = stock.MarketValue / netAssetValue;
                stock.PreviousWeight = stock.Volume * stock.PreviousPrice * stock.Lot / previousNetAssetValue;
            }

            return snapshot;
        }

        private RuntimeAsset BuildRuntimeAsset(BotTabSimple tab, DateTime serverTime)
        {
            if (tab == null || tab.Security == null || tab.CandlesFinishedOnly == null)
            {
                return null;
            }

            if (tab.TimeServerCurrent == DateTime.MinValue
                || tab.TimeServerCurrent.Date != serverTime.Date)
            {
                return null;
            }

            decimal price = GetMarketPrice(tab);

            if (price <= 0m)
            {
                return null;
            }

            DailyPriceCache dailyPriceCache;

            if (_dailyPriceCaches.TryGetValue(tab, out dailyPriceCache) == false)
            {
                dailyPriceCache = new DailyPriceCache();
                _dailyPriceCaches[tab] = dailyPriceCache;
            }

            UpdateDailyPrices(dailyPriceCache, tab.CandlesFinishedOnly, serverTime.Date, price);

            if (dailyPriceCache.Prices.Count < 2
                || dailyPriceCache.Prices[dailyPriceCache.Prices.Count - 1].Date.Date != serverTime.Date)
            {
                return null;
            }

            RuntimeAsset result = new RuntimeAsset();
            result.Tab = tab;
            result.Name = tab.Security.Name;
            result.Price = price;
            result.PreviousPrice = dailyPriceCache.Prices[dailyPriceCache.Prices.Count - 2].Price;
            result.Volume = GetOpenLongVolume(tab);
            result.Lot = tab.Security.Lot > 0m ? tab.Security.Lot : 1m;
            result.MarketValue = result.Volume * result.Price * result.Lot;
            result.PriceValues = dailyPriceCache.Values;
            result.DailyPriceCache = dailyPriceCache;
            return result;
        }

        private void UpdateDailyPrices(
            DailyPriceCache cache,
            List<Candle> candles,
            DateTime currentDate,
            decimal currentPrice)
        {
            currentDate = currentDate.Date;
            cache.ChangedFromIndex = cache.Prices.Count;
            cache.DatesChangedFromIndex = cache.Prices.Count;

            if (DailyPriceCacheNeedsReset(cache, candles, currentDate))
            {
                ResetDailyPriceCache(cache);
            }

            RestorePreviousSyntheticPrice(cache, currentDate);

            int startIndex = cache.ProcessedCandleCount;

            if (startIndex > 0 && candles != null && startIndex <= candles.Count)
            {
                Candle previousCandle = candles[startIndex - 1];

                if (previousCandle == null
                    || previousCandle.TimeStart != cache.LastProcessedCandleTime
                    || previousCandle.Close != cache.LastProcessedCandleClose)
                {
                    startIndex--;
                }
            }

            int processedCount = startIndex;

            for (int index = startIndex; candles != null && index < candles.Count; index++)
            {
                Candle candle = candles[index];

                if (candle != null && candle.TimeStart.Date > currentDate)
                {
                    break;
                }

                processedCount = index + 1;

                if (candle == null || candle.Close <= 0m)
                {
                    continue;
                }

                if (cache.SyntheticDate == candle.TimeStart.Date)
                {
                    cache.SyntheticHadOriginalPrice = true;
                    cache.SyntheticOriginalPrice = candle.Close;
                }

                UpsertDailyPrice(cache, candle.TimeStart.Date, candle.Close);
            }

            cache.ProcessedCandleCount = processedCount;
            UpdateProcessedCandleSignature(cache, candles);

            if (currentPrice > 0m)
            {
                if (cache.SyntheticDate != currentDate)
                {
                    int priceIndex = FindDailyPriceIndex(cache.Prices, currentDate);
                    cache.SyntheticDate = currentDate;
                    cache.SyntheticHadOriginalPrice = priceIndex >= 0;
                    cache.SyntheticOriginalPrice = priceIndex >= 0 ? cache.Prices[priceIndex].Price : 0m;
                }

                UpsertDailyPrice(cache, currentDate, currentPrice);
            }

            cache.CandleSource = candles;
            cache.LastCurrentDate = currentDate;
        }

        private static bool DailyPriceCacheNeedsReset(
            DailyPriceCache cache,
            List<Candle> candles,
            DateTime currentDate)
        {
            if (cache.LastCurrentDate != DateTime.MinValue && currentDate < cache.LastCurrentDate)
            {
                return true;
            }

            if (cache.CandleSource != null && ReferenceEquals(cache.CandleSource, candles) == false)
            {
                return true;
            }

            int candleCount = candles == null ? 0 : candles.Count;

            if (candleCount < cache.ProcessedCandleCount)
            {
                return true;
            }

            if (cache.ProcessedCandleCount > 0 && candles != null)
            {
                Candle firstCandle = candles[0];
                DateTime firstCandleTime = firstCandle == null ? DateTime.MinValue : firstCandle.TimeStart;
                decimal firstCandleClose = firstCandle == null ? 0m : firstCandle.Close;

                if (firstCandleTime != cache.FirstProcessedCandleTime
                    || firstCandleClose != cache.FirstProcessedCandleClose)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ResetDailyPriceCache(DailyPriceCache cache)
        {
            cache.Prices.Clear();
            cache.Values.Clear();
            cache.CandleSource = null;
            cache.ProcessedCandleCount = 0;
            cache.FirstProcessedCandleTime = DateTime.MinValue;
            cache.FirstProcessedCandleClose = 0m;
            cache.LastProcessedCandleTime = DateTime.MinValue;
            cache.LastProcessedCandleClose = 0m;
            cache.LastCurrentDate = DateTime.MinValue;
            cache.SyntheticDate = DateTime.MinValue;
            cache.SyntheticOriginalPrice = 0m;
            cache.SyntheticHadOriginalPrice = false;
            cache.ChangedFromIndex = 0;
            cache.DatesChangedFromIndex = 0;
        }

        private static void RestorePreviousSyntheticPrice(DailyPriceCache cache, DateTime currentDate)
        {
            if (cache.SyntheticDate == DateTime.MinValue || cache.SyntheticDate == currentDate)
            {
                return;
            }

            int priceIndex = FindDailyPriceIndex(cache.Prices, cache.SyntheticDate);

            if (cache.SyntheticHadOriginalPrice)
            {
                UpsertDailyPrice(cache, cache.SyntheticDate, cache.SyntheticOriginalPrice);
            }
            else if (priceIndex >= 0)
            {
                cache.Prices.RemoveAt(priceIndex);
                cache.Values.RemoveAt(priceIndex);
                cache.ChangedFromIndex = Math.Min(cache.ChangedFromIndex, priceIndex);
                cache.DatesChangedFromIndex = Math.Min(cache.DatesChangedFromIndex, priceIndex);
            }

            cache.SyntheticDate = DateTime.MinValue;
            cache.SyntheticOriginalPrice = 0m;
            cache.SyntheticHadOriginalPrice = false;
        }

        private static void UpdateProcessedCandleSignature(DailyPriceCache cache, List<Candle> candles)
        {
            if (cache.ProcessedCandleCount <= 0 || candles == null)
            {
                cache.FirstProcessedCandleTime = DateTime.MinValue;
                cache.FirstProcessedCandleClose = 0m;
                cache.LastProcessedCandleTime = DateTime.MinValue;
                cache.LastProcessedCandleClose = 0m;
                return;
            }

            Candle firstCandle = candles[0];
            Candle lastCandle = candles[cache.ProcessedCandleCount - 1];
            cache.FirstProcessedCandleTime = firstCandle == null ? DateTime.MinValue : firstCandle.TimeStart;
            cache.FirstProcessedCandleClose = firstCandle == null ? 0m : firstCandle.Close;
            cache.LastProcessedCandleTime = lastCandle == null ? DateTime.MinValue : lastCandle.TimeStart;
            cache.LastProcessedCandleClose = lastCandle == null ? 0m : lastCandle.Close;
        }

        private static void UpsertDailyPrice(DailyPriceCache cache, DateTime date, decimal price)
        {
            date = date.Date;
            int priceIndex = FindDailyPriceIndex(cache.Prices, date);

            if (priceIndex >= 0)
            {
                if (cache.Prices[priceIndex].Price != price)
                {
                    cache.Prices[priceIndex].Price = price;
                    cache.Values[priceIndex] = price;
                    cache.ChangedFromIndex = Math.Min(cache.ChangedFromIndex, priceIndex);
                }

                return;
            }

            int insertIndex = ~priceIndex;
            KorovinDailyPrice dailyPrice = new KorovinDailyPrice();
            dailyPrice.Date = date;
            dailyPrice.Price = price;
            cache.Prices.Insert(insertIndex, dailyPrice);
            cache.Values.Insert(insertIndex, price);
            cache.ChangedFromIndex = Math.Min(cache.ChangedFromIndex, insertIndex);
            cache.DatesChangedFromIndex = Math.Min(cache.DatesChangedFromIndex, insertIndex);
        }

        private static int FindDailyPriceIndex(List<KorovinDailyPrice> prices, DateTime date)
        {
            int left = 0;
            int right = prices.Count - 1;

            while (left <= right)
            {
                int middle = left + (right - left) / 2;
                int comparison = prices[middle].Date.Date.CompareTo(date.Date);

                if (comparison == 0)
                {
                    return middle;
                }

                if (comparison < 0)
                {
                    left = middle + 1;
                }
                else
                {
                    right = middle - 1;
                }
            }

            return ~left;
        }

        private StockSignalCache UpdateStockSignals(
            BotTabSimple tab,
            string ticker,
            DailyPriceCache priceCache)
        {
            StockSignalCache signalCache;

            if (_stockSignalCaches.TryGetValue(tab, out signalCache) == false)
            {
                signalCache = new StockSignalCache();
                _stockSignalCaches[tab] = signalCache;
            }

            DividendHistoryCache dividendCache = GetDividendHistoryCache(ticker, priceCache.LastCurrentDate);
            bool resetSignals = signalCache.PriceCache != priceCache
                || signalCache.Points.Count > priceCache.Prices.Count
                || signalCache.MappedPriceCount > priceCache.Prices.Count
                || signalCache.DividendHistoryVersion != dividendCache.Version;

            if (resetSignals == false
                && priceCache.DatesChangedFromIndex < signalCache.MappedPriceCount)
            {
                resetSignals = true;
            }

            if (resetSignals)
            {
                ResetStockSignalCache(signalCache, priceCache, dividendCache.Version);
            }

            MapDividendExDates(signalCache, dividendCache, priceCache.Prices);

            int startIndex = resetSignals ? 0 : Math.Min(priceCache.ChangedFromIndex, signalCache.Points.Count);

            if (signalCache.Points.Count < priceCache.Prices.Count)
            {
                startIndex = Math.Min(startIndex, signalCache.Points.Count);
            }

            if (startIndex < signalCache.Points.Count)
            {
                signalCache.Points.RemoveRange(startIndex, signalCache.Points.Count - startIndex);
                signalCache.Values.RemoveRange(startIndex, signalCache.Values.Count - startIndex);
            }

            for (int index = startIndex; index < priceCache.Prices.Count; index++)
            {
                decimal dividendAmount = 0m;
                signalCache.DividendsByDate.TryGetValue(
                    priceCache.Prices[index].Date.Date,
                    out dividendAmount);
                KorovinSignalPoint point = index == 0
                    ? SDKRebalancerByKorovinCalculator.BuildFirstDividendAdjustedSignalPoint(
                        priceCache.Prices[index],
                        dividendAmount)
                    : SDKRebalancerByKorovinCalculator.BuildDividendAdjustedSignalPoint(
                        priceCache.Prices[index - 1],
                        signalCache.Points[index - 1],
                        priceCache.Prices[index],
                        dividendAmount);
                signalCache.Points.Add(point);
                signalCache.Values.Add(point.SignalValue);
            }

            return signalCache;
        }

        private static void ResetStockSignalCache(
            StockSignalCache signalCache,
            DailyPriceCache priceCache,
            int dividendHistoryVersion)
        {
            signalCache.PriceCache = priceCache;
            signalCache.DividendHistoryVersion = dividendHistoryVersion;
            signalCache.Points.Clear();
            signalCache.Values.Clear();
            signalCache.DividendsByDate.Clear();
            signalCache.MappedPriceCount = 0;
            signalCache.NextDividendIndex = 0;
        }

        private static void MapDividendExDates(
            StockSignalCache signalCache,
            DividendHistoryCache dividendCache,
            List<KorovinDailyPrice> prices)
        {
            for (int priceIndex = signalCache.MappedPriceCount; priceIndex < prices.Count; priceIndex++)
            {
                DateTime priceDate = prices[priceIndex].Date.Date;
                decimal dividendAmount = 0m;

                while (signalCache.NextDividendIndex < dividendCache.Records.Count
                    && dividendCache.Records[signalCache.NextDividendIndex].RegistryDate < priceDate)
                {
                    dividendAmount += dividendCache.Records[signalCache.NextDividendIndex].Amount;
                    signalCache.NextDividendIndex++;
                }

                if (dividendAmount > 0m)
                {
                    signalCache.DividendsByDate[priceDate] = dividendAmount;
                }

                signalCache.MappedPriceCount++;
            }
        }

        private DividendHistoryCache GetDividendHistoryCache(string ticker, DateTime currentDate)
        {
            DividendHistoryCache cache;
            bool testerMode = StartProgram == StartProgram.IsTester
                || StartProgram == StartProgram.IsOsOptimizer;

            if (_dividendHistoryCaches.TryGetValue(ticker, out cache)
                && (testerMode || cache.LoadedDate == currentDate.Date))
            {
                return cache;
            }

            if (cache == null)
            {
                cache = new DividendHistoryCache();
            }

            DateTime historyDate = testerMode ? new DateTime(9999, 12, 31) : currentDate.Date;
            WikiDividendHistory history = WikiMaster.GetDividendsHistory(ticker, historyDate);
            List<CachedDividendRecord> loadedRecords = new List<CachedDividendRecord>();

            for (int index = 0; history != null && history.historical != null && index < history.historical.Count; index++)
            {
                WikiDividendRecord record = history.historical[index];
                DateTime registryDate;

                if (record == null || record.dividend_amount <= 0m
                    || DateTime.TryParseExact(
                        record.registry_close_date,
                        "dd.MM.yyyy",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out registryDate) == false)
                {
                    continue;
                }

                CachedDividendRecord cachedRecord = new CachedDividendRecord();
                cachedRecord.RegistryDate = registryDate.Date;
                cachedRecord.Amount = record.dividend_amount;
                loadedRecords.Add(cachedRecord);
            }

            loadedRecords.Sort(CompareCachedDividends);

            if (DividendRecordsAreEqual(cache.Records, loadedRecords) == false)
            {
                cache.Records.Clear();
                cache.Records.AddRange(loadedRecords);
                cache.Version++;
            }

            cache.LoadedDate = currentDate.Date;
            _dividendHistoryCaches[ticker] = cache;
            return cache;
        }

        private static bool DividendRecordsAreEqual(
            List<CachedDividendRecord> left,
            List<CachedDividendRecord> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (int index = 0; index < left.Count; index++)
            {
                if (left[index].RegistryDate != right[index].RegistryDate
                    || left[index].Amount != right[index].Amount)
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareCachedDividends(CachedDividendRecord left, CachedDividendRecord right)
        {
            return left.RegistryDate.CompareTo(right.RegistryDate);
        }

        #endregion

        #region Order execution

        private void PrepareMoneyMarketFundingPlan(
            KorovinRebalancePlanResult plan,
            RuntimeSnapshot snapshot)
        {
            if (StoreFreeCashInMoneyMarket() == false
                || (_positionExecutionMode.ValueString == "Reopen"
                    && (StartProgram == StartProgram.IsTester
                        || StartProgram == StartProgram.IsOsOptimizer)))
            {
                return;
            }

            Dictionary<string, BotTabSimple> tabs = BuildExecutionTabMap(snapshot);
            decimal executableBuyMoney = 0m;
            decimal otherSellMoney = 0m;
            decimal plannedMoneyMarketSale = 0m;
            KorovinRebalanceOrder moneyMarketSell = null;

            for (int index = plan.Orders.Count - 1; index >= 0; index--)
            {
                KorovinRebalanceOrder order = plan.Orders[index];
                BotTabSimple tab;

                if (tabs.TryGetValue(order.Name, out tab) == false)
                {
                    plan.Orders.RemoveAt(index);
                    continue;
                }

                decimal price = order.Side == KorovinRebalanceOrderSide.Sell
                    ? GetSellPrice(tab)
                    : GetBuyPrice(tab);
                decimal currentVolume = order.Side == KorovinRebalanceOrderSide.Sell
                    ? GetOpenLongVolume(tab)
                    : 0m;
                KorovinOrderSizeResult size = CalculateOrderSize(
                    tab,
                    order.PlannedMoney,
                    price,
                    currentVolume);

                if (order.Side == KorovinRebalanceOrderSide.Buy && tab != _tabMoneyMarket)
                {
                    if (size.Side != KorovinRebalanceOrderSide.Buy
                        || size.Volume <= 0m
                        || tab.CanTradeThisVolume(size.Volume) == false)
                    {
                        plan.Orders.RemoveAt(index);
                        continue;
                    }

                    order.PlannedMoney = size.Money;
                    executableBuyMoney += size.Money;
                    continue;
                }

                if (order.Side != KorovinRebalanceOrderSide.Sell
                    || size.Side != KorovinRebalanceOrderSide.Sell
                    || size.Volume <= 0m
                    || tab.CanTradeThisVolume(size.Volume) == false)
                {
                    continue;
                }

                if (tab == _tabMoneyMarket)
                {
                    moneyMarketSell = order;
                    plannedMoneyMarketSale = Math.Abs(order.RequestedMoney);
                }
                else
                {
                    otherSellMoney += size.Money;
                }
            }

            if (moneyMarketSell == null)
            {
                return;
            }

            decimal requiredSale = SDKRebalancerByKorovinCalculator.CalculateRequiredMoneyMarketSale(
                plannedMoneyMarketSale,
                executableBuyMoney,
                snapshot.FreeCash,
                otherSellMoney);

            if (requiredSale <= 0m)
            {
                plan.Orders.Remove(moneyMarketSell);
                return;
            }

            moneyMarketSell.PlannedMoney = -requiredSale;
            moneyMarketSell.Reason = "Money market funding for executable buys";
        }

        private void ExecutePlan(
            KorovinRebalancePlanResult plan,
            RuntimeSnapshot snapshot,
            DateTime rebalanceDate)
        {
            Dictionary<string, BotTabSimple> tabs = BuildExecutionTabMap(snapshot);

            if (UseReopenPositionExecution())
            {
                ExecutePlanByReopening(plan, tabs);
                return;
            }

            bool sellWasSubmitted = false;

            lock (_stateLocker)
            {
                _pendingBuyOrders.Clear();
                _pendingReopenOrders.Clear();
                _pendingMoneyMarketFundingSell = null;
                ResetMoneyMarketFundingSubmission();
                _pendingBuyRetryRequested = false;
                _reopenClosuresSubmitted = false;
            }

            for (int index = 0; index < plan.Orders.Count; index++)
            {
                KorovinRebalanceOrder order = plan.Orders[index];
                BotTabSimple tab;

                if (tabs.TryGetValue(order.Name, out tab) == false)
                {
                    continue;
                }

                if (order.Side == KorovinRebalanceOrderSide.Sell)
                {
                    if (StoreFreeCashInMoneyMarket() && tab == _tabMoneyMarket)
                    {
                        lock (_stateLocker)
                        {
                            _pendingMoneyMarketFundingSell = order;
                        }

                        continue;
                    }

                    if (ExecuteSell(tab, order))
                    {
                        sellWasSubmitted = true;
                    }
                }
                else if (order.Side == KorovinRebalanceOrderSide.Buy && order.PlannedMoney > 0m)
                {
                    PendingBuyOrder pending = new PendingBuyOrder();
                    pending.Name = order.Name;
                    pending.Tab = tab;
                    pending.RemainingMoney = order.PlannedMoney;
                    pending.IsCashSweep = StoreFreeCashInMoneyMarket() && tab == _tabMoneyMarket;
                    pending.RebalanceDate = rebalanceDate;

                    lock (_stateLocker)
                    {
                        _pendingBuyOrders.Add(pending);
                    }
                }
            }

            EnsureMoneyMarketCashSweepPending();

            if (sellWasSubmitted == false || StartProgram != StartProgram.IsOsTrader)
            {
                TryExecutePendingBuys();
            }
            else
            {
                SendNewLogMessage("Sell orders submitted. Buys wait for actual cash update", LogMessageType.System);
            }
        }

        private bool UseReopenPositionExecution()
        {
            if (_positionExecutionMode.ValueString != "Reopen")
            {
                return false;
            }

            if (StartProgram == StartProgram.IsTester || StartProgram == StartProgram.IsOsOptimizer)
            {
                return true;
            }

            bool sendWarning = false;

            lock (_stateLocker)
            {
                if (_liveReopenModeWarningLogged == false)
                {
                    _liveReopenModeWarningLogged = true;
                    sendWarning = true;
                }
            }

            if (sendWarning)
            {
                SendNewLogMessage(
                    "Position execution mode Reopen is tester-only. Modify mode is used in live trading",
                    LogMessageType.System);
            }

            return false;
        }

        private void ExecutePlanByReopening(
            KorovinRebalancePlanResult plan,
            Dictionary<string, BotTabSimple> tabs)
        {
            List<PendingReopenOrder> pendingOrders = new List<PendingReopenOrder>();

            for (int index = 0; index < plan.Orders.Count; index++)
            {
                KorovinRebalanceOrder order = plan.Orders[index];
                BotTabSimple tab;

                if (tabs.TryGetValue(order.Name, out tab) == false)
                {
                    continue;
                }

                decimal currentVolume = GetOpenLongVolume(tab);
                decimal price = order.Side == KorovinRebalanceOrderSide.Sell
                    ? GetSellPrice(tab)
                    : GetBuyPrice(tab);
                KorovinOrderSizeResult size = CalculateOrderSize(
                    tab,
                    order.PlannedMoney,
                    price,
                    currentVolume);

                if (size.Side != order.Side || size.Volume <= 0m)
                {
                    SendNewLogMessage(
                        order.Side + " skipped after lot rounding for " + order.Name,
                        LogMessageType.System);
                    continue;
                }

                decimal targetVolume = SDKRebalancerByKorovinCalculator.CalculateReopenTargetVolume(
                    currentVolume,
                    size.Side,
                    size.Volume);

                if (targetVolume > 0m && tab.CanTradeThisVolume(targetVolume) == false)
                {
                    SendNewLogMessage(
                        "Reopen skipped. Target volume is not tradable for " + order.Name
                        + " targetVolume " + targetVolume,
                        LogMessageType.System);
                    continue;
                }

                PendingReopenOrder pending = new PendingReopenOrder();
                pending.Name = order.Name;
                pending.Tab = tab;
                pending.CurrentVolume = currentVolume;
                pending.TargetVolume = targetVolume;
                pendingOrders.Add(pending);

                SendNewLogMessage(
                    "REOPEN PLAN " + order.Name
                    + " currentVolume " + currentVolume
                    + " changeVolume " + size.Volume
                    + " side " + size.Side
                    + " targetVolume " + targetVolume,
                    LogMessageType.System);
            }

            ApplyReopenMoneyMarketCashSweep(pendingOrders);

            lock (_stateLocker)
            {
                _pendingBuyOrders.Clear();
                _pendingReopenOrders.Clear();
                _pendingReopenOrders.AddRange(pendingOrders);
                _reopenClosuresSubmitted = true;
            }

            TryExecutePendingReopens();
        }

        private void ApplyReopenMoneyMarketCashSweep(List<PendingReopenOrder> pendingOrders)
        {
            if (StoreFreeCashInMoneyMarket() == false)
            {
                return;
            }

            KorovinPortfolioValuationResult valuation = GetPortfolioValuation();

            if (valuation.IsValid == false)
            {
                return;
            }

            decimal estimatedFreeCash = valuation.FreeCash;
            PendingReopenOrder moneyMarketOrder = null;

            for (int index = 0; index < pendingOrders.Count; index++)
            {
                PendingReopenOrder pending = pendingOrders[index];
                decimal lot = GetLot(pending.Tab);
                estimatedFreeCash += pending.CurrentVolume * GetSellPrice(pending.Tab) * lot;
                estimatedFreeCash -= pending.TargetVolume * GetBuyPrice(pending.Tab) * lot;

                if (pending.Tab == _tabMoneyMarket)
                {
                    moneyMarketOrder = pending;
                }
            }

            bool addMoneyMarketOrder = false;

            if (moneyMarketOrder == null)
            {
                decimal currentVolume = GetOpenLongVolume(_tabMoneyMarket);
                decimal lot = GetLot(_tabMoneyMarket);
                estimatedFreeCash += currentVolume * GetSellPrice(_tabMoneyMarket) * lot;
                estimatedFreeCash -= currentVolume * GetBuyPrice(_tabMoneyMarket) * lot;

                moneyMarketOrder = new PendingReopenOrder();
                moneyMarketOrder.Name = _tabMoneyMarket.Security == null
                    ? "Money market"
                    : _tabMoneyMarket.Security.Name;
                moneyMarketOrder.Tab = _tabMoneyMarket;
                moneyMarketOrder.CurrentVolume = currentVolume;
                moneyMarketOrder.TargetVolume = currentVolume;
                addMoneyMarketOrder = true;
            }

            decimal price = GetBuyPrice(_tabMoneyMarket);
            KorovinOrderSizeResult sweepSize = CalculateOrderSize(
                _tabMoneyMarket,
                estimatedFreeCash,
                price,
                0m);

            if (sweepSize.Side != KorovinRebalanceOrderSide.Buy || sweepSize.Volume <= 0m)
            {
                return;
            }

            decimal targetVolume = moneyMarketOrder.TargetVolume + sweepSize.Volume;

            if (_tabMoneyMarket.CanTradeThisVolume(targetVolume) == false)
            {
                return;
            }

            moneyMarketOrder.TargetVolume = targetVolume;

            if (addMoneyMarketOrder)
            {
                pendingOrders.Add(moneyMarketOrder);
            }

            SendNewLogMessage(
                "REOPEN CASH SWEEP " + moneyMarketOrder.Name
                + " additionalVolume " + sweepSize.Volume
                + " targetVolume " + moneyMarketOrder.TargetVolume,
                LogMessageType.System);
        }

        private void SubmitReopenClosures(PendingReopenOrder pending)
        {
            List<Position> positions = pending.Tab.PositionsOpenAll;

            for (int index = 0; positions != null && index < positions.Count; index++)
            {
                Position position = positions[index];

                if (position == null || position.Direction != Side.Buy
                    || position.State != PositionStateType.Open || position.OpenVolume <= 0m
                    || position.OpenActive || position.CloseActive)
                {
                    continue;
                }

                decimal closeVolume = position.OpenVolume;

                if (TrySubmitClosePosition(pending.Tab, position, closeVolume))
                {
                    SendNewLogMessage(
                        "REOPEN CLOSE " + pending.Name
                        + " position " + position.Number
                        + " volume " + closeVolume,
                        LogMessageType.Trade);
                }
                else
                {
                    SendDailyRebalanceLog(
                        "REOPEN CLOSE WAIT " + pending.Name
                        + " position " + position.Number
                        + " close order was not accepted",
                        LogMessageType.System);
                }
            }
        }

        private void TryExecutePendingReopens()
        {
            if (_isDeleted || _regime.ValueString == "Off")
            {
                lock (_stateLocker)
                {
                    _pendingReopenOrders.Clear();
                    _reopenClosuresSubmitted = false;
                }

                return;
            }

            lock (_stateLocker)
            {
                if (_pendingReopenExecutionInProgress
                    || _reopenClosuresSubmitted == false
                    || _pendingReopenOrders.Count == 0)
                {
                    return;
                }

                _pendingReopenExecutionInProgress = true;
            }

            try
            {
                List<PendingReopenOrder> orders;

                lock (_stateLocker)
                {
                    orders = new List<PendingReopenOrder>(_pendingReopenOrders);
                }

                for (int index = 0; index < orders.Count; index++)
                {
                    SubmitReopenClosures(orders[index]);
                }

                if (AllPendingReopenPositionsAreClosed(orders) == false)
                {
                    return;
                }

                KorovinPortfolioValuationResult valuation = GetPortfolioValuation();

                if (valuation.IsValid == false)
                {
                    return;
                }

                decimal reserveMoney = StoreFreeCashInMoneyMarket()
                    ? 0m
                    : _hardCashReserveWeight.ValueDecimal * valuation.NetAssetValue;
                decimal availableCash = Math.Max(0m, valuation.FreeCash - reserveMoney);
                decimal requestedMoney = 0m;

                for (int index = 0; index < orders.Count; index++)
                {
                    if (orders[index].TargetVolume <= 0m)
                    {
                        continue;
                    }

                    decimal price = GetBuyPrice(orders[index].Tab);

                    if (price <= 0m)
                    {
                        return;
                    }

                    requestedMoney += orders[index].TargetVolume * price * GetLot(orders[index].Tab);
                }

                decimal scale = requestedMoney > availableCash && requestedMoney > 0m
                    ? availableCash / requestedMoney
                    : 1m;

                for (int index = 0; index < orders.Count; index++)
                {
                    PendingReopenOrder pending = orders[index];

                    if (pending.TargetVolume <= 0m)
                    {
                        RemovePendingReopen(pending);
                        continue;
                    }

                    if (GetPrimaryLongPosition(pending.Tab) != null)
                    {
                        return;
                    }

                    decimal price = GetBuyPrice(pending.Tab);
                    decimal targetMoney = pending.TargetVolume * price * GetLot(pending.Tab) * scale;
                    KorovinOrderSizeResult size = CalculateOrderSize(
                        pending.Tab,
                        targetMoney,
                        price,
                        0m);

                    if (size.Side != KorovinRebalanceOrderSide.Buy
                        || size.Volume <= 0m
                        || pending.Tab.CanTradeThisVolume(size.Volume) == false)
                    {
                        SendNewLogMessage(
                            "REOPEN BUY skipped after lot rounding for " + pending.Name,
                            LogMessageType.System);
                        RemovePendingReopen(pending);
                        continue;
                    }

                    pending.Tab.BuyAtMarket(size.Volume);
                    RemovePendingReopen(pending);
                    SendNewLogMessage(
                        "REOPEN BUY " + pending.Name
                        + " targetVolume " + pending.TargetVolume
                        + " actualVolume " + size.Volume
                        + " actual " + size.Money.ToString("F2"),
                        LogMessageType.Trade);
                }
            }
            finally
            {
                lock (_stateLocker)
                {
                    _pendingReopenExecutionInProgress = false;

                    if (_pendingReopenOrders.Count == 0)
                    {
                        _reopenClosuresSubmitted = false;
                    }
                }
            }
        }

        private static bool AllPendingReopenPositionsAreClosed(List<PendingReopenOrder> orders)
        {
            for (int orderIndex = 0; orderIndex < orders.Count; orderIndex++)
            {
                List<Position> positions = orders[orderIndex].Tab.PositionsOpenAll;

                for (int positionIndex = 0;
                    positions != null && positionIndex < positions.Count;
                    positionIndex++)
                {
                    Position position = positions[positionIndex];

                    if (position != null && position.Direction == Side.Buy
                        && position.State != PositionStateType.Done
                        && position.State != PositionStateType.Deleted
                        && (position.OpenVolume > 0m || position.OpenActive || position.CloseActive))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private bool HasPendingReopen(BotTabSimple tab)
        {
            lock (_stateLocker)
            {
                for (int index = 0; index < _pendingReopenOrders.Count; index++)
                {
                    if (_pendingReopenOrders[index].Tab == tab)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void RemovePendingReopen(PendingReopenOrder pending)
        {
            lock (_stateLocker)
            {
                _pendingReopenOrders.Remove(pending);
            }
        }

        private bool ExecuteSell(BotTabSimple tab, KorovinRebalanceOrder order)
        {
            decimal currentVolume = GetOpenLongVolume(tab);
            decimal price = GetSellPrice(tab);
            KorovinOrderSizeResult size = CalculateOrderSize(tab, order.PlannedMoney, price, currentVolume);

            if (size.Side != KorovinRebalanceOrderSide.Sell || size.Volume <= 0m
                || tab.CanTradeThisVolume(size.Volume) == false)
            {
                SendNewLogMessage("Sell skipped after lot rounding for " + order.Name, LogMessageType.System);
                return false;
            }

            decimal remainingVolume = size.Volume;
            List<Position> positions = tab.PositionsOpenAll;

            for (int index = 0; index < positions.Count && remainingVolume > 0m; index++)
            {
                Position position = positions[index];

                if (position == null || position.Direction != Side.Buy
                    || position.State != PositionStateType.Open || position.OpenVolume <= 0m
                    || position.OpenActive || position.CloseActive)
                {
                    continue;
                }

                decimal closeVolume = Math.Min(remainingVolume, position.OpenVolume);

                if (TrySubmitClosePosition(tab, position, closeVolume))
                {
                    remainingVolume -= closeVolume;
                }
            }

            decimal submittedVolume = size.Volume - remainingVolume;

            if (submittedVolume > 0m)
            {
                SendNewLogMessage(
                    "SELL " + order.Name + " requested " + order.RequestedMoney.ToString("F2")
                    + " actual " + (submittedVolume * price * GetLot(tab)).ToString("F2")
                    + " volume " + submittedVolume,
                    LogMessageType.Trade);
                return true;
            }

            return false;
        }

        private static bool TrySubmitClosePosition(
            BotTabSimple tab,
            Position position,
            decimal closeVolume)
        {
            if (tab == null || position == null || closeVolume <= 0m
                || position.Direction != Side.Buy
                || position.State != PositionStateType.Open
                || position.OpenVolume <= 0m
                || position.OpenActive || position.CloseActive)
            {
                return false;
            }

            decimal volumeBefore = position.OpenVolume;
            int closeOrderCountBefore = position.CloseOrders == null ? 0 : position.CloseOrders.Count;
            tab.CloseAtMarket(position, Math.Min(closeVolume, volumeBefore));
            int closeOrderCountAfter = position.CloseOrders == null ? 0 : position.CloseOrders.Count;

            return position.OpenVolume < volumeBefore
                || closeOrderCountAfter > closeOrderCountBefore
                || position.CloseActive;
        }

        private void TryExecutePendingBuys()
        {
            if (_isDeleted || _regime.ValueString == "Off")
            {
                lock (_stateLocker)
                {
                    _pendingBuyOrders.Clear();
                    _pendingBuyRetryRequested = false;
                }

                return;
            }

            RemoveExpiredPendingBuys();

            lock (_stateLocker)
            {
                if (_pendingBuyExecutionInProgress)
                {
                    _pendingBuyRetryRequested = true;
                    return;
                }

                if (_pendingBuyOrders.Count == 0)
                {
                    return;
                }

                _pendingBuyExecutionInProgress = true;
            }

            try
            {
                RestoreCompletedPartialClosures();

                KorovinPortfolioValuationResult valuation = GetPortfolioValuation();
                decimal netAssetValue = valuation.NetAssetValue;

                if (valuation.IsValid == false)
                {
                    return;
                }

                List<PendingBuyOrder> orders;

                lock (_stateLocker)
                {
                    orders = new List<PendingBuyOrder>(_pendingBuyOrders);
                }

                if (StoreFreeCashInMoneyMarket())
                {
                    TryExecutePendingBuysWithCashSweep(valuation, orders);
                    return;
                }

                decimal freeCash = valuation.FreeCash;
                decimal availableCash = Math.Max(0m, freeCash - _hardCashReserveWeight.ValueDecimal * netAssetValue);

                if (availableCash <= 0m)
                {
                    return;
                }

                decimal requestedMoney = 0m;

                for (int index = 0; index < orders.Count; index++)
                {
                    requestedMoney += orders[index].RemainingMoney;
                }

                decimal scale = requestedMoney > availableCash ? availableCash / requestedMoney : 1m;

                for (int index = 0; index < orders.Count; index++)
                {
                    PendingBuyOrder pending = orders[index];
                    decimal moneyToSubmit = pending.RemainingMoney * scale;
                    TrySubmitPendingBuy(pending, moneyToSubmit);
                }
            }
            finally
            {
                bool retryRequested;

                lock (_stateLocker)
                {
                    _pendingBuyExecutionInProgress = false;
                    retryRequested = _pendingBuyRetryRequested;
                    _pendingBuyRetryRequested = false;
                }

                if (retryRequested)
                {
                    TryExecutePendingBuys();
                }
            }
        }

        private void EnsureMoneyMarketCashSweepPending()
        {
            if (StoreFreeCashInMoneyMarket() == false)
            {
                return;
            }

            lock (_stateLocker)
            {
                for (int index = 0; index < _pendingBuyOrders.Count; index++)
                {
                    if (_pendingBuyOrders[index].Tab == _tabMoneyMarket)
                    {
                        _pendingBuyOrders[index].IsCashSweep = true;
                        return;
                    }
                }

                PendingBuyOrder pending = new PendingBuyOrder();
                pending.Name = _tabMoneyMarket.Security == null
                    ? "Money market"
                    : _tabMoneyMarket.Security.Name;
                pending.Tab = _tabMoneyMarket;
                pending.IsCashSweep = true;
                _pendingBuyOrders.Add(pending);
            }
        }

        private void TryExecutePendingBuysWithCashSweep(
            KorovinPortfolioValuationResult valuation,
            List<PendingBuyOrder> orders)
        {
            decimal provisionalAvailableCash;

            if (TrySubmitMoneyMarketFundingSale(valuation, orders, out provisionalAvailableCash))
            {
                return;
            }

            valuation = GetPortfolioValuation();

            if (valuation.IsValid == false)
            {
                return;
            }

            decimal availableCash = Math.Max(
                Math.Max(0m, valuation.FreeCash),
                provisionalAvailableCash);
            decimal requestedMoney = 0m;

            for (int index = 0; index < orders.Count; index++)
            {
                if (orders[index].IsCashSweep == false)
                {
                    requestedMoney += orders[index].RemainingMoney;
                }
            }

            decimal scale = requestedMoney > availableCash && requestedMoney > 0m
                ? availableCash / requestedMoney
                : 1m;
            bool regularBuyWasSubmitted = false;

            for (int index = 0; index < orders.Count; index++)
            {
                PendingBuyOrder pending = orders[index];

                if (pending.IsCashSweep)
                {
                    continue;
                }

                decimal moneyToSubmit = pending.RemainingMoney * scale;
                regularBuyWasSubmitted |= TrySubmitPendingBuy(pending, moneyToSubmit);
            }

            if (regularBuyWasSubmitted)
            {
                return;
            }

            if (HasPendingRegularBuy())
            {
                return;
            }

            if (HasActiveExecutionOrders())
            {
                return;
            }

            PendingBuyOrder cashSweep = GetPendingCashSweep();

            if (cashSweep == null)
            {
                return;
            }

            KorovinPortfolioValuationResult currentValuation = GetPortfolioValuation();
            decimal cashToSweep = currentValuation.IsValid ? Math.Max(0m, currentValuation.FreeCash) : 0m;
            decimal sweepPrice = GetBuyPrice(cashSweep.Tab);
            KorovinOrderSizeResult sweepSize = CalculateOrderSize(
                cashSweep.Tab,
                cashToSweep,
                sweepPrice,
                0m);

            if (sweepSize.Side != KorovinRebalanceOrderSide.Buy
                || sweepSize.Volume <= 0m
                || cashSweep.Tab.CanTradeThisVolume(sweepSize.Volume) == false)
            {
                RemovePendingBuy(cashSweep);
                return;
            }

            Position moneyMarketPosition = GetPrimaryLongPosition(cashSweep.Tab);

            if (moneyMarketPosition == null)
            {
                cashSweep.Tab.BuyAtMarket(sweepSize.Volume);
            }
            else if (CanIncreaseLongPosition(moneyMarketPosition) == false)
            {
                SendDailyRebalanceLog(
                    "CASH SWEEP WAIT " + cashSweep.Name
                    + " position " + moneyMarketPosition.Number
                    + " state " + moneyMarketPosition.State + " has an active order",
                    LogMessageType.System);
                return;
            }
            else
            {
                cashSweep.Tab.BuyAtMarketToPosition(moneyMarketPosition, sweepSize.Volume);
            }

            RemovePendingBuy(cashSweep);
            SendNewLogMessage(
                "CASH SWEEP BUY " + cashSweep.Name
                + " actual " + sweepSize.Money.ToString("F2")
                + " volume " + sweepSize.Volume,
                LogMessageType.Trade);
        }

        private bool TrySubmitPendingBuy(PendingBuyOrder pending, decimal moneyToSubmit)
        {
            decimal price = GetBuyPrice(pending.Tab);
            KorovinOrderSizeResult remainingSize = CalculateOrderSize(
                pending.Tab,
                pending.RemainingMoney,
                price,
                0m);

            if (remainingSize.Side != KorovinRebalanceOrderSide.Buy
                || remainingSize.Volume <= 0m
                || pending.Tab.CanTradeThisVolume(remainingSize.Volume) == false)
            {
                RemovePendingBuy(pending);
                return false;
            }

            KorovinOrderSizeResult size = CalculateOrderSize(
                pending.Tab,
                moneyToSubmit,
                price,
                0m);

            if (size.Side != KorovinRebalanceOrderSide.Buy
                || size.Volume <= 0m
                || pending.Tab.CanTradeThisVolume(size.Volume) == false)
            {
                return false;
            }

            Position openPosition = GetPrimaryLongPosition(pending.Tab);
            bool buyWasSubmitted;

            if (openPosition == null)
            {
                buyWasSubmitted = pending.Tab.BuyAtMarket(size.Volume) != null;
            }
            else if (CanIncreaseLongPosition(openPosition) == false)
            {
                SendDailyRebalanceLog(
                    "BUY WAIT " + pending.Name + " position " + openPosition.Number
                    + " state " + openPosition.State + " has an active order",
                    LogMessageType.System);
                return false;
            }
            else
            {
                buyWasSubmitted = TrySubmitBuyToPosition(pending.Tab, openPosition, size.Volume);
            }

            if (buyWasSubmitted == false)
            {
                SendDailyRebalanceLog(
                    "BUY SUBMIT WAIT " + pending.Name + " order was not accepted",
                    LogMessageType.System);
                return false;
            }

            RemovePendingBuy(pending);
            SendNewLogMessage(
                "BUY " + pending.Name + " actual " + size.Money.ToString("F2")
                + " volume " + size.Volume,
                LogMessageType.Trade);
            return true;
        }

        private bool TrySubmitMoneyMarketFundingSale(
            KorovinPortfolioValuationResult valuation,
            List<PendingBuyOrder> orders,
            out decimal provisionalAvailableCash)
        {
            provisionalAvailableCash = 0m;
            KorovinRebalanceOrder fundingSell;
            bool saleSubmitted;
            decimal volumeBefore;
            decimal expectedVolume;
            DateTime lastAttemptTime;

            lock (_stateLocker)
            {
                fundingSell = _pendingMoneyMarketFundingSell;
                saleSubmitted = _moneyMarketFundingSellSubmitted;
                volumeBefore = _moneyMarketFundingVolumeBefore;
                expectedVolume = _moneyMarketFundingExpectedVolume;
                lastAttemptTime = _moneyMarketFundingLastAttemptTime;
            }

            if (fundingSell == null)
            {
                return false;
            }

            if (saleSubmitted)
            {
                decimal currentVolume = GetOpenLongVolume(_tabMoneyMarket);
                decimal executedVolume = Math.Max(0m, volumeBefore - currentVolume);
                decimal volumeTolerance = GetVolumeStep(_tabMoneyMarket) / 2m;

                if (HasActiveExecutionOrders())
                {
                    return true;
                }

                if (executedVolume + volumeTolerance >= expectedVolume)
                {
                    ClearPendingMoneyMarketFundingSell(fundingSell);
                    return false;
                }

                if (TimeServer <= lastAttemptTime)
                {
                    return true;
                }

                lock (_stateLocker)
                {
                    if (ReferenceEquals(_pendingMoneyMarketFundingSell, fundingSell))
                    {
                        _moneyMarketFundingSellSubmitted = false;
                    }
                }
            }

            if (HasActiveExecutionOrders())
            {
                return true;
            }

            if (lastAttemptTime != DateTime.MinValue && TimeServer <= lastAttemptTime)
            {
                return true;
            }

            decimal executableBuyMoney = 0m;

            for (int index = orders.Count - 1; index >= 0; index--)
            {
                PendingBuyOrder pending = orders[index];

                if (pending.IsCashSweep)
                {
                    continue;
                }

                Position openPosition = GetPrimaryLongPosition(pending.Tab);

                if (openPosition != null && CanIncreaseLongPosition(openPosition) == false)
                {
                    SendDailyRebalanceLog(
                        "MONEY MARKET FUNDING WAIT " + pending.Name
                        + " position " + openPosition.Number
                        + " state " + openPosition.State + " has an active order",
                        LogMessageType.System);
                    return true;
                }

                decimal price = GetBuyPrice(pending.Tab);
                KorovinOrderSizeResult size = CalculateOrderSize(
                    pending.Tab,
                    pending.RemainingMoney,
                    price,
                    0m);

                if (size.Side != KorovinRebalanceOrderSide.Buy
                    || size.Volume <= 0m
                    || pending.Tab.CanTradeThisVolume(size.Volume) == false)
                {
                    RemovePendingBuy(pending);
                    orders.RemoveAt(index);
                    continue;
                }

                executableBuyMoney += size.Money;
            }

            decimal requiredSale = SDKRebalancerByKorovinCalculator.CalculateRequiredMoneyMarketSale(
                Math.Abs(fundingSell.RequestedMoney),
                executableBuyMoney,
                valuation.FreeCash,
                0m);

            if (StartProgram == StartProgram.IsOsTrader && executableBuyMoney > 0m)
            {
                requiredSale += executableBuyMoney * LiveBuyCashBufferRate;
            }

            if (requiredSale <= 0m)
            {
                ClearPendingMoneyMarketFundingSell(fundingSell);
                return false;
            }

            decimal priceMoneyMarket = GetSellPrice(_tabMoneyMarket);
            decimal volumeStep = GetVolumeStep(_tabMoneyMarket);
            decimal moneyStep = priceMoneyMarket * GetLot(_tabMoneyMarket) * volumeStep;

            if (moneyStep > 0m)
            {
                requiredSale = Math.Ceiling(requiredSale / moneyStep) * moneyStep;
            }

            fundingSell.PlannedMoney = -requiredSale;
            fundingSell.Reason = "Money market funding for executable buys";
            decimal fundingVolumeBefore = GetOpenLongVolume(_tabMoneyMarket);
            KorovinOrderSizeResult fundingSize = CalculateOrderSize(
                _tabMoneyMarket,
                fundingSell.PlannedMoney,
                priceMoneyMarket,
                fundingVolumeBefore);

            if (fundingSize.Side != KorovinRebalanceOrderSide.Sell || fundingSize.Volume <= 0m)
            {
                ClearPendingMoneyMarketFundingSell(fundingSell);
                return false;
            }

            lock (_stateLocker)
            {
                if (ReferenceEquals(_pendingMoneyMarketFundingSell, fundingSell))
                {
                    _moneyMarketFundingSellSubmitted = true;
                    _moneyMarketFundingVolumeBefore = fundingVolumeBefore;
                    _moneyMarketFundingExpectedVolume = fundingSize.Volume;
                    _moneyMarketFundingLastAttemptTime = TimeServer;
                }
            }

            if (ExecuteSell(_tabMoneyMarket, fundingSell) == false)
            {
                lock (_stateLocker)
                {
                    if (ReferenceEquals(_pendingMoneyMarketFundingSell, fundingSell))
                    {
                        _moneyMarketFundingSellSubmitted = false;
                    }
                }

                SendDailyRebalanceLog(
                    "Money market funding sell was not accepted and will be retried on the next market time",
                    LogMessageType.System);
                return true;
            }

            if (StartProgram == StartProgram.IsTester || StartProgram == StartProgram.IsOsOptimizer)
            {
                provisionalAvailableCash = valuation.FreeCash + fundingSize.Money;
                ClearPendingMoneyMarketFundingSell(fundingSell);
                return false;
            }

            return true;
        }

        private void ClearPendingMoneyMarketFundingSell(KorovinRebalanceOrder fundingSell)
        {
            lock (_stateLocker)
            {
                if (ReferenceEquals(_pendingMoneyMarketFundingSell, fundingSell))
                {
                    _pendingMoneyMarketFundingSell = null;
                    ResetMoneyMarketFundingSubmission();
                }
            }
        }

        private void ResetMoneyMarketFundingSubmission()
        {
            _moneyMarketFundingSellSubmitted = false;
            _moneyMarketFundingVolumeBefore = 0m;
            _moneyMarketFundingExpectedVolume = 0m;
            _moneyMarketFundingLastAttemptTime = DateTime.MinValue;
        }

        private bool HasPendingRegularBuy()
        {
            lock (_stateLocker)
            {
                for (int index = 0; index < _pendingBuyOrders.Count; index++)
                {
                    if (_pendingBuyOrders[index].IsCashSweep == false)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private PendingBuyOrder GetPendingCashSweep()
        {
            lock (_stateLocker)
            {
                for (int index = 0; index < _pendingBuyOrders.Count; index++)
                {
                    if (_pendingBuyOrders[index].IsCashSweep)
                    {
                        return _pendingBuyOrders[index];
                    }
                }
            }

            return null;
        }

        private bool HasActiveExecutionOrders()
        {
            List<BotTabSimple> executionTabs;

            lock (_stateLocker)
            {
                executionTabs = new List<BotTabSimple>(_executionEventTabs);
            }

            for (int tabIndex = 0; tabIndex < executionTabs.Count; tabIndex++)
            {
                List<Position> positions = executionTabs[tabIndex].PositionsOpenAll;

                for (int positionIndex = 0;
                    positions != null && positionIndex < positions.Count;
                    positionIndex++)
                {
                    Position position = positions[positionIndex];

                    if (position != null && (position.OpenActive || position.CloseActive))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private KorovinOrderSizeResult CalculateOrderSize(
            BotTabSimple tab,
            decimal money,
            decimal price,
            decimal currentVolume)
        {
            decimal step = GetVolumeStep(tab);

            return SDKRebalancerByKorovinCalculator.CalculateOrderSize(
                money,
                price,
                GetLot(tab),
                step,
                tab.Security.DecimalsVolume,
                currentVolume);
        }

        private static decimal GetVolumeStep(BotTabSimple tab)
        {
            decimal step = tab.Security.VolumeStep;

            if (step > 0m)
            {
                return step;
            }

            return tab.Security.DecimalsVolume > 0
                ? 1m / (decimal)Math.Pow(10, tab.Security.DecimalsVolume)
                : 1m;
        }

        private void RemovePendingBuy(PendingBuyOrder pending)
        {
            lock (_stateLocker)
            {
                for (int index = _pendingBuyOrders.Count - 1; index >= 0; index--)
                {
                    if (_pendingBuyOrders[index].Name == pending.Name)
                    {
                        _pendingBuyOrders.RemoveAt(index);
                        return;
                    }
                }
            }
        }

        private void RemoveExpiredPendingBuys()
        {
            DateTime serverTime = TimeServer;
            List<PendingBuyOrder> orders;

            lock (_stateLocker)
            {
                orders = new List<PendingBuyOrder>(_pendingBuyOrders);
            }

            bool hasRegularBuy = false;

            for (int index = 0; index < orders.Count; index++)
            {
                if (orders[index].IsCashSweep == false)
                {
                    hasRegularBuy = true;
                    break;
                }
            }

            if (hasRegularBuy == false)
            {
                return;
            }

            bool hasActiveOrder = HasActiveExecutionOrders();

            for (int index = 0; index < orders.Count; index++)
            {
                PendingBuyOrder pending = orders[index];

                if (pending.IsCashSweep)
                {
                    continue;
                }

                if (SDKRebalancerByKorovinCalculator.ShouldExpirePendingBuy(
                    pending.RebalanceDate,
                    serverTime,
                    _rebalanceTime.Value.TimeSpan,
                    hasActiveOrder) == false)
                {
                    continue;
                }

                bool removed = false;

                lock (_stateLocker)
                {
                    for (int pendingIndex = _pendingBuyOrders.Count - 1;
                        pendingIndex >= 0;
                        pendingIndex--)
                    {
                        if (ReferenceEquals(_pendingBuyOrders[pendingIndex], pending))
                        {
                            _pendingBuyOrders.RemoveAt(pendingIndex);
                            removed = true;
                            break;
                        }
                    }
                }

                if (removed)
                {
                    SendNewLogMessage(
                        "Pending buy expired before new rebalance " + pending.Name
                        + " planDate " + FormatLogDate(pending.RebalanceDate)
                        + " remaining " + pending.RemainingMoney.ToString("F2")
                        + " no active order",
                        LogMessageType.System);
                }
            }
        }

        private static bool TrySubmitBuyToPosition(
            BotTabSimple tab,
            Position position,
            decimal volume)
        {
            int openOrderCountBefore = position.OpenOrders == null ? 0 : position.OpenOrders.Count;
            decimal openVolumeBefore = position.OpenVolume;
            bool openActiveBefore = position.OpenActive;
            tab.BuyAtMarketToPosition(position, volume);
            int openOrderCountAfter = position.OpenOrders == null ? 0 : position.OpenOrders.Count;

            return openOrderCountAfter > openOrderCountBefore
                || position.OpenVolume > openVolumeBefore
                || (openActiveBefore == false && position.OpenActive);
        }

        #endregion

        #region Portfolio helpers

        private KorovinPortfolioValuationResult GetPortfolioValuation()
        {
            decimal engineBalance = GetEnginePortfolioBalance();
            decimal openPositionsProfit = 0m;

            if (StartProgram == StartProgram.IsTester
                || StartProgram == StartProgram.IsOsOptimizer)
            {
                openPositionsProfit = GetStrategyOpenPositionsProfit();
            }

            return SDKRebalancerByKorovinCalculator.CalculatePortfolioValuation(
                engineBalance,
                openPositionsProfit,
                GetStrategyMarketValue(),
                StartProgram == StartProgram.IsTester || StartProgram == StartProgram.IsOsOptimizer);
        }

        private decimal GetEnginePortfolioBalance()
        {
            if (_tabMoneyMarket.Portfolio == null)
            {
                return 0m;
            }

            decimal result = _tabMoneyMarket.Portfolio.ValueCurrent;
            return result > 0m ? result : _tabMoneyMarket.Portfolio.ValueBegin;
        }

        private decimal GetStrategyMarketValue()
        {
            decimal result = GetAssetMarketValue(_tabMoneyMarket) + GetAssetMarketValue(_tabGold);

            for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
            {
                result += GetAssetMarketValue(_tabStocks.Tabs[index]);
            }

            return result;
        }

        private decimal GetStrategyOpenPositionsProfit()
        {
            decimal result = GetOpenPositionsProfit(_tabMoneyMarket) + GetOpenPositionsProfit(_tabGold);

            for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
            {
                result += GetOpenPositionsProfit(_tabStocks.Tabs[index]);
            }

            return result;
        }

        private decimal GetOpenPositionsProfit(BotTabSimple tab)
        {
            decimal result = 0m;
            List<Position> positions = tab == null ? null : tab.PositionsOpenAll;

            for (int index = 0; positions != null && index < positions.Count; index++)
            {
                Position position = positions[index];

                if (position != null
                    && position.State != PositionStateType.Done
                    && position.State != PositionStateType.Deleted)
                {
                    result += position.ProfitPortfolioAbs;
                }
            }

            return result;
        }

        private decimal GetAssetMarketValue(BotTabSimple tab)
        {
            if (tab == null || tab.Security == null)
            {
                return 0m;
            }

            return GetOpenLongVolume(tab) * GetMarketPrice(tab) * GetLot(tab);
        }

        private decimal GetOpenLongVolume(BotTabSimple tab)
        {
            decimal result = 0m;
            List<Position> positions = tab.PositionsOpenAll;

            for (int index = 0; positions != null && index < positions.Count; index++)
            {
                Position position = positions[index];

                if (position != null && position.Direction == Side.Buy
                    && position.State != PositionStateType.Done && position.OpenVolume > 0m)
                {
                    result += position.OpenVolume;
                }
            }

            return result;
        }

        private Position GetPrimaryLongPosition(BotTabSimple tab)
        {
            List<Position> positions = tab.PositionsOpenAll;

            for (int index = 0; positions != null && index < positions.Count; index++)
            {
                Position position = positions[index];

                if (position != null && position.Direction == Side.Buy
                    && position.State != PositionStateType.Done
                    && position.State != PositionStateType.Deleted
                    && (position.OpenVolume > 0m || position.OpenActive || position.CloseActive))
                {
                    return position;
                }
            }

            return null;
        }

        private static bool CanIncreaseLongPosition(Position position)
        {
            return position != null
                && position.OpenVolume > 0m
                && position.OpenActive == false
                && position.CloseActive == false;
        }

        private decimal GetMarketPrice(BotTabSimple tab)
        {
            decimal bid = tab.PriceBestBid;
            decimal ask = tab.PriceBestAsk;

            if (bid > 0m && ask > 0m)
            {
                return (bid + ask) / 2m;
            }

            if (bid > 0m)
            {
                return bid;
            }

            if (ask > 0m)
            {
                return ask;
            }

            if (tab.CandlesAll != null && tab.CandlesAll.Count > 0)
            {
                return tab.CandlesAll[tab.CandlesAll.Count - 1].Close;
            }

            return 0m;
        }

        private decimal GetBuyPrice(BotTabSimple tab)
        {
            return tab.PriceBestAsk > 0m ? tab.PriceBestAsk : GetMarketPrice(tab);
        }

        private decimal GetSellPrice(BotTabSimple tab)
        {
            return tab.PriceBestBid > 0m ? tab.PriceBestBid : GetMarketPrice(tab);
        }

        private decimal GetLot(BotTabSimple tab)
        {
            return tab.Security != null && tab.Security.Lot > 0m ? tab.Security.Lot : 1m;
        }

        private Dictionary<string, BotTabSimple> BuildExecutionTabMap(RuntimeSnapshot snapshot)
        {
            Dictionary<string, BotTabSimple> result = new Dictionary<string, BotTabSimple>(StringComparer.OrdinalIgnoreCase);
            result[snapshot.MoneyMarket.Name] = snapshot.MoneyMarket.Tab;
            result[snapshot.Gold.Name] = snapshot.Gold.Tab;

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                if (result.ContainsKey(snapshot.Stocks[index].Name) == false)
                {
                    result[snapshot.Stocks[index].Name] = snapshot.Stocks[index].Tab;
                }
            }

            return result;
        }

        #endregion

        #region Stock settings table

        private string StockSettingsPath
        {
            get
            {
                return Path.Combine("Engine", NameStrategyUniq + "KorovinStocks.txt");
            }
        }

        private void LoadStockSettings()
        {
            try
            {
                List<KorovinStockUserSetting> loaded = KorovinStockSettingsStorage.Load(StockSettingsPath);

                lock (_stockSettingsLocker)
                {
                    _stockSettings.Clear();
                    _stockSettings.AddRange(loaded);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SaveStockSettings()
        {
            if (UseAllScreenerStockSettings())
            {
                return;
            }

            try
            {
                List<KorovinStockUserSetting> settings = new List<KorovinStockUserSetting>();

                lock (_stockSettingsLocker)
                {
                    for (int index = 0; index < _stockSettings.Count; index++)
                    {
                        settings.Add(CopySetting(_stockSettings[index]));
                    }
                }

                KorovinStockSettingsStorage.Save(StockSettingsPath, settings);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void RefreshStockSettingsFromTabs()
        {
            bool useAllScreenerStocks = UseAllScreenerStockSettings();
            bool changed = false;

            if (useAllScreenerStocks == false)
            {
                for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
                {
                    changed |= TryAddStockSetting(_tabStocks.Tabs[index]);
                }
            }

            for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
            {
                SubscribeExecutionEvents(_tabStocks.Tabs[index]);
            }

            if (useAllScreenerStocks)
            {
                if (_allScreenerModeLogged == false
                    && _tabStocks.Tabs != null
                    && _tabStocks.Tabs.Count > 0)
                {
                    _allScreenerModeLogged = true;
                    SendNewLogMessage(
                        "All screener securities are enabled with equal base weights and max NAV weight "
                            + _defaultStockMaxNavWeight.ValueDecimal.ToString(CultureInfo.InvariantCulture),
                        LogMessageType.System);
                }

                return;
            }

            if (changed)
            {
                SaveAddedStockSettings();
            }
        }

        private void AddStockSettingFromTab(BotTabSimple tab)
        {
            if (UseAllScreenerStockSettings() || TryAddStockSetting(tab) == false)
            {
                return;
            }

            SaveAddedStockSettings();
        }

        private bool TryAddStockSetting(BotTabSimple tab)
        {
            if (tab == null || tab.Security == null || string.IsNullOrWhiteSpace(tab.Security.Name))
            {
                return false;
            }

            lock (_stockSettingsLocker)
            {
                if (FindSetting(tab.Security.Name) != null)
                {
                    return false;
                }

                KorovinStockUserSetting setting = new KorovinStockUserSetting();
                setting.Ticker = tab.Security.Name;
                setting.Enabled = false;
                setting.BaseWeight = 1m;
                setting.MaxNavWeight = 0.10m;
                _stockSettings.Add(setting);
                return true;
            }
        }

        private void SaveAddedStockSettings()
        {
            SaveStockSettings();
            RefreshStockSettingsTable();
            SendNewLogMessage(
                "New screener securities were added disabled. Review stock settings",
                LogMessageType.System);
        }

        private void CreateStockSettingsTable()
        {
            try
            {
                if (MainWindow.GetDispatcher.CheckAccess() == false)
                {
                    MainWindow.GetDispatcher.Invoke(new Action(CreateStockSettingsTable));
                    return;
                }

                ParamGuiSettings.Height = 750;
                ParamGuiSettings.Width = 760;
                CustomTabToParametersUi customTab = ParamGuiSettings.CreateCustomTab("Stock weights");
                _hostStockSettings = new WindowsFormsHost();
                _gridStockSettings = DataGridFactory.GetDataGridView(
                    DataGridViewSelectionMode.FullRowSelect,
                    DataGridViewAutoSizeRowsMode.AllCells);
                _gridStockSettings.AllowUserToAddRows = false;

                DataGridViewTextBoxColumn tickerColumn = new DataGridViewTextBoxColumn();
                tickerColumn.HeaderText = "Ticker";
                tickerColumn.ReadOnly = true;
                tickerColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _gridStockSettings.Columns.Add(tickerColumn);

                DataGridViewCheckBoxColumn enabledColumn = new DataGridViewCheckBoxColumn();
                enabledColumn.HeaderText = "Enabled";
                enabledColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _gridStockSettings.Columns.Add(enabledColumn);

                DataGridViewTextBoxColumn baseWeightColumn = new DataGridViewTextBoxColumn();
                baseWeightColumn.HeaderText = "Base weight";
                baseWeightColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _gridStockSettings.Columns.Add(baseWeightColumn);

                DataGridViewTextBoxColumn maxWeightColumn = new DataGridViewTextBoxColumn();
                maxWeightColumn.HeaderText = "Max NAV weight";
                maxWeightColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _gridStockSettings.Columns.Add(maxWeightColumn);

                _gridStockSettings.DataError += GridStockSettings_DataError;
                _gridStockSettings.CellEndEdit += GridStockSettings_CellEndEdit;
                _hostStockSettings.Child = _gridStockSettings;
                customTab.AddChildren(_hostStockSettings);
                RefreshStockSettingsTable();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void RefreshStockSettingsTable()
        {
            if (_gridStockSettings == null || _gridStockSettings.IsDisposed)
            {
                return;
            }

            if (_gridStockSettings.InvokeRequired)
            {
                _gridStockSettings.Invoke(new Action(RefreshStockSettingsTable));
                return;
            }

            try
            {
                _tableIsUpdating = true;
                _gridStockSettings.Rows.Clear();
                List<KorovinStockUserSetting> settings = GetSortedSettingsCopy();

                for (int index = 0; index < settings.Count; index++)
                {
                    _gridStockSettings.Rows.Add(
                        settings[index].Ticker,
                        settings[index].Enabled,
                        settings[index].BaseWeight.ToString(CultureInfo.InvariantCulture),
                        settings[index].MaxNavWeight.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
            finally
            {
                _tableIsUpdating = false;
            }
        }

        private void GridStockSettings_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (_tableIsUpdating || e.RowIndex < 0 || e.RowIndex >= _gridStockSettings.Rows.Count)
                {
                    return;
                }

                DataGridViewRow row = _gridStockSettings.Rows[e.RowIndex];
                string ticker = Convert.ToString(row.Cells[0].Value);
                bool enabled = Convert.ToBoolean(row.Cells[1].Value);
                decimal baseWeight = Convert.ToString(row.Cells[2].Value).ToDecimal();
                decimal maxWeight = Convert.ToString(row.Cells[3].Value).ToDecimal();

                if (string.IsNullOrWhiteSpace(ticker) || baseWeight < 0m || maxWeight < 0m || maxWeight > 1m)
                {
                    SendNewLogMessage("Invalid stock setting for " + ticker, LogMessageType.Error);
                    RefreshStockSettingsTable();
                    return;
                }

                lock (_stockSettingsLocker)
                {
                    KorovinStockUserSetting setting = FindSetting(ticker);

                    if (setting != null)
                    {
                        setting.Enabled = enabled;
                        setting.BaseWeight = baseWeight;
                        setting.MaxNavWeight = maxWeight;
                    }
                }

                SaveStockSettings();
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
                RefreshStockSettingsTable();
            }
        }

        private void GridStockSettings_DataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            try
            {
                SendNewLogMessage(e.Exception == null ? e.ToString() : e.Exception.ToString(), LogMessageType.Error);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void DisposeStockSettingsTable()
        {
            if (_gridStockSettings == null && _hostStockSettings == null)
            {
                return;
            }

            if (MainWindow.GetDispatcher.CheckAccess() == false)
            {
                MainWindow.GetDispatcher.Invoke(new Action(DisposeStockSettingsTable));
                return;
            }

            if (_gridStockSettings != null)
            {
                _gridStockSettings.DataError -= GridStockSettings_DataError;
                _gridStockSettings.CellEndEdit -= GridStockSettings_CellEndEdit;
                DataGridFactory.ClearLinks(_gridStockSettings);
                _gridStockSettings.Rows.Clear();
                _gridStockSettings.Columns.Clear();
                _gridStockSettings.DataSource = null;
                _gridStockSettings.Dispose();
                _gridStockSettings = null;
            }

            if (_hostStockSettings != null)
            {
                _hostStockSettings.Child = null;
                _hostStockSettings.Dispose();
                _hostStockSettings = null;
            }
        }

        private Dictionary<string, KorovinStockUserSetting> GetStockSettingsCopy()
        {
            Dictionary<string, KorovinStockUserSetting> result =
                new Dictionary<string, KorovinStockUserSetting>(StringComparer.OrdinalIgnoreCase);

            if (UseAllScreenerStockSettings())
            {
                for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
                {
                    BotTabSimple tab = _tabStocks.Tabs[index];

                    if (tab == null || tab.Security == null || string.IsNullOrWhiteSpace(tab.Security.Name))
                    {
                        continue;
                    }

                    KorovinStockUserSetting setting = new KorovinStockUserSetting();
                    setting.Ticker = tab.Security.Name;
                    setting.Enabled = true;
                    setting.BaseWeight = 1m;
                    setting.MaxNavWeight = _defaultStockMaxNavWeight.ValueDecimal;
                    result[setting.Ticker] = setting;
                }

                return result;
            }

            lock (_stockSettingsLocker)
            {
                for (int index = 0; index < _stockSettings.Count; index++)
                {
                    result[_stockSettings[index].Ticker] = CopySetting(_stockSettings[index]);
                }
            }

            return result;
        }

        private List<KorovinStockUserSetting> GetSortedSettingsCopy()
        {
            List<KorovinStockUserSetting> result = new List<KorovinStockUserSetting>();
            Dictionary<string, KorovinStockUserSetting> settings = GetStockSettingsCopy();

            foreach (KeyValuePair<string, KorovinStockUserSetting> pair in settings)
            {
                result.Add(CopySetting(pair.Value));
            }

            result.Sort(CompareSettings);
            return result;
        }

        private KorovinStockUserSetting FindSetting(string ticker)
        {
            for (int index = 0; index < _stockSettings.Count; index++)
            {
                if (string.Equals(_stockSettings[index].Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                {
                    return _stockSettings[index];
                }
            }

            return null;
        }

        private static int CompareSettings(KorovinStockUserSetting left, KorovinStockUserSetting right)
        {
            return string.Compare(left.Ticker, right.Ticker, StringComparison.OrdinalIgnoreCase);
        }

        private static KorovinStockUserSetting CopySetting(KorovinStockUserSetting source)
        {
            KorovinStockUserSetting result = new KorovinStockUserSetting();
            result.Ticker = source.Ticker;
            result.Enabled = source.Enabled;
            result.BaseWeight = source.BaseWeight;
            result.MaxNavWeight = source.MaxNavWeight;
            return result;
        }

        private bool UseAllScreenerStockSettings()
        {
            return _stockSettingsMode.ValueString == "All screener";
        }

        private static bool IsOptimizerBotName(string name)
        {
            return string.IsNullOrEmpty(name) == false
                && name.StartsWith("OptimizerBot", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Logging

        private static string FormatLogDate(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "none";
            }

            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private void SendDailyRebalanceLog(string message, LogMessageType type)
        {
            DateTime logDate = TimeServer.Date;

            if (logDate == DateTime.MinValue.Date)
            {
                logDate = DateTime.Today;
            }

            string key = type + "|" + message;
            SendDailyLog(key, message, type, logDate);
        }

        private void SendDailyLog(
            string key,
            string message,
            LogMessageType type,
            DateTime logDate)
        {
            lock (_stateLocker)
            {
                if (_dailyLogDate != logDate.Date)
                {
                    _dailyLogKeys.Clear();
                    _dailyLogDate = logDate.Date;
                }

                if (_dailyLogKeys.Add(key) == false)
                {
                    return;
                }
            }

            SendNewLogMessage(message, type);
        }

        private void MarkPortfolioStateLogPending(DateTime rebalanceDate)
        {
            lock (_stateLocker)
            {
                _portfolioStateLogPending = true;
                _portfolioStateLogDate = rebalanceDate.Date;
            }
        }

        private void TryLogPortfolioStateAfterRebalance()
        {
            DateTime rebalanceDate;
            List<BotTabSimple> executionTabs;

            lock (_stateLocker)
            {
                if (_portfolioStateLogPending == false
                    || _pendingBuyOrders.Count > 0
                    || _pendingReopenOrders.Count > 0)
                {
                    return;
                }

                rebalanceDate = _portfolioStateLogDate;
                executionTabs = new List<BotTabSimple>(_executionEventTabs);
            }

            for (int index = 0; index < executionTabs.Count; index++)
            {
                List<Position> positions = executionTabs[index].PositionsOpenAll;

                for (int positionIndex = 0;
                    positions != null && positionIndex < positions.Count;
                    positionIndex++)
                {
                    Position position = positions[positionIndex];

                    if (position != null && (position.OpenActive || position.CloseActive))
                    {
                        return;
                    }
                }
            }

            KorovinPortfolioValuationResult valuation = GetPortfolioValuation();
            decimal netAssetValue = valuation.NetAssetValue;

            if (valuation.IsValid == false)
            {
                return;
            }

            decimal freeCashWeight = valuation.FreeCash / netAssetValue;
            decimal moneyMarketWeight = GetAssetMarketValue(_tabMoneyMarket) / netAssetValue;
            decimal goldWeight = GetAssetMarketValue(_tabGold) / netAssetValue;
            decimal equityWeight = 0m;
            string stockWeights = string.Empty;

            for (int index = 0; _tabStocks.Tabs != null && index < _tabStocks.Tabs.Count; index++)
            {
                BotTabSimple tab = _tabStocks.Tabs[index];

                if (tab == null || tab.Security == null)
                {
                    continue;
                }

                decimal volume = GetOpenLongVolume(tab);
                decimal weight = GetAssetMarketValue(tab) / netAssetValue;
                equityWeight += weight;

                if (stockWeights.Length > 0)
                {
                    stockWeights += ", ";
                }

                stockWeights += tab.Security.Name
                    + "=" + weight.ToString("P4")
                    + "(volume " + volume + ")";
            }

            lock (_stateLocker)
            {
                if (_portfolioStateLogPending == false
                    || _portfolioStateLogDate != rebalanceDate)
                {
                    return;
                }

                _portfolioStateLogPending = false;
            }

            SendNewLogMessage(
                "Portfolio after rebalance date " + rebalanceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + " NAV " + netAssetValue.ToString("F2")
                + " engineBalance " + valuation.EngineBalance.ToString("F2")
                + " openPositionsPnL " + valuation.OpenPositionsProfit.ToString("F2")
                + " freeCash " + freeCashWeight.ToString("P4")
                + " moneyMarket " + moneyMarketWeight.ToString("P4")
                + " gold " + goldWeight.ToString("P4")
                + " equity " + equityWeight.ToString("P4")
                + " stocks [" + stockWeights + "]",
                LogMessageType.System);
        }

        private void LogCalculation(
            RuntimeSnapshot snapshot,
            KorovinMarketPanicResult panic,
            KorovinAllocationResult allocation,
            KorovinCappedAllocationResult cappedAllocation,
            KorovinRebalancePlanResult plan)
        {
            decimal currentEquityWeight = 0m;

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                currentEquityWeight += snapshot.Stocks[index].CurrentWeight;
            }

            decimal effectiveCashTarget = allocation.TargetCashWeight + cappedAllocation.CashShortfallWeight;
            SendNewLogMessage(
                "Portfolio calculation NAV " + snapshot.NetAssetValue.ToString("F2")
                + " mode " + _tradeMode.ValueString
                + " engineBalance " + snapshot.EngineBalance.ToString("F2")
                + " openPositionsPnL " + snapshot.OpenPositionsProfit.ToString("F2")
                + " marketDD " + panic.MarketDrawdown.ToString("P4")
                + " breadth " + panic.Breadth.ToString("P4")
                + " panic " + panic.Panic.ToString("F6")
                + " targetCash " + effectiveCashTarget.ToString("P4")
                + " targetEquity " + cappedAllocation.AllocatedEquityWeight.ToString("P4")
                + " targetGold " + allocation.TargetGoldWeight.ToString("P4")
                + " currentCash "
                + ((snapshot.FreeCash + snapshot.MoneyMarket.MarketValue) / snapshot.NetAssetValue).ToString("P4")
                + " currentFreeCash " + (snapshot.FreeCash / snapshot.NetAssetValue).ToString("P4")
                + " currentMoneyMarket " + snapshot.MoneyMarket.CurrentWeight.ToString("P4")
                + " currentEquity " + currentEquityWeight.ToString("P4")
                + " currentGold " + snapshot.Gold.CurrentWeight.ToString("P4"),
                LogMessageType.System);

            for (int index = 0; index < snapshot.Stocks.Count; index++)
            {
                RuntimeStock stock = snapshot.Stocks[index];
                decimal lastSignal = stock.SignalValues.Count == 0 ? 0m : stock.SignalValues[stock.SignalValues.Count - 1];
                KorovinStockScoreResult score = stock.Score ?? new KorovinStockScoreResult();
                KorovinRebalanceOrder order = FindOrder(plan.Orders, stock.Name);
                decimal requestedTrade = order == null ? 0m : order.RequestedMoney;
                decimal plannedTrade = order == null ? 0m : order.PlannedMoney;
                SendNewLogMessage(
                    "Stock " + stock.Name
                    + " market " + stock.Price
                    + " signal " + lastSignal
                    + " drawdown " + score.Drawdown.ToString("P4")
                    + " relativeDD " + score.RelativeDrawdown.ToString("P4")
                    + " qAbs " + score.AbsoluteCheapness.ToString("F6")
                    + " qRel " + score.RelativeCheapness.ToString("F6")
                    + " cheapness " + score.Cheapness.ToString("F6")
                    + " multiplier " + score.Multiplier.ToString("F6")
                    + " baseWeight " + stock.Setting.BaseWeight
                    + " currentWeight " + stock.CurrentWeight.ToString("P4")
                    + " rebalanceWeight " + stock.RebalanceWeight.ToString("P4")
                    + " targetWeight " + stock.TargetWeight.ToString("P4")
                    + " maxWeight " + stock.Setting.MaxNavWeight.ToString("P4")
                    + " rebalanceError " + (stock.TargetWeight - stock.RebalanceWeight).ToString("P4")
                    + " requestedTrade " + requestedTrade.ToString("F2")
                    + " plannedTrade " + plannedTrade.ToString("F2"),
                    LogMessageType.System);
            }

            for (int index = 0; index < plan.Orders.Count; index++)
            {
                KorovinRebalanceOrder order = plan.Orders[index];

                if (ShouldLogPlanOrder(order) == false)
                {
                    continue;
                }

                SendNewLogMessage(
                    "Plan " + order.Name + " " + order.Side
                    + " currentWeight " + order.CurrentWeight.ToString("P4")
                    + " rebalanceWeight " + order.RebalanceWeight.ToString("P4")
                    + " targetWeight " + order.TargetWeight.ToString("P4")
                    + " error " + order.ErrorWeight.ToString("P4")
                    + " requested " + order.RequestedMoney.ToString("F2")
                    + " planned " + order.PlannedMoney.ToString("F2")
                    + " reason " + order.Reason,
                    LogMessageType.System);
            }
        }

        private bool ShouldLogPlanOrder(KorovinRebalanceOrder order)
        {
            if (order == null)
            {
                return false;
            }

            if (order.Side != KorovinRebalanceOrderSide.Buy || _tabMoneyMarket.Security == null
                || string.Equals(
                    order.Name,
                    _tabMoneyMarket.Security.Name,
                    StringComparison.OrdinalIgnoreCase) == false)
            {
                return true;
            }

            decimal price = GetBuyPrice(_tabMoneyMarket);
            KorovinOrderSizeResult size = CalculateOrderSize(
                _tabMoneyMarket,
                order.PlannedMoney,
                price,
                0m);
            return size.Side == KorovinRebalanceOrderSide.Buy
                && size.Volume > 0m
                && _tabMoneyMarket.CanTradeThisVolume(size.Volume);
        }

        private void LogDividendEvent(RuntimeStock stock)
        {
            KorovinSignalPoint last = stock.SignalPoints[stock.SignalPoints.Count - 1];
            KorovinSignalPoint previous = stock.SignalPoints[stock.SignalPoints.Count - 2];
            decimal expectedPrice = previous.MarketPrice - last.DividendAmount;
            LogMessageType type = last.DividendAdjustmentIsValid ? LogMessageType.System : LogMessageType.Error;
            string message = "Dividend " + stock.Name
                + " previousMarket " + previous.MarketPrice
                + " dividend " + last.DividendAmount
                + " expectedPrice " + expectedPrice
                + " currentMarket " + last.MarketPrice
                + " adjustedReturn " + last.AdjustedReturn.ToString("P6")
                + " previousSignal " + previous.SignalValue
                + " newSignal " + last.SignalValue
                + " valid " + last.DividendAdjustmentIsValid;
            SendDailyLog("Dividend|" + stock.Name, message, type, last.Date);
        }

        private static KorovinRebalanceOrder FindOrder(List<KorovinRebalanceOrder> orders, string name)
        {
            for (int index = 0; index < orders.Count; index++)
            {
                if (string.Equals(orders[index].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return orders[index];
                }
            }

            return null;
        }

        #endregion

        #region Types

        private class RuntimeAsset
        {
            public BotTabSimple Tab;
            public string Name;
            public decimal Price;
            public decimal PreviousPrice;
            public decimal Volume;
            public decimal Lot;
            public decimal MarketValue;
            public decimal CurrentWeight;
            public List<decimal> PriceValues;
            public DailyPriceCache DailyPriceCache;
        }

        private class RuntimeStock : RuntimeAsset
        {
            public KorovinStockUserSetting Setting;
            public List<KorovinSignalPoint> SignalPoints = new List<KorovinSignalPoint>();
            public List<decimal> SignalValues = new List<decimal>();
            public KorovinStockScoreResult Score;
            public bool IsExDividendDate;
            public decimal AdjustedReturn;
            public decimal PreviousWeight;
            public decimal RebalanceWeight;
            public decimal TargetWeight;
        }

        private class RuntimeSnapshot
        {
            public decimal EngineBalance;
            public decimal OpenPositionsProfit;
            public decimal NetAssetValue;
            public decimal FreeCash;
            public RuntimeAsset MoneyMarket;
            public RuntimeAsset Gold;
            public List<decimal> IndexValues = new List<decimal>();
            public List<decimal> GoldValues = new List<decimal>();
            public List<RuntimeStock> Stocks = new List<RuntimeStock>();
        }

        private class PendingBuyOrder
        {
            public string Name;
            public BotTabSimple Tab;
            public decimal RemainingMoney;
            public bool IsCashSweep;
            public DateTime RebalanceDate;
        }

        private class PendingReopenOrder
        {
            public string Name;
            public BotTabSimple Tab;
            public decimal CurrentVolume;
            public decimal TargetVolume;
        }

        private class DailyPriceCache
        {
            public readonly List<KorovinDailyPrice> Prices = new List<KorovinDailyPrice>();
            public readonly List<decimal> Values = new List<decimal>();
            public List<Candle> CandleSource;
            public int ProcessedCandleCount;
            public DateTime FirstProcessedCandleTime;
            public decimal FirstProcessedCandleClose;
            public DateTime LastProcessedCandleTime;
            public decimal LastProcessedCandleClose;
            public DateTime LastCurrentDate;
            public DateTime SyntheticDate;
            public decimal SyntheticOriginalPrice;
            public bool SyntheticHadOriginalPrice;
            public int ChangedFromIndex;
            public int DatesChangedFromIndex;
        }

        private class CachedDividendRecord
        {
            public DateTime RegistryDate;
            public decimal Amount;
        }

        private class DividendHistoryCache
        {
            public readonly List<CachedDividendRecord> Records = new List<CachedDividendRecord>();
            public DateTime LoadedDate;
            public int Version;
        }

        private class StockSignalCache
        {
            public DailyPriceCache PriceCache;
            public readonly List<KorovinSignalPoint> Points = new List<KorovinSignalPoint>();
            public readonly List<decimal> Values = new List<decimal>();
            public readonly Dictionary<DateTime, decimal> DividendsByDate =
                new Dictionary<DateTime, decimal>();
            public int MappedPriceCount;
            public int NextDividendIndex;
            public int DividendHistoryVersion;
        }

        private enum RebalanceSourceKind
        {
            SimpleTab,
            MarketIndex,
            StockUniverse
        }

        private class RebalanceSourceBlocker
        {
            public RebalanceSourceKind Kind;
            public BotTabSimple Tab;
            public long Version;
        }

        private static RuntimeStock FindStock(List<RuntimeStock> stocks, string ticker)
        {
            for (int index = 0; index < stocks.Count; index++)
            {
                if (string.Equals(stocks[index].Name, ticker, StringComparison.OrdinalIgnoreCase))
                {
                    return stocks[index];
                }
            }

            return null;
        }

        #endregion
    }
}
