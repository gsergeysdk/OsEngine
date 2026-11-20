using OsEngine.Market.Connectors;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Tab;
using System.Collections.Generic;

namespace OsEngine.Robots.Helpers
{
    /// <summary>
    /// Server connection status of all robot sources for the Telegram status message.
    /// Unlike IsConnected of screener and index, it does not require candle series
    /// of every nested security - only the server connection
    /// - Статус подключения серверов всех источников робота для статуса в Телеграм.
    /// В отличие от IsConnected скринера и индекса не требует свечных серий
    /// по каждой вложенной бумаге - только подключения сервера
    /// </summary>
    public static class SDKServerStatus
    {
        /// <summary>
        /// Connect - all servers of configured sources are connected,
        /// otherwise the status of the first not connected one; Empty - no configured sources
        /// - Connect - подключены серверы всех настроенных источников,
        /// иначе статус первого неподключенного; Empty - настроенных источников нет
        /// </summary>
        public static string Get(BotPanel bot)
        {
            List<ServerConnectStatus> statuses = new List<ServerConnectStatus>();

            List<IIBotTab> tabs = bot.GetTabs();

            if (tabs != null)
            {
                IIBotTab[] tabsArray = tabs.ToArray();

                for (int i = 0; i < tabsArray.Length; i++)
                {
                    if (tabsArray[i] is BotTabSimple simple)
                    {
                        AddSimple(simple, statuses);
                    }
                    else if (tabsArray[i] is BotTabScreener screener)
                    {
                        BotTabSimple[] nested = screener.Tabs.ToArray();

                        for (int j = 0; j < nested.Length; j++)
                        {
                            AddSimple(nested[j], statuses);
                        }
                    }
                    else if (tabsArray[i] is BotTabIndex index)
                    {
                        ConnectorCandles[] connectors = index.Tabs.ToArray();

                        for (int j = 0; j < connectors.Length; j++)
                        {
                            AddConnector(connectors[j], statuses);
                        }
                    }
                }
            }

            if (statuses.Count == 0)
            {
                return "Empty";
            }

            for (int i = 0; i < statuses.Count; i++)
            {
                if (statuses[i] != ServerConnectStatus.Connect)
                {
                    return statuses[i].ToString();
                }
            }

            return ServerConnectStatus.Connect.ToString();
        }

        private static void AddSimple(BotTabSimple tab, List<ServerConnectStatus> statuses)
        {
            if (tab == null
                || tab.Connector == null
                || string.IsNullOrEmpty(tab.Connector.SecurityName))
            {
                // источник не настроен - в статусе не участвует
                return;
            }

            statuses.Add(tab.ServerStatus);
        }

        private static void AddConnector(ConnectorCandles connector, List<ServerConnectStatus> statuses)
        {
            if (connector == null
                || string.IsNullOrEmpty(connector.SecurityName))
            {
                return;
            }

            if (connector.MyServer == null)
            {
                statuses.Add(ServerConnectStatus.Disconnect);
                return;
            }

            statuses.Add(connector.MyServer.ServerStatus);
        }
    }
}
