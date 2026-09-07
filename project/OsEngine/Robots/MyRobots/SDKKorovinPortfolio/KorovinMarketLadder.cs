/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Total return ряд индекса: ценовой ряд, из которого убраны дивидендные гэпы.
    /// Без этого робот принимает отсечки за просадку рынка: корзина российских акций
    /// теряет на гэпах 8-12 процентов в год.
    /// </summary>
    public class KorovinTotalReturnIndex
    {
        #region Settings

        /// <summary>
        /// Убирать дивидендные гэпы из ряда
        /// </summary>
        public bool DividendAdjust = true;

        /// <summary>
        /// Ряд пересобран по новым правилам: накопленное состояние лестницы к нему не относится.
        /// Флаг снимает тот, кто его обработал
        /// </summary>
        public bool SeriesRebuilt;

        private List<decimal> _formulaWeights = new List<decimal>();

        private List<bool> _formulaHasIndex = new List<bool>();

        private string _buildSignature = "";

        private string _basketSignature = "";

        /// <summary>
        /// Разобрать формулу индекса и запомнить множители бумаг. В автоформуле
        /// равного веса множители сильно различаются: (A0*6.86)+(A1*50.28)+...
        /// Без них дивидендный гэп корзины считался бы взвешенным по ценам,
        /// то есть с перекосом в сторону дорогих бумаг
        /// </summary>
        public void SetFormula(string formula)
        {
            _formulaWeights = new List<decimal>();
            _formulaHasIndex = new List<bool>();

            if (string.IsNullOrWhiteSpace(formula))
            {
                return;
            }

            for (int i = 0; i < formula.Length; i++)
            {
                if (formula[i] != 'A')
                {
                    continue;
                }

                int numberStart = i + 1;
                int numberEnd = numberStart;

                while (numberEnd < formula.Length && char.IsDigit(formula[numberEnd]))
                {
                    numberEnd++;
                }

                if (numberEnd == numberStart)
                {
                    continue;
                }

                int securityIndex;

                if (int.TryParse(formula.Substring(numberStart, numberEnd - numberStart),
                    out securityIndex) == false)
                {
                    continue;
                }

                decimal weight = 1m;

                int position = numberEnd;

                while (position < formula.Length && formula[position] == ' ')
                {
                    position++;
                }

                if (position < formula.Length && formula[position] == '*')
                {
                    position++;

                    while (position < formula.Length && formula[position] == ' ')
                    {
                        position++;
                    }

                    int valueStart = position;

                    while (position < formula.Length
                        && (char.IsDigit(formula[position]) || formula[position] == '.' || formula[position] == ','))
                    {
                        position++;
                    }

                    string value = formula.Substring(valueStart, position - valueStart).Replace(',', '.');

                    decimal parsed;

                    if (decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed)
                        && parsed > 0)
                    {
                        weight = parsed;
                    }
                }

                while (_formulaWeights.Count <= securityIndex)
                {
                    _formulaWeights.Add(1m);
                    _formulaHasIndex.Add(false);
                }

                _formulaWeights[securityIndex] = weight;
                _formulaHasIndex[securityIndex] = true;

                i = position - 1;
            }
        }

        private decimal GetFormulaWeight(int index)
        {
            if (index < 0
                || index >= _formulaWeights.Count)
            {
                return 1m;
            }

            return _formulaWeights[index];
        }

        /// <summary>
        /// Формула не разобрана: вкладка ещё не настроена либо в строке нет номеров A{n}
        /// </summary>
        public bool FormulaIsEmpty
        {
            get { return _formulaHasIndex.Count == 0; }
        }

        /// <summary>
        /// Есть ли бумага с таким номером в формуле. По самому весу этого не понять:
        /// у ценового взвешивания настоящий множитель тоже равен единице,
        /// и пропущенная бумага получает единицу по умолчанию
        /// </summary>
        public bool HasFormulaIndex(int index)
        {
            if (index < 0
                || index >= _formulaHasIndex.Count)
            {
                return false;
            }

            return _formulaHasIndex[index];
        }

        #endregion

        #region Data

        /// <summary>
        /// Даты завершённых дней: сохранённая история плюс то, что построено по свечам
        /// </summary>
        public List<DateTime> Dates = new List<DateTime>();

        /// <summary>
        /// Значения ряда на завершённых днях
        /// </summary>
        public List<decimal> Values = new List<decimal>();

        private List<DateTime> _liveDates = new List<DateTime>();

        private List<decimal> _liveValues = new List<decimal>();

        private List<DateTime> _historyDates = new List<DateTime>();

        private List<decimal> _historyValues = new List<decimal>();

        /// <summary>
        /// Текущее значение ряда, включая незавершённый день
        /// </summary>
        public decimal CurrentValue;

        #endregion

        #region Build

        /// <summary>
        /// Подставить сохранённый ряд перед тем, что удалось построить по свечам.
        /// Ряд может быть в любом масштабе: при склейке он приводится к масштабу живой части.
        /// Это позволяет пережить перезапуск робота и подложить готовый индекс полной
        /// доходности вместо накопленного самим роботом
        /// </summary>
        public void SetHistory(List<DateTime> dates, List<decimal> values)
        {
            _historyDates = new List<DateTime>();
            _historyValues = new List<decimal>();

            if (dates == null
                || values == null)
            {
                return;
            }

            for (int i = 0; i < dates.Count && i < values.Count; i++)
            {
                if (values[i] <= 0)
                {
                    continue;
                }

                _historyDates.Add(dates[i].Date);
                _historyValues.Add(values[i]);
            }
        }

        /// <summary>
        /// Пересобрать ряд. Уже посчитанные дни не пересчитываются.
        ///
        /// dividendsReliable - вся ли база дивидендов прочитана. Когда нет, ряд не достраивается
        /// вовсе: шаг без дивидендной поправки в день отсечки впечатал бы гэп в ряд как настоящую
        /// просадку, и убрать его было бы уже нельзя. Ряд догонит, когда база прочитается
        /// </summary>
        public void Rebuild(KorovinDailySeries index, List<KorovinDailySeries> basket,
            List<string> basketTickers, KorovinDividendCache dividends,
            bool dividendsReliable = true)
        {
            if (index == null
                || index.Count == 0)
            {
                return;
            }

            if (dividendsReliable == false
                && DividendAdjust)
            {
                return;
            }

            bool canAdjust = DividendAdjust
                && basket != null
                && basketTickers != null
                && basket.Count > 0
                && basket.Count == basketTickers.Count;

            // формула индекса, состав корзины или сам флаг коррекции могли поменяться при той же
            // длине ряда: без сброса в Values остался бы ряд, посчитанный по старым правилам
            string signature = BuildSignature(basketTickers);

            if (signature != _buildSignature)
            {
                // Лестницу сбрасывать надо только при СОДЕРЖАТЕЛЬНОЙ смене правил: другой состав
                // корзины или другой флаг коррекции. Множители формулы меняются и сами по себе -
                // пока бумаги подключаются, автоформула равного веса пересчитывается, и по ним
                // сброс срабатывал бы сотни раз за прогрев, каждый раз обнуляя лестницу
                string basketNow = BuildBasketSignature(basketTickers);

                if (_basketSignature.Length > 0
                    && basketNow != _basketSignature
                    && _liveValues.Count > 0)
                {
                    SeriesRebuilt = true;
                }

                _basketSignature = basketNow;
                _buildSignature = signature;
                _liveDates = new List<DateTime>();
                _liveValues = new List<decimal>();
            }

            if (canAdjust == false)
            {
                _liveDates = index.Dates;
                _liveValues = index.Closes;
                CurrentValue = index.CurrentPrice;
                Compose();
                return;
            }

            if (_liveValues.Count > index.Count
                || (_liveValues.Count > 0 && _liveDates.Count > 0 && index.Dates[0] != _liveDates[0]))
            {
                // история переехала, считаем заново
                _liveDates = new List<DateTime>();
                _liveValues = new List<decimal>();
            }

            if (_liveValues.Count == 0)
            {
                _liveDates = new List<DateTime>();
                _liveValues = new List<decimal>();

                _liveDates.Add(index.Dates[0]);
                _liveValues.Add(index.Closes[0]);
            }

            for (int i = _liveValues.Count; i < index.Count; i++)
            {
                decimal prevPrice = index.Closes[i - 1];
                decimal price = index.Closes[i];

                decimal step = 0;

                if (prevPrice != 0)
                {
                    decimal relativeGap = GetRelativeGap(index.Dates[i - 1], basket, basketTickers, dividends);
                    step = price / prevPrice + relativeGap;
                }

                if (step <= 0)
                {
                    step = 1;
                }

                _liveDates.Add(index.Dates[i]);
                _liveValues.Add(_liveValues[_liveValues.Count - 1] * step);
            }

            // текущий незавершённый день
            decimal lastClose = index.Closes[index.Count - 1];

            if (lastClose != 0
                && index.CurrentPrice != 0)
            {
                decimal relativeGap = GetRelativeGap(index.Dates[index.Count - 1], basket, basketTickers, dividends);
                CurrentValue = _liveValues[_liveValues.Count - 1] * (index.CurrentPrice / lastClose + relativeGap);
            }
            else
            {
                CurrentValue = _liveValues[_liveValues.Count - 1];
            }

            Compose();
        }

        /// <summary>
        /// Склеить сохранённую историю с живой частью. История масштабируется по точке стыка,
        /// поэтому её абсолютные значения могут быть любыми
        /// </summary>
        private void Compose()
        {
            if (_historyDates.Count == 0
                || _liveValues.Count == 0)
            {
                Dates = _liveDates;
                Values = _liveValues;
                return;
            }

            DateTime firstLiveDate = _liveDates[0];
            decimal firstLiveValue = _liveValues[0];

            int joinIndex = -1;

            for (int i = _historyDates.Count - 1; i >= 0; i--)
            {
                if (_historyDates[i] <= firstLiveDate)
                {
                    joinIndex = i;
                    break;
                }
            }

            if (joinIndex < 0
                || _historyValues[joinIndex] <= 0)
            {
                Dates = _liveDates;
                Values = _liveValues;
                return;
            }

            decimal scale = firstLiveValue / _historyValues[joinIndex];

            List<DateTime> dates = new List<DateTime>();
            List<decimal> values = new List<decimal>();

            int lastHistoryIndex = _historyDates[joinIndex] == firstLiveDate ? joinIndex - 1 : joinIndex;

            for (int i = 0; i <= lastHistoryIndex; i++)
            {
                dates.Add(_historyDates[i]);
                values.Add(_historyValues[i] * scale);
            }

            dates.AddRange(_liveDates);
            values.AddRange(_liveValues);

            Dates = dates;
            Values = values;
        }

        /// <summary>
        /// Отпечаток содержательной части правил: состав корзины и флаг дивидендной коррекции.
        /// Множители формулы сюда не входят - они пересчитываются по ходу подключения бумаг
        /// </summary>
        private string BuildBasketSignature(List<string> basketTickers)
        {
            List<string> sorted = new List<string>();

            for (int i = 0; basketTickers != null && i < basketTickers.Count; i++)
            {
                sorted.Add(basketTickers[i]);
            }

            sorted.Sort();

            string tickers = "";

            for (int i = 0; i < sorted.Count; i++)
            {
                tickers += sorted[i] + ";";
            }

            return (DividendAdjust ? "adj" : "raw") + "|" + tickers;
        }

        /// <summary>
        /// Отпечаток правил построения ряда: пока он не меняется, пересчитывать историю незачем
        /// </summary>
        private string BuildSignature(List<string> basketTickers)
        {
            string tickers = "";

            for (int i = 0; basketTickers != null && i < basketTickers.Count; i++)
            {
                tickers += basketTickers[i] + ";";
            }

            string weights = "";

            for (int i = 0; i < _formulaWeights.Count; i++)
            {
                weights += _formulaWeights[i].ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            }

            return (DividendAdjust ? "adj" : "raw") + "|" + tickers + "|" + weights;
        }

        /// <summary>
        /// Доля, на которую корзина проседает из-за отсечек с датой Т-1 в указанный день.
        /// Считается как сумма дивидендов корзины к сумме цен корзины: для формул вида
        /// суммы или среднего цен результат не зависит от масштаба индекса
        /// </summary>
        public decimal GetRelativeGap(DateTime recordDate, List<KorovinDailySeries> basket,
            List<string> basketTickers, KorovinDividendCache dividends)
        {
            if (dividends == null)
            {
                return 0;
            }

            decimal dividendSum = 0;
            decimal priceSum = 0;

            for (int i = 0; i < basket.Count; i++)
            {
                KorovinDailySeries series = basket[i];

                if (series == null
                    || series.Count == 0)
                {
                    continue;
                }

                decimal price = series.CloseByDate(recordDate);

                if (price == 0)
                {
                    continue;
                }

                decimal weight = GetFormulaWeight(i);

                priceSum += price * weight;
                dividendSum += dividends.GetAmountOnDate(basketTickers[i], recordDate) * weight;
            }

            if (priceSum == 0
                || dividendSum == 0)
            {
                return 0;
            }

            return dividendSum / priceSum;
        }

        /// <summary>
        /// Сбросить ряд
        /// </summary>
        public void Clear()
        {
            Dates = new List<DateTime>();
            Values = new List<decimal>();
            _liveDates = new List<DateTime>();
            _liveValues = new List<decimal>();
            CurrentValue = 0;
            _buildSignature = "";
            _basketSignature = "";
        }

        #endregion
    }

    /// <summary>
    /// Лестница состояний рынка. Вверх по факту просадки - сразу, вниз - по одной ступени,
    /// с задержкой и только после реального роста от дна. Автомат прогоняется по всей
    /// доступной истории, поэтому уровень восстанавливается даже после потери файла состояния.
    /// </summary>
    public class KorovinMarketLadder
    {
        #region Settings

        public decimal PeakDecayPercentPerYear = 15;
        public int LongSmaPeriod = 200;


        public decimal Hysteresis = 2;

        public int ExitDelayDays = 20;
        public decimal ExitRecoveryPercent = 10;
        public int StaleDays = 120;
        public bool StaleLowersLevel = true;

        public int SpeedWindow = 5;
        public int SpeedHoldDays = 10;

        /// <summary>
        /// Непрерывный режим: загрузка считается долей доступного резерва,
        /// а не ступенями в процентных пунктах
        /// </summary>

        public decimal DdStartPercent = 7;
        public decimal DdFullPercent = 45;
        public decimal DepthCurve = 1.5m;

        public decimal SpeedStartPercent = 5;
        public decimal SpeedFullPercent = 15;
        public decimal SpeedWeight = 0.25m;

        public decimal EuphoriaDevStartPercent = 10;
        public decimal EuphoriaDevFullPercent = 25;
        public decimal EuphoriaWeight = 0.3m;

        public decimal StepUp = 0.1m;
        public decimal StepDown = 0.15m;
        public decimal MinKStep = 0.05m;

        #endregion

        #region State

        /// <summary>
        /// Текущий уровень лестницы. -1 эйфория, 0 норма, 1..4 ступени просадки
        /// </summary>
        public int Level;

        public decimal Peak;
        public decimal Trough;
        public decimal Drawdown;
        public decimal Recovery;
        public decimal Speed;

        public DateTime LevelEnterDate = DateTime.MinValue;
        public DateTime TroughDate = DateTime.MinValue;

        /// <summary>
        /// Фактическая загрузка резерва. 1 - резерв израсходован полностью,
        /// 0 - базовые веса, отрицательные значения - сокращение доли акций на перегреве
        /// </summary>
        public decimal KApplied;

        public decimal KTarget;
        public decimal KDepth;
        public decimal KSpeed;
        public decimal KEuphoria;

        private int _processedCount;
        private int _levelEnterIndex;
        private int _troughIndex;
        private decimal _smaSum;

        private decimal _reentryGuardValue;
        private bool _reentryGuardActive;

        private decimal _speedImpulse;
        private int _speedImpulseIndex = -1;

        private DateTime _lastLoadChangeDate = DateTime.MinValue;

        #endregion

        #region Calculation

        /// <summary>
        /// Прогнать автомат по ряду. Уже пройденные дни не пересчитываются
        /// </summary>
        public void Process(List<DateTime> dates, List<decimal> values, decimal currentValue)
        {
            Process(dates, values, currentValue, DateTime.MinValue);
        }

        /// <summary>
        /// barTime - время текущего бара. Внутридневной шаг должен помечаться сегодняшней датой:
        /// последний элемент dates - это вчерашний завершённый день, и запись его датой
        /// сдвигала бы на день отсчёт Exit delay days и путала бы лог
        /// </summary>
        public void Process(List<DateTime> dates, List<decimal> values, decimal currentValue,
            DateTime barTime)
        {
            if (dates == null
                || values == null
                || values.Count == 0)
            {
                return;
            }

            if (_processedCount > values.Count)
            {
                Reset();
            }

            decimal loadBeforeDays = KApplied;

            for (int i = _processedCount; i < values.Count; i++)
            {
                ProcessDay(dates, values, i);
            }

            _processedCount = values.Count;

            // завершённый день пометил смену загрузки вчерашней датой, а внутридневной шаг
            // считает себя сегодняшним - без этого флага в один расчёт попадали бы оба,
            // и в обвал загрузка росла бы на два Step up за бар вместо одного
            bool changedByDays = KApplied != loadBeforeDays;

            UpdateMetrics(values, currentValue);

            ApplyCurrentValue(dates, values, currentValue, barTime, changedByDays);
        }

        /// <summary>
        /// Реакция на движение текущего, ещё не завершённого дня.
        /// Дневной ряд принципиально состоит из закрытий, поэтому обвал внутри сессии попал бы
        /// в расчёт только назавтра. Здесь загрузка пересчитывается по текущей цене сразу,
        /// но не чаще одного шага в день: иначе каждый бар добавлял бы ещё Step up.
        ///
        /// Пик и дно при этом НЕ трогаются: они - линейка, по которой меряются пороги, и вести
        /// её по внутридневным значениям означало бы мерить просадку часовыми экстремумами,
        /// а пороги калибровались по дневным закрытиям. Внутридневной всплеск вверх иначе
        /// поднял бы пик и создал фантомную просадку на всё время его затухания.
        /// Обвал внутри сессии виден и так - просадка считается от вчерашнего пика
        /// </summary>
        private void ApplyCurrentValue(List<DateTime> dates, List<decimal> values,
            decimal currentValue, DateTime barTime, bool changedByDays)
        {
            if (currentValue <= 0
                || values.Count == 0)
            {
                return;
            }

            // загрузку уже сдвинул только что закрывшийся день. Шаг по незакрытой цене поверх
            // него - это второе изменение за один расчёт, а разрешено одно
            if (changedByDays)
            {
                UpdateLevelLabel();
                return;
            }

            // шаг относится к текущему дню, а dates заканчивается вчерашним завершённым
            DateTime today = barTime == DateTime.MinValue
                ? dates[dates.Count - 1]
                : barTime.Date;

            if (_lastLoadChangeDate.Date == today.Date)
            {
                return;
            }

            decimal drawdown = 0;

            if (Peak != 0)
            {
                drawdown = (Peak - currentValue) / Peak * 100m;
            }

            decimal speed = 0;
            int speedIndex = values.Count - SpeedWindow;

            if (speedIndex >= 0
                && speedIndex < values.Count
                && values[speedIndex] != 0)
            {
                speed = (currentValue / values[speedIndex] - 1m) * 100m;
            }

            decimal before = KApplied;

            KDepth = GetDepthFactor(drawdown);
            KSpeed = GetSpeedFactor(values.Count, speed);
            KEuphoria = GetEuphoriaFactor(currentValue, values.Count - 1);

            KTarget = KDepth + KSpeed - KEuphoria;

            if (KTarget > 1m)
            {
                KTarget = 1m;
            }

            if (KTarget < -1m)
            {
                KTarget = -1m;
            }

            if (KTarget > KApplied + MinKStep)
            {
                if (_reentryGuardActive
                    && currentValue > _reentryGuardValue * (1m - Hysteresis / 100m))
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal stepUp = KTarget - KApplied;

                if (stepUp > StepUp)
                {
                    stepUp = StepUp;
                }

                SetLoadByCurrent(KApplied + stepUp, today, currentValue, values.Count - 1);
                _reentryGuardActive = false;
            }
            else if (KTarget < KApplied - MinKStep)
            {
                if (values.Count - 1 - _levelEnterIndex < ExitDelayDays)
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal recovery = 0;

                if (Trough != 0)
                {
                    recovery = (currentValue / Trough - 1m) * 100m;
                }

                bool byRecovery = recovery >= ExitRecoveryPercent;
                bool byEuphoria = KApplied <= 0;

                if (byRecovery == false
                    && byEuphoria == false)
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal stepDown = KApplied - KTarget;

                if (stepDown > StepDown)
                {
                    stepDown = StepDown;
                }

                SetLoadByCurrent(KApplied - stepDown, today, currentValue, values.Count - 1);

                _reentryGuardActive = true;
                _reentryGuardValue = currentValue;
            }

            if (KApplied != before)
            {
                _lastLoadChangeDate = today;
            }

            UpdateLevelLabel();
        }

        private void SetLoadByCurrent(decimal newLoad, DateTime today, decimal currentValue, int index)
        {
            KApplied = newLoad;

            if (KApplied > 1m)
            {
                KApplied = 1m;
            }

            if (KApplied < -1m)
            {
                KApplied = -1m;
            }

            _levelEnterIndex = index;
            LevelEnterDate = today;
            Trough = currentValue;
            _troughIndex = index;
            TroughDate = today;

            UpdateLevelLabel();
        }

        private void ProcessDay(List<DateTime> dates, List<decimal> values, int index)
        {
            decimal value = values[index];

            // пик
            if (index == 0)
            {
                Peak = value;
                Trough = value;
                _troughIndex = 0;
                _levelEnterIndex = 0;
                LevelEnterDate = dates[0];
                TroughDate = dates[0];
                _smaSum = value;
                return;
            }

            // пик затухает: жёсткий максимум за год давал бы "вечную панику" - рынок давно
            // успокоился, а робот всё ещё считает просадку от годовалого максимума
            decimal decayDaily = PeakDecayPercentPerYear / 100m / 252m;
            decimal decayedPeak = Peak * (1m - decayDaily);
            Peak = value > decayedPeak ? value : decayedPeak;

            // сумма для скользящей средней
            _smaSum += value;

            if (index >= LongSmaPeriod)
            {
                _smaSum -= values[index - LongSmaPeriod];
            }

            decimal drawdown = 0;

            if (Peak != 0)
            {
                drawdown = (Peak - value) / Peak * 100m;
            }

            // дно
            bool loaded = KApplied > 0;

            if (loaded
                && value < Trough)
            {
                Trough = value;
                _troughIndex = index;
                TroughDate = dates[index];
            }

            // скорость падения
            decimal speed = 0;

            if (index >= SpeedWindow
                && values[index - SpeedWindow] != 0)
            {
                speed = (value / values[index - SpeedWindow] - 1m) * 100m;
            }

            // ступень, проданная в рост, отработала: запрет на обратный вход снимается
            if (_reentryGuardActive
                && value >= _reentryGuardValue * (1m + ExitRecoveryPercent / 100m))
            {
                _reentryGuardActive = false;
            }

            ProcessDayContinuous(dates, values, index, value, drawdown, speed);
        }

        /// <summary>
        /// Непрерывный расчёт загрузки. Глубина задаёт основную часть, скорость добавляет
        /// ограниченную долю, перегрев вычитает. Применяется через храповик: вверх сразу
        /// порциями, вниз только с задержкой и по факту роста от дна
        /// </summary>
        private void ProcessDayContinuous(List<DateTime> dates, List<decimal> values, int index,
            decimal value, decimal drawdown, decimal speed)
        {
            KDepth = GetDepthFactor(drawdown);
            KSpeed = GetSpeedFactor(index, speed);
            KEuphoria = GetEuphoriaFactor(value, index);

            KTarget = KDepth + KSpeed - KEuphoria;

            if (KTarget > 1m)
            {
                KTarget = 1m;
            }

            if (KTarget < -1m)
            {
                KTarget = -1m;
            }

            if (KTarget > KApplied + MinKStep)
            {
                // обратно в только что отданную загрузку заходим только ниже точки выхода
                if (_reentryGuardActive
                    && value > _reentryGuardValue * (1m - Hysteresis / 100m))
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal stepUp = KTarget - KApplied;

                if (stepUp > StepUp)
                {
                    stepUp = StepUp;
                }

                SetLoad(KApplied + stepUp, dates, values, index);
                _reentryGuardActive = false;
                return;
            }

            if (KTarget < KApplied - MinKStep)
            {
                if (index - _levelEnterIndex < ExitDelayDays)
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal recovery = 0;

                if (Trough != 0)
                {
                    recovery = (value / Trough - 1m) * 100m;
                }

                bool byRecovery = recovery >= ExitRecoveryPercent;
                bool byStale = StaleLowersLevel
                    && index - _levelEnterIndex >= StaleDays
                    && _troughIndex <= _levelEnterIndex;
                bool byEuphoria = KApplied <= 0;

                if (byRecovery == false
                    && byStale == false
                    && byEuphoria == false)
                {
                    UpdateLevelLabel();
                    return;
                }

                decimal stepDown = KApplied - KTarget;

                if (stepDown > StepDown)
                {
                    stepDown = StepDown;
                }

                SetLoad(KApplied - stepDown, dates, values, index);

                _reentryGuardActive = true;
                _reentryGuardValue = value;
                return;
            }

            UpdateLevelLabel();
        }

        private decimal GetDepthFactor(decimal drawdown)
        {
            if (DdFullPercent <= DdStartPercent)
            {
                return 0;
            }

            decimal raw = (drawdown - DdStartPercent) / (DdFullPercent - DdStartPercent);

            if (raw <= 0)
            {
                return 0;
            }

            if (raw >= 1m)
            {
                return 1m;
            }

            if (DepthCurve == 1m)
            {
                return raw;
            }

            return (decimal)Math.Pow((double)raw, (double)DepthCurve);
        }

        /// <summary>
        /// Вклад скорости падения. Это добавка к глубине, ограниченная сверху SpeedWeight,
        /// и она затухает за SpeedHoldDays: обвал ускоряет докупку, но не расходует весь резерв
        /// </summary>
        private decimal GetSpeedFactor(int index, decimal speed)
        {
            if (SpeedFullPercent > SpeedStartPercent
                && speed < 0)
            {
                decimal fall = -speed;
                decimal raw = (fall - SpeedStartPercent) / (SpeedFullPercent - SpeedStartPercent);

                if (raw > 1m)
                {
                    raw = 1m;
                }

                if (raw > 0)
                {
                    decimal impulse = raw * SpeedWeight;

                    if (impulse > _speedImpulse
                        || _speedImpulseIndex < 0)
                    {
                        _speedImpulse = impulse;
                        _speedImpulseIndex = index;
                    }
                }
            }

            if (_speedImpulseIndex < 0
                || SpeedHoldDays <= 0)
            {
                return 0;
            }

            int age = index - _speedImpulseIndex;

            if (age >= SpeedHoldDays)
            {
                _speedImpulse = 0;
                _speedImpulseIndex = -1;
                return 0;
            }

            return _speedImpulse * (1m - (decimal)age / SpeedHoldDays);
        }

        private decimal GetEuphoriaFactor(decimal value, int index)
        {
            if (EuphoriaDevFullPercent <= EuphoriaDevStartPercent)
            {
                return 0;
            }

            decimal deviation = GetDeviation(value, index);

            decimal raw = (deviation - EuphoriaDevStartPercent)
                / (EuphoriaDevFullPercent - EuphoriaDevStartPercent);

            if (raw <= 0)
            {
                return 0;
            }

            if (raw > 1m)
            {
                raw = 1m;
            }

            return raw * EuphoriaWeight;
        }

        private void SetLoad(decimal newLoad, List<DateTime> dates, List<decimal> values, int index)
        {
            _lastLoadChangeDate = dates[index];

            KApplied = newLoad;

            if (KApplied > 1m)
            {
                KApplied = 1m;
            }

            if (KApplied < -1m)
            {
                KApplied = -1m;
            }

            _levelEnterIndex = index;
            LevelEnterDate = dates[index];
            Trough = values[index];
            _troughIndex = index;
            TroughDate = dates[index];

            UpdateLevelLabel();
        }

        /// <summary>
        /// Имя ступени в непрерывном режиме - только ярлык для лога
        /// </summary>
        private void UpdateLevelLabel()
        {
            if (KApplied <= -0.05m)
            {
                Level = -1;
            }
            else if (KApplied < 0.05m)
            {
                Level = 0;
            }
            else if (KApplied < 0.3m)
            {
                Level = 1;
            }
            else if (KApplied < 0.55m)
            {
                Level = 2;
            }
            else if (KApplied < 0.8m)
            {
                Level = 3;
            }
            else
            {
                Level = 4;
            }
        }

        private decimal GetDeviation(decimal value, int index)
        {
            int period = index + 1;

            if (period > LongSmaPeriod)
            {
                period = LongSmaPeriod;
            }

            if (period == 0)
            {
                return 0;
            }

            decimal sma = _smaSum / period;

            if (sma == 0)
            {
                return 0;
            }

            return (value / sma - 1m) * 100m;
        }

        private void UpdateMetrics(List<decimal> values, decimal currentValue)
        {
            decimal value = currentValue;

            if (value == 0)
            {
                value = values[values.Count - 1];
            }

            if (Peak != 0)
            {
                Drawdown = (Peak - value) / Peak * 100m;

                if (Drawdown < 0)
                {
                    // цена выше пика: просадки нет
                    Drawdown = 0;
                }
            }

            if (Trough != 0)
            {
                Recovery = (value / Trough - 1m) * 100m;
            }


            int speedIndex = values.Count - SpeedWindow;

            if (speedIndex >= 0
                && speedIndex < values.Count
                && values[speedIndex] != 0)
            {
                Speed = (value / values[speedIndex] - 1m) * 100m;
            }
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Название уровня для логов
        /// </summary>
        public string GetLevelName()
        {
            if (Level == -1)
            {
                return "Euphoria";
            }

            if (Level == 0)
            {
                return "Normal";
            }

            if (Level == 1)
            {
                return "Correction";
            }

            if (Level == 2)
            {
                return "Drawdown";
            }

            if (Level == 3)
            {
                return "Crisis";
            }

            return "Panic";
        }

        /// <summary>
        /// Поставить лестницу в сохранённое положение после перезапуска. Прогон автомата
        /// по дневному ряду восстанавливает только то, что видно в закрытиях: внутридневные
        /// шаги в ряду не отражены, и без этого метода загрузка разошлась бы с сохранённой,
        /// а робот принял бы расхождение за сигнал и дал оборот сразу после старта
        /// </summary>
        public void RestoreState(decimal load, int level, decimal peak, decimal trough,
            DateTime troughDate, DateTime levelEnterDate)
        {
            KApplied = load;
            Level = level;

            if (peak > 0)
            {
                Peak = peak;
            }

            if (trough > 0)
            {
                Trough = trough;
            }

            if (troughDate != DateTime.MinValue)
            {
                TroughDate = troughDate;
            }

            if (levelEnterDate != DateTime.MinValue)
            {
                LevelEnterDate = levelEnterDate;
            }

            UpdateLevelLabel();
        }

        /// <summary>
        /// Индекс дня, с которого отсчитывается выдержка уровня. Нужен, чтобы после перезапуска
        /// восстановить отсчёт Exit delay days по сохранённой дате входа в уровень
        /// </summary>
        public void RestoreLevelEnterIndex(List<DateTime> dates)
        {
            if (dates == null
                || LevelEnterDate == DateTime.MinValue)
            {
                return;
            }

            for (int i = dates.Count - 1; i >= 0; i--)
            {
                if (dates[i].Date <= LevelEnterDate.Date)
                {
                    _levelEnterIndex = i;
                    return;
                }
            }
        }

        public void Reset()
        {
            Level = 0;
            Peak = 0;
            Trough = 0;
            Drawdown = 0;
            Recovery = 0;
            Speed = 0;
            LevelEnterDate = DateTime.MinValue;
            TroughDate = DateTime.MinValue;
            _processedCount = 0;
            _levelEnterIndex = 0;
            _troughIndex = 0;
            _smaSum = 0;
            _reentryGuardActive = false;
            _reentryGuardValue = 0;
            KApplied = 0;
            KTarget = 0;
            KDepth = 0;
            KSpeed = 0;
            KEuphoria = 0;
            _speedImpulse = 0;
            _speedImpulseIndex = -1;
            _lastLoadChangeDate = DateTime.MinValue;
        }

        #endregion
    }
}
