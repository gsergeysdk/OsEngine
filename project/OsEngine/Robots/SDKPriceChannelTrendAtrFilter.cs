/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.Market.Servers;
using OsEngine.Market;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using OsEngine.Robots.Helpers;

namespace OsEngine.Robots.SDKRobots
{
    [Bot("SDKPriceChannelTrendAtrFilter")]
    public class SDKPriceChannelTrendAtrFilter : BotPanel
    {
        private BotTabSimple _tab;

        private Aindicator _pc;
        private Aindicator _atr;

        public StrategyParameterString Regime;
        public StrategyParameterInt PriceChannelLength;
        public StrategyParameterInt AtrLength;
        public StrategyParameterBool AtrFilterIsOn;
        public StrategyParameterDecimal AtrGrowPercent;
        public StrategyParameterInt AtrGrowLookBack;

        public StrategyParameterBool SmaFilterIsOn;
        public StrategyParameterInt SmaFilterLen;

        public SDKVolume volume;

        public SDKPriceChannelTrendAtrFilter(string name, StartProgram startProgram)
            : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort", "OnlyClosePosition" });

            PriceChannelLength = CreateParameter("Price channel length", 50, 10, 80, 3);
            AtrLength = CreateParameter("Atr length", 25, 10, 80, 3);

            AtrFilterIsOn = CreateParameter("Atr filter is on", false);
            AtrGrowPercent = CreateParameter("Atr grow percent", 3, 1.0m, 50, 4);
            AtrGrowLookBack = CreateParameter("Atr grow look back", 20, 1, 50, 4);

            SmaFilterIsOn = CreateParameter("Sma filter is on", true);
            SmaFilterLen = CreateParameter("Sma filter Len", 100, 100, 300, 10);

            _pc = IndicatorsFactory.CreateIndicatorByName("PriceChannel", name + "PriceChannel", false);
            _pc = (Aindicator)_tab.CreateCandleIndicator(_pc, "Prime");
            _pc.ParametersDigit[0].Value = PriceChannelLength.ValueInt;
            _pc.ParametersDigit[1].Value = PriceChannelLength.ValueInt;
            _pc.Save();

            _atr = IndicatorsFactory.CreateIndicatorByName("ATR", name + "Atr", false);
            _atr = (Aindicator)_tab.CreateCandleIndicator(_atr, "AtrArea");
            _atr.ParametersDigit[0].Value = AtrLength.ValueInt;

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;

            volume = new SDKVolume(this);

            ParametrsChangeByUser += Event_ParametrsChangeByUser;
        }

        void Event_ParametrsChangeByUser()
        {
            if (PriceChannelLength.ValueInt != _pc.ParametersDigit[0].Value ||
                PriceChannelLength.ValueInt != _pc.ParametersDigit[1].Value)
            {
                _pc.ParametersDigit[0].Value = PriceChannelLength.ValueInt;
                _pc.ParametersDigit[1].Value = PriceChannelLength.ValueInt;
                _pc.Reload();
                _pc.Save();
            }

            if(_atr.ParametersDigit[0].Value != AtrLength.ValueInt)
            {
                _atr.ParametersDigit[0].Value = AtrLength.ValueInt;
                _atr.Reload();
                _atr.Save();
            }
        }

        public override string GetNameStrategyType()
        {
            return "SDKPriceChannelTrendAtrFilter";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        // logic

        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }

            if (_pc.DataSeries[0].Values == null 
                || _pc.DataSeries[1].Values == null)
            {
                return;
            }

            if (_pc.DataSeries[0].Values.Count < _pc.ParametersDigit[0].Value + 2 
                || _pc.DataSeries[1].Values.Count < _pc.ParametersDigit[1].Value + 2)
            {
                return;
            }

            List<Position> openPositions = _tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
            {
                if (Regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }
                LogicOpenPosition(candles);
            }
            else
            {
                LogicClosePosition(candles, openPositions[0]);
            }
        }

        private void LogicOpenPosition(List<Candle> candles)
        {
            decimal lastPrice = candles[candles.Count - 1].Close;
            decimal lastPcUp = _pc.DataSeries[0].Values[_pc.DataSeries[0].Values.Count - 2];
            decimal lastPcDown = _pc.DataSeries[1].Values[_pc.DataSeries[1].Values.Count - 2];

            if(lastPcUp == 0 
                || lastPcDown == 0)
            {
                return;
            }

            if (lastPrice > lastPcUp
                && Regime.ValueString != "OnlyShort")
            {
                if (AtrFilterIsOn.ValueBool == true)
                {
                    if (_atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt <= 0)
                    {
                        return;
                    }

                    decimal atrLast = _atr.DataSeries[0].Values[_atr.DataSeries[0].Values.Count - 1];
                    decimal atrLookBack =
                        _atr.DataSeries[0].Values[_atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt];

                    if (atrLast == 0
                        || atrLookBack == 0)
                    {
                        return;
                    }

                    decimal atrGrowPercent = atrLast / (atrLookBack / 100) - 100;

                    if (atrGrowPercent < AtrGrowPercent.ValueDecimal)
                    {
                        return;
                    }
                }

                if (SmaFilterIsOn.ValueBool == true)
                {
                    decimal smaValue = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 1);
                    decimal smaPrev = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 2);
                    if (smaValue < smaPrev)
                        return;
                }

                _tab.BuyAtMarket(volume.GetVolume(_tab));
            }
            if (lastPrice < lastPcDown
                && Regime.ValueString != "OnlyLong")
            {
                if (AtrFilterIsOn.ValueBool == true)
                {
                    if (_atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt <= 0)
                    {
                        return;
                    }

                    decimal atrLast = _atr.DataSeries[0].Values[_atr.DataSeries[0].Values.Count - 1];
                    decimal atrLookBack =
                        _atr.DataSeries[0].Values[_atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt];

                    if (atrLast == 0
                        || atrLookBack == 0)
                    {
                        return;
                    }

                    decimal atrGrowPercent = atrLast / (atrLookBack / 100) - 100;

                    if (atrGrowPercent < AtrGrowPercent.ValueDecimal)
                    {
                        return;
                    }
                }

                if (SmaFilterIsOn.ValueBool == true)
                {
                    decimal smaValue = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 1);
                    decimal smaPrev = Sma(candles, SmaFilterLen.ValueInt, candles.Count - 2);
                    if (smaValue > smaPrev)
                        return;
                }

                _tab.SellAtMarket(volume.GetVolume(_tab));
            }
        }

        private void LogicClosePosition(List<Candle> candles, Position position)
        {
            decimal lastPcUp = _pc.DataSeries[0].Values[_pc.DataSeries[0].Values.Count - 1];
            decimal lastPcDown = _pc.DataSeries[1].Values[_pc.DataSeries[1].Values.Count - 1];

            if(position.Direction == Side.Buy)
            {
                _tab.CloseAtTrailingStopMarket(position, lastPcDown);
            }
            if (position.Direction == Side.Sell)
            {
                _tab.CloseAtTrailingStopMarket(position, lastPcUp);
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
}