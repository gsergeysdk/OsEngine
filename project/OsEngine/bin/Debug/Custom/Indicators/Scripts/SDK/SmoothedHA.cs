using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;

namespace CustomIndicators.Scripts
{
    public class SmoothedHA : Aindicator
    {
        private IndicatorParameterInt length;
        private IndicatorParameterInt length2;
        private IndicatorDataSeries _series;
        private IndicatorDataSeries _series2;
        private IndicatorDataSeries _seriesEmaO;
        private IndicatorDataSeries _seriesEmaC;
        private IndicatorDataSeries _seriesEmaH;
        private IndicatorDataSeries _seriesEmaL;
        private IndicatorDataSeries _seriesEmaO2;
        private IndicatorDataSeries _seriesEmaC2;
        private IndicatorDataSeries _seriesHaOpen;
        private IndicatorDataSeries _seriesHaClose;

        public override void OnStateChange(IndicatorState state)
        {
            if (state == IndicatorState.Configure)
            {
                length = CreateParameterInt("Period", 20);
                length2 = CreateParameterInt("Period2", 20);

                _series = CreateSeries("ValuesUp", Color.Green, IndicatorChartPaintType.Column, true);
                _series2 = CreateSeries("ValuesDown", Color.Red, IndicatorChartPaintType.Column, true);
                _seriesEmaO = CreateSeries("EmaO", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEmaC = CreateSeries("EmaC", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEmaH = CreateSeries("EmaH", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEmaL = CreateSeries("EmaL", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEmaO2 = CreateSeries("EmaO2", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesEmaC2 = CreateSeries("EmaC2", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesHaOpen = CreateSeries("HaOpen", Color.DarkRed, IndicatorChartPaintType.Line, false);
                _seriesHaClose = CreateSeries("HaClose", Color.DarkRed, IndicatorChartPaintType.Line, false);
            }
        }

        public override void OnProcess(List<Candle> candles, int index)
        {
            //decimal price = candles[index].Close;
            decimal o = ProcessEma(_seriesEmaO, index, candles[index].Open, length);
            decimal c = ProcessEma(_seriesEmaC, index, candles[index].Close, length);
            decimal h = ProcessEma(_seriesEmaH, index, candles[index].High, length);
            decimal l = ProcessEma(_seriesEmaL, index, candles[index].Low, length);
            _seriesHaClose.Values[index] = (o + c + h + l) / 4.0m;
            _seriesHaOpen.Values[index] = index == 0 ? (o + c) / 2.0m : (_seriesHaOpen.Values[index - 1] + _seriesHaClose.Values[index - 1]) / 2.0m;
            ProcessEma(_seriesEmaO2, index, _seriesHaOpen.Values[index], length2);
            ProcessEma(_seriesEmaC2, index, _seriesHaClose.Values[index], length2);
            decimal val = _seriesEmaC2.Values[index] - _seriesEmaO2.Values[index];
            _series.Values[index] = val > 0m ? val : 0m;
            _series2.Values[index] = val < 0m ? val : 0m;
        }

        private decimal ProcessEma(IndicatorDataSeries _series, int index, decimal val, IndicatorParameterInt length)
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
            return _series.Values[index];
        }

    }
}