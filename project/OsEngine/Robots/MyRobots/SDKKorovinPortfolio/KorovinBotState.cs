/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OsEngine.Robots.MyRobots
{
    /// <summary>
    /// Состояние робота между запусками. Только реальная торговля: в тестере весь прогон
    /// идёт за один запуск. Хранится лишь то, что нельзя восстановить из журнала OsEngine
    /// и из истории котировок. Формат текстовый, чтобы трейдер мог поправить его руками.
    /// </summary>
    public class KorovinBotState
    {
        #region Fields

        public DateTime LastRebalanceDate = DateTime.MinValue;

        public DateTime LastScheduledDate = DateTime.MinValue;

        /// <summary>
        /// Дата последней ребалансировки по лестнице. Отдельно от общей, потому что
        /// пауза между ступенями считается только по ним
        /// </summary>
        public DateTime LastLadderDate = DateTime.MinValue;

        /// <summary>
        /// Уровень лестницы на момент последней ребалансировки
        /// </summary>
        public int LastLevel;

        /// <summary>
        /// Фактическая загрузка резерва на момент последней ребалансировки
        /// </summary>
        public decimal LastLoad;

        /// <summary>
        /// Снимок лестницы. Прогон автомата по истории её не воспроизводит: внутридневные шаги
        /// делались по ценам, которых в дневном ряду нет. Без снимка после перезапуска
        /// восстановленная загрузка разошлась бы с сохранённой и робот сразу дал бы оборот
        /// </summary>
        public bool LadderSaved;

        public decimal LadderPeak;

        public decimal LadderTrough;

        public DateTime LadderTroughDate = DateTime.MinValue;

        public DateTime LadderLevelEnterDate = DateTime.MinValue;

        /// <summary>
        /// План покупок, отложенный на следующую свечу. Ключ - имя бумаги, значение - деньги
        /// </summary>
        public Dictionary<string, decimal> PendingBuys = new Dictionary<string, decimal>();

        /// <summary>
        /// Время свечи, на которой был составлен план покупок
        /// </summary>
        public DateTime PendingBuysTime = DateTime.MinValue;

        public string PendingReason = "";

        /// <summary>
        /// Когда робот последний раз запрашивал базу дивидендов и обновление прошло.
        ///
        /// Отметки времени самих файлов для этого не годятся: файл тикера переписывается
        /// только когда по нему вообще что-то нашлось, а отметка каталога меняется только при
        /// создании или удалении записей. Важно не когда данные менялись, а когда их спрашивали
        /// </summary>
        public DateTime LastDividendsUpdateDate = DateTime.MinValue;

        /// <summary>
        /// Дивиденды, начисленные после последней плановой ребалансировки.
        /// Ключ - имя бумаги, значение - деньги после налога
        /// </summary>
        public Dictionary<string, decimal> PendingDividends = new Dictionary<string, decimal>();

        #endregion

        #region Save and load

        /// <summary>
        /// Событие для диагностики: молча терять состояние нельзя, трейдер должен видеть проблему
        /// </summary>
        public event Action<string> LogMessageEvent;

        private const string EndMarker = "end=ok";

        private void Log(string message)
        {
            Action<string> handler = LogMessageEvent;

            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>
        /// Запись через временный файл с последующей заменой: прерывание на середине
        /// оставит нетронутым прежний файл, а не обрубок. Прежняя версия сохраняется как .bak
        /// </summary>
        public void Save(string filePath)
        {
            string tempPath = filePath + ".tmp";
            string backupPath = filePath + ".bak";

            try
            {
                using (StreamWriter writer = new StreamWriter(tempPath, false))
                {
                    writer.WriteLine("lastRebalanceDate=" + DateToString(LastRebalanceDate));
                    writer.WriteLine("lastScheduledDate=" + DateToString(LastScheduledDate));
                    writer.WriteLine("lastLadderDate=" + DateToString(LastLadderDate));
                    writer.WriteLine("lastLevel=" + LastLevel);
                    writer.WriteLine("lastLoad=" + LastLoad.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("pendingBuysTime=" + TimeToString(PendingBuysTime));
                    writer.WriteLine("pendingReason=" + PendingReason);
                    writer.WriteLine("pendingBuys=" + MapToString(PendingBuys));
                    writer.WriteLine("pendingDiv=" + MapToString(PendingDividends));
                    writer.WriteLine("ladderSaved=" + (LadderSaved ? "1" : "0"));
                    writer.WriteLine("ladderPeak=" + LadderPeak.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("ladderTrough=" + LadderTrough.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("ladderTroughDate=" + DateToString(LadderTroughDate));
                    writer.WriteLine("ladderLevelEnterDate=" + DateToString(LadderLevelEnterDate));
                    writer.WriteLine("lastDividendsUpdate=" + TimeToString(LastDividendsUpdateDate));
                    writer.WriteLine(EndMarker);
                }

                if (File.Exists(filePath))
                {
                    File.Copy(filePath, backupPath, true);
                }

                File.Copy(tempPath, filePath, true);
                File.Delete(tempPath);
            }
            catch (Exception error)
            {
                Log("Не удалось сохранить состояние робота: " + error.Message);
            }
        }

        /// <summary>
        /// Чтение с проверкой целостности. Файл без маркера конца считается обрубком:
        /// такой лучше отбросить целиком, чем тихо продолжить с обнулёнными датами.
        /// При повреждении делается попытка поднять резервную копию
        /// </summary>
        public void Load(string filePath)
        {
            if (TryLoad(filePath))
            {
                return;
            }

            string backupPath = filePath + ".bak";

            if (File.Exists(backupPath))
            {
                Log("Состояние повреждено, поднимается резервная копия " + backupPath);

                if (TryLoad(backupPath))
                {
                    return;
                }
            }

            Clear();
        }

        private bool TryLoad(string filePath)
        {
            try
            {
                if (File.Exists(filePath) == false)
                {
                    return false;
                }

                KorovinBotState parsed = new KorovinBotState();
                bool complete = false;

                using (StreamReader reader = new StreamReader(filePath))
                {
                    while (reader.EndOfStream == false)
                    {
                        string line = reader.ReadLine();

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        if (line.Trim() == EndMarker)
                        {
                            complete = true;
                            continue;
                        }

                        int separator = line.IndexOf('=');

                        if (separator <= 0)
                        {
                            continue;
                        }

                        string key = line.Substring(0, separator).Trim();
                        string value = line.Substring(separator + 1).Trim();

                        parsed.ApplyField(key, value);
                    }
                }

                if (complete == false)
                {
                    Log("Файл состояния " + filePath + " не дочитан до маркера конца и отброшен");
                    return false;
                }

                CopyFrom(parsed);
                return true;
            }
            catch (Exception error)
            {
                Log("Ошибка чтения состояния " + filePath + ": " + error.Message);
                return false;
            }
        }

        private void ApplyField(string key, string value)
        {
            if (key == "lastRebalanceDate")
            {
                LastRebalanceDate = StringToDate(value);
            }
            else if (key == "lastScheduledDate")
            {
                LastScheduledDate = StringToDate(value);
            }
            else if (key == "lastLadderDate")
            {
                LastLadderDate = StringToDate(value);
            }
            else if (key == "lastLevel")
            {
                int level;

                if (int.TryParse(value, out level))
                {
                    LastLevel = level;
                }
            }
            else if (key == "lastLoad")
            {
                decimal load;

                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out load))
                {
                    LastLoad = load;
                }
            }
            else if (key == "pendingBuysTime")
            {
                PendingBuysTime = StringToTime(value);
            }
            else if (key == "pendingReason")
            {
                PendingReason = value;
            }
            else if (key == "pendingBuys")
            {
                PendingBuys = StringToMap(value);
            }
            else if (key == "pendingDiv")
            {
                PendingDividends = StringToMap(value);
            }
            else if (key == "ladderSaved")
            {
                LadderSaved = value == "1";
            }
            else if (key == "ladderPeak")
            {
                decimal peak;

                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out peak))
                {
                    LadderPeak = peak;
                }
            }
            else if (key == "ladderTrough")
            {
                decimal trough;

                if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out trough))
                {
                    LadderTrough = trough;
                }
            }
            else if (key == "ladderTroughDate")
            {
                LadderTroughDate = StringToDate(value);
            }
            else if (key == "ladderLevelEnterDate")
            {
                LadderLevelEnterDate = StringToDate(value);
            }
            else if (key == "lastDividendsUpdate")
            {
                LastDividendsUpdateDate = StringToTime(value);
            }
        }

        private void CopyFrom(KorovinBotState other)
        {
            LastRebalanceDate = other.LastRebalanceDate;
            LastScheduledDate = other.LastScheduledDate;
            LastLadderDate = other.LastLadderDate;
            LastLevel = other.LastLevel;
            LastLoad = other.LastLoad;
            PendingBuysTime = other.PendingBuysTime;
            PendingReason = other.PendingReason;
            PendingBuys = other.PendingBuys;
            PendingDividends = other.PendingDividends;
            LadderSaved = other.LadderSaved;
            LadderPeak = other.LadderPeak;
            LadderTrough = other.LadderTrough;
            LadderTroughDate = other.LadderTroughDate;
            LadderLevelEnterDate = other.LadderLevelEnterDate;
            LastDividendsUpdateDate = other.LastDividendsUpdateDate;
        }

        public void Delete(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception)
            {
                // ignore
            }
        }

        public void Clear()
        {
            LastRebalanceDate = DateTime.MinValue;
            LastScheduledDate = DateTime.MinValue;
            LastLadderDate = DateTime.MinValue;
            LastLevel = 0;
            LastLoad = 0;
            PendingBuys = new Dictionary<string, decimal>();
            PendingBuysTime = DateTime.MinValue;
            PendingReason = "";
            PendingDividends = new Dictionary<string, decimal>();
            LadderSaved = false;
            LadderPeak = 0;
            LadderTrough = 0;
            LadderTroughDate = DateTime.MinValue;
            LadderLevelEnterDate = DateTime.MinValue;
            LastDividendsUpdateDate = DateTime.MinValue;
        }

        #endregion

        #region Index history cache

        /// <summary>
        /// Сохранить дневной ряд индекса. Нужен в реальной торговле, когда коннектор
        /// отдаёт историю короче, чем требуется лестнице.
        ///
        /// Возвращает описание ошибки или пустую строку. Метод статический и своего лога
        /// не имеет, поэтому о неудаче сообщает вызывающему: молчать здесь нельзя. Замерший
        /// кэш ничем себя не выдаёт, а через несколько месяцев превращается в разрыв ряда,
        /// который склейка в Compose сошьёт без предупреждения - то есть проглоченная ошибка
        /// сама изготавливает ту самую проблему, от которой кэш и заведён
        /// </summary>
        public static string SaveIndexCache(string filePath, List<DateTime> dates, List<decimal> values)
        {
            try
            {
                if (dates == null
                    || values == null
                    || dates.Count == 0)
                {
                    return "";
                }

                using (StreamWriter writer = new StreamWriter(filePath, false))
                {
                    for (int i = 0; i < dates.Count && i < values.Count; i++)
                    {
                        writer.WriteLine(DateToString(dates[i]) + ";" +
                            values[i].ToString(CultureInfo.InvariantCulture));
                    }
                }

                return "";
            }
            catch (Exception error)
            {
                return error.Message;
            }
        }

        /// <summary>
        /// Прочитать дневной ряд индекса. Возвращает описание ошибки или пустую строку:
        /// битый кэш означает укороченную историю, и знать об этом нужно
        /// </summary>
        public static string LoadIndexCache(string filePath, List<DateTime> dates, List<decimal> values)
        {
            try
            {
                if (File.Exists(filePath) == false)
                {
                    return "";
                }

                using (StreamReader reader = new StreamReader(filePath))
                {
                    while (reader.EndOfStream == false)
                    {
                        string line = reader.ReadLine();

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        string[] parts = line.Split(';');

                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        DateTime date = StringToDate(parts[0]);

                        decimal value;

                        if (date == DateTime.MinValue
                            || decimal.TryParse(parts[1], NumberStyles.Any,
                                CultureInfo.InvariantCulture, out value) == false)
                        {
                            continue;
                        }

                        dates.Add(date);
                        values.Add(value);
                    }
                }

                return "";
            }
            catch (Exception error)
            {
                // частично прочитанный ряд хуже отсутствующего: по нему склейка встала бы
                // в случайную точку. Лучше начать с пустой истории и сказать об этом
                dates.Clear();
                values.Clear();

                return error.Message;
            }
        }

        #endregion

        #region Converters

        private static string DateToString(DateTime date)
        {
            if (date == DateTime.MinValue)
            {
                return "";
            }

            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static DateTime StringToDate(string value)
        {
            DateTime date;

            if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            {
                return date;
            }

            return DateTime.MinValue;
        }

        private static string TimeToString(DateTime time)
        {
            if (time == DateTime.MinValue)
            {
                return "";
            }

            return time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static DateTime StringToTime(string value)
        {
            DateTime time;

            if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out time))
            {
                return time;
            }

            return DateTime.MinValue;
        }

        private static string MapToString(Dictionary<string, decimal> map)
        {
            if (map == null
                || map.Count == 0)
            {
                return "";
            }

            string result = "";

            foreach (KeyValuePair<string, decimal> pair in map)
            {
                if (result.Length > 0)
                {
                    result += ";";
                }

                result += pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }

        private static Dictionary<string, decimal> StringToMap(string value)
        {
            Dictionary<string, decimal> result = new Dictionary<string, decimal>();

            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            string[] items = value.Split(';');

            for (int i = 0; i < items.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(items[i]))
                {
                    continue;
                }

                string[] parts = items[i].Split(':');

                if (parts.Length < 2)
                {
                    continue;
                }

                decimal money;

                if (decimal.TryParse(parts[1], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out money) == false)
                {
                    continue;
                }

                if (result.ContainsKey(parts[0]) == false)
                {
                    result.Add(parts[0], money);
                }
            }

            return result;
        }

        #endregion
    }
}
