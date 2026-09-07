/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Entity;
using System;
using System.Collections.Generic;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Свёртка внутридневных свечей в дневной ряд закрытий.
    /// Робот работает на интрадей таймфрейме (рекомендуется час), а вся аналитика
    /// строится по дневным закрытиям. Текущий день в историю не попадает, пока не завершится.
    /// </summary>
    public class KorovinDailySeries
    {
        #region Data

        /// <summary>
        /// Даты завершённых дней
        /// </summary>
        public List<DateTime> Dates = new List<DateTime>();

        /// <summary>
        /// Закрытия завершённых дней
        /// </summary>
        public List<decimal> Closes = new List<decimal>();

        /// <summary>
        /// Цена последней завершённой свечи. Может относиться к незавершённому дню
        /// </summary>
        public decimal CurrentPrice;

        /// <summary>
        /// Дата последней завершённой свечи
        /// </summary>
        public DateTime CurrentDate;

        #endregion

        #region Public methods

        /// <summary>
        /// Дополнить дневной ряд по свежим свечам.
        /// maxDate - время текущего бара робота: всё, что позже, в ряд не берётся.
        /// Таб может отдать данные другого прогона или другого инструмента, и без этой
        /// отсечки ряд один раз заполнился бы чужими днями и перестал бы пополняться
        /// </summary>
        public void Update(List<Candle> candles, DateTime maxDate)
        {
            Update(candles, maxDate, null);
        }

        /// <summary>
        /// isTradeTime - фильтр торгового времени. Без него закрытием дня становилась
        /// последняя свеча суток: у акций это вечерняя сессия 23:00, тогда как золото
        /// закрывается в 18:00, а решения принимаются до 19:00. В ряд попадали и выходные дни,
        /// хотя робот в них не работает.
        ///
        /// Ряд - это ещё и шкала времени: Long sma period, Speed window, Exit delay days,
        /// Stale days отсчитываются в его элементах, а затухание пика делит годовую скорость
        /// на 252. Лишние записи сжимают все эти окна в календарном выражении
        /// </summary>
        public void Update(List<Candle> candles, DateTime maxDate, Func<DateTime, bool> isTradeTime)
        {
            if (candles == null
                || candles.Count == 0)
            {
                return;
            }

            DateTime lastAllowedDate = maxDate.Date;

            TrimAfter(lastAllowedDate);

            int lastIndex = -1;

            // сравнение по полному времени, а не по дате: свеча той же даты, но более поздняя
            // чем текущий бар, - это заглядывание вперёд.
            for (int i = candles.Count - 1; i >= 0; i--)
            {
                if (candles[i].TimeStart <= maxDate
                    && (isTradeTime == null || isTradeTime(candles[i].TimeStart)))
                {
                    lastIndex = i;
                    break;
                }
            }

            if (lastIndex < 0)
            {
                return;
            }

            CurrentPrice = candles[lastIndex].Close;
            CurrentDate = candles[lastIndex].TimeStart.Date;

            DateTime lastStoredDate = DateTime.MinValue;

            if (Dates.Count > 0)
            {
                lastStoredDate = Dates[Dates.Count - 1];
            }

            List<DateTime> newDates = new List<DateTime>();
            List<decimal> newCloses = new List<decimal>();

            DateTime collectedDay = DateTime.MinValue;

            for (int i = lastIndex; i >= 0; i--)
            {
                if (isTradeTime != null
                    && isTradeTime(candles[i].TimeStart) == false)
                {
                    continue;
                }

                DateTime day = candles[i].TimeStart.Date;

                if (day >= CurrentDate)
                {
                    // текущий день ещё не завершён
                    continue;
                }

                if (day <= lastStoredDate)
                {
                    break;
                }

                if (day == collectedDay)
                {
                    // закрытие дня уже взято: идём с конца, первая встреченная свеча дня и есть закрытие
                    continue;
                }

                collectedDay = day;
                newDates.Add(day);
                newCloses.Add(candles[i].Close);
            }

            for (int i = newDates.Count - 1; i >= 0; i--)
            {
                Dates.Add(newDates[i]);
                Closes.Add(newCloses[i]);
            }
        }

        /// <summary>
        /// Отрезать дни, которые оказались позже указанной даты
        /// </summary>
        public void TrimAfter(DateTime date)
        {
            DateTime lastAllowedDate = date.Date;

            int firstBadIndex = -1;

            for (int i = Dates.Count - 1; i >= 0; i--)
            {
                if (Dates[i] <= lastAllowedDate)
                {
                    break;
                }

                firstBadIndex = i;
            }

            if (firstBadIndex < 0)
            {
                return;
            }

            Dates.RemoveRange(firstBadIndex, Dates.Count - firstBadIndex);
            Closes.RemoveRange(firstBadIndex, Closes.Count - firstBadIndex);
        }

        /// <summary>
        /// Количество дней в истории
        /// </summary>
        public int Count
        {
            get { return Dates.Count; }
        }

        /// <summary>
        /// Закрытие дня по индексу от конца истории. 0 - последний завершённый день
        /// </summary>
        public decimal CloseAgo(int daysBack)
        {
            int index = Closes.Count - 1 - daysBack;

            if (index < 0
                || index >= Closes.Count)
            {
                return 0;
            }

            return Closes[index];
        }

        /// <summary>
        /// Закрытие на конкретную дату. Ноль, если дня нет в истории
        /// </summary>
        public decimal CloseByDate(DateTime date)
        {
            int index = IndexOfDate(date);

            if (index < 0)
            {
                return 0;
            }

            return Closes[index];
        }

        /// <summary>
        /// Индекс дня в истории. Минус один, если дня нет
        /// </summary>
        public int IndexOfDate(DateTime date)
        {
            DateTime day = date.Date;

            int left = 0;
            int right = Dates.Count - 1;

            while (left <= right)
            {
                int middle = left + (right - left) / 2;

                if (Dates[middle] == day)
                {
                    return middle;
                }

                if (Dates[middle] < day)
                {
                    left = middle + 1;
                }
                else
                {
                    right = middle - 1;
                }
            }

            return -1;
        }

        /// <summary>
        /// Очистить ряд
        /// </summary>
        public void Clear()
        {
            Dates.Clear();
            Closes.Clear();
            CurrentPrice = 0;
            CurrentDate = DateTime.MinValue;
        }

        #endregion
    }
}
