using OsEngine.Charts.CandleChart.Indicators;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Robots.VolatilityStageRotationSamples;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OsEngine.Robots
{
    public class SDKRsiFilter
    {
        private BotPanel panel;
        private BotTabScreener tabScreener;

        public StrategyParameterBool UseRsiFilter;
        public StrategyParameterInt RsiLen;
        public StrategyParameterInt MaxSecuritiesToTrade;
        public StrategyParameterInt TopVolumeSecurities;
        public StrategyParameterInt TopVolumeDaysLookBack;
        public StrategyParameterString SecuritiesToTrade;

        public int rsiIndx;

        public SDKRsiFilter(BotPanel panel, ref int indicatorIndex)
        {
            this.panel = panel;
            Init(ref indicatorIndex);
        }
        public void Init(ref int indicatorIndex)
        {
            UseRsiFilter = panel.CreateParameter("Use Rsi Filter", false, "Rsi Filter");
            RsiLen = panel.CreateParameter("Rsi length", 25, 0, 20, 1, "Rsi Filter");
            TopVolumeSecurities = panel.CreateParameter("Top volume securities", 15, 0, 20, 1, "Rsi Filter");
            TopVolumeDaysLookBack = panel.CreateParameter("Top volume days look back", 3, 0, 20, 1, "Rsi Filter");
            MaxSecuritiesToTrade = panel.CreateParameter("Max securities to trade", 5, 0, 20, 1, "Rsi Filter");
            SecuritiesToTrade = panel.CreateParameter("Securities to trade", "", "Rsi Filter");
            StrategyParameterButton button = panel.CreateParameterButton("Check securities rating", "Rsi Filter");
            button.UserClickOnButtonEvent += Button_UserClickOnButtonEvent;

            tabScreener = panel.TabsScreener[0];
            if (tabScreener != null)
            {
                rsiIndx = indicatorIndex;
                tabScreener.CreateCandleIndicator(++indicatorIndex,
                    "RSI", new List<string>() { RsiLen.ValueInt.ToString() }, "RSI Area");
            }
        }

        public void Restart()
        {
            SecuritiesToTrade.ValueString = "";
            _lastTimeRating = DateTime.MinValue;
        }

        public Aindicator getRsiIndicator(BotTabSimple tab)
        {
            Aindicator rsi = (Aindicator)tab.Indicators[rsiIndx];
            if (rsi.ParametersDigit[0].Value != RsiLen.ValueInt)
            {
                rsi.ParametersDigit[0].Value = RsiLen.ValueInt;
                rsi.Save();
                rsi.Reload();
            }
            return rsi;
        }

        DateTime _lastTimeRating = DateTime.MinValue;

        private void Button_UserClickOnButtonEvent()
        {
            _lastTimeRating = DateTime.MinValue;
            CheckSecuritiesRating();
        }

        public void CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            CheckSecuritiesRating();
        }

        public bool filterSecurityToTrade(string name)
        {
            return !UseRsiFilter.ValueBool || SecuritiesToTrade.ValueString.Contains(name);
        }



        private void CheckSecuritiesRating()
        {
            if (!UseRsiFilter.ValueBool
                || tabScreener == null
                || tabScreener.Tabs == null
                || tabScreener.Tabs.Count == 0
                || tabScreener.Tabs[0].IsConnected == false)
            {
                return;
            }

            DateTime currentTime = tabScreener.Tabs[0].TimeServerCurrent;

            if (currentTime.Date == _lastTimeRating.Date)
            {
                return;
            }
            _lastTimeRating = tabScreener.Tabs[0].TimeServerCurrent;

            List<SecurityRatingData> securityRatingData = new List<SecurityRatingData>();

            List<BotTabSimple> tabs = tabScreener.Tabs;

            for (int i = 0; i < tabs.Count; i++)
            {
                SecurityRatingData newData = new SecurityRatingData();
                newData.SecurityName = tabs[i].Security.Name;
                newData.Volume = CalculateVolume(TopVolumeDaysLookBack.ValueInt, tabs[i]);
                newData.Rsi = GetRsi(tabs[i]);

                if (newData.Volume == 0
                    || newData.Rsi == 0)
                {
                    continue;
                }

                securityRatingData.Add(newData);
            }

            if (securityRatingData.Count == 0)
            {
                return;
            }

            for (int i = 0; i < securityRatingData.Count; i++)
            {
                for (int j = 1; j < securityRatingData.Count; j++)
                {
                    if (securityRatingData[j - 1].Volume < securityRatingData[j].Volume)
                    {
                        SecurityRatingData d = securityRatingData[j - 1];
                        securityRatingData[j - 1] = securityRatingData[j];
                        securityRatingData[j] = d;
                    }
                }
            }

            securityRatingData = securityRatingData.GetRange(0, TopVolumeSecurities.ValueInt);

            for (int i = 0; i < securityRatingData.Count; i++)
            {
                for (int j = 1; j < securityRatingData.Count; j++)
                {
                    if (securityRatingData[j - 1].Rsi < securityRatingData[j].Rsi)
                    {
                        SecurityRatingData d = securityRatingData[j - 1];
                        securityRatingData[j - 1] = securityRatingData[j];
                        securityRatingData[j] = d;
                    }
                }
            }

            securityRatingData = securityRatingData.GetRange(0, MaxSecuritiesToTrade.ValueInt);

            string securitiesInTrade = "";

            for (int i = 0; i < securityRatingData.Count; i++)
            {
                securitiesInTrade += securityRatingData[i].SecurityName + " ";
            }

            SecuritiesToTrade.ValueString = securitiesInTrade;
        }

        public decimal CalculateVolume(int daysCount, BotTabSimple tab)
        {
            List<Candle> candles = tab.CandlesAll;

            if (candles == null
                || candles.Count == 0)
            {
                return 0;
            }

            int curDay = candles[candles.Count - 1].TimeStart.Day;
            int curDaysCount = 1;
            decimal volume = 0;

            for (int i = candles.Count - 1; i > 0; i--)
            {
                Candle curCandle = candles[i];
                volume += curCandle.Volume * curCandle.Open;

                if (curDay != curCandle.TimeStart.Day)
                {
                    curDay = candles[i].TimeStart.Day;
                    curDaysCount++;

                    if (daysCount == curDaysCount)
                    {
                        break;
                    }
                }
            }

            if (tab.Security.Lot > 1)
            {
                volume = volume * tab.Security.Lot;
            }

            return volume;
        }

        private decimal GetRsi(BotTabSimple tab)
        {
            Aindicator rsi = getRsiIndicator(tab);

            if (rsi.DataSeries[0].Values.Count == 0 ||
                rsi.DataSeries[0].Last == 0)
            {
                return 0;
            }

            decimal rsiValue = rsi.DataSeries[0].Last;

            return rsiValue;
        }

    }
}
