/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OsEngine.Robots.MyRobots
{
    internal class KorovinStockUserSetting
    {
        public string Ticker { get; set; }

        public bool Enabled { get; set; }

        public decimal BaseWeight { get; set; }

        public decimal MaxNavWeight { get; set; }
    }

    internal static class KorovinStockSettingsStorage
    {
        #region Public methods

        public static List<KorovinStockUserSetting> Load(string path)
        {
            string fullPath = GetFullPath(path);
            List<KorovinStockUserSetting> result = new List<KorovinStockUserSetting>();

            if (File.Exists(fullPath) == false)
            {
                return result;
            }

            Dictionary<string, KorovinStockUserSetting> settingsByTicker =
                new Dictionary<string, KorovinStockUserSetting>(StringComparer.OrdinalIgnoreCase);
            List<string> tickerOrder = new List<string>();

            using (StreamReader reader = new StreamReader(fullPath, Encoding.UTF8, true))
            {
                string line;

                while ((line = reader.ReadLine()) != null)
                {
                    KorovinStockUserSetting setting;

                    if (TryParseLine(line, out setting) == false)
                    {
                        continue;
                    }

                    RemoveTicker(tickerOrder, setting.Ticker);
                    settingsByTicker[setting.Ticker] = setting;
                    tickerOrder.Add(setting.Ticker);
                }
            }

            for (int index = 0; index < tickerOrder.Count; index++)
            {
                result.Add(settingsByTicker[tickerOrder[index]]);
            }

            return result;
        }

        public static void Save(string path, List<KorovinStockUserSetting> settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            string fullPath = GetFullPath(path);
            string directoryPath = Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(directoryPath) == false)
            {
                Directory.CreateDirectory(directoryPath);
            }

            List<KorovinStockUserSetting> normalizedSettings = NormalizeSettings(settings);
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                WriteTempFile(tempPath, normalizedSettings);
                CommitTempFile(tempPath, fullPath);
            }
            catch (Exception error)
            {
                Exception cleanupError = TryDeleteTempFile(tempPath);

                if (cleanupError != null)
                {
                    throw new AggregateException(error, cleanupError);
                }

                throw;
            }
        }

        #endregion

        #region Parsing and normalization

        private static bool TryParseLine(string line, out KorovinStockUserSetting setting)
        {
            setting = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string trimmedLine = line.Trim();

            if (trimmedLine.StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            string[] values = line.Split('\t');

            if (values.Length != 4)
            {
                return false;
            }

            bool enabled;
            decimal baseWeight;
            decimal maxNavWeight;

            if (bool.TryParse(values[1].Trim(), out enabled) == false ||
                decimal.TryParse(values[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out baseWeight) == false ||
                decimal.TryParse(values[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maxNavWeight) == false)
            {
                return false;
            }

            return TryCreateSetting(values[0], enabled, baseWeight, maxNavWeight, out setting);
        }

        private static bool TryCreateSetting(string ticker, bool enabled, decimal baseWeight,
            decimal maxNavWeight, out KorovinStockUserSetting setting)
        {
            setting = null;
            string normalizedTicker = ticker == null ? string.Empty : ticker.Trim();

            if (string.IsNullOrWhiteSpace(normalizedTicker) ||
                normalizedTicker.StartsWith("#", StringComparison.Ordinal) ||
                normalizedTicker.IndexOfAny(new char[] { '\t', '\r', '\n' }) >= 0 ||
                baseWeight < 0 ||
                maxNavWeight < 0)
            {
                return false;
            }

            setting = new KorovinStockUserSetting
            {
                Ticker = normalizedTicker,
                Enabled = enabled,
                BaseWeight = baseWeight,
                MaxNavWeight = maxNavWeight
            };

            return true;
        }

        private static List<KorovinStockUserSetting> NormalizeSettings(List<KorovinStockUserSetting> settings)
        {
            Dictionary<string, KorovinStockUserSetting> settingsByTicker =
                new Dictionary<string, KorovinStockUserSetting>(StringComparer.OrdinalIgnoreCase);
            List<string> tickerOrder = new List<string>();

            for (int index = 0; index < settings.Count; index++)
            {
                KorovinStockUserSetting source = settings[index];
                KorovinStockUserSetting normalized;

                if (source == null ||
                    TryCreateSetting(source.Ticker, source.Enabled, source.BaseWeight, source.MaxNavWeight, out normalized) == false)
                {
                    continue;
                }

                RemoveTicker(tickerOrder, normalized.Ticker);
                settingsByTicker[normalized.Ticker] = normalized;
                tickerOrder.Add(normalized.Ticker);
            }

            List<KorovinStockUserSetting> result = new List<KorovinStockUserSetting>();

            for (int index = 0; index < tickerOrder.Count; index++)
            {
                result.Add(settingsByTicker[tickerOrder[index]]);
            }

            return result;
        }

        private static void RemoveTicker(List<string> tickers, string ticker)
        {
            for (int index = tickers.Count - 1; index >= 0; index--)
            {
                if (string.Equals(tickers[index], ticker, StringComparison.OrdinalIgnoreCase))
                {
                    tickers.RemoveAt(index);
                    return;
                }
            }
        }

        #endregion

        #region File operations

        private static string GetFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Settings path is empty.", nameof(path));
            }

            return Path.GetFullPath(path);
        }

        private static void WriteTempFile(string tempPath, List<KorovinStockUserSetting> settings)
        {
            using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.NewLine = "\r\n";
                writer.WriteLine("#SDKRebalancerByKorovinStockSettings\t1");

                for (int index = 0; index < settings.Count; index++)
                {
                    KorovinStockUserSetting setting = settings[index];
                    writer.Write(setting.Ticker);
                    writer.Write('\t');
                    writer.Write(setting.Enabled ? "true" : "false");
                    writer.Write('\t');
                    writer.Write(setting.BaseWeight.ToString("G29", CultureInfo.InvariantCulture));
                    writer.Write('\t');
                    writer.WriteLine(setting.MaxNavWeight.ToString("G29", CultureInfo.InvariantCulture));
                }

                writer.Flush();
                stream.Flush(true);
            }
        }

        private static void CommitTempFile(string tempPath, string fullPath)
        {
            if (File.Exists(fullPath) == false)
            {
                File.Move(tempPath, fullPath);
                return;
            }

            try
            {
                File.Replace(tempPath, fullPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(tempPath, fullPath, true);
            }
            catch (IOException)
            {
                if (File.Exists(tempPath) == false)
                {
                    return;
                }

                File.Move(tempPath, fullPath, true);
            }
        }

        private static Exception TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        #endregion
    }
}
