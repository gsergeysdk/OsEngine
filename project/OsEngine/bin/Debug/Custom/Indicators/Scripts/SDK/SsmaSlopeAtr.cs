using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class SsmaSlopeAtr : Aindicator
    {
        private IndicatorParameterInt _lengthSsma;
        private IndicatorParameterInt _lengthAtr;
        private IndicatorParameterInt _slopeCandles;
        private IndicatorParameterDecimal _upLine;
        private IndicatorParameterDecimal _downLine;
        private IndicatorDataSeries _series;
        private IndicatorDataSeries _seriesUp;
        private IndicatorDataSeries _seriesDown;
        private IndicatorDataSeries _seriesZero;
        private Aindicator _ssma;
        private Aindicator _atr;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                _lengthSsma = CreateParameterInt("Period Ssma", 100);
                _lengthAtr = CreateParameterInt("Period Atr", 14);
                _slopeCandles = CreateParameterInt("Slope candles", 10);

                _upLine = CreateParameterDecimal("Up line", 1m);
                _downLine = CreateParameterDecimal("Down line", -1m);

                _series = CreateSeries("Values", Color.DodgerBlue, IndicatorChartPaintType.Line, true);
                _seriesUp = CreateSeries("Up line", Color.Green, IndicatorChartPaintType.Line, true);
                _seriesDown = CreateSeries("Down line", Color.Red, IndicatorChartPaintType.Line, true);
                _seriesZero = CreateSeries("Zero line", Color.Gray, IndicatorChartPaintType.Line, true);

                _ssma = IndicatorsFactory.CreateIndicatorByName("Ssma", Name + "Ssma", false);
                ((IndicatorParameterInt)_ssma.Parameters[0]).Bind(_lengthSsma);
                ProcessIndicator("Ssma", _ssma);

                _atr = IndicatorsFactory.CreateIndicatorByName("ATR", Name + "Atr", false);
                ((IndicatorParameterInt)_atr.Parameters[0]).Bind(_lengthAtr);
                ((IndicatorParameterString)_atr.Parameters[1]).ValueString = "Absolute";
                ProcessIndicator("Atr", _atr);
            }
        }

        public override void OnProcess(List<Candle> candles, int index)
        {
            _seriesUp.Values[index] = _upLine.ValueDecimal;
            _seriesDown.Values[index] = _downLine.ValueDecimal;
            _seriesZero.Values[index] = 0m;

            // S(t) = (SSMA(t) - SSMA(t - k)) / sum(ATR(t - j), j = 0 .. k - 1) - средний наклон Ssma за свечу
            // в долях ATR. Ssma - сглаживание Уайлдера: SSMA(t) - SSMA(t - 1) = (Close(t) - SSMA(t - 1)) / N,
            // поэтому знак S - знак суммы (Close - SSMA) за k свечей (цена выше / ниже Ssma), а значение -
            // среднее расстояние от цены до Ssma в ATR, делённое на N. На дневках крипты обычно ±0.1
            int k = _slopeCandles.ValueInt;

            // Дожидаемся готовности обеих точек Ssma и всех k значений ATR
            if (k <= 0
                || index - k < _lengthSsma.ValueInt
                || index - k + 1 < _lengthAtr.ValueInt - 1)
            {
                _series.Values[index] = 0m;
                return;
            }

            decimal atrSum = 0m;

            for (int i = 0; i < k; i++)
            {
                atrSum += _atr.DataSeries[0].Values[index - i];
            }

            if (atrSum <= 0)
            {
                _series.Values[index] = 0m;
                return;
            }

            decimal ssmaNow = _ssma.DataSeries[0].Values[index];
            decimal ssmaPrev = _ssma.DataSeries[0].Values[index - k];

            _series.Values[index] = Math.Round((ssmaNow - ssmaPrev) / atrSum, 8);
        }
    }
}
