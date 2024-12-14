using System;
using System.Collections.Generic;
using System.Drawing;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.Market.Servers;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.Market.Servers.Alor.Json;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using OsEngine.Logging;

namespace OsEngine.Robots.MyBots
{
    [Bot("SDKCryptoTrend")] // We create an attribute so that we don't write anything to the BotFactory
    internal class SDKCryptoTrend : BotPanel
    {
        private BotTabSimple _tab1;
        private BotTabSimple _tab2;

        // Basic Settings
        private StrategyParameterString Regime;
        public StrategyParameterString VolumeType;
        public StrategyParameterDecimal Volume;
        public StrategyParameterString TradeAssetInPortfolio;
        private StrategyParameterDecimal Slippage;
        private StrategyParameterDecimal stopLossPercent;
        private StrategyParameterDecimal takeProfitPercent;
        private StrategyParameterDecimal trailingStopPercent;
        private StrategyParameterInt maxOrdersCount;
        private StrategyParameterInt maxProfitOrdersCount;
        private StrategyParameterDecimal stepOrdersPercent;
        private StrategyParameterDecimal stepProfitOrdersPercent;

        // Indicator setting 
        private StrategyParameterInt bollinger_length;
        private StrategyParameterDecimal bollinger_deviation;

        private StrategyParameterInt superTrandLength;
        private StrategyParameterDecimal superTrandDeviation;

        // Indicator
        private Aindicator bollinger;
        private Aindicator superTrand;
        public SDKCryptoTrend(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            TabCreate(BotTabType.Simple);
            _tab1 = TabsSimple[0];
            _tab2 = TabsSimple[1];

            // Basic setting
            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort" }, "Base");
            VolumeType = CreateParameter("Volume type", "Contracts", new[] { "Contracts", "Contract currency", "Deposit percent" }, "Base");
            Volume = CreateParameter("Volume", 1, 1.0m, 50, 4, "Base");
            TradeAssetInPortfolio = CreateParameter("Asset in portfolio", "Base", "Base");
            Slippage = CreateParameter("Slippage steps", 0m, 0, 20, 1, "Base");
            stopLossPercent = CreateParameter("StopLoss %", 0m, 0m, 10m, 1m, "Base");
            takeProfitPercent = CreateParameter("TakeProfit %", 0m, 0m, 20m, 1m, "Base");
            trailingStopPercent = CreateParameter("TrailingStop %", 0m, 0m, 20m, 1m, "Base");

            maxOrdersCount = CreateParameter("MaxOrdersCount", 5, 1, 10, 1, "Base");
            maxProfitOrdersCount = CreateParameter("MaxProfitOrdersCount", 15, 1, 10, 1, "Base");
            stepOrdersPercent = CreateParameter("StepOrders %", 3.0m, 1.0m, 10.0m, 1.0m, "Base");
            stepProfitOrdersPercent = CreateParameter("StepProfitOrders %", 3.0m, 1.0m, 10.0m, 1.0m, "Base");

            // Indicator setting
            bollinger_length = CreateParameter("Bollinger Length", 20, 10, 30, 2, "Indicator");
            bollinger_deviation = CreateParameter("Bollinger Deviation", 2m, 1m, 3m, 0.5m, "Indicator");

            superTrandLength = CreateParameter("SuperTrand Length", 10, 10, 50, 10, "Indicator");
            superTrandDeviation = CreateParameter("SuperTrand Deviation", 1, 1m, 10, 1, "Indicator");

            // Create indicators
            bollinger = IndicatorsFactory.CreateIndicatorByName("Bollinger", name + "Bollinger", false);
            bollinger = (Aindicator)_tab1.CreateCandleIndicator(bollinger, "Prime");
            ((IndicatorParameterInt)bollinger.Parameters[0]).ValueInt = bollinger_length.ValueInt;
            ((IndicatorParameterDecimal)bollinger.Parameters[1]).ValueDecimal = bollinger_deviation.ValueDecimal;
            bollinger.Save();

            superTrand = IndicatorsFactory.CreateIndicatorByName("SuperTrend_indicator", name + "SuperTrendFast", false);
            superTrand = (Aindicator)_tab2.CreateCandleIndicator(superTrand, "Prime");
            ((IndicatorParameterInt)superTrand.Parameters[0]).ValueInt = superTrandLength.ValueInt;
            ((IndicatorParameterDecimal)superTrand.Parameters[1]).ValueDecimal = superTrandDeviation.ValueDecimal;
            ((IndicatorParameterString)superTrand.Parameters[2]).ValueString = "Typical";
            ((IndicatorParameterBool)superTrand.Parameters[3]).ValueBool = false;
            superTrand.DataSeries[2].Color = Color.Red;
            superTrand.Save();

            // Subscribe to the indicator update event
            ParametrsChangeByUser += SDKTestBot_ParametrsChangeByUser;

            //PositionOpeningFailEven
            //PositionOpeningSuccesEvent
            //PositionClosingSuccesEvent
            //PositionClosingFailEvent

            //Подписка на получение событий/команд из телеграма - Subscribe to receive events/commands from Telegram
            ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;

            // Subscribe to the candle finished event
            _tab1.CandleFinishedEvent += _tab_CandleFinishedEvent;
            _tab2.CandleFinishedEvent += _tab2_CandleFinishedEvent;

            Description = "SDKCryptoTrend";
        }

        private void SDKTestBot_ParametrsChangeByUser()
        {
            ((IndicatorParameterInt)bollinger.Parameters[0]).ValueInt = bollinger_length.ValueInt;
            ((IndicatorParameterDecimal)bollinger.Parameters[1]).ValueDecimal = bollinger_deviation.ValueDecimal;
            bollinger.Reload();
            bollinger.Save();

            ((IndicatorParameterInt)superTrand.Parameters[0]).ValueInt = superTrandLength.ValueInt;
            ((IndicatorParameterDecimal)superTrand.Parameters[1]).ValueDecimal = superTrandDeviation.ValueDecimal;
            ((IndicatorParameterBool)superTrand.Parameters[3]).ValueBool = false;
            superTrand.Reload();
            superTrand.Save();

            if (_tab1.StartProgram == StartProgram.IsOsTrader && GetVolume(_tab1) == 0m)
                SendNewLogMessage("Volume of trade: " + GetVolume(_tab1), Logging.LogMessageType.Error);
        }

        public override string GetNameStrategyType()
        {
            return "SDKCryptoTrend";
        }

        public override void ShowIndividualSettingsDialog()
        {
        }

        private void TelegramCommandHandler(string botName, Command cmd)
        {
            if (botName != null && !_tab1.TabName.Equals(botName))
                return;

            if (cmd == Command.StopAllBots || cmd == Command.StopBot)
            {
                Regime.ValueString = BotTradeRegime.Off.ToString();

                SendNewLogMessage($"Changed Bot {_tab1.TabName} Regime to Off " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.StartAllBots || cmd == Command.StartBot)
            {
                Regime.ValueString = BotTradeRegime.On.ToString();

                //changing bot mode to its previous state or On
                SendNewLogMessage($"Changed bot {_tab1.TabName} mode to state {Regime} " +
                                  $"by telegram command {cmd}", LogMessageType.User);
            }
            else if (cmd == Command.CancelAllActiveOrders)
            {
                //Some logic for cancel all active orders
            }
            else if (cmd == Command.GetStatus)
            {
                SendNewLogMessage($"Bot {_tab1.TabName} is {Regime}. Emulator - {_tab1.EmulatorIsOn}, " +
                                  $"Server Status - {_tab1.ServerStatus}.", LogMessageType.User);
            }
        }


        // Logic

        private bool hasSignalToOpen(List<Candle> candles, bool to_buy)
        {
            if ((to_buy && Regime.ValueString == "OnlyShort") || (!to_buy && Regime.ValueString == "OnlyLong"))
                return false;

            decimal bollingerUpVal = bollinger.DataSeries[0].Last;
            decimal bollingerDownVal = bollinger.DataSeries[1].Last;
            decimal bollingerUpValPrev = bollinger.DataSeries[0].Values[bollinger.DataSeries[0].Values.Count - 2];
            decimal bollingerDownValPrev = bollinger.DataSeries[1].Values[bollinger.DataSeries[1].Values.Count - 2];

            decimal price = candles[candles.Count - 1].Close;
            decimal pricePrev = candles[candles.Count - 2].Close;

            if (to_buy && pricePrev < bollingerDownValPrev && price >= bollingerDownVal && candles[candles.Count - 1].IsUp)
                return true;
            if (!to_buy && pricePrev > bollingerUpValPrev && price <= bollingerUpVal && candles[candles.Count - 1].IsDown)
                return true;

            return false;
        }

        private bool hasSignalToClose(List<Candle> candles, bool buy_position)
        {
            decimal lastPrice = candles[candles.Count - 1].Close;
            // to get a value from a higher timeframe, need to take the value of the completed candle.
            decimal lastSuperTrand = superTrand.DataSeries[2].Values[superTrand.DataSeries[2].Values.Count - 2];
            return (!buy_position && lastPrice > lastSuperTrand) || (buy_position && lastPrice < lastSuperTrand);
        }

        bool currentTradeTypeBuy = false;
        bool currentTradeTypeSell = false;
        int ordersCount = 0; 
        decimal orderMinOpenPrice = 0;
        decimal orderMaxOpenPrice = 0;
        decimal allOrdersAverageOpenPrice = 0;
        decimal allOrdersVolume = 0;
        private void checkOpenedPosition()
        {
            currentTradeTypeBuy = false;
            currentTradeTypeSell = false;
            ordersCount = 0;
            orderMinOpenPrice = decimal.MaxValue;
            orderMaxOpenPrice = decimal.MinValue;
            allOrdersAverageOpenPrice = 0;
            allOrdersVolume = 0;
            List<Position> openPositions = _tab1.PositionsOpenAll;
            for (int i = 0; i < openPositions.Count; i++)
            {
                Position position = openPositions[i];

                if (position.State != PositionStateType.Open)
                    continue;
                if (position.OpenVolume == 0)
                    continue;
                if (position.Direction == Side.Buy) // If the direction of the position is purchase
                    currentTradeTypeBuy = true;
                else // If the direction of the position is sale
                    currentTradeTypeSell = true;
                ordersCount++;
                orderMaxOpenPrice = Math.Max(position.EntryPrice, orderMaxOpenPrice);
                orderMinOpenPrice = Math.Min(position.EntryPrice, orderMinOpenPrice);
                allOrdersVolume += position.OpenVolume;
                allOrdersAverageOpenPrice += position.EntryPrice * position.OpenVolume;
            }
            if (ordersCount != 0)
                allOrdersAverageOpenPrice /= allOrdersVolume;
            if (currentTradeTypeBuy && currentTradeTypeSell)
                SendNewLogMessage("Opened position on buy and sell!!!", Logging.LogMessageType.NoName);
        }

        // Candle Finished Event
        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            // If the robot is turned off, exit the event handler
            if (Regime.ValueString == "Off")
                return;

            // If there are not enough candles to build an indicator, we exit
            if (candles.Count < bollinger_length.ValueInt + 10 || superTrand.DataSeries[2].Values.Count < superTrandLength.ValueInt)
                return;

            checkOpenedPosition();
            LogicClosePosition(candles);
            checkOpenedPosition();
            LogicOpenPosition(candles);
        }

        private void _tab2_CandleFinishedEvent(List<Candle> candles)
        {
            // If the robot is turned off, exit the event handler
            if (Regime.ValueString == "Off")
                return;

            // If there are not enough candles to build an indicator, we exit
            if (superTrand.DataSeries[2].Values.Count < superTrandLength.ValueInt)
                return;
            checkOpenedPosition();
            prepareLogicClosePosition(candles);
        }

        // Opening logic
        private void LogicOpenPosition(List<Candle> candles)
        {
            // to get a value from a higher timeframe, need to take the value of the completed candle.
            decimal lastSuperTrand = superTrand.DataSeries[2].Values[superTrand.DataSeries[2].Values.Count - 2];
            //decimal prevSuperTrand = superTrand.DataSeries[2].Values[superTrand.DataSeries[2].Values.Count - 3];

            decimal slippage = Slippage.ValueDecimal * _tab1.Security.PriceStep;
            decimal lastPrice = candles[candles.Count - 1].Close;

            bool trandUp = lastPrice > lastSuperTrand && !closePosBuy;
            bool trandDown = lastPrice < lastSuperTrand && !closePosSell;
            bool tradeTypeBuy = trandUp && (hasSignalToOpen(candles, true));// || lastSuperTrand > prevSuperTrand);
            bool tradeTypeSell = trandDown && (hasSignalToOpen(candles, false));// || lastSuperTrand < prevSuperTrand);

            if (!tradeTypeBuy && !tradeTypeSell)
                return;

            if (ordersCount == 0) // initial position
            {
                if (tradeTypeBuy)
                    _tab1.BuyAtLimit(GetVolume(_tab1), _tab1.PriceBestBid - slippage);
                else if (tradeTypeSell)
                    _tab1.SellAtLimit(GetVolume(_tab1), _tab1.PriceBestAsk + slippage);
                return;
            }

            if (ordersCount < maxOrdersCount.ValueInt) // усреднение убыточных сделок
            {
                if (tradeTypeBuy && currentTradeTypeBuy && lastPrice <= orderMinOpenPrice * (1.0m - stepOrdersPercent.ValueDecimal / 100))
                    _tab1.BuyAtLimit(GetVolume(_tab1), _tab1.PriceBestBid - slippage);
                if (tradeTypeSell && currentTradeTypeSell && lastPrice >= orderMaxOpenPrice * (1.0m + stepOrdersPercent.ValueDecimal / 100))
                    _tab1.SellAtLimit(GetVolume(_tab1), _tab1.PriceBestAsk + slippage);
            }

            if (ordersCount < maxProfitOrdersCount.ValueInt) // доливка по тренду с прибыльными сделками
            {
                if (tradeTypeBuy && currentTradeTypeBuy && lastPrice >= orderMaxOpenPrice * (1.0m + stepProfitOrdersPercent.ValueDecimal / 100))
                {
                    _tab1.BuyAtLimit(GetVolume(_tab1), _tab1.PriceBestBid - slippage);
                    //breakevenStopPercent = stepProfitOrdersPercent.ValueDecimal;
                }
                if (tradeTypeSell && currentTradeTypeSell && lastPrice <= orderMinOpenPrice * (1.0m - stepProfitOrdersPercent.ValueDecimal / 100))
                {
                    _tab1.SellAtLimit(GetVolume(_tab1), _tab1.PriceBestAsk + slippage);
                    //breakevenStopPercent = stepProfitOrdersPercent.ValueDecimal;
                }
            }
        }

        bool closePosBuy;
        bool closePosSell;
        private void prepareLogicClosePosition(List<Candle> candles)
        {
            closePosBuy = hasSignalToClose(candles, true);
            closePosSell = hasSignalToClose(candles, false);
        }

        // Logic close position
        decimal breakevenStopPercent = 0;
        private void LogicClosePosition(List<Candle> candles) 
        {
            decimal slippage = Slippage.ValueDecimal * _tab1.Security.PriceStep;
            decimal lastPrice = candles[candles.Count - 1].Close;
            List<Position> openPositions = _tab1.PositionsOpenAll;

            for (int i = 0; i < openPositions.Count; i++)
            {
                Position position = openPositions[i];

                if (position.State != PositionStateType.Open)
                    continue;
                if (position.OpenVolume == 0)
                    continue;

                if (position.Direction == Side.Buy) // If the direction of the position is purchase
                {
                    if (closePosBuy)
                    {
                        _tab1.CloseAtLimit(position, _tab1.PriceBestAsk + slippage, position.OpenVolume);
                    }
                    else
                    {
                        decimal stopPrice = 0m;
                        decimal takePrice = 0m;
                        decimal trailPrice = lastPrice * (1m - trailingStopPercent.ValueDecimal / 100);// TrailStopPrice;
                        decimal breakevenPrice = lastPrice * (1m - breakevenStopPercent / 100);
                        if (trailingStopPercent.ValueDecimal > 0 && trailPrice >= position.EntryPrice)
                            stopPrice = Math.Max(position.StopOrderPrice, trailPrice);
                        else if (breakevenStopPercent > 0 && trailPrice >= position.EntryPrice && position.StopOrderPrice < position.EntryPrice)
                            stopPrice = position.EntryPrice;
                        else if (stopLossPercent.ValueDecimal > 0)
                            // стоп лосс в сетке по последнему ордеру
                            stopPrice = (ordersCount > 1 ? orderMaxOpenPrice : position.EntryPrice) * ( 1m - stopLossPercent.ValueDecimal / 100);

                        if (ordersCount > 3 && lastPrice < allOrdersAverageOpenPrice)
                        {
                            // убыточная сетка - ставим тейк профит на безубыток по средней цене сетки
                            takePrice = allOrdersAverageOpenPrice;
                        }
                        else if (takeProfitPercent.ValueDecimal > 0 && position.ProfitOrderPrice == 0m)
                            takePrice = position.EntryPrice * (1m + takeProfitPercent.ValueDecimal / 100);

                        if (stopPrice != 0m && position.StopOrderPrice != stopPrice &&
                            (position.StopOrderPrice == 0 || position.StopOrderPrice < position.EntryPrice || position.StopOrderPrice < stopPrice))
                            _tab1.CloseAtStop(position, stopPrice, stopPrice - slippage);
                        if (takePrice != 0 && position.ProfitOrderPrice != takePrice)
                            _tab1.CloseAtProfit(position, takePrice, takePrice + slippage);
                    }
                }
                else // If the direction of the position is sale
                {
                    if (closePosSell)
                    {
                        _tab1.CloseAtLimit(position, _tab1.PriceBestBid - slippage, position.OpenVolume);
                    }
                    else
                    {
                        decimal stopPrice = 0m;
                        decimal takePrice = 0m;
                        decimal trailPrice = lastPrice * (1m + trailingStopPercent.ValueDecimal / 100);// TrailStopPrice;
                        decimal breakevenPrice = lastPrice * (1m + breakevenStopPercent / 100);
                        if (trailingStopPercent.ValueDecimal > 0 && trailPrice <= position.EntryPrice)
                            stopPrice = Math.Min(position.StopOrderPrice, trailPrice);
                        else if (breakevenStopPercent > 0 && trailPrice <= position.EntryPrice &&
                            (position.StopOrderPrice == 0 || position.StopOrderPrice > position.EntryPrice))
                            stopPrice = position.EntryPrice;
                        else if (stopLossPercent.ValueDecimal > 0)
                            // стоп лосс в сетке по последнему ордеру
                            stopPrice = (ordersCount > 1 ? orderMinOpenPrice : position.EntryPrice) * (1m + stopLossPercent.ValueDecimal / 100);

                        if (ordersCount > 3 && lastPrice > allOrdersAverageOpenPrice)
                        {
                            // убыточная сетка - ставим тейк профит на безубыток по средней цене сетки
                            takePrice = allOrdersAverageOpenPrice;
                        }
                        else if (takeProfitPercent.ValueDecimal > 0 && position.ProfitOrderPrice == 0m)
                            takePrice = position.EntryPrice * (1m - takeProfitPercent.ValueDecimal / 100);

                        if (stopPrice != 0m && position.StopOrderPrice != stopPrice &&
                            (position.StopOrderPrice == 0 || position.StopOrderPrice > position.EntryPrice || position.StopOrderPrice > stopPrice))
                            _tab1.CloseAtStop(position, stopPrice, stopPrice + slippage);
                        if (takePrice != 0 && position.ProfitOrderPrice != takePrice)
                            _tab1.CloseAtProfit(position, takePrice, takePrice - slippage);
                    }
                }
            }
            breakevenStopPercent = 0;
        }

        // Method for calculating the volume of entry into a position
        private decimal GetVolume(BotTabSimple tab)
        {
            decimal volume = 0;

            if (VolumeType.ValueString == "Contracts")
            {
                volume = Volume.ValueDecimal;
            }
            else if (VolumeType.ValueString == "Contract currency")
            {
                decimal contractPrice = tab.PriceBestAsk;
                volume = Volume.ValueDecimal / contractPrice;

                if (StartProgram == StartProgram.IsOsTrader)
                {
                    IServerPermission serverPermission = ServerMaster.GetServerPermission(tab.Connector.ServerType);

                    if (serverPermission != null &&
                        serverPermission.IsUseLotToCalculateProfit &&
                    tab.Security.Lot != 0 &&
                        tab.Security.Lot > 1)
                    {
                        volume = Volume.ValueDecimal / (contractPrice * tab.Security.Lot);
                    }

                    volume = Math.Round(volume, tab.Security.DecimalsVolume);
                }
                else // Tester or Optimizer
                {
                    volume = Math.Round(volume, 6);
                }
            }
            else if (VolumeType.ValueString == "Deposit percent")
            {
                Portfolio myPortfolio = tab.Portfolio;

                if (myPortfolio == null)
                {
                    SendNewLogMessage("myPortfolio == null", Logging.LogMessageType.Error);
                    return 0;
                }

                decimal portfolioPrimeAsset = 0;

                if (TradeAssetInPortfolio.ValueString == "Prime")
                {
                    portfolioPrimeAsset = myPortfolio.ValueCurrent;
                }
                else
                {
                    List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

                    if (positionOnBoard == null)
                    {
                        SendNewLogMessage("positionOnBoard == null", Logging.LogMessageType.Error);
                        return 0;
                    }

                    for (int i = 0; i < positionOnBoard.Count; i++)
                    {
                        if (positionOnBoard[i].SecurityNameCode == TradeAssetInPortfolio.ValueString)
                        {
                            portfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                            break;
                        }
                    }
                }

                if (portfolioPrimeAsset == 0)
                {
                    SendNewLogMessage("Can`t found portfolio " + TradeAssetInPortfolio.ValueString, Logging.LogMessageType.Error);
                    return 0;
                }

                decimal moneyOnPosition = portfolioPrimeAsset * (Volume.ValueDecimal / 100);

                decimal qty = moneyOnPosition / tab.PriceBestAsk / tab.Security.Lot;

                if (tab.StartProgram == StartProgram.IsOsTrader)
                {
                    qty = Math.Round(qty, tab.Security.DecimalsVolume);
                }
                else
                {
                    qty = Math.Round(qty, 7);
                }

                return qty;
            }

            return volume;
        }
    }
}