using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class TrandPhaseTema : Aindicator
    {
        private IndicatorParameterInt _length_s;
        private IndicatorParameterInt _length_b;
        private IndicatorParameterInt _trand_counter;
        private IndicatorDataSeries _seriesL;
        private IndicatorDataSeries _seriesS;
        private IndicatorDataSeries _seriesLongCount;
        private IndicatorDataSeries _seriesShortCount;
        private Aindicator _ma_s;
        private Aindicator _ma_b;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                _length_s = CreateParameterInt("Period fast", 900);
                _length_b = CreateParameterInt("Period slow", 1500);
                _trand_counter = CreateParameterInt("Trand counter", 100);

                _seriesL = CreateSeries("ValuesLong", Color.Green, IndicatorChartPaintType.Column, true);
                _seriesS = CreateSeries("ValuesShort", Color.Red, IndicatorChartPaintType.Column, true);
                _seriesLongCount = CreateSeries("Longs", Color.DarkGreen, IndicatorChartPaintType.Line, false);
                _seriesShortCount = CreateSeries("Shorts", Color.DarkRed, IndicatorChartPaintType.Line, false);

                _ma_s = IndicatorsFactory.CreateIndicatorByName("TEMA", Name + "Ema", false);
                ((IndicatorParameterInt)_ma_s.Parameters[0]).Bind(_length_s);
                ProcessIndicator("TEMA_S", _ma_s);
                _ma_b = IndicatorsFactory.CreateIndicatorByName("TEMA", Name + "Ema", false);
                ((IndicatorParameterInt)_ma_b.Parameters[0]).Bind(_length_b);
                ProcessIndicator("TEMA_B", _ma_b);
            }
        }

        public override void OnProcess(List<Candle> candles, int index)
        {
            if (index == 0 /*< _length_s.ValueInt || index < _length_b.ValueInt*/)
            {
                _seriesL.Values[index] = 0m;
                _seriesS.Values[index] = 0m;
                _seriesLongCount.Values[index] = 0m;
                _seriesShortCount.Values[index] = 0m;
                return;
            }

            decimal currVal = candles[index].Close;
            bool upS = currVal > _ma_s.DataSeries[0].Values[index];
            bool upB = currVal > _ma_b.DataSeries[0].Values[index];
            bool downS = currVal < _ma_s.DataSeries[0].Values[index];
            bool downB = currVal < _ma_b.DataSeries[0].Values[index];

            if (upS && upB)
            {
                _seriesShortCount.Values[index] = 0m;
                _seriesLongCount.Values[index] = _seriesLongCount.Values[index - 1] + 1m;
            }
            else if (downS && downB)
            {
                _seriesLongCount.Values[index] = 0m;
                _seriesShortCount.Values[index] = _seriesShortCount.Values[index - 1] + 1m;
            }
            else
            {
                _seriesLongCount.Values[index] = 0m;
                _seriesShortCount.Values[index] = 0m;
            }

            _seriesL.Values[index] = _seriesLongCount.Values[index] > _trand_counter.ValueInt ? 1.0m : 0m;
            _seriesS.Values[index] = _seriesShortCount.Values[index] > _trand_counter.ValueInt ? 1.0m : 0m;

        }
    }
}