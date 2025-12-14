using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Language;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

/* Description
Trading robot for osEngine

The trend robot-screener on LinearRegression channel and Volatility group.

Buy:
1. The candle closed above the upper line of the Linear Regression Channel
2. Filter by volatility groups. All screener papers are divided into 3 groups. One of them is traded.

Exit for long: When the Linear Regression Channel bottom line is broken

*/

namespace OsEngine.Robots
{
    [Bot("SDKLinearRegression")]
    public class SDKLinearRegression : BotPanel
    {
        private BotTabScreener _screenerTab;

        // Basic settings
        private StrategyParameterString _regime;
        private StrategyParameterInt _icebergCount;
        private StrategyParameterInt _maxPositionsCount;
        private StrategyParameterInt _clusterToTrade;
        private StrategyParameterInt _clustersLookBack;
        private StrategyParameterInt _clustersMaxPercent;
       
        // Indicator settings
        private StrategyParameterInt _lrLength;
        private StrategyParameterDecimal _lrDeviation;
        private StrategyParameterBool _smaFilterIsOn;
        private StrategyParameterInt _smaFilterLen;

        // Volatility clusters
        private VolatilityStageClusters _volatilityStageClusters = new VolatilityStageClusters();
        private DateTime _lastTimeSetClusters;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        public SDKVolume volume;

        public SDKLinearRegression(string name, StartProgram startProgram) : base(name, startProgram)
        {

            // non trade periods
            _tradePeriodsSettings = new NonTradePeriods(name);

            _tradePeriodsSettings.NonTradePeriodGeneral.NonTradePeriod1Start = new TimeOfDay() { Hour = 5, Minute = 0 };
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
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On"});
            _icebergCount = CreateParameter("Iceberg orders count", 1, 1, 3, 1);
            _clusterToTrade = CreateParameter("Volatility cluster to trade", 1, 1, 3, 1);
            _clustersLookBack = CreateParameter("Volatility cluster lookBack", 30, 10, 300, 1);
            _clustersMaxPercent = CreateParameter("Volatility cluster maxPercent", 10, 0, 20, 1);
            _maxPositionsCount = CreateParameter("Max positions ", 10, 1, 50, 4);
            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            // Indicator settings
            _smaFilterIsOn = CreateParameter("Sma filter is on", true);
            _smaFilterLen = CreateParameter("Sma filter Len", 170, 100, 300, 10);
            _lrLength = CreateParameter("Linear regression Length", 180, 20, 300, 10);
            _lrDeviation = CreateParameter("Linear regression deviation", 2.4m, 1, 4, 0.1m);


            // Create indicator LinearRegressionChannelFast_Indicator
            _screenerTab.CreateCandleIndicator(1, "LinearRegressionChannelFast_Indicator", new List<string>() { _lrLength.ValueInt.ToString(), "Close", _lrDeviation.ValueDecimal.ToString(), _lrDeviation.ValueDecimal.ToString() }, "Prime");

            // Create indicator Sma
            _screenerTab.CreateCandleIndicator(2, "Sma", new List<string>() { _smaFilterLen.ValueInt.ToString(), "Close" }, "Prime");

            // Subscribe to the indicator update event
            ParametrsChangeByUser += SmaScreener_ParametrsChangeByUser;

            Description = OsLocalization.Description.DescriptionLabel324;
            DeleteEvent += AlgoStart1ScreenerLinearRegression_DeleteEvent;

            volume = new SDKVolume(this);
        }

        private void AlgoStart1ScreenerLinearRegression_DeleteEvent()
        {
            try
            {
                _tradePeriodsSettings.Delete();
            }
            catch (Exception)
            {
                // ignore
            }
        }

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
        }

        private void SmaScreener_ParametrsChangeByUser()
        {
            _screenerTab._indicators[0].Parameters
              = new List<string>()
             {
                 _lrLength.ValueInt.ToString(),
                 "Close",
                 _lrDeviation.ValueDecimal.ToString(),
                 _lrDeviation.ValueDecimal.ToString()
             };

            _screenerTab._indicators[1].Parameters
                = new List<string>() { _smaFilterLen.ValueInt.ToString(), "Close" };

            _screenerTab.UpdateIndicatorsParameters();
        }

        // Logic
        private void recalculateClusters(DateTime currTime)
        {

            if (_lastTimeSetClusters == DateTime.MinValue
             || _lastTimeSetClusters != currTime)
            {
                _lastTimeSetClusters = currTime;
                if (_clustersMaxPercent == 0)
                    _volatilityStageClusters.Calculate(_screenerTab.Tabs, _clustersLookBack.ValueInt);
               
                // перестраиваем кластеры по волатильности от максимальных процентов
                if (_clustersMaxPercent > 0)
                {
                    _volatilityStageClusters.ClusterOne.Clear();
                    _volatilityStageClusters.ClusterTwo.Clear();
                    _volatilityStageClusters.ClusterThree.Clear();

                    List<SourceVolatility> sourcesWithCandles = new List<SourceVolatility>();

                    for (int i = 0; i < _screenerTab.Tabs.Count; i++)
                    {
                        List<Candle> candles = _screenerTab.Tabs[i].CandlesFinishedOnly;

                        if (candles == null
                            || candles.Count == 0)
                        {
                            continue;
                        }

                        SourceVolatility newVola = new SourceVolatility();
                        newVola.Tab = _screenerTab.Tabs[i];
                        newVola.Candles = _screenerTab.Tabs[i].CandlesAll;
                        newVola.Calculate(_clustersLookBack.ValueInt);

                        sourcesWithCandles.Add(newVola);
                    }

                    if (sourcesWithCandles.Count <= 1)
                    {
                        return;
                    }

                    sourcesWithCandles = sourcesWithCandles.OrderBy(x => x.Volatility).ToList();

                    decimal clusterOneVolatility = _volatilityStageClusters.ClusterOnePercent * _clustersMaxPercent / 100;
                    decimal clusterTwoVolatility = (_volatilityStageClusters.ClusterOnePercent + _volatilityStageClusters.ClusterTwoPercent) *
                        _clustersMaxPercent / 100;

                    for (int i = 0; i < sourcesWithCandles.Count; i++)
                    {
                        if (sourcesWithCandles[i].Volatility <= clusterOneVolatility)
                        {
                            _volatilityStageClusters.ClusterOne.Add(sourcesWithCandles[i].Tab);
                        }
                        else if (sourcesWithCandles[i].Volatility <= clusterTwoVolatility)
                        {
                            _volatilityStageClusters.ClusterTwo.Add(sourcesWithCandles[i].Tab);
                        }
                        else
                        {
                            _volatilityStageClusters.ClusterThree.Add(sourcesWithCandles[i].Tab);
                        }
                    }

                }
            }
        }

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
                recalculateClusters(candles[^1].TimeStart);

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

            if (positions.Count == 0)
            { // Opening logic

                if (_screenerTab.PositionsOpenAll.Count >= _maxPositionsCount.ValueInt)
                {
                    return;
                }

                decimal candleClose = candles[candles.Count - 1].Close;

                Aindicator lrIndicator = (Aindicator)tab.Indicators[0];

                decimal lrUp = lrIndicator.DataSeries[0].Values[^1];
                decimal lrDown = lrIndicator.DataSeries[2].Values[^1];

                if (lrUp == 0
                    || lrDown == 0)
                {
                    return;
                }

                if (_smaFilterIsOn.ValueBool == true)
                {// Sma filter
                    Aindicator sma = (Aindicator)tab.Indicators[1];

                    decimal lastSma = sma.DataSeries[0].Values[^1];

                    if (candleClose < lastSma)
                    {
                        return;
                    }
                }

                if (candleClose > lrUp)
                {
                    tab.BuyAtIcebergMarket(volume.GetVolume(tab), _icebergCount.ValueInt, 1000);
                }
            }
            else // Logic close position
            {
                Position pos = positions[0];

                if (pos.State != PositionStateType.Open)
                {
                    return;
                }

                Aindicator lrIndicator = (Aindicator)tab.Indicators[0];

                decimal lrDown = lrIndicator.DataSeries[2].Values[^2];
                decimal lrUp = lrIndicator.DataSeries[0].Values[^1];

                if (lrDown == 0)
                {
                    return;
                }

                decimal lastClose = candles[^1].Close;
                decimal lastClose2 = candles[^2].Close;

                if (lastClose <= lrDown && lastClose2 <= lrDown)
                {
                    tab.CloseAtIcebergMarket(pos, pos.OpenVolume, _icebergCount.ValueInt, 1000);
                }
            }
        }
    }
}