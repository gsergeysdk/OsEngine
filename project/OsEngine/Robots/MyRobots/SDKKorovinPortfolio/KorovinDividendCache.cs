/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using OsEngine.Wiki;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Кэш дивидендов по тикерам из базы Wiki/Dividends.
    /// Нужен трижды: для дивидендной коррекции ряда индекса, для total return доходности бумаг
    /// и для нейтрализации дивидендного гэпа в триггерах ребалансировки.
    /// Даты в базе - это даты Т-1: последний день, когда позиция должна быть открыта.
    /// </summary>
    public class KorovinDividendCache
    {
        #region Service

        private Dictionary<string, Dictionary<DateTime, decimal>> _amounts
            = new Dictionary<string, Dictionary<DateTime, decimal>>();

        private Dictionary<string, DateTime> _loadedTo = new Dictionary<string, DateTime>();

        /// <summary>
        /// По каким тикерам уже сообщали о проблеме и в какой день. Чтобы не сыпать
        /// одним и тем же сообщением на каждом баре
        /// </summary>
        private Dictionary<string, DateTime> _reported = new Dictionary<string, DateTime>();

        /// <summary>
        /// Тикеры, по которым уже сообщили об отсутствии файла. Это состояние
        /// статичное, повторять о нём каждый день незачем
        /// </summary>
        private HashSet<string> _missingReported = new HashSet<string>();

        /// <summary>
        /// Сообщения роботу. Сам WikiMaster пишет свои ошибки в лог сервера, а не робота,
        /// поэтому без этого события проблема с базой осталась бы незамеченной
        /// </summary>
        public event Action<string> LogMessageEvent;

        private void Log(string message)
        {
            Action<string> handler = LogMessageEvent;

            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>
        /// Загрузить историю тикера, если она ещё не загружена или устарела.
        ///
        /// Возвращает false, если данные до нас не дошли: файл есть, но не прочитался.
        /// Такой ответ нельзя принимать за "дивидендов нет" - в день отсечки это впечатало бы
        /// дивидендный гэп в ряд total return как настоящую просадку, причём навсегда:
        /// уже посчитанные дни ряда не пересчитываются. Неудача не кэшируется, следующий
        /// бар попробует снова.
        ///
        /// Отсутствие файла - это законное "данных нет" (у бумаги может не быть истории
        /// выплат). Такой ответ кэшируется как пустой, но о нём сообщается раз в день
        /// </summary>
        public bool Load(string ticker, DateTime asOf)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return true;
            }

            DateTime loadedTo;

            if (_loadedTo.TryGetValue(ticker, out loadedTo)
                && loadedTo >= asOf.Date)
            {
                return true;
            }

            WikiDividendHistory history = WikiMaster.GetDividendsHistory(ticker, asOf);

            // признак успешного чтения - непустая отметка из Metadata файла. Оба провальных
            // пути API (файла нет, исключение при разборе) отдают её пустой
            bool wasRead = history != null
                && string.IsNullOrWhiteSpace(history.last_updated) == false;

            if (wasRead == false)
            {
                if (FileExists(ticker))
                {
                    Report(ticker, asOf, "Не удалось прочитать дивиденды по " + ticker
                        + ": файл базы есть, но данные не пришли. Ряд total return "
                        + "не достраивается, попытка повторится. Смотрите лог сервера");

                    return false;
                }

                if (_missingReported.Add(ticker))
                {
                    Log("По " + ticker + " нет файла в базе дивидендов. Бумага считается "
                        + "бездивидендной: если это не так, обновите базу");
                }
            }

            Dictionary<DateTime, decimal> byDate = new Dictionary<DateTime, decimal>();

            if (history != null
                && history.historical != null)
            {
                for (int i = 0; i < history.historical.Count; i++)
                {
                    WikiDividendRecord record = history.historical[i];

                    if (record == null
                        || string.IsNullOrWhiteSpace(record.registry_close_date))
                    {
                        continue;
                    }

                    DateTime recordDate;

                    if (!DateTime.TryParseExact(record.registry_close_date, "dd.MM.yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out recordDate))
                    {
                        continue;
                    }

                    if (recordDate.Date > asOf.Date)
                    {
                        continue;
                    }

                    if (byDate.ContainsKey(recordDate.Date))
                    {
                        byDate[recordDate.Date] += record.dividend_amount;
                    }
                    else
                    {
                        byDate.Add(recordDate.Date, record.dividend_amount);
                    }
                }
            }

            _amounts[ticker] = byDate;
            _loadedTo[ticker] = asOf.Date;

            return true;
        }

        /// <summary>
        /// Есть ли файл тикера в базе. Отличает "файла нет" от "файл есть, но не читается":
        /// само API оба случая отдаёт одинаково пустым ответом
        /// </summary>
        private bool FileExists(string ticker)
        {
            try
            {
                string name = ticker.Trim();

                // так же, как делает WikiMaster.NormalizeTicker: в тестере имя бумаги может
                // прийти с расширением файла данных, а файл базы назван без него
                if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - 4);
                }

                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Wiki", "Dividends", name.ToUpperInvariant() + ".md");

                return File.Exists(path);
            }
            catch (Exception)
            {
                // не смогли проверить - считаем, что файл есть: лучше придержать ряд
                // и повторить, чем впечатать в него чужой гэп
                return true;
            }
        }

        private void Report(string ticker, DateTime asOf, string message)
        {
            DateTime reported;

            if (_reported.TryGetValue(ticker, out reported)
                && reported == asOf.Date)
            {
                return;
            }

            _reported[ticker] = asOf.Date;

            Log(message);
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Добавить выплату вручную. Нужно, когда данных Wiki нет, а также в проверках
        /// </summary>
        public void AddAmount(string ticker, DateTime recordDate, decimal amount)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return;
            }

            Dictionary<DateTime, decimal> byDate;

            if (_amounts.TryGetValue(ticker, out byDate) == false
                || byDate == null)
            {
                byDate = new Dictionary<DateTime, decimal>();
                _amounts[ticker] = byDate;
            }

            if (byDate.ContainsKey(recordDate.Date))
            {
                byDate[recordDate.Date] += amount;
            }
            else
            {
                byDate.Add(recordDate.Date, amount);
            }

            _loadedTo[ticker] = DateTime.MaxValue;
        }

        /// <summary>
        /// Дивиденд на одну акцию с датой Т-1 равной указанному дню. Без учёта налога
        /// </summary>
        public decimal GetAmountOnDate(string ticker, DateTime recordDate)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return 0;
            }

            Dictionary<DateTime, decimal> byDate;

            if (!_amounts.TryGetValue(ticker, out byDate)
                || byDate == null)
            {
                return 0;
            }

            decimal amount;

            if (byDate.TryGetValue(recordDate.Date, out amount))
            {
                return amount;
            }

            return 0;
        }

        /// <summary>
        /// Сумма дивидендов на одну акцию за период. Границы включаются
        /// </summary>
        public decimal GetAmountInPeriod(string ticker, DateTime from, DateTime to)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return 0;
            }

            Dictionary<DateTime, decimal> byDate;

            if (!_amounts.TryGetValue(ticker, out byDate)
                || byDate == null)
            {
                return 0;
            }

            decimal result = 0;

            foreach (KeyValuePair<DateTime, decimal> pair in byDate)
            {
                if (pair.Key >= from.Date
                    && pair.Key <= to.Date)
                {
                    result += pair.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Выплаты за период по датам фиксации. Нужны, чтобы каждую умножить на тот объём,
        /// которым робот владел именно на её дату
        /// </summary>
        public List<KeyValuePair<DateTime, decimal>> GetPaymentsInPeriod(string ticker,
            DateTime from, DateTime to)
        {
            List<KeyValuePair<DateTime, decimal>> result = new List<KeyValuePair<DateTime, decimal>>();

            if (string.IsNullOrWhiteSpace(ticker))
            {
                return result;
            }

            Dictionary<DateTime, decimal> byDate;

            if (!_amounts.TryGetValue(ticker, out byDate)
                || byDate == null)
            {
                return result;
            }

            foreach (KeyValuePair<DateTime, decimal> pair in byDate)
            {
                if (pair.Key >= from.Date
                    && pair.Key <= to.Date)
                {
                    result.Add(pair);
                }
            }

            result.Sort((first, second) => first.Key.CompareTo(second.Key));

            return result;
        }

        /// <summary>
        /// Есть ли по тикеру хоть какие-то данные
        /// </summary>
        public bool HasData(string ticker)
        {
            if (string.IsNullOrWhiteSpace(ticker))
            {
                return false;
            }

            Dictionary<DateTime, decimal> byDate;

            if (!_amounts.TryGetValue(ticker, out byDate)
                || byDate == null)
            {
                return false;
            }

            return byDate.Count > 0;
        }

        /// <summary>
        /// Ближайшая будущая дата Т-1 по тикеру. DateTime.MinValue, если её нет
        /// </summary>
        public DateTime GetNextRecordDate(string ticker, DateTime asOf)
        {
            WikiDividendFuture future = WikiMaster.GetDividendsFuture(ticker, asOf);

            if (future == null
                || future.future == null
                || string.IsNullOrWhiteSpace(future.future.registry_close_date))
            {
                return DateTime.MinValue;
            }

            DateTime recordDate;

            if (!DateTime.TryParseExact(future.future.registry_close_date, "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out recordDate))
            {
                return DateTime.MinValue;
            }

            return recordDate.Date;
        }

        /// <summary>
        /// Сбросить кэш
        /// </summary>
        public void Clear()
        {
            _amounts.Clear();
            _loadedTo.Clear();
            _reported.Clear();
            _missingReported.Clear();
        }

        #endregion
    }
}
