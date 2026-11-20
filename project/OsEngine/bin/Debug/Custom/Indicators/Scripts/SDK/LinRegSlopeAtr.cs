using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class LinRegSlopeAtr : Aindicator
    {
        private IndicatorParameterInt _lengthRegression;
        private IndicatorParameterInt _lengthAtr;
        private IndicatorParameterDecimal _upLine;
        private IndicatorParameterDecimal _downLine;
        private IndicatorDataSeries _series;
        private IndicatorDataSeries _seriesUp;
        private IndicatorDataSeries _seriesDown;
        private IndicatorDataSeries _seriesZero;
        private Aindicator _atr;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                _lengthRegression = CreateParameterInt("Period regression", 30);
                _lengthAtr = CreateParameterInt("Period Atr", 14);

                _upLine = CreateParameterDecimal("Up line", 0.1m);
                _downLine = CreateParameterDecimal("Down line", -0.1m);

                _series = CreateSeries("Values", Color.DodgerBlue, IndicatorChartPaintType.Line, true);
                _seriesUp = CreateSeries("Up line", Color.Green, IndicatorChartPaintType.Line, true);
                _seriesDown = CreateSeries("Down line", Color.Red, IndicatorChartPaintType.Line, true);
                _seriesZero = CreateSeries("Zero line", Color.Gray, IndicatorChartPaintType.Line, true);

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

            // NormSlope(t) = b / ATR(t), где b - наклон прямой y = b*x + a
            // по ценам закрытия последних N свечей (x = 0 .. N-1, от старой к новой)
            int n = _lengthRegression.ValueInt;

            if (n < 2
                || index < n - 1
                || index < _lengthAtr.ValueInt)
            {
                _series.Values[index] = 0m;
                return;
            }

            decimal atr = _atr.DataSeries[0].Values[index];

            if (atr <= 0)
            {
                _series.Values[index] = 0m;
                return;
            }

            decimal sumY = 0m;
            decimal sumXY = 0m;
            int start = index - n + 1;

            for (int i = 0; i < n; i++)
            {
                decimal y = candles[start + i].Close;
                sumY += y;
                sumXY += i * y;
            }

            decimal sumX = n * (n - 1) / 2m;
            decimal sumX2 = (n - 1) * n * (2m * n - 1) / 6m;

            decimal slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);

            _series.Values[index] = Math.Round(slope / atr, 8);
        }
    }
}
