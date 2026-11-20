using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using OsEngine.Entity;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using System.Windows;

namespace OsEngine.Logging
{
    public class ServerTelegram
    {
        private static ServerTelegram _server;

        private HttpClient _httpClient;

        private string _httpClientLocker = "HttpClientLocker";

        /// <summary>
        /// Proxy type. None/Auto/Manual
        /// - Тип прокси
        /// </summary>
        public string ProxyType;

        /// <summary>
        /// Proxy value for Manual regime
        /// - Значение прокси для ручного режима
        /// </summary>
        public string Proxy;

        /// <summary>
        /// Last message Update Id
        /// -  ID последнего сообщения
        /// </summary>
        private long _lastUpdateId;

        /// <summary>
        /// Bot Token
        /// - Токен бота
        /// </summary>
        public string BotToken;

        /// <summary>
        /// Chat Id
        /// - ID чата
        /// </summary>
        public long ChatId;

        /// <summary>
        /// Processing Command from Telegram
        /// - Разрешение на обработку команд
        /// </summary>
        public bool ProcessingCommand;

        /// <summary>
        /// Shows whether the server is ready to work
        /// - Готов ли сервер к работе
        /// </summary>
        private static bool _isReady;

        /// <summary>
        /// Queue of messages
        /// - Очередь сообщений
        /// </summary>
        private ConcurrentQueue<(string, LogMessage)> _messagesQueue = new ConcurrentQueue<(string, LogMessage)>();

        public static ServerTelegram GetServer()
        {
            if (_server == null)
            {
                _server = new ServerTelegram();
            }
            return _server;
        }

        //constructor - конструктор

        private ServerTelegram()
        {
            Load();

            InitHttpClient();

            //message queue parsing thread - поток разбора очереди сообщений
            Thread worker1 = new Thread(PullMessages);
            worker1.CurrentCulture = new CultureInfo("ru-RU");
            worker1.IsBackground = true;
            worker1.Start();

            //the stream of receiving commands and messages from Telegram - поток получения команд и сообщений из Телеграм
            Thread worker2 = new Thread(PollAndHandleUpdatesAsync);
            worker2.CurrentCulture = new CultureInfo("ru-RU");
            worker2.IsBackground = true;
            worker2.Start();
        }

        /// <summary>
        /// Send message to telegram
        /// - Отправка сообщения в телеграм
        /// </summary>
        /// <param name="messageText"></param>
        private void SendMessageAsync(string messageText)
        {
            try
            {
                string replyKeyboardJson = $@"
                    {{""keyboard"": 
                    [
                        [{{""text"": ""StopAllBots""}}, {{""text"": ""StartAllBots""}}],
                        [{{""text"": ""StopBot""}}, {{""text"": ""StartBot""}}],
                        [{{""text"": ""CancelAllActiveOrders""}}, {{""text"": ""GetStatus""}}]
                    ],
                    ""resize_keyboard"": true
                    }}";

                if (!ProcessingCommand)
                {
                    replyKeyboardJson = @"{""keyboard"": [[]]}";
                }

                messageText = CheckString(messageText);
                string requestUrl = $"https://api.telegram.org/bot{BotToken}/sendMessage?chat_id={ChatId}" +
                                    $"&text={Uri.EscapeDataString(messageText)}" +
                                    $"&parse_mode=MarkdownV2" +
                                    $"&reply_markup={Uri.EscapeDataString(replyKeyboardJson)}";

                HttpResponseMessage response = _httpClient.GetAsync(requestUrl).Result;
                string responseContent = response.Content.ReadAsStringAsync().Result;
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Initialize HTTP client with actual proxy
        /// - Инициализация HTTP клиента с актуальным прокси
        /// </summary>
        public void InitHttpClient()
        {
            try
            {
                lock (_httpClientLocker)
                {
                    WebProxy proxy = GetProxy();

                    HttpClientHandler handler = new HttpClientHandler();

                    if (proxy != null)
                    {
                        handler.Proxy = proxy;
                        handler.UseProxy = true;
                    }

                    _httpClient = new HttpClient(handler);
                }
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Get web proxy by current settings
        /// - Получить веб-прокси по текущим настройкам
        /// </summary>
        private WebProxy GetProxy()
        {
            try
            {
                if (string.IsNullOrEmpty(ProxyType)
                    || ProxyType == "None")
                {
                    return null;
                }

                if (ProxyType == "Manual")
                {
                    if (string.IsNullOrEmpty(Proxy))
                    {
                        return null;
                    }

                    return ServerMaster.GetProxyManualRegime(Proxy);
                }

                if (ProxyType == "Auto")
                {
                    return ServerMaster.GetProxyAutoRegime(ServerType.TelegramNews, "ServerTelegram");
                }

                return null;
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
                return null;
            }
        }

        /// <summary>
        /// Apply proxy settings and recreate HTTP client
        /// - Применить настройки прокси и пересоздать HTTP клиент
        /// </summary>
        public void ApplyProxySettings(string proxyType, string proxy)
        {
            try
            {
                ProxyType = proxyType;
                Proxy = proxy;
                InitHttpClient();
            }
            catch
            {
                //ignore
            }
        }

        /// <summary>
        /// Poll and handle updates (commands)
        /// - Прием и обработка обновлений (команд)
        /// </summary>
        private void PollAndHandleUpdatesAsync()
        {
            while (true)
            {
                if (MainWindow.ProccesIsWorked == false)
                {
                    return;
                }

                try
                {
                    HttpResponseMessage response = _httpClient.GetAsync($"https://api.telegram.org/bot{BotToken}/getUpdates" +
                                                                        $"?offset={_lastUpdateId + 1}" +
                                                                        $"&timeout=2" +
                                                                        $"&allowed_updates=[\"message\",\"callback_query\"]").Result;

                    string responseContent = response.Content.ReadAsStringAsync().Result;

                    Response updates = JsonConvert.DeserializeAnonymousType(responseContent, new Response());

                    if (updates.result == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < updates.result.Length; i++)
                    {
                        if (updates.result[i].message != null)
                        {
                            HandleCallbackQuery(updates.result[i].message);
                        }
                        else if (updates.result[i].callback_query != null)
                        {
                            HandleBotChoice(updates.result[i].callback_query);
                        }

                        _lastUpdateId = updates.result[i].update_id;
                    }

                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// command handler
        /// - обработчик команд
        /// </summary>
        /// <param name="msg"></param>
        private void HandleCallbackQuery(Message msg)
        {
            if (msg == null)
            {
                return;
            }

            if (!ProcessingCommand)
            {
                return;
            }

            switch (msg.text)
            {
                case "StopAllBots":
                    ExecuteCommand(null, Command.StopAllBots);
                    break;
                case "StartAllBots":
                    ExecuteCommand(null, Command.StartAllBots);
                    break;
                case "StopBot":
                    SendBotChoiceMenu(Command.StopBot);
                    break;
                case "StartBot":
                    SendBotChoiceMenu(Command.StartBot);
                    break;
                case "CancelAllActiveOrders":
                    ExecuteCommand(null, Command.CancelAllActiveOrders);
                    break;
                case "GetStatus":
                    ExecuteCommand(null, Command.GetStatus);
                    break;
            }
        }

        /// <summary>
        /// Robot names of the last sent choice menu. The buttons carry an index into this array
        /// - Имена роботов последнего отправленного меню выбора. Кнопки несут индекс в этом массиве
        /// </summary>
        private string[] _botMenuNames = new string[0];

        /// <summary>
        /// Number of the last sent choice menu. Buttons of older menus are rejected
        /// - Номер последнего отправленного меню выбора. Кнопки старых меню отклоняются
        /// </summary>
        private int _botMenuId;

        /// <summary>
        /// Send inline menu to choose the robot for StopBot / StartBot
        /// - Отправить встроенное меню выбора робота для StopBot / StartBot
        /// </summary>
        private void SendBotChoiceMenu(Command cmd)
        {
            try
            {
                List<BotPanel> bots = ServerMaster.GetAllBotsFromBotStation();

                if (bots == null || bots.Count == 0)
                {
                    SendTelegramRequest("sendMessage", $"chat_id={ChatId}" +
                        $"&text={Uri.EscapeDataString("Нет запущенных роботов")}");
                    return;
                }

                BotPanel[] botsArray = bots.ToArray();
                string[] names = new string[botsArray.Length];

                StringBuilder keyboard = new StringBuilder();
                keyboard.Append("{\"inline_keyboard\":[");

                _botMenuId++;

                for (int i = 0; i < botsArray.Length; i++)
                {
                    names[i] = botsArray[i].NameStrategyUniq;

                    string buttonText = names[i];
                    string regime = GetBotRegime(botsArray[i]);

                    if (regime != null)
                    {
                        buttonText += " (" + regime + ")";
                    }

                    if (i > 0)
                    {
                        keyboard.Append(',');
                    }

                    keyboard.Append("[{\"text\":")
                        .Append(JsonConvert.ToString(buttonText))
                        .Append(",\"callback_data\":")
                        .Append(JsonConvert.ToString(cmd + "|" + _botMenuId + "|" + i))
                        .Append("}]");
                }

                keyboard.Append("]}");

                _botMenuNames = names;

                string text = cmd == Command.StopBot
                    ? "Какого робота остановить?"
                    : "Какого робота включить?";

                SendTelegramRequest("sendMessage", $"chat_id={ChatId}" +
                    $"&text={Uri.EscapeDataString(text)}" +
                    $"&reply_markup={Uri.EscapeDataString(keyboard.ToString())}");
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Value of the robot parameter "Regime", if it exists
        /// - Значение параметра робота "Regime", если он есть
        /// </summary>
        private string GetBotRegime(BotPanel bot)
        {
            try
            {
                List<IIStrategyParameter> parameters = bot.Parameters;

                for (int i = 0; i < parameters.Count; i++)
                {
                    if (parameters[i].Name == "Regime"
                        && parameters[i] is StrategyParameterString regime)
                    {
                        return regime.ValueString;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        /// <summary>
        /// Handle pressing a robot button in the choice menu
        /// - Обработка нажатия кнопки робота в меню выбора
        /// </summary>
        private void HandleBotChoice(Callback_Query query)
        {
            try
            {
                SendTelegramRequest("answerCallbackQuery", $"callback_query_id={Uri.EscapeDataString(query.id)}");

                if (!ProcessingCommand
                    || query.message == null
                    || query.message.chat == null
                    || query.message.chat.id != ChatId
                    || string.IsNullOrEmpty(query.data))
                {
                    return;
                }

                string[] parts = query.data.Split('|');

                if (parts.Length != 3
                    || !Enum.TryParse(parts[0], out Command cmd)
                    || (cmd != Command.StopBot && cmd != Command.StartBot)
                    || !int.TryParse(parts[1], out int menuId)
                    || !int.TryParse(parts[2], out int index))
                {
                    return;
                }

                string resultText;

                if (menuId != _botMenuId
                    || index < 0
                    || index >= _botMenuNames.Length)
                {
                    resultText = "Меню устарело. Запросите список роботов заново";
                }
                else
                {
                    string botName = _botMenuNames[index];
                    ExecuteCommand(botName, cmd);
                    resultText = $"Команда {cmd} отправлена роботу {botName}";
                }

                // убираем кнопки, чтобы одно меню не срабатывало дважды
                SendTelegramRequest("editMessageText", $"chat_id={ChatId}" +
                    $"&message_id={query.message.message_id}" +
                    $"&text={Uri.EscapeDataString(resultText)}");
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// Call Telegram Bot API method
        /// - Вызов метода Telegram Bot API
        /// </summary>
        private void SendTelegramRequest(string method, string query)
        {
            try
            {
                string requestUrl = $"https://api.telegram.org/bot{BotToken}/{method}?{query}";

                HttpResponseMessage response = _httpClient.GetAsync(requestUrl).Result;
                string responseContent = response.Content.ReadAsStringAsync().Result;

                if (!response.IsSuccessStatusCode)
                {
                    ServerMaster.SendNewLogMessage($"Telegram {method} error: {responseContent}", LogMessageType.Error);
                }
            }
            catch (Exception error)
            {
                ServerMaster.SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        /// <summary>
        /// message queue handling
        /// - обработка очереди сообщений
        /// </summary>
        private void PullMessages()
        {
            Thread.Sleep(500);

            while (true)
            {
                try
                {
                    if (MainWindow.ProccesIsWorked == false)
                    {
                        return;
                    }

                    if (_messagesQueue == null)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (_messagesQueue.Count == 0)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (!_isReady)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    if (!_messagesQueue.TryDequeue(out (string, LogMessage) msg))
                        continue;

                    if (msg.Item2.Type == LogMessageType.Error)
                    {
                        msg.Item2.Message = "__" + msg.Item2.Message + "__";
                    }

                    SendMessageAsync($"{msg.Item1} | {msg.Item2.Message}");
                }
                catch
                {
                    Thread.Sleep(2000);
                }
            }
        }

        /// <summary>
        /// Create command event
        /// - Создание события c командой
        /// </summary>
        /// <param name="botName"></param>
        /// <param name="cmd"></param>
        public void ExecuteCommand(string botName, Command cmd)
        {
            try
            {
                TelegramCommandEvent?.Invoke(botName, cmd);
            }
            catch (Exception ex)
            {
                ServerMaster.SendNewLogMessage(ex.ToString(), LogMessageType.Error);
            }
        }

        public event Action<string, Command> TelegramCommandEvent;

        /// <summary>
        /// Checking the message string for valid characters
        /// - проверка строки сообщения на валидные символы
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        string CheckString(string str)
        {
            Char[] symbols = str.ToCharArray();
            string newStr = "";
            for (int i = 0; i < symbols.Length; i++)
            {
                if (symbols[i] == '_' || symbols[i] == '*' || symbols[i] == '[' || symbols[i] == ']' || symbols[i] == '('
                    || symbols[i] == ')' || symbols[i] == '`' || symbols[i] == '~' || symbols[i] == '>' || symbols[i] == '#'
                    || symbols[i] == '+' || symbols[i] == '-' || symbols[i] == '=' || symbols[i] == '|'
                    || symbols[i] == '{' || symbols[i] == '}' || symbols[i] == '.' || symbols[i] == '!'
                    || symbols[i] == '\\')
                {
                    newStr += "\\" + symbols[i];
                }
                else
                {
                    newStr += symbols[i];
                }
            }
            return newStr;
        }

        /// <summary>
        /// Send message to telegram server
        /// - Отправка сообщения в сервер телеграм
        /// </summary>
        /// <param name="nameBot"></param>
        /// <param name="message"></param>
        public void Send(LogMessage message, string nameBot)
        {
            if (!_isReady)
            {
                return;
            }

            _messagesQueue.Enqueue((nameBot, message));
        }

        /// <summary>
        /// Read initial data
        /// - Загрузить установочные данные
        /// </summary>
        public void Load()
        {
            try
            {
                if (File.Exists(@"Engine\telegramSet.txt"))
                {
                    using StreamReader reader = new StreamReader(@"Engine\telegramSet.txt");
                    BotToken = reader.ReadLine();
                    ChatId = Convert.ToInt64(reader.ReadLine());
                    string isProcessingCommand = reader.ReadLine();
                    if (isProcessingCommand == "True" || isProcessingCommand == "true")
                    {
                        ProcessingCommand = true;
                    }
                    else
                    {
                        ProcessingCommand = false;
                    }

                    ProxyType = reader.ReadLine();
                    if (string.IsNullOrEmpty(ProxyType))
                    {
                        ProxyType = "None";
                    }

                    Proxy = reader.ReadLine();
                    if (Proxy == null)
                    {
                        Proxy = string.Empty;
                    }

                    _isReady = true;
                }
                else
                {
                    BotToken = string.Empty;
                    ChatId = 0;
                    ProcessingCommand = false;
                    ProxyType = "None";
                    Proxy = string.Empty;
                    _isReady = false;
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Save initial data
        /// - Cохранить установочные данные
        /// </summary>
        public void Save()
        {
            try
            {
                using StreamWriter writer = new StreamWriter(@"Engine\telegramSet.txt");
                writer.WriteLine(BotToken);
                writer.WriteLine(ChatId);
                writer.WriteLine(ProcessingCommand);
                writer.WriteLine(ProxyType);
                writer.WriteLine(Proxy);

                if (string.IsNullOrEmpty(BotToken) == false &&
                    ChatId != 0)
                {
                    _isReady = true;
                }
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Show settings window
        /// - Показать окно настроек
        /// </summary>
        public void ShowDialog()
        {
            ServerTelegramDeliveryUi ui = new ServerTelegramDeliveryUi();
            ui.ShowDialog();
        }
    }

    #region Helper

    public class Response
    {
        public bool ok { get; set; }
        public ResultTelegram[] result { get; set; }
    }

    public class ResultTelegram
    {
        public int update_id { get; set; }
        public Callback_Query callback_query { get; set; }
        public Message message { get; set; }
    }

    public class Callback_Query
    {
        public string id { get; set; }
        public From from { get; set; }
        public Message message { get; set; }
        public string chat_instance { get; set; }
        public string data { get; set; }
    }

    public class From
    {
        public long id { get; set; }
        public bool is_bot { get; set; }
        public string first_name { get; set; }
        public string last_name { get; set; }
        public string username { get; set; }
        public string language_code { get; set; }
    }

    public class Message
    {
        public int message_id { get; set; }
        public From1 from { get; set; }
        public Chat chat { get; set; }
        public int date { get; set; }
        public string text { get; set; }
        public Reply_Markup reply_markup { get; set; }
    }

    public class From1
    {
        public long id { get; set; }
        public bool is_bot { get; set; }
        public string first_name { get; set; }
        public string username { get; set; }
    }

    public class Chat
    {
        public long id { get; set; }
        public string first_name { get; set; }
        public string last_name { get; set; }
        public string username { get; set; }
        public string type { get; set; }
    }

    public class Reply_Markup
    {
        public Inline_Keyboard[][] inline_keyboard { get; set; }
    }

    public class Inline_Keyboard
    {
        public string text { get; set; }
        public string callback_data { get; set; }
    }

    public enum Mode
    {
        Off,
        On
    }

    /// <summary>
    /// Commands sent to the bot
    /// - Посылаемые боту команды
    /// </summary>
    public enum Command
    {
        None,
        StopBot,
        StartBot,
        StopAllBots,
        StartAllBots,
        CancelActiveOrdersForBot,
        CancelAllActiveOrders,
        GetStatus
    }

    #endregion
}
