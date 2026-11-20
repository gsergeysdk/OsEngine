using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Robots.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;

/* Описание
Трендовый робот-скринер для крипты.

Источники:
1. Скринер торгового таймфрейма: PriceChannel и Ssma (свои для лонга и для шорта).
2. Скринер дневного таймфрейма (те же инструменты): ATR и индикаторы дневного тренда SsmaSlopeAtr, LinRegSlopeAtr
   (скрипты Custom\Indicators\Scripts). Инструменты сопоставляются по имени бумаги.

Режим: Off / On (лонг и шорт) / OnlyLong / OnlyShort / OnlyClosePosition.

Тренд - наклон Ssma торгового таймфрейма, своя Ssma для каждой стороны:
- лонг: Ssma("Long ssma length") выше своего значения "Long ssma slope bars" свечей назад
  больше чем на "Long ssma slope min %" (в % от того значения);
- шорт: Ssma("Short ssma length") ниже своего значения "Short ssma slope bars" свечей назад
  больше чем на "Short ssma slope min %".
Порог 0 - достаточно любого роста / падения. Ssma - сглаживание Уайлдера, её изменение за свечу
= (Close - Ssma) / N, поэтому порог p% за k свечей примерно означает: цена в среднем выше (ниже) Ssma на p * N / k %.

Дневной тренд - дополнительный фильтр, свой для каждой стороны (группы "Long daily trend" и "Short daily trend",
"Long daily trend type" / "Short daily trend type": Off / SsmaSlopeAtr / LinRegSlopeAtr). Значение индикатора дневного
скринера берётся по последней дневке, завершённой к моменту решения:
- SsmaSlopeAtr = (Ssma("... daily ssma length") - она же k = "... daily ssma slope days" дней назад)
  / сумма ATR за те же k дней, то есть средний наклон Ssma за день в долях дневного ATR.
  Ssma - сглаживание Уайлдера: Ssma(t) - Ssma(t-1) = (Close(t) - Ssma(t-1)) / N, поэтому знак индикатора -
  знак суммы (Close - Ssma) за k дней: это фильтр "цена выше / ниже Ssma", а не наклон тренда.
  Значение ~ среднее расстояние от цены до Ssma в ATR / N: смысл порога зависит от длины Ssma;
- LinRegSlopeAtr = наклон линейной регрессии закрытий за "... daily linreg length" дней (за день) / ATR -
  настоящий наклон окна;
  ATR - "... daily trend atr length".
Типичные значения обоих индикаторов на крипте ±0.1, пороги подбираются в диапазоне -0.2..0.2 с шагом 0.01.
Лонг разрешён, если значение выше "Long daily ... slope min" (может быть отрицательным),
шорт - если ниже "Short daily ... slope max". Индикатор не готов (мало дневок) - вход не открывается.

Параметры индикаторов (периоды, тип ATR, точка свечи Ssma, линии порога) сверяются с параметрами робота
перед каждым использованием: изменённый вручную на графике индикатор приводится к настройкам робота и пересчитывается.

Лонг:
1. Свеча закрылась выше верхней линии PriceChannel (линия по предыдущей свече).
2. Ssma лонга растёт.

Шорт (свои параметры, группа "Short"):
1. Свеча закрылась ниже нижней линии своего PriceChannel "Short channel entry" (линия по предыдущей свече).
2. Ssma шорта падает.

Фильтр волатильности: если "Max daily ATR %" (для шорта "Short max daily ATR %") > 0 и дневной ATR
в % от закрытия той же дневки больше него, сигнал пропускается (на крипте лонги при высокой волатильности убыточны).

Стоп: сразу после открытия, от цены входа на расстоянии "Atr stop multiplier"
(для шорта "Short atr stop multiplier") * дневной ATR. Уровень не двигается.
В реале на коннекторе с серверными стопами (Bybit) стоп - условный рыночный ордер биржи (reduceOnly) на весь
открытый объём: срабатывает, даже если OsEngine отключён. После частичной фиксации переставляется на остаток.
Если цена уже за уровнем стопа (биржа не примет условный ордер) - позиция закрывается по рынку.
В тестере и на коннекторах без серверных стопов - стоп OsEngine.

Исполнение (группа "Orders"), "Order execution":
- LimitThenMarket - вход, выход по каналу и частичная фиксация лимиткой по лучшей цене своей стороны стакана
  (покупка - bid, продажа - ask: комиссия мейкера). Через "Limit wait seconds" лимитка снимается, неисполненный
  остаток добивается рынком. В реале остаток считается по исполненному объёму лимитки с биржи после того,
  как биржа подтвердила, что лимитка не активна; исполненный объём неизвестен - вход и фиксация
  не добиваются (выход по каналу добивается: ордера закрытия на Bybit reduceOnly). Если на набранной
  части уже сработал стоп, набор прекращается без добивки. В тестере лимитка ставится по закрытию
  сигнальной свечи и ждёт одну свечу;
- Market - всё по рынку.
Пока лимитка инструмента не исполнена, новых решений по нему робот не принимает.
Режим Off / OnlyClosePosition или аварийная остановка: ожидающий вход снимается без добивки.
В реале раз в 5 секунд робот проверяет стоп у каждой открытой позиции (в том числе на частично набранный
объём - стоп переставляется по мере набора) и берёт в сопровождение лимитки без него (после перезапуска).
Сопровождение позиций OsEngine (снятие по времени, DoubleExit, стоп/профит вкладки) робот выключает на своих
вкладках: снятие последнего ордера закрытия (перестановка стопа) перевело бы позицию в ClosingFail,
и DoubleExit закрыл бы её по рынку.

Выход по каналу: лонг закрывается, когда свеча закрылась ниже нижней линии канала,
шорт — когда свеча закрылась выше верхней линии своего канала "Short channel exit".
Линия берётся самая строгая за "Exit channel bars" предыдущих свечей (для лонга - максимум нижней линии,
для шорта - минимум верхней): тени, отодвигающие канал от цены, не откладывают выход. 1 - линия по предыдущей свече.

Объём: как в других SDK-роботах (SDKVolume: контракты / валюта контракта / % депозита),
но не больше объёма, при котором убыток по стопу превысит "Max loss by stop %" от депозита.

Частичная фиксация (группа "Take profit", свой набор для каждой стороны), режим "Long take profit mode"
("Short take profit mode"): Off / Once / Ladder. Когда свеча закрылась с прибылью от цены входа не меньше уровня,
по рынку закрывается "Long take profit part %" ("Short take profit part %") от оставшегося объёма:
- Once - один раз за позицию, уровень "Long take profit %" ("Short take profit %");
- Ladder - лестница: первая ступень на "... take profit %", каждая следующая выше на "... take profit step %"
  (10% и шаг 5% - ступени 10%, 15%, 20%, ...). За свечу закрывается не больше одной ступени; если цена
  перескочила несколько уровней, следующие ступени закрываются на следующих свечах, пока прибыль выше их.
Остаток живёт по обычным правилам: тот же стоп и выход по каналу. "... take profit %" = 0 - фиксация выключена.
Число ступеней хранится в комментарии позиции ("TpSteps=N"): ступень может исполниться двумя ордерами.
*/

namespace OsEngine.Robots.SDKRobots
{
    [Bot("SDKPriceChannelCrypto")]
    public class SDKPriceChannelCrypto : BotPanel
    {
        private BotTabScreener _tradeScreener;
        private BotTabScreener _dailyScreener;

        // Базовые настройки
        private StrategyParameterString _regime;
        private StrategyParameterInt _maxPositions;

        // Индикаторы торгового таймфрейма
        private StrategyParameterInt _pcLengthUp;
        private StrategyParameterInt _pcLengthDown;
        private StrategyParameterInt _exitChannelBars;
        private StrategyParameterInt _longSsmaLength;
        private StrategyParameterInt _longSsmaSlopeBars;
        private StrategyParameterDecimal _longSsmaSlopeMin;

        // Дневной ATR и стоп
        private StrategyParameterInt _atrLength;
        private StrategyParameterDecimal _atrStopMultiplier;
        private StrategyParameterDecimal _maxDailyAtrPercent;

        // Шорт: свой канал, стоп и фильтр волатильности
        private StrategyParameterInt _shortChannelEntry;
        private StrategyParameterInt _shortChannelExit;
        private StrategyParameterDecimal _shortAtrStopMultiplier;
        private StrategyParameterDecimal _shortMaxDailyAtrPercent;
        private StrategyParameterInt _shortSsmaLength;
        private StrategyParameterInt _shortSsmaSlopeBars;
        private StrategyParameterDecimal _shortSsmaSlopeMin;

        // Дневной тренд по сторонам: тип индикатора, его периоды и порог
        private DailyTrendSettings _longDailyTrend;
        private DailyTrendSettings _shortDailyTrend;

        // Настройки дневного тренда одной стороны и номера её индикаторов на дневном скринере
        private class DailyTrendSettings
        {
            public bool IsLong;
            public int SsmaNum;
            public int LinRegNum;
            public StrategyParameterString Type;
            public StrategyParameterInt AtrLength;
            public StrategyParameterInt SsmaLength;
            public StrategyParameterInt SsmaSlopeDays;
            public StrategyParameterDecimal SsmaThreshold;
            public StrategyParameterInt LinRegLength;
            public StrategyParameterDecimal LinRegThreshold;
        }

        // Номера индикаторов в скринере. Индикаторы ищутся по имени (номер + тип): у роботов, созданных
        // до удаления TrandPhase, в сохранённом списке скринера остались старые индикаторы, и индексы сдвинуты
        private const int PcNum = 1;
        private const int PcShortNum = 3;
        private const int LongSsmaNum = 4;
        private const int ShortSsmaNum = 5;

        // Индикаторы дневного тренда на дневном скринере (номера 2 и 3 там раньше занимал глобальный тренд):
        // 6, 7 - лонг, 8, 9 - шорт
        private const int LongDailySsmaSlopeNum = 6;
        private const int LongDailyLinRegSlopeNum = 7;
        private const int ShortDailySsmaSlopeNum = 8;
        private const int ShortDailyLinRegSlopeNum = 9;

        // Риск
        private StrategyParameterDecimal _maxLossByStopPercent;

        // Частичная фиксация по сторонам: режим, прибыль в % от цены входа, шаг лестницы
        // и доля закрываемого объёма от оставшегося
        private StrategyParameterString _longTakeProfitMode;
        private StrategyParameterDecimal _longTakeProfitPercent;
        private StrategyParameterDecimal _longTakeProfitStep;
        private StrategyParameterDecimal _longTakeProfitPart;
        private StrategyParameterString _shortTakeProfitMode;
        private StrategyParameterDecimal _shortTakeProfitPercent;
        private StrategyParameterDecimal _shortTakeProfitStep;
        private StrategyParameterDecimal _shortTakeProfitPart;

        // Исполнение ордеров: рынок или лимитка с добивкой рынком
        private StrategyParameterString _orderExecution;
        private StrategyParameterInt _limitWaitSeconds;

        // Лимитки, которые ждут исполнения: по истечении времени снимаются, остаток добивается рынком
        private class PendingExecution
        {
            public BotTabSimple Tab;
            public Position Position;
            public Order Order;
            public string Kind;
            public decimal Volume;
            public DateTime StartTime;
            public DateTime CancelTime;
            public DateTime FinalTime;

            // статус снятой лимитки подтверждён запросом к бирже (в реале)
            public bool ExchangeConfirmed;
            public int ExchangeChecks;

            // исполненный объём лимитки по данным биржи, -1 - неизвестен
            public decimal ExchangeExecuted = -1;
        }

        private const string PendingOpen = "Open";
        private const string PendingClose = "Close";
        private const string PendingPartial = "PartialTp";

        private readonly List<PendingExecution> _pending = new List<PendingExecution>();
        private readonly object _pendingLocker = new object();

        private volatile bool _isDeleted;

        private bool _manualSupportDisabledLogged;

        public SDKVolume volume;

        public SDKPriceChannelCrypto(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);
            _tradeScreener = TabsScreener[0];

            TabCreate(BotTabType.Screener);
            _dailyScreener = TabsScreener[1];

            // Базовые настройки
            _regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort", "OnlyClosePosition" }, "Base");
            _maxPositions = CreateParameter("Max positions", 20, 1, 50, 1, "Base");

            // Значения по умолчанию - итоговый подбор пользователя 2026-11-19
            // (Research/SDKPriceChannelTrendPhase/Parameters_final.txt), 1ч CryptoTop, режим On, объём 15%, риск 1%

            // Индикаторы торгового таймфрейма
            _pcLengthUp = CreateParameter("Price channel length up", 120, 10, 300, 10, "Base");
            _pcLengthDown = CreateParameter("Price channel length down", 50, 10, 300, 10, "Base");

            // Выход по каналу (лонг и шорт): сколько предыдущих свечей просматривать, берётся самая строгая линия
            _exitChannelBars = CreateParameter("Exit channel bars", 5, 1, 20, 1, "Base");

            // Тренд лонга - рост Ssma
            _longSsmaLength = CreateParameter("Long ssma length", 340, 20, 1000, 20, "Base");
            _longSsmaSlopeBars = CreateParameter("Long ssma slope bars", 12, 1, 100, 1, "Base");

            // Порог изменения Ssma за "slope bars" свечей в % (лонг и шорт свои). 0 - любой наклон
            _longSsmaSlopeMin = CreateParameter("Long ssma slope min %", 0.02m, 0m, 1m, 0.01m, "Base");

            // Дневной ATR и стоп
            _atrLength = CreateParameter("Daily atr length", 14, 5, 50, 1, "Base");
            _atrStopMultiplier = CreateParameter("Atr stop multiplier", 1.7m, 0.5m, 5m, 0.5m, "Base");

            // Фильтр волатильности: максимальный дневной ATR в % от цены. 0 - без фильтра
            _maxDailyAtrPercent = CreateParameter("Max daily ATR %", 5.4m, 0m, 20m, 0.5m, "Base");

            // Шорт: свой канал, стоп и фильтр волатильности
            _shortChannelEntry = CreateParameter("Short channel entry", 40, 10, 300, 10, "Short");
            _shortChannelExit = CreateParameter("Short channel exit", 130, 10, 300, 10, "Short");
            _shortAtrStopMultiplier = CreateParameter("Short atr stop multiplier", 0.7m, 0.5m, 5m, 0.5m, "Short");
            _shortMaxDailyAtrPercent = CreateParameter("Short max daily ATR %", 6m, 0m, 20m, 0.5m, "Short");

            // Тренд шорта - падение Ssma
            _shortSsmaLength = CreateParameter("Short ssma length", 380, 20, 1000, 20, "Short");
            _shortSsmaSlopeBars = CreateParameter("Short ssma slope bars", 12, 1, 100, 1, "Short");
            _shortSsmaSlopeMin = CreateParameter("Short ssma slope min %", 0.02m, 0m, 1m, 0.01m, "Short");

            // Дневной тренд, свой для каждой стороны: Off - фильтр выключен.
            // Пороги - в единицах индикатора (наклон в долях дневного ATR)
            _longDailyTrend = CreateDailyTrendSettings(true, "Long", "min", LongDailySsmaSlopeNum, LongDailyLinRegSlopeNum,
                "Off", 15, 4, -0.04m, 20, 0m);
            _shortDailyTrend = CreateDailyTrendSettings(false, "Short", "max", ShortDailySsmaSlopeNum, ShortDailyLinRegSlopeNum,
                "LinRegSlopeAtr", 15, 4, -0.04m, 35, -0.02m);

            volume = new SDKVolume(this);

            // Риск: максимальный убыток по стопу в % от депозита. 0 - без ограничения
            _maxLossByStopPercent = CreateParameter("Max loss by stop %", 1m, 0m, 5m, 0.5m, "Volume");

            // Частичная фиксация: снимает откат больших плавающих прибылей при общем откате рынка.
            // "... take profit %" = 0 или режим Off - выключена
            _longTakeProfitMode = CreateParameter("Long take profit mode", "Off", new[] { "Off", "Once", "Ladder" }, "Take profit");
            _longTakeProfitPercent = CreateParameter("Long take profit %", 0m, 0m, 100m, 5m, "Take profit");
            _longTakeProfitStep = CreateParameter("Long take profit step %", 10m, 1m, 100m, 5m, "Take profit");
            _longTakeProfitPart = CreateParameter("Long take profit part %", 90m, 10m, 100m, 10m, "Take profit");
            _shortTakeProfitMode = CreateParameter("Short take profit mode", "Ladder", new[] { "Off", "Once", "Ladder" }, "Take profit");
            _shortTakeProfitPercent = CreateParameter("Short take profit %", 10m, 0m, 100m, 5m, "Take profit");
            _shortTakeProfitStep = CreateParameter("Short take profit step %", 5m, 1m, 100m, 5m, "Take profit");
            _shortTakeProfitPart = CreateParameter("Short take profit part %", 50m, 10m, 100m, 10m, "Take profit");

            // Исполнение: LimitThenMarket - вход, выход по каналу и фиксация лимиткой по лучшей цене стакана
            // (комиссия мейкера), через "Limit wait seconds" остаток добивается рынком. Market - всё по рынку
            _orderExecution = CreateParameter("Order execution", "LimitThenMarket", new[] { "Market", "LimitThenMarket" }, "Orders");
            _limitWaitSeconds = CreateParameter("Limit wait seconds", 30, 5, 600, 5, "Orders");

            _tradeScreener.CreateCandleIndicator(PcNum, "PriceChannel", GetPcParameters(), "Prime");
            _tradeScreener.CreateCandleIndicator(PcShortNum, "PriceChannel", GetPcShortParameters(), "Prime");
            _tradeScreener.CreateCandleIndicator(LongSsmaNum, "Ssma", GetSsmaParameters(_longSsmaLength), "Prime");
            _tradeScreener.CreateCandleIndicator(ShortSsmaNum, "Ssma", GetSsmaParameters(_shortSsmaLength), "Prime");

            _dailyScreener.CreateCandleIndicator(1, "ATR", GetAtrParameters(), "AtrArea");
            // Оба индикатора тренда каждой стороны строятся всегда, используется выбранный в "... daily trend type"
            _dailyScreener.CreateCandleIndicator(LongDailySsmaSlopeNum, "SsmaSlopeAtr", GetSsmaSlopeAtrParameters(_longDailyTrend), "TrendArea");
            _dailyScreener.CreateCandleIndicator(LongDailyLinRegSlopeNum, "LinRegSlopeAtr", GetLinRegSlopeAtrParameters(_longDailyTrend), "TrendArea");
            _dailyScreener.CreateCandleIndicator(ShortDailySsmaSlopeNum, "SsmaSlopeAtr", GetSsmaSlopeAtrParameters(_shortDailyTrend), "ShortTrendArea");
            _dailyScreener.CreateCandleIndicator(ShortDailyLinRegSlopeNum, "LinRegSlopeAtr", GetLinRegSlopeAtrParameters(_shortDailyTrend), "ShortTrendArea");

            _tradeScreener.CandleFinishedEvent += _tradeScreener_CandleFinishedEvent;
            _tradeScreener.PositionOpeningSuccesEvent += _tradeScreener_PositionOpeningSuccesEvent;
            _tradeScreener.PositionNetVolumeChangeEvent += _tradeScreener_PositionNetVolumeChangeEvent;
            _tradeScreener.TestStartEvent += _tradeScreener_TestStartEvent;

            ParametrsChangeByUser += SDKPriceChannelCrypto_ParametrsChangeByUser;
            DeleteEvent += SDKPriceChannelCrypto_DeleteEvent;

            // В реале лимитки сопровождаются раз в секунду, в тестере - на закрытии следующей свечи
            if (startProgram == StartProgram.IsOsTrader)
            {
                Thread worker = new Thread(PendingWorker);
                worker.IsBackground = true;
                worker.Name = "SDKPriceChannelCryptoPending";
                worker.Start();

                // Команды из Телеграма: старт/стоп робота и статус
                ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;
            }
        }

        private void SDKPriceChannelCrypto_DeleteEvent()
        {
            _isDeleted = true;

            if (StartProgram == StartProgram.IsOsTrader)
            {
                ServerTelegram.GetServer().TelegramCommandEvent -= TelegramCommandHandler;
            }
        }

        private string _lastRegime = "Off";

        private void TelegramCommandHandler(string botName, Command cmd)
        {
            try
            {
                if (botName != null && !NameStrategyUniq.Equals(botName))
                {
                    return;
                }

                if (cmd == Command.StopAllBots || cmd == Command.StopBot)
                {
                    _lastRegime = _regime.ValueString;
                    _regime.ValueString = "Off";

                    SendNewLogMessage($"Changed Bot {NameStrategyUniq} Regime to {_regime.ValueString} " +
                                      $"by telegram command {cmd}", LogMessageType.User);
                }
                else if ((cmd == Command.StartAllBots || cmd == Command.StartBot)
                    && _regime.ValueString == "Off")
                {
                    _regime.ValueString = _lastRegime != "Off" ? _lastRegime : "On";

                    SendNewLogMessage($"Changed bot {NameStrategyUniq} mode to state {_regime.ValueString} " +
                                      $"by telegram command {cmd}", LogMessageType.User);
                }
                else if (cmd == Command.GetStatus)
                {
                    SendTelegramStatus(_tradeScreener.IsConnected && _dailyScreener.IsConnected);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void SendTelegramStatus(bool isConnected)
        {
            List<Journal.Journal> journals = GetJournals();

            int count = 0;
            decimal profit = 0;
            decimal inputs = 0;

            for (int j = 0; j < journals.Count; j++)
            {
                List<Position> openPositions = journals[j].OpenPositions;

                for (int i = 0; i < openPositions.Count; i++)
                {
                    Position position = openPositions[i];
                    count++;
                    profit += position.ProfitPortfolioAbs;
                    inputs += position.OpenVolume * position.EntryPrice * position.Lots;
                }
            }

            SendNewLogMessage($"\nBot {NameStrategyUniq} is {_regime.ValueString}.\n" +
                              $"Server Status - {(isConnected ? "Connected" : "Empty")}.\n" +
                              $"Positions count {count}.\n" +
                              $"Total invested {inputs.ToString("F2")}.\n" +
                              $"Profit for all {profit.ToString("F2")}.\n"
                              , LogMessageType.User);
        }

        private void _tradeScreener_TestStartEvent()
        {
            lock (_pendingLocker)
            {
                _pending.Clear();
            }
        }

        // Раз в столько проходов потока (секунд) - подбор лимиток без сопровождения и проверка стопов
        private const int ProtectionCheckPasses = 5;

        private void PendingWorker()
        {
            int pass = 0;

            while (true)
            {
                try
                {
                    Thread.Sleep(1000);

                    if (_isDeleted)
                    {
                        return;
                    }

                    // и в режиме Off: уже выставленную лимитку нужно довести до конца
                    ProcessPendingExecutions(null);

                    pass++;

                    if (pass >= ProtectionCheckPasses)
                    {
                        pass = 0;
                        AdoptOrphanLimits();
                        CheckProtection();
                    }
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        public override string GetNameStrategyType()
        {
            return "SDKPriceChannelCrypto";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        private void SDKPriceChannelCrypto_ParametrsChangeByUser()
        {
            SetScreenerIndicatorParameters(_tradeScreener, PcNum, GetPcParameters());
            SetScreenerIndicatorParameters(_tradeScreener, PcShortNum, GetPcShortParameters());
            SetScreenerIndicatorParameters(_tradeScreener, LongSsmaNum, GetSsmaParameters(_longSsmaLength));
            SetScreenerIndicatorParameters(_tradeScreener, ShortSsmaNum, GetSsmaParameters(_shortSsmaLength));
            _tradeScreener.UpdateIndicatorsParameters();

            SetScreenerIndicatorParameters(_dailyScreener, 1, GetAtrParameters());
            SetScreenerIndicatorParameters(_dailyScreener, LongDailySsmaSlopeNum, GetSsmaSlopeAtrParameters(_longDailyTrend));
            SetScreenerIndicatorParameters(_dailyScreener, LongDailyLinRegSlopeNum, GetLinRegSlopeAtrParameters(_longDailyTrend));
            SetScreenerIndicatorParameters(_dailyScreener, ShortDailySsmaSlopeNum, GetSsmaSlopeAtrParameters(_shortDailyTrend));
            SetScreenerIndicatorParameters(_dailyScreener, ShortDailyLinRegSlopeNum, GetLinRegSlopeAtrParameters(_shortDailyTrend));
            _dailyScreener.UpdateIndicatorsParameters();
        }

        // Параметры дневного тренда одной стороны (side - "Long"/"Short", bound - "min"/"max" в имени порога)
        // Значения по умолчанию задаются отдельно для каждой стороны
        private DailyTrendSettings CreateDailyTrendSettings(bool isLong, string side, string bound, int ssmaNum, int linRegNum,
            string type, int ssmaLength, int ssmaSlopeDays, decimal ssmaThreshold, int linRegLength, decimal linRegThreshold)
        {
            string group = side + " daily trend";

            DailyTrendSettings s = new DailyTrendSettings();
            s.IsLong = isLong;
            s.SsmaNum = ssmaNum;
            s.LinRegNum = linRegNum;
            s.Type = CreateParameter(side + " daily trend type", type, new[] { "Off", "SsmaSlopeAtr", "LinRegSlopeAtr" }, group);
            s.AtrLength = CreateParameter(side + " daily trend atr length", 14, 5, 50, 1, group);
            s.SsmaLength = CreateParameter(side + " daily ssma length", ssmaLength, 10, 300, 10, group);
            s.SsmaSlopeDays = CreateParameter(side + " daily ssma slope days", ssmaSlopeDays, 1, 50, 1, group);
            // Пороги: типичные значения индикаторов ±0.1 (средний наклон за день в долях дневного ATR)
            s.SsmaThreshold = CreateParameter(side + " daily ssma slope " + bound, ssmaThreshold, -0.2m, 0.2m, 0.01m, group);
            s.LinRegLength = CreateParameter(side + " daily linreg length", linRegLength, 5, 300, 5, group);
            s.LinRegThreshold = CreateParameter(side + " daily linreg slope " + bound, linRegThreshold, -0.2m, 0.2m, 0.01m, group);
            return s;
        }

        // Параметры SsmaSlopeAtr: Period Ssma, Period Atr, Slope candles, Up line, Down line (обе линии - порог стороны)
        private List<string> GetSsmaSlopeAtrParameters(DailyTrendSettings s)
        {
            string threshold = s.SsmaThreshold.ValueDecimal.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return new List<string>()
            {
                s.SsmaLength.ValueInt.ToString(),
                s.AtrLength.ValueInt.ToString(),
                s.SsmaSlopeDays.ValueInt.ToString(),
                threshold,
                threshold
            };
        }

        // Параметры LinRegSlopeAtr: Period regression, Period Atr, Up line, Down line (обе линии - порог стороны)
        private List<string> GetLinRegSlopeAtrParameters(DailyTrendSettings s)
        {
            string threshold = s.LinRegThreshold.ValueDecimal.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return new List<string>()
            {
                s.LinRegLength.ValueInt.ToString(),
                s.AtrLength.ValueInt.ToString(),
                threshold,
                threshold
            };
        }

        private void SetScreenerIndicatorParameters(BotTabScreener screener, int num, List<string> parameters)
        {
            IndicatorOnTabs indicator = screener._indicators.Find(ind => ind.Num == num);

            if (indicator != null)
            {
                indicator.Parameters = parameters;
            }
        }

        // Индикатор вкладки скринера по номеру и типу (имя индикатора - номер + тип + имя скринера)
        private Aindicator FindIndicator(BotTabSimple tab, int num, string type)
        {
            if (tab.Indicators == null)
            {
                return null;
            }

            string prefix = num + type;

            for (int i = 0; i < tab.Indicators.Count; i++)
            {
                Aindicator ind = tab.Indicators[i] as Aindicator;

                if (ind != null
                    && ind.Name != null
                    && ind.Name.StartsWith(prefix))
                {
                    return ind;
                }
            }

            return null;
        }

        // Логика

        private void _tradeScreener_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            try
            {
                if (_regime.ValueString == "Off")
                {
                    return;
                }

                DisableManualSupport(tab);

                // В тестере лимитки сопровождаются здесь: свеча прошла - время ожидания истекло
                if (StartProgram != StartProgram.IsOsTrader)
                {
                    ProcessPendingExecutions(tab);
                }

                // Лимитка этого инструмента ещё не исполнена или добивается рынком - новых решений не принимаем
                if (HasPending(tab))
                {
                    return;
                }

                // Индикаторы так и не стали готовы (бывает после смены параметров - редкая гонка
                // при пересоздании индикаторов): инструмент молча не торгует - предупреждаем в логе
                if (candles.Count == NotReadyCheckCandle
                    && _regime.ValueString != "OnlyClosePosition")
                {
                    CheckTabReadyState(candles, tab);
                }

                Aindicator pc = FindIndicator(tab, PcNum, "PriceChannel");
                Aindicator pcShort = FindIndicator(tab, PcShortNum, "PriceChannel");
                Aindicator longSsma = FindIndicator(tab, LongSsmaNum, "Ssma");
                Aindicator shortSsma = FindIndicator(tab, ShortSsmaNum, "Ssma");

                if (pc == null
                    || pcShort == null
                    || longSsma == null
                    || shortSsma == null)
                {
                    return;
                }

                if (CheckTradeIndicatorsParameters(pc, pcShort, longSsma, shortSsma) == false)
                {
                    return;
                }

                if (candles.Count < 3
                    || pc.DataSeries[0].Values.Count < 2
                    || pc.DataSeries[1].Values.Count < 2
                    || pcShort.DataSeries[0].Values.Count < 2
                    || pcShort.DataSeries[1].Values.Count < 2)
                {
                    return;
                }

                List<Position> openPositions = tab.PositionsOpenAll;

                if (openPositions.Count > 0)
                {
                    SetTabReady(tab);
                }

                if (openPositions.Count == 0)
                {
                    if (_regime.ValueString == "OnlyClosePosition")
                    {
                        return;
                    }

                    LogicOpenPosition(candles, tab, pc, pcShort, longSsma, shortSsma);
                }
                else
                {
                    LogicClosePosition(candles, tab, openPositions[0], pc, pcShort);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void _tradeScreener_PositionOpeningSuccesEvent(Position position, BotTabSimple tab)
        {
            try
            {
                EnsureStop(position, tab);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void _tradeScreener_PositionNetVolumeChangeEvent(Position position, BotTabSimple tab)
        {
            try
            {
                RestoreAfterPartialClose(position, tab);
            }
            catch (Exception error)
            {
                SendNewLogMessage(error.ToString(), LogMessageType.Error);
            }
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab, Aindicator pc, Aindicator pcShort,
            Aindicator longSsma, Aindicator shortSsma)
        {
            if (_tradeScreener.PositionsOpenAll.Count >= _maxPositions.ValueInt)
            {
                return;
            }

            decimal pcUp = pc.DataSeries[0].Values[^2];
            decimal pcDown = pc.DataSeries[1].Values[^2];
            decimal pcShortDown = pcShort.DataSeries[1].Values[^2];

            if (pcUp == 0
                || pcDown == 0
                || pcShortDown == 0)
            {
                return;
            }

            decimal lastClose = candles[^1].Close;

            bool longReady = GetTrend(longSsma, _longSsmaSlopeBars.ValueInt, _longSsmaSlopeMin.ValueDecimal, out bool trendUp, out _);
            bool shortReady = GetTrend(shortSsma, _shortSsmaSlopeBars.ValueInt, _shortSsmaSlopeMin.ValueDecimal, out _, out bool trendDown);

            if (longReady == false
                && shortReady == false)
            {
                return;
            }

            SetTabReady(tab);

            Side side;

            if (lastClose > pcUp
                && trendUp
                && _regime.ValueString != "OnlyShort")
            {
                side = Side.Buy;
            }
            else if (lastClose < pcShortDown
                && trendDown
                && _regime.ValueString != "OnlyLong")
            {
                side = Side.Sell;
            }
            else
            {
                return;
            }

            if (CheckDailyTrend(tab, side) == false)
            {
                return;
            }

            decimal atr = GetDailyAtr(tab, out decimal dailyClose);

            if (atr <= 0)
            {
                SendNewLogMessage($"{tab.Security.Name}: no daily ATR, signal skipped", LogMessageType.Trade);
                return;
            }

            decimal maxAtrPercent = side == Side.Buy ? _maxDailyAtrPercent.ValueDecimal : _shortMaxDailyAtrPercent.ValueDecimal;

            // ATR в % от закрытия той же дневки: от текущей цены фильтр начинал пропускать вход
            // на сильной часовой свече, хотя волатильность не снизилась
            if (maxAtrPercent > 0
                && (dailyClose <= 0
                    || atr / dailyClose * 100m > maxAtrPercent))
            {
                return;
            }

            decimal stopDistance = atr * GetStopMultiplier(side);

            if (stopDistance <= 0)
            {
                return;
            }

            if (side == Side.Buy
                && lastClose - stopDistance <= 0)
            {
                return;
            }

            decimal vol = volume.GetVolume(tab);

            vol = LimitVolumeByStopLoss(tab, vol, stopDistance);

            if (vol <= 0)
            {
                return;
            }

            OpenPosition(tab, side, vol, lastClose);
        }

        private void LogicClosePosition(List<Candle> candles, BotTabSimple tab, Position position, Aindicator pc,
            Aindicator pcShort)
        {
            // Позиция ещё набирается или из неё уже выходят - ждём. Серверный стоп выходом не считается:
            // с ним OsEngine держит позицию в состоянии Closing, поэтому состояние позиции здесь не проверяется
            if (position.OpenVolume <= 0
                || position.OpenActive
                || HasActiveExitOrder(position))
            {
                return;
            }

            // Страховка: стоп не выставился (не было ATR, биржа отклонила, перезапуск) или не совпадает с объёмом
            RestoreAfterPartialClose(position, tab);

            decimal lastClose = candles[^1].Close;

            if (TryTakePartialProfit(tab, position, lastClose))
            {
                return;
            }

            if (position.Direction == Side.Buy)
            {
                decimal pcDown = GetExitLine(pc.DataSeries[1].Values, true);

                if (pcDown != 0
                    && lastClose < pcDown)
                {
                    ClosePosition(tab, position, position.OpenVolume, "ChannelExit", lastClose);
                }
            }
            else
            {
                decimal pcShortUp = GetExitLine(pcShort.DataSeries[0].Values, false);

                if (pcShortUp != 0
                    && lastClose > pcShortUp)
                {
                    ClosePosition(tab, position, position.OpenVolume, "ChannelExit", lastClose);
                }
            }
        }

        // Линия выхода: самая строгая из линий канала на "Exit channel bars" свечах перед текущей
        // (лонг - максимум нижней линии, шорт - минимум верхней). Неготовые значения (0) пропускаются.
        // 0 - линия не готова
        private decimal GetExitLine(List<decimal> values, bool isLong)
        {
            int bars = _exitChannelBars.ValueInt;

            if (bars < 1)
            {
                bars = 1;
            }

            decimal result = 0;

            for (int i = 2; i <= bars + 1 && i <= values.Count; i++)
            {
                decimal value = values[^i];

                if (value == 0)
                {
                    continue;
                }

                if (result == 0
                    || (isLong && value > result)
                    || (isLong == false && value < result))
                {
                    result = value;
                }
            }

            return result;
        }

        // Частичная фиксация, когда свеча закрылась с прибылью от цены входа не меньше уровня стороны.
        // Once - один раз за позицию (пока из неё ничего не закрывалось), уровень "... take profit %".
        // Ladder - уровень ступени n (с 0) = "... take profit %" + n * "... take profit step %"; n - число уже
        // исполненных частичных закрытий. За свечу закрывается не больше одной ступени.
        // true - ордер отправлен, выход по каналу на этой свече не проверяем
        private bool TryTakePartialProfit(BotTabSimple tab, Position position, decimal lastClose)
        {
            bool isLong = position.Direction == Side.Buy;

            string mode = isLong ? _longTakeProfitMode.ValueString : _shortTakeProfitMode.ValueString;
            decimal takeProfit = isLong ? _longTakeProfitPercent.ValueDecimal : _shortTakeProfitPercent.ValueDecimal;

            if (mode == "Off"
                || takeProfit <= 0
                || position.EntryPrice <= 0
                || lastClose <= 0
                || HasActiveExitOrder(position))
            {
                return false;
            }

            decimal openVolume = position.OpenVolume;

            if (openVolume <= 0)
            {
                return false;
            }

            decimal level = takeProfit;

            if (mode == "Ladder")
            {
                decimal step = isLong ? _longTakeProfitStep.ValueDecimal : _shortTakeProfitStep.ValueDecimal;

                if (step <= 0)
                {
                    return false;
                }

                level = takeProfit + GetPartialClosesCount(position) * step;
            }
            else if (openVolume < position.MaxVolume)
            {
                // Once: фиксация уже была - закрыта часть объёма
                return false;
            }

            decimal profitPercent = isLong
                ? (lastClose / position.EntryPrice - 1m) * 100m
                : (1m - lastClose / position.EntryPrice) * 100m;

            if (profitPercent < level)
            {
                return false;
            }

            decimal part = isLong ? _longTakeProfitPart.ValueDecimal : _shortTakeProfitPart.ValueDecimal;

            decimal closeVolume = part >= 100m ? openVolume : RoundVolume(tab, openVolume * part / 100m);

            if (closeVolume <= 0)
            {
                return false;
            }

            if (closeVolume > openVolume)
            {
                closeVolume = openVolume;
            }

            SetPartialClosesCount(position, GetPartialClosesCount(position) + 1);

            ClosePosition(tab, position, closeVolume, PendingPartial, lastClose);

            return true;
        }

        // Число ступеней частичной фиксации, хранится в комментарии позиции ("TpSteps=N"): ступень может
        // исполниться двумя ордерами (лимитка и добивка рынком), поэтому по ордерам не считается.
        // Позиции, открытые до появления счётчика: каждый исполненный ордер закрытия - ступень
        private const string TpStepsPrefix = "TpSteps=";

        private int GetPartialClosesCount(Position position)
        {
            string comment = position.Comment;

            if (string.IsNullOrEmpty(comment) == false
                && comment.StartsWith(TpStepsPrefix)
                && int.TryParse(comment.Substring(TpStepsPrefix.Length), out int steps))
            {
                return steps;
            }

            List<Order> closeOrders = position.CloseOrders;

            if (closeOrders == null)
            {
                return 0;
            }

            int count = 0;

            for (int i = 0; i < closeOrders.Count; i++)
            {
                if (closeOrders[i] != null
                    && IsServerStopOrder(closeOrders[i]) == false
                    && closeOrders[i].VolumeExecute > 0)
                {
                    count++;
                }
            }

            return count;
        }

        private void SetPartialClosesCount(Position position, int count)
        {
            position.Comment = TpStepsPrefix + count;
        }

        // После частичного закрытия OsEngine оставляет позицию в состоянии Closing и снимает стоп.
        // Возвращаем Open (если активных ордеров закрытия нет) и выставляем стоп заново на остаток
        private void RestoreAfterPartialClose(Position position, BotTabSimple tab)
        {
            if (position == null
                || position.OpenVolume <= 0
                || position.OpenActive
                || HasActiveExitOrder(position))
            {
                return;
            }

            if (position.State == PositionStateType.Closing
                && position.CloseActive == false)
            {
                position.State = PositionStateType.Open;
            }

            EnsureStop(position, tab);
        }

        // Свеча, к которой индикаторы любого инструмента должны быть готовы (с запасом больше самой длинной средней)
        private const int NotReadyCheckCandle = 3000;

        // Инструменты, по которым хотя бы раз дошли до проверки сигнала или есть позиция
        private readonly HashSet<string> _readyTabs = new HashSet<string>();

        private readonly object _readyTabsLocker = new object();

        private void SetTabReady(BotTabSimple tab)
        {
            lock (_readyTabsLocker)
            {
                _readyTabs.Add(tab.Connector.SecurityName);
            }
        }

        private bool IsTabReady(BotTabSimple tab)
        {
            lock (_readyTabsLocker)
            {
                return _readyTabs.Contains(tab.Connector.SecurityName);
            }
        }

        private readonly object _stateFileLocker = new object();

        // Состояние вкладки на свече NotReadyCheckCandle. Строка по каждому инструменту сразу пишется в файл
        // Engine\Log\<имя робота>_TabsState.txt (лог OsEngine пишется с задержкой и удаляется вместе с роботом).
        // Не готовый инструмент дополнительно попадает в лог робота как INDICATORS NOT READY
        private void CheckTabReadyState(List<Candle> candles, BotTabSimple tab)
        {
            bool ready = IsTabReady(tab);
            string state = BuildTabState(candles, tab);

            try
            {
                lock (_stateFileLocker)
                {
                    System.IO.File.AppendAllText(@"Engine\Log\" + NameStrategyUniq + "_TabsState.txt",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ";" + (ready ? "READY" : "NOT_READY") + ";" + state
                        + Environment.NewLine);
                }
            }
            catch (Exception error)
            {
                SendNewLogMessage("tabs state file error " + error.Message, LogMessageType.Error);
            }

            if (ready == false)
            {
                SendNewLogMessage("INDICATORS NOT READY " + state, LogMessageType.Error);
            }
        }

        // Состояние индикаторов инструмента одной строкой
        private string BuildTabState(List<Candle> candles, BotTabSimple tab)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            try
            {
                sb.Append(tab.Connector.SecurityName + " candles=" + candles.Count
                    + " time=" + candles[^1].TimeStart.ToString("yyyy-MM-dd HH:mm")
                    + " close=" + candles[^1].Close
                    + " positions=" + tab.PositionsAll.Count);

                if (tab.Indicators == null)
                {
                    sb.Append(" indicators=null");
                    return sb.ToString();
                }

                sb.Append(" indicators=" + tab.Indicators.Count);

                for (int i = 0; i < tab.Indicators.Count; i++)
                {
                    Aindicator ind = (Aindicator)tab.Indicators[i];
                    sb.Append(" [" + i + " " + ind.Name + " type=" + ind.GetType().Name);
                    sb.Append(" pd=" + (ind.ParametersDigit == null ? "null" : ind.ParametersDigit.Count.ToString()));

                    if (ind.ParametersDigit != null)
                    {
                        for (int j = 0; j < ind.ParametersDigit.Count; j++)
                        {
                            sb.Append(":" + ind.ParametersDigit[j].Value);
                        }
                    }

                    if (ind.DataSeries != null)
                    {
                        for (int s = 0; s < ind.DataSeries.Count && s < 2; s++)
                        {
                            sb.Append(" s" + s + "=" + ind.DataSeries[s].Values.Count + "/" + ind.DataSeries[s].Last);
                        }
                    }

                    sb.Append("]");
                }

                Aindicator pc = FindIndicator(tab, PcNum, "PriceChannel");
                Aindicator pcShort = FindIndicator(tab, PcShortNum, "PriceChannel");
                Aindicator longSsma = FindIndicator(tab, LongSsmaNum, "Ssma");
                Aindicator shortSsma = FindIndicator(tab, ShortSsmaNum, "Ssma");

                if (pc != null
                    && pcShort != null
                    && longSsma != null
                    && shortSsma != null)
                {
                    sb.Append(" check=" + CheckTradeIndicatorsParameters(pc, pcShort, longSsma, shortSsma));
                    bool longReady = GetTrend(longSsma, _longSsmaSlopeBars.ValueInt, _longSsmaSlopeMin.ValueDecimal, out bool up, out _);
                    bool shortReady = GetTrend(shortSsma, _shortSsmaSlopeBars.ValueInt, _shortSsmaSlopeMin.ValueDecimal, out _, out bool down);
                    sb.Append(" long=" + longReady + "/" + up + " short=" + shortReady + "/" + down);
                }
                else
                {
                    sb.Append(" indicators not found");
                }
            }
            catch (Exception error)
            {
                sb.Append(" state error " + error.Message);
            }

            return sb.ToString();
        }

        // Наклон Ssma: вверх - Ssma выше своего значения slopeBars свечей назад больше чем на minPercent %,
        // вниз - ниже больше чем на minPercent %. false - индикатор ещё не накопил данных
        private bool GetTrend(Aindicator ssma, int slopeBars, decimal minPercent, out bool trendUp, out bool trendDown)
        {
            trendUp = false;
            trendDown = false;

            if (ssma.DataSeries == null
                || ssma.DataSeries.Count < 1)
            {
                return false;
            }

            List<decimal> values = ssma.DataSeries[0].Values;

            if (slopeBars < 1)
            {
                slopeBars = 1;
            }

            if (values == null
                || values.Count <= slopeBars)
            {
                return false;
            }

            decimal now = values[^1];
            decimal before = values[^(1 + slopeBars)];

            if (now == 0
                || before == 0)
            {
                return false;
            }

            if (minPercent < 0)
            {
                minPercent = 0;
            }

            decimal changePercent = (now - before) / before * 100m;

            trendUp = changePercent > minPercent;
            trendDown = changePercent < -minPercent;
            return true;
        }

        private decimal GetStopMultiplier(Side side)
        {
            return side == Side.Buy ? _atrStopMultiplier.ValueDecimal : _shortAtrStopMultiplier.ValueDecimal;
        }

        #region Стоп

        // Стоп от цены входа на расстоянии множитель * дневной ATR. Уровень считается один раз и не двигается:
        // дальше берётся из выставленного стопа. В реале на бирже с серверными стопами (Bybit) - условный
        // ордер биржи на весь открытый объём (после частичной фиксации переставляется на остаток),
        // в тестере и на коннекторах без серверных стопов - стоп OsEngine
        private readonly object _stopLocker = new object();

        private void EnsureStop(Position position, BotTabSimple tab)
        {
            // вызывается из событий позиций, свечей и потока сопровождения - стоп не должен уйти дважды
            lock (_stopLocker)
            {
                EnsureStopUnsafe(position, tab);
            }
        }

        private void EnsureStopUnsafe(Position position, BotTabSimple tab)
        {
            if (position == null
                || position.OpenVolume <= 0
                || position.EntryPrice <= 0
                || position.State == PositionStateType.Done
                || position.State == PositionStateType.OpeningFail)
            {
                return;
            }

            // Из позиции выходят (лимитка или рынок): стоп не трогаем. Пока позиция набирается, стоп ставится
            // на уже открытый объём и переставляется по мере набора - открытый объём не остаётся без защиты
            if (HasActiveExitOrder(position)
                || HasUnfinishedExitPending(position))
            {
                return;
            }

            decimal stopPrice = GetStopPrice(position);

            if (stopPrice <= 0)
            {
                stopPrice = CalculateStopPrice(position, tab);

                if (stopPrice <= 0)
                {
                    return;
                }
            }

            if (UseServerStop(tab) == false)
            {
                if (position.StopOrderIsActive
                    && position.StopOrderPrice == stopPrice)
                {
                    return;
                }

                tab.CloseAtStopMarket(position, stopPrice, "AtrStop");
                return;
            }

            // Серверный стоп: нужен ровно один активный на весь открытый объём
            List<Order> stops = GetActiveServerStops(position);
            bool stopIsOk = false;

            for (int i = 0; i < stops.Count; i++)
            {
                if (string.IsNullOrEmpty(stops[i].NumberMarket))
                {
                    return; // стоп только что отправлен, биржа ещё не ответила
                }
            }

            for (int i = 0; i < stops.Count; i++)
            {
                if (stopIsOk == false
                    && stops[i].Volume == position.OpenVolume
                    && stops[i].StopPrice == stopPrice)
                {
                    stopIsOk = true;
                    continue;
                }

                tab.CloseOrder(stops[i]);
            }

            if (stopIsOk)
            {
                position.StopOrderIsActive = false; // стоп OsEngine (позиция до перехода на серверные стопы) не нужен
                return;
            }

            // Цена уже за стопом: условный ордер биржа не примет - закрываем по рынку
            decimal price = position.Direction == Side.Buy ? tab.PriceBestBid : tab.PriceBestAsk;

            if (price > 0
                && ((position.Direction == Side.Buy && price <= stopPrice)
                    || (position.Direction == Side.Sell && price >= stopPrice)))
            {
                SendNewLogMessage($"{tab.Security.Name}: price {price} is beyond stop {stopPrice}, closing at market", LogMessageType.Trade);
                tab.CloseAtMarket(position, position.OpenVolume, "AtrStop");
                return;
            }

            position.SignalTypeStop = "AtrStop";
            tab.CloseAtStopMarketOnServer(position, stopPrice, position.OpenVolume);
            position.StopOrderIsActive = false;
        }

        private decimal CalculateStopPrice(Position position, BotTabSimple tab)
        {
            decimal atr = GetDailyAtr(tab);

            if (atr <= 0)
            {
                SendNewLogMessage($"{tab.Security.Name}: no daily ATR, stop is not set", LogMessageType.Error);
                return 0;
            }

            decimal stopDistance = atr * GetStopMultiplier(position.Direction);

            decimal stopPrice = position.Direction == Side.Buy
                ? position.EntryPrice - stopDistance
                : position.EntryPrice + stopDistance;

            return stopPrice > 0 ? stopPrice : 0;
        }

        // Уровень уже выставленного стопа: последний серверный стоп позиции (цена округлена до шага),
        // иначе стоп OsEngine. 0 - стопа ещё не было
        private decimal GetStopPrice(Position position)
        {
            List<Order> closeOrders = position.CloseOrders;

            for (int i = closeOrders == null ? -1 : closeOrders.Count - 1; i >= 0; i--)
            {
                if (closeOrders[i] != null
                    && IsServerStopOrder(closeOrders[i])
                    && closeOrders[i].StopPrice > 0)
                {
                    return closeOrders[i].StopPrice;
                }
            }

            return position.StopOrderPrice;
        }

        private bool UseServerStop(BotTabSimple tab)
        {
            return StartProgram == StartProgram.IsOsTrader
                && tab.ServerIsSupportStopOrders;
        }

        private List<Order> GetActiveServerStops(Position position)
        {
            List<Order> result = new List<Order>();
            List<Order> closeOrders = position.CloseOrders;

            for (int i = 0; closeOrders != null && i < closeOrders.Count; i++)
            {
                if (closeOrders[i] != null
                    && IsServerStopOrder(closeOrders[i])
                    && IsOrderActive(closeOrders[i]))
                {
                    result.Add(closeOrders[i]);
                }
            }

            return result;
        }

        private static bool IsServerStopOrder(Order order)
        {
            return order.TypeOrder == OrderPriceType.StopMarket
                || order.TypeOrder == OrderPriceType.StopLimit;
        }

        private static bool IsOrderActive(Order order)
        {
            return order.State == OrderStateType.Active
                || order.State == OrderStateType.Pending
                || order.State == OrderStateType.None
                || order.State == OrderStateType.Partial;
        }

        // Активный ордер выхода (лимитка или рынок), кроме серверного стопа
        private bool HasActiveExitOrder(Position position)
        {
            List<Order> closeOrders = position.CloseOrders;

            for (int i = 0; closeOrders != null && i < closeOrders.Count; i++)
            {
                if (closeOrders[i] != null
                    && IsServerStopOrder(closeOrders[i]) == false
                    && IsOrderActive(closeOrders[i]))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Исполнение ордеров

        // Сопровождение OsEngine (снятие ордеров по времени, повторный выход DoubleExit) мешает роботу:
        // снятие последнего ордера закрытия (например, перестановка стопа) переводит позицию в ClosingFail,
        // и DoubleExit закрывает её по рынку. Ордера сопровождает сам робот - сопровождение выключается
        private void DisableManualSupport(BotTabSimple tab)
        {
            DisableManualSupportOnTab(tab);

            // скринер раздаёт настройки сопровождения всем вкладкам с первой
            if (_tradeScreener.Tabs != null
                && _tradeScreener.Tabs.Count > 0
                && _tradeScreener.Tabs[0] != tab)
            {
                DisableManualSupportOnTab(_tradeScreener.Tabs[0]);
            }
        }

        private void DisableManualSupportOnTab(BotTabSimple tab)
        {
            OsEngine.OsTrader.Panels.Tab.Internal.BotManualControl control = tab.ManualPositionSupport;

            if (control == null)
            {
                return;
            }

            if (control.DoubleExitIsOn
                || control.SecondToOpenIsOn
                || control.SecondToCloseIsOn
                || control.SetbackToOpenIsOn
                || control.SetbackToCloseIsOn
                || control.StopIsOn
                || control.ProfitIsOn)
            {
                control.DisableManualSupport();

                if (_manualSupportDisabledLogged == false)
                {
                    _manualSupportDisabledLogged = true;
                    SendNewLogMessage("Manual position support is disabled: orders are maintained by the robot", LogMessageType.System);
                }
            }
        }

        // Вход: лимитка по лучшей цене своей стороны стакана (покупка - bid, продажа - ask), иначе по рынку
        private void OpenPosition(BotTabSimple tab, Side side, decimal vol, decimal lastClose)
        {
            if (_orderExecution.ValueString != "LimitThenMarket")
            {
                if (side == Side.Buy)
                {
                    tab.BuyAtMarket(vol);
                }
                else
                {
                    tab.SellAtMarket(vol);
                }

                return;
            }

            decimal price = GetLimitPrice(tab, side, lastClose);

            Position position = side == Side.Buy
                ? tab.BuyAtLimit(vol, price)
                : tab.SellAtLimit(vol, price);

            if (position == null
                || position.OpenOrders == null
                || position.OpenOrders.Count == 0)
            {
                return;
            }

            AddPending(tab, position, position.OpenOrders[^1], PendingOpen, vol);
        }

        // Выход (весь объём или часть): лимитка без снятия серверного стопа, иначе по рынку
        private void ClosePosition(BotTabSimple tab, Position position, decimal vol, string signal, decimal lastClose)
        {
            if (vol > position.OpenVolume)
            {
                vol = position.OpenVolume;
            }

            if (vol <= 0)
            {
                return;
            }

            if (_orderExecution.ValueString != "LimitThenMarket")
            {
                tab.CloseAtMarket(position, vol, signal);
                return;
            }

            Side orderSide = position.Direction == Side.Buy ? Side.Sell : Side.Buy;
            decimal price = GetLimitPrice(tab, orderSide, lastClose);
            int ordersBefore = position.CloseOrders == null ? 0 : position.CloseOrders.Count;

            position.SignalTypeClose = signal;

            // Unsafe: активные ордера закрытия (серверный стоп) не снимаются, стоп страхует, пока висит лимитка
            tab.CloseAtLimitUnsafe(position, price, vol);

            if (position.CloseOrders == null
                || position.CloseOrders.Count <= ordersBefore)
            {
                return;
            }

            string kind = vol >= position.OpenVolume ? PendingClose : PendingPartial;

            AddPending(tab, position, position.CloseOrders[^1], kind, vol);
        }

        // Лучшая цена своей стороны стакана (мейкер): покупка - bid, продажа - ask. Нет стакана - закрытие свечи
        private decimal GetLimitPrice(BotTabSimple tab, Side orderSide, decimal lastClose)
        {
            decimal price = orderSide == Side.Buy ? tab.PriceBestBid : tab.PriceBestAsk;

            if (price <= 0
                || StartProgram != StartProgram.IsOsTrader)
            {
                price = lastClose;
            }

            return price;
        }

        private void AddPending(BotTabSimple tab, Position position, Order order, string kind, decimal vol)
        {
            PendingExecution pending = new PendingExecution();
            pending.Tab = tab;
            pending.Position = position;
            pending.Order = order;
            pending.Kind = kind;
            pending.Volume = vol;
            pending.StartTime = GetNow(tab);

            lock (_pendingLocker)
            {
                // лимитку мог раньше подобрать поток сопровождения
                if (_pending.Exists(p => p.Order == order))
                {
                    return;
                }

                _pending.Add(pending);
            }
        }

        // После перезапуска очередь сопровождения пуста, а лимитки робота висят на бирже (GTC).
        // Активные лимитки позиций без сопровождения берутся в очередь: время ожидания отсчитывается заново
        private void AdoptOrphanLimits()
        {
            List<BotTabSimple> tabs = _tradeScreener.Tabs;

            for (int t = 0; tabs != null && t < tabs.Count; t++)
            {
                BotTabSimple tab = tabs[t];
                List<Position> positions = tab.PositionsOpenAll;

                for (int i = 0; positions != null && i < positions.Count; i++)
                {
                    Position position = positions[i];

                    List<Order> openOrders = position.OpenOrders;

                    for (int j = 0; openOrders != null && j < openOrders.Count; j++)
                    {
                        if (IsOrphanLimit(openOrders[j]))
                        {
                            SendNewLogMessage($"{tab.Security.Name}: entry limit {openOrders[j].NumberUser} without support - taken", LogMessageType.System);
                            AddPending(tab, position, openOrders[j], PendingOpen, openOrders[j].Volume);
                        }
                    }

                    List<Order> closeOrders = position.CloseOrders;

                    for (int j = 0; closeOrders != null && j < closeOrders.Count; j++)
                    {
                        if (IsOrphanLimit(closeOrders[j]))
                        {
                            string kind = closeOrders[j].Volume >= position.OpenVolume ? PendingClose : PendingPartial;
                            SendNewLogMessage($"{tab.Security.Name}: exit limit {closeOrders[j].NumberUser} without support - taken", LogMessageType.System);
                            AddPending(tab, position, closeOrders[j], kind, closeOrders[j].Volume);
                        }
                    }
                }
            }
        }

        private bool IsOrphanLimit(Order order)
        {
            if (order == null
                || order.TypeOrder != OrderPriceType.Limit
                || IsOrderActive(order) == false
                || string.IsNullOrEmpty(order.NumberMarket))
            {
                return false;
            }

            lock (_pendingLocker)
            {
                return _pending.Exists(p => p.Order == order) == false;
            }
        }

        // Стоп у каждой открытой позиции: стоп мог не выставиться (отклонён биржей), а рыночный выход,
        // снявший стоп, - не исполниться. Без этой проверки повтор был бы только на следующей свече
        private void CheckProtection()
        {
            List<BotTabSimple> tabs = _tradeScreener.Tabs;

            for (int t = 0; tabs != null && t < tabs.Count; t++)
            {
                List<Position> positions = tabs[t].PositionsOpenAll;

                for (int i = 0; positions != null && i < positions.Count; i++)
                {
                    try
                    {
                        EnsureStop(positions[i], tabs[t]);
                    }
                    catch (Exception error)
                    {
                        SendNewLogMessage(error.ToString(), LogMessageType.Error);
                    }
                }
            }
        }

        // Исполненный объём ордера по данным биржи (последние ордера с биржи, поиск по номеру).
        // -1 - коннектор не отдаёт ордера или ордер не найден
        private decimal GetExchangeExecutedVolume(BotTabSimple tab, Order order)
        {
            if ((tab.Connector.MyServer is OsEngine.Market.Servers.AServer server) == false
                || server.ServerRealization == null)
            {
                return -1;
            }

            try
            {
                Order found = FindOrder(server.ServerRealization.GetHistoricalOrders(0, 100), order);

                if (found == null)
                {
                    found = FindOrder(server.ServerRealization.GetActiveOrders(0, 100), order);
                }

                return found == null ? -1 : found.VolumeExecute;
            }
            catch (Exception error)
            {
                SendNewLogMessage($"{tab.Security.Name}: exchange orders request error {error.Message}", LogMessageType.Error);
                return -1;
            }
        }

        private static Order FindOrder(List<Order> orders, Order order)
        {
            for (int i = 0; orders != null && i < orders.Count; i++)
            {
                Order o = orders[i];

                if (o == null)
                {
                    continue;
                }

                if ((string.IsNullOrEmpty(order.NumberMarket) == false && o.NumberMarket == order.NumberMarket)
                    || (o.NumberUser == order.NumberUser && o.SecurityNameCode == order.SecurityNameCode))
                {
                    return o;
                }
            }

            return null;
        }

        // По позиции уже исполнялся выход (стоп, канал, ручное закрытие)
        private static bool HasExecutedExit(Position position)
        {
            List<Order> closeOrders = position.CloseOrders;

            for (int i = 0; closeOrders != null && i < closeOrders.Count; i++)
            {
                if (closeOrders[i] != null
                    && closeOrders[i].VolumeExecute > 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Новые входы разрешены: не Off / OnlyClosePosition и вкладка не выключена аварийной остановкой
        private bool CanOpenPositions(BotTabSimple tab)
        {
            return _regime.ValueString != "Off"
                && _regime.ValueString != "OnlyClosePosition"
                && tab.EventsIsOn;
        }

        // Статус ордера на бирже (REST). Коннектор при этом догружает сделки по ордеру, которые
        // ещё не дошли через сокет. None - биржа не ответила или коннектор не умеет
        private OrderStateType GetExchangeOrderState(BotTabSimple tab, Order order)
        {
            if (tab.Connector.MyServer is OsEngine.Market.Servers.AServer server
                && server.ServerRealization != null)
            {
                return server.ServerRealization.GetOrderStatus(order);
            }

            return order.State;
        }

        private bool HasPending(BotTabSimple tab)
        {
            lock (_pendingLocker)
            {
                return _pending.Exists(p => p.Tab == tab);
            }
        }

        // Лимитка выхода позиции ещё не исполнена полностью (висит или остаток будет добиваться рынком)
        private bool HasUnfinishedExitPending(Position position)
        {
            lock (_pendingLocker)
            {
                return _pending.Exists(p => p.Position == position
                    && p.Kind != PendingOpen
                    && p.Order.State != OrderStateType.Done);
            }
        }

        // Время для сопровождения: в реале - часы компьютера, в тестере - время тестера
        private DateTime GetNow(BotTabSimple tab)
        {
            return StartProgram == StartProgram.IsOsTrader ? DateTime.Now : tab.TimeServerCurrent;
        }

        // onlyTab - сопровождать только лимитки этого инструмента, null - все
        private void ProcessPendingExecutions(BotTabSimple onlyTab)
        {
            List<PendingExecution> list;

            lock (_pendingLocker)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                list = _pending.FindAll(p => onlyTab == null || p.Tab == onlyTab);
            }

            for (int i = 0; i < list.Count; i++)
            {
                bool isFinished;

                try
                {
                    isFinished = ProcessPending(list[i]);
                }
                catch (Exception error)
                {
                    SendNewLogMessage(error.ToString(), LogMessageType.Error);
                    isFinished = true;
                }

                if (isFinished)
                {
                    lock (_pendingLocker)
                    {
                        _pending.Remove(list[i]);
                    }
                }
            }
        }

        // Секунды ожидания сделок после снятия частично исполненной лимитки: в реале сделки по ордеру
        // приходят отдельным сообщением и могут опоздать к статусу "снят"
        private const int TradesWaitSeconds = 3;

        // Повторное снятие лимитки, если биржа не подтвердила первое
        private const int CancelRepeatSeconds = 20;

        // Сколько раз (раз в секунду) спрашивать статус снятой лимитки, если биржа не отвечает
        private const int ExchangeChecksMax = 10;

        // Сколько ждать, пока до OsEngine дойдут сделки на объём, который биржа назвала исполненным
        private const int TradesWaitMaxSeconds = 30;

        // true - сопровождение закончено
        private bool ProcessPending(PendingExecution p)
        {
            BotTabSimple tab = p.Tab;
            Position position = p.Position;
            Order order = p.Order;
            DateTime now = GetNow(tab);

            if (order.State == OrderStateType.Done)
            {
                if (p.Kind == PendingOpen)
                {
                    EnsureStop(position, tab);
                }

                return true;
            }

            if (IsOrderActive(order))
            {
                bool waitIsOver = StartProgram == StartProgram.IsOsTrader
                    ? now >= p.StartTime.AddSeconds(_limitWaitSeconds.ValueInt)
                    : now > p.StartTime;

                // Позицию закрыл стоп - лимитку выхода снимаем сразу. По объёму, а не по состоянию:
                // пока висит сама лимитка, OsEngine держит позицию в Closing, а не Done
                if (p.Kind != PendingOpen
                    && position.OpenVolume <= 0
                    && position.MaxVolume > 0)
                {
                    waitIsOver = true;
                }

                // Off / OnlyClosePosition / аварийная остановка - вход больше не ждём.
                // Стоп (или другой выход) уже сработал на набранной части - набор прекращаем
                if (p.Kind == PendingOpen
                    && (CanOpenPositions(tab) == false
                        || HasExecutedExit(position)))
                {
                    waitIsOver = true;
                }

                if (waitIsOver == false)
                {
                    return false;
                }

                if (p.CancelTime == DateTime.MinValue
                    || (StartProgram == StartProgram.IsOsTrader && now >= p.CancelTime.AddSeconds(CancelRepeatSeconds)))
                {
                    p.CancelTime = now;
                    tab.CloseOrder(order);
                }

                // в тестере ордер снимается сразу
                if (IsOrderActive(order))
                {
                    return false;
                }
            }

            // Ордер снят или отклонён. В реале коннектор помечает ордер снятым по ответу на запрос отмены,
            // а биржа подтверждает отмену и присылает сделки отдельно. Остаток считается по исполненному
            // объёму с биржи: сначала биржа подтверждает, что ордер не активен (запрос статуса догружает
            // недошедшие сделки), затем берётся исполненный объём ордера с биржи и ждём, пока до OsEngine
            // дойдут сделки на этот объём (не дольше TradesWaitMaxSeconds)
            decimal executed = order.VolumeExecute;
            bool executedIsKnown = true;

            if (StartProgram == StartProgram.IsOsTrader)
            {
                if (p.ExchangeConfirmed == false)
                {
                    OrderStateType exchangeState = GetExchangeOrderState(tab, order);

                    if (exchangeState == OrderStateType.Active
                        || exchangeState == OrderStateType.Partial
                        || exchangeState == OrderStateType.Pending)
                    {
                        // на бирже ордер ещё жив - снимаем повторно
                        if (now >= p.CancelTime.AddSeconds(CancelRepeatSeconds))
                        {
                            p.CancelTime = now;
                            tab.CloseOrder(order);
                        }

                        return false;
                    }

                    if (exchangeState == OrderStateType.None)
                    {
                        p.ExchangeChecks++;

                        if (p.ExchangeChecks < ExchangeChecksMax)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        p.ExchangeExecuted = GetExchangeExecutedVolume(tab, order);
                    }

                    p.ExchangeConfirmed = true;
                    p.FinalTime = now;
                    return false;
                }

                bool tradesCame = p.ExchangeExecuted >= 0
                    && order.VolumeExecute >= p.ExchangeExecuted;

                if (now < p.FinalTime.AddSeconds(TradesWaitSeconds)
                    || (tradesCame == false
                        && p.ExchangeExecuted > 0
                        && now < p.FinalTime.AddSeconds(TradesWaitMaxSeconds)))
                {
                    return false;
                }

                if (p.ExchangeExecuted >= 0)
                {
                    executed = Math.Max(order.VolumeExecute, p.ExchangeExecuted);
                }
                else
                {
                    executedIsKnown = false;
                    SendNewLogMessage($"{tab.Security.Name}: no exchange executed volume for order {order.NumberUser}", LogMessageType.Error);
                }
            }

            decimal rest = p.Volume - executed;

            if (p.Kind == PendingOpen)
            {
                // Исполненный объём неизвестен - вход не добиваем: недобор безопаснее лишнего объёма.
                // Стоп уже сработал на набранной части - заново не входим
                if (rest <= 0
                    || executedIsKnown == false
                    || CanOpenPositions(tab) == false
                    || HasExecutedExit(position))
                {
                    EnsureStop(position, tab);
                    return true;
                }

                if (position.OpenVolume > 0
                    && position.State != PositionStateType.Done
                    && position.State != PositionStateType.OpeningFail)
                {
                    if (position.Direction == Side.Buy)
                    {
                        tab.BuyAtMarketToPosition(position, rest);
                    }
                    else
                    {
                        tab.SellAtMarketToPosition(position, rest);
                    }
                }
                else if (position.Direction == Side.Buy)
                {
                    tab.BuyAtMarket(rest);
                }
                else
                {
                    tab.SellAtMarket(rest);
                }

                return true;
            }

            if (position.OpenVolume <= 0
                || position.State == PositionStateType.Done)
            {
                return true;
            }

            // по позиции уже идёт другой выход (аварийная остановка, ручное закрытие) - второй не нужен
            if (HasActiveExitOrder(position))
            {
                return true;
            }

            if (p.Kind == PendingClose)
            {
                // выход по каналу: закрываем весь остаток (рыночное закрытие снимет и серверный стоп)
                tab.CloseAtMarket(position, position.OpenVolume, position.SignalTypeClose);
                return true;
            }

            // частичная фиксация: добиваем неисполненную часть ступени, стоп переставится на остаток.
            // Исполненный объём неизвестен - не добиваем (недобор фиксации безопаснее)
            if (rest > 0
                && executedIsKnown)
            {
                tab.CloseAtMarket(position, Math.Min(rest, position.OpenVolume), PendingPartial);
            }

            return true;
        }

        #endregion

        // ATR последней дневной свечи по той же бумаге, которая закончилась к концу текущей торговой свечи.
        // Тестер выдаёт свечу целиком в момент её начала, поэтому дневная свеча текущего дня
        // в тестере уже "завершена" и содержит будущие High/Low - её брать нельзя
        private decimal GetDailyAtr(BotTabSimple tab)
        {
            return GetDailyAtr(tab, out _);
        }

        // dailyClose - закрытие той же дневки, по которой взят ATR (для ATR в % от цены)
        private decimal GetDailyAtr(BotTabSimple tab, out decimal dailyClose)
        {
            dailyClose = 0;

            List<Candle> tradeCandles = tab.CandlesFinishedOnly;

            if (tradeCandles == null
                || tradeCandles.Count == 0)
            {
                return 0;
            }

            DateTime decisionTime = tradeCandles[^1].TimeStart + tab.Connector.TimeFrameTimeSpan;

            BotTabSimple dailyTab = _dailyScreener.Tabs.Find(t => t.Connector.SecurityName == tab.Connector.SecurityName);

            if (dailyTab == null
                || dailyTab.Indicators == null
                || dailyTab.Indicators.Count == 0)
            {
                return 0;
            }

            Aindicator atr = FindIndicator(dailyTab, 1, "ATR");

            // индикатор не найден или ещё пересоздаётся после смены параметров робота.
            // Тип ATR тоже сверяется: стоп и фильтр волатильности считаются от ATR в пунктах цены
            if (atr == null
                || SyncIndicatorParameters(atr, GetAtrParameters()) == false)
            {
                return 0;
            }

            List<Candle> dailyCandles = dailyTab.CandlesFinishedOnly;

            if (dailyCandles == null
                || dailyCandles.Count <= _atrLength.ValueInt)
            {
                return 0;
            }

            List<decimal> values = atr.DataSeries[0].Values;

            if (values == null)
            {
                return 0;
            }

            TimeSpan dailySpan = dailyTab.Connector.TimeFrameTimeSpan;

            for (int i = dailyCandles.Count - 1; i >= _atrLength.ValueInt; i--)
            {
                if (dailyCandles[i].TimeStart + dailySpan <= decisionTime)
                {
                    if (i >= values.Count)
                    {
                        return 0;
                    }

                    dailyClose = dailyCandles[i].Close;

                    return values[i];
                }
            }

            return 0;
        }

        // Фильтр дневного тренда стороны: лонг - значение индикатора выше порога лонга, шорт - ниже порога шорта.
        // Off - всегда true; индикатор не найден или не готов - false
        private bool CheckDailyTrend(BotTabSimple tab, Side side)
        {
            DailyTrendSettings s = side == Side.Buy ? _longDailyTrend : _shortDailyTrend;
            string type = s.Type.ValueString;

            if (type == "Off")
            {
                return true;
            }

            if (GetDailyTrendValue(tab, s, type, out decimal value) == false)
            {
                return false;
            }

            decimal threshold = type == "SsmaSlopeAtr" ? s.SsmaThreshold.ValueDecimal : s.LinRegThreshold.ValueDecimal;

            if (s.IsLong)
            {
                return value > threshold;
            }

            return value < threshold;
        }

        // Значение индикатора дневного тренда стороны по последней дневке той же бумаги, закончившейся к концу
        // текущей торговой свечи (как у ATR). false - индикатора нет или он ещё не готов (0 до накопления данных)
        private bool GetDailyTrendValue(BotTabSimple tab, DailyTrendSettings s, string type, out decimal value)
        {
            value = 0;

            List<Candle> tradeCandles = tab.CandlesFinishedOnly;

            if (tradeCandles == null
                || tradeCandles.Count == 0)
            {
                return false;
            }

            BotTabSimple dailyTab = _dailyScreener.Tabs.Find(t => t.Connector.SecurityName == tab.Connector.SecurityName);

            if (dailyTab == null)
            {
                return false;
            }

            bool isSsma = type == "SsmaSlopeAtr";

            Aindicator indicator = FindIndicator(dailyTab, isSsma ? s.SsmaNum : s.LinRegNum, type);

            if (indicator == null
                || CheckDailyTrendParameters(indicator, s, isSsma) == false
                || indicator.DataSeries == null
                || indicator.DataSeries.Count == 0)
            {
                return false;
            }

            List<Candle> dailyCandles = dailyTab.CandlesFinishedOnly;
            List<decimal> values = indicator.DataSeries[0].Values;

            if (dailyCandles == null
                || values == null)
            {
                return false;
            }

            DateTime decisionTime = tradeCandles[^1].TimeStart + tab.Connector.TimeFrameTimeSpan;
            TimeSpan dailySpan = dailyTab.Connector.TimeFrameTimeSpan;

            for (int i = dailyCandles.Count - 1; i >= 0; i--)
            {
                if (dailyCandles[i].TimeStart + dailySpan <= decisionTime)
                {
                    if (i >= values.Count
                        || values[i] == 0)
                    {
                        return false;
                    }

                    value = values[i];
                    return true;
                }
            }

            return false;
        }

        // Параметры индикатора дневного тренда (периоды и линии порога) совпадают с параметрами робота
        private bool CheckDailyTrendParameters(Aindicator indicator, DailyTrendSettings s, bool isSsma)
        {
            return SyncIndicatorParameters(indicator,
                isSsma ? GetSsmaSlopeAtrParameters(s) : GetLinRegSlopeAtrParameters(s));
        }

        // Все параметры индикатора (числовые, строковые, логические) совпадают с ожидаемыми - тем же списком,
        // что передаётся скринеру при создании. Иначе выставляем значения робота и пересчитываем индикатор:
        // параметры могли изменить вручную на графике (файл параметров индикатора общий для всех вкладок скринера).
        // false - индикатор ещё пересоздаётся (параметров меньше ожидаемого), значения брать нельзя
        private bool SyncIndicatorParameters(Aindicator indicator, List<string> expected)
        {
            List<IndicatorParameter> prms = indicator.Parameters;

            if (prms == null
                || prms.Count < expected.Count)
            {
                return false;
            }

            bool changed = false;

            for (int i = 0; i < expected.Count; i++)
            {
                IndicatorParameter p = prms[i];

                if (p.Type == IndicatorParameterType.Int)
                {
                    int value = Convert.ToInt32(expected[i]);

                    if (((IndicatorParameterInt)p).ValueInt != value)
                    {
                        ((IndicatorParameterInt)p).ValueInt = value;
                        changed = true;
                    }
                }
                else if (p.Type == IndicatorParameterType.Decimal)
                {
                    decimal value = expected[i].ToDecimal();

                    if (((IndicatorParameterDecimal)p).ValueDecimal != value)
                    {
                        ((IndicatorParameterDecimal)p).ValueDecimal = value;
                        changed = true;
                    }
                }
                else if (p.Type == IndicatorParameterType.String)
                {
                    if (((IndicatorParameterString)p).ValueString != expected[i])
                    {
                        ((IndicatorParameterString)p).ValueString = expected[i];
                        changed = true;
                    }
                }
                else if (p.Type == IndicatorParameterType.Bool)
                {
                    bool value = Convert.ToBoolean(expected[i]);

                    if (((IndicatorParameterBool)p).ValueBool != value)
                    {
                        ((IndicatorParameterBool)p).ValueBool = value;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                indicator.Save();
                indicator.Reload();
            }

            return true;
        }

        private List<string> GetPcParameters()
        {
            return new List<string>() { _pcLengthUp.ValueInt.ToString(), _pcLengthDown.ValueInt.ToString() };
        }

        // Канал шорта: верхняя линия - выход, нижняя - вход
        private List<string> GetPcShortParameters()
        {
            return new List<string>() { _shortChannelExit.ValueInt.ToString(), _shortChannelEntry.ValueInt.ToString() };
        }

        private List<string> GetSsmaParameters(StrategyParameterInt length)
        {
            return new List<string>() { length.ValueInt.ToString(), "Close" };
        }

        private List<string> GetAtrParameters()
        {
            return new List<string>() { _atrLength.ValueInt.ToString(), "Absolute" };
        }

        // Ограничение объёма: убыток по стопу не больше заданного % от депозита
        private decimal LimitVolumeByStopLoss(BotTabSimple tab, decimal vol, decimal stopDistance)
        {
            if (vol <= 0
                || _maxLossByStopPercent.ValueDecimal <= 0)
            {
                return vol;
            }

            decimal deposit = GetDepositValue(tab);

            if (deposit <= 0)
            {
                SendNewLogMessage($"{tab.Security.Name}: can`t get deposit value for risk limit", LogMessageType.Error);
                return 0;
            }

            decimal lot = tab.Security.Lot > 0 ? tab.Security.Lot : 1;

            decimal maxLossMoney = deposit * _maxLossByStopPercent.ValueDecimal / 100m;

            decimal maxVol = maxLossMoney / (stopDistance * lot);

            if (StartProgram == StartProgram.IsOsTrader)
            {
                maxVol = Math.Round(maxVol, tab.Security.DecimalsVolume, MidpointRounding.ToNegativeInfinity);
            }
            else
            {
                maxVol = Math.Round(maxVol, 7, MidpointRounding.ToNegativeInfinity);
            }

            if (maxVol < vol)
            {
                return maxVol;
            }

            return vol;
        }

        // Округление объёма вниз: в реале до точности бумаги, в тестере и оптимизаторе - 7 знаков (как в SDKVolume)
        private decimal RoundVolume(BotTabSimple tab, decimal vol)
        {
            if (StartProgram == StartProgram.IsOsTrader)
            {
                if (tab.Security.DecimalsVolume == 0)
                {
                    return Math.Floor(vol);
                }

                return Math.Round(vol, tab.Security.DecimalsVolume, MidpointRounding.ToNegativeInfinity);
            }

            return Math.Round(vol, 7, MidpointRounding.ToNegativeInfinity);
        }

        // Депозит считается так же, как в SDKVolume ("Full Asset in portfolio")
        private decimal GetDepositValue(BotTabSimple tab)
        {
            Portfolio portfolio = tab.Portfolio;

            if (portfolio == null)
            {
                return 0;
            }

            if (volume.FullTradeAssetInPortfolio.ValueString == "Prime"
                || StartProgram != StartProgram.IsOsTrader)
            {
                return portfolio.ValueCurrent;
            }

            List<PositionOnBoard> positionOnBoard = portfolio.GetPositionOnBoard();

            if (positionOnBoard == null)
            {
                return 0;
            }

            for (int i = 0; i < positionOnBoard.Count; i++)
            {
                if (positionOnBoard[i].SecurityNameCode == volume.FullTradeAssetInPortfolio.ValueString)
                {
                    return positionOnBoard[i].ValueCurrent;
                }
            }

            return 0;
        }

        // false - индикаторы ещё пересоздаются (смена параметров робота идёт в другом потоке), свечу пропускаем
        private bool CheckTradeIndicatorsParameters(Aindicator pc, Aindicator pcShort, Aindicator longSsma,
            Aindicator shortSsma)
        {
            // Сверяются все индикаторы (без короткого замыкания): каждый расходящийся приводится к настройкам робота
            bool ready = SyncIndicatorParameters(pc, GetPcParameters());
            ready &= SyncIndicatorParameters(pcShort, GetPcShortParameters());
            ready &= SyncIndicatorParameters(longSsma, GetSsmaParameters(_longSsmaLength));
            ready &= SyncIndicatorParameters(shortSsma, GetSsmaParameters(_shortSsmaLength));

            return ready;
        }
    }
}
