using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class TSI : Aindicator
    {
        private IndicatorParameterInt _fastlength;
        private IndicatorParameterInt _slowlength;
        private IndicatorDataSeries _series;
        private IndicatorDataSeries _seriesEma1;
        private IndicatorDataSeries _seriesEma2;
        private IndicatorDataSeries _seriesEma3;
        private IndicatorDataSeries _seriesEma4;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                _fastlength = CreateParameterInt("Period fast", 20);
                _slowlength = CreateParameterInt("Period slow", 100);

                _series = CreateSeries("Values", Color.OrangeRed, IndicatorChartPaintType.Column, true);
                _seriesEma1 = CreateSeries("Ema1", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEma2 = CreateSeries("Ema2", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEma3 = CreateSeries("Ema3", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEma4 = CreateSeries("Ema4", Color.DarkRed, IndicatorChartPaintType.Line, false);
            }
        }

        public override void OnProcess(List<Candle> candles, int index)
        {
            //((Fast MA – Slow MA) / |Slow MA|) * 100
            //_series.Values[index] = ((_fastsma.DataSeries[0].Values[index] - _slowsma.DataSeries[0].Values[index]) / Math.Abs(_slowsma.DataSeries[0].Values[index])) * 100.0m;

            // 100*EMA_1(EMA_2(m, fl), sl)/EMA_3(EMA_4(|m|, fl), sl)
            decimal momentum = index > 0 ? candles[index].Close - candles[index - 1].Close : 0m;
            ProcessEma(_seriesEma2, index, momentum, _fastlength);
            ProcessEma(_seriesEma4, index, Math.Abs(momentum), _fastlength);
            ProcessEma(_seriesEma1, index, _seriesEma2.Values[index], _slowlength);
            ProcessEma(_seriesEma3, index, _seriesEma4.Values[index], _slowlength);
            if (_seriesEma3.Values[index] != 0m)
                _series.Values[index] = 100.0m * _seriesEma1.Values[index] / _seriesEma3.Values[index];
        }

        private void ProcessEma(IndicatorDataSeries _series, int index, decimal val, IndicatorParameterInt length)
        {
            decimal result = 0;

            if (index == 0)
            {
                result = val;
            }
            else
            {
                decimal a = Math.Round(2.0m / (length.ValueInt + 1), 8);
                decimal emaLast = _series.Values[index - 1];
                result = emaLast + (a * (val - emaLast));
            }

            _series.Values[index] = Math.Round(result, 8);
        }

    }
}