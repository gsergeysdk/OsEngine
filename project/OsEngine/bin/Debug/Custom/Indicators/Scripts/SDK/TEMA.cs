using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class TEMA : Aindicator
    {
        private IndicatorParameterInt length;
        private IndicatorDataSeries _series;
        private IndicatorDataSeries _seriesEma1;
        private IndicatorDataSeries _seriesEma2;
        private IndicatorDataSeries _seriesEma3;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                length = CreateParameterInt("Period", 20);

                _series = CreateSeries("Values", Color.Red, IndicatorChartPaintType.Line, true);
                _seriesEma1 = CreateSeries("Ema1", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEma2 = CreateSeries("Ema2", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEma3 = CreateSeries("Ema3", Color.DarkRed, IndicatorChartPaintType.Line, false);
            }
        }

        public override void OnProcess(List<Candle> candles, int index)
        {
            // TEMA(i) = 3*EMA_1(price, length) - 3*EMA_2(EMA_1(price, length), length) + EMA_3(EMA_2(EMA_1(price, length), length), length)
            decimal price = candles[index].Close;
            ProcessEma(_seriesEma1, index, price, length);
            ProcessEma(_seriesEma2, index, _seriesEma1.Values[index], length);
            ProcessEma(_seriesEma3, index, _seriesEma2.Values[index], length);
            _series.Values[index] = 3.0m * _seriesEma1.Values[index] - 3.0m * _seriesEma2.Values[index] + _seriesEma3.Values[index];
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