using System;
using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Market.Servers;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System.IO;
using System.Globalization;
using OsEngine.Indicators;
using OsEngine.Market.Servers.Tester;

namespace OsEngine.Robots
{
    [Bot("SDKLQDT")] // We create an attribute so that we don't write anything to the BotFactory
    public class SDKLQDT : BotPanel
    {
        private BotTabSimple _tab;

        // Basic Settings
        private StrategyParameterString Regime;
        public StrategyParameterString TradeAssetInPortfolio;
        private StrategyParameterDecimal Slippage;
        private StrategyParameterDecimal maxCountForTrade;

        private StrategyParameterTimeOfDay TimeStart;
        private StrategyParameterTimeOfDay TimeEnd;

        private StrategyParameterBool showErrorMessage;

        public SDKLQDT(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            // Basic setting
            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On"});
            TradeAssetInPortfolio = CreateParameter("Asset in portfolio", "TMON@");
            Slippage = CreateParameter("Slippage steps", 0.1m, 0, 20, 1);
            maxCountForTrade = CreateParameter("Max count lots for trade", 10m, 0, 20, 1);

            TimeStart = CreateParameterTimeOfDay("Start Trade Time", 22, 35, 0, 0);
            TimeEnd = CreateParameterTimeOfDay("End Trade Time", 23, 45, 0, 0);

            showErrorMessage = CreateParameter("Show Error Message", false);

            // Subscribe to the indicator update event
            ParametrsChangeByUser += _ParametrsChangeByUser;

            //Подписка на получение событий/команд из телеграма - Subscribe to receive events/commands from Telegram
            //ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;

            // Subscribe to the candle finished event
            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
        }

        private void _ParametrsChangeByUser()
        {
        }

        public override string GetNameStrategyType()
        {
            return "SDKLQDT";
        }

        public override void ShowIndividualSettingsDialog()
        {
        }

        //private void TelegramCommandHandler(string botName, Command cmd)
        //{
        //}

        // Logic
        // Candle Finished Event
        private void _tab_CandleFinishedEvent(List<Candle> candles)
        {
            // If the robot is turned off, exit the event handler
            if (Regime.ValueString == "Off")
                return;

            if (candles[^1].TimeStart.DayOfWeek < DayOfWeek.Monday ||
                candles[^1].TimeStart.DayOfWeek > DayOfWeek.Friday)
                return;

            if (TimeStart.Value > _tab.TimeServerCurrent ||
                TimeEnd.Value < _tab.TimeServerCurrent)
                return;

            Portfolio myPortfolio = _tab.Portfolio;

            if (myPortfolio == null)
            {
                if (showErrorMessage.ValueBool)
                    SendNewLogMessage("No portfolio, exit!!!", Logging.LogMessageType.Error);
                return;
            }

            List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

            if (positionOnBoard == null)
            {
                if (showErrorMessage.ValueBool)
                    SendNewLogMessage("No posions in portfolio, exit!!!", Logging.LogMessageType.Error);
                return;
            }

            decimal fullMoney = 0;
            decimal lqdtCount = 0;
            decimal lqdtMoney = 0;

            for (int i = 0; i < positionOnBoard.Count; i++)
            {
                if (positionOnBoard[i].SecurityNameCode == _tab.Security.Name)
                    lqdtCount = positionOnBoard[i].ValueCurrent;
                if (positionOnBoard[i].SecurityNameCode == TradeAssetInPortfolio.ValueString)
                    fullMoney = positionOnBoard[i].ValueCurrent;
            }

            lqdtMoney = lqdtCount * _tab.PriceBestBid;

            decimal qty = (fullMoney > 0 ? (fullMoney / _tab.PriceBestAsk) : (-fullMoney / _tab.PriceBestBid)) / _tab.Security.Lot;
            qty = Math.Round(qty, _tab.Security.DecimalsVolume, MidpointRounding.ToNegativeInfinity);
            if (fullMoney < 0)
                qty += 1m;

            if (qty < 1m)
                return;

            if (fullMoney < 0 && lqdtMoney < -fullMoney)
            {
                if (showErrorMessage.ValueBool)
                    SendNewLogMessage("Not enough quantity for sale !!!", Logging.LogMessageType.Error);
                qty = lqdtCount;
            }

            if (fullMoney > 0 && qty > 1m)
            {
                decimal entryPrice = _tab.PriceBestAsk + _tab.PriceBestAsk * (Slippage.ValueDecimal / 100);
                List<Position> openPositions = _tab.PositionsOpenAll;
                if (openPositions != null && openPositions.Count != 0)
                    _tab.BuyAtLimitToPosition(openPositions[0], entryPrice, Math.Min(maxCountForTrade.ValueDecimal, qty));
                else
                    _tab.BuyAtLimit(Math.Min(maxCountForTrade.ValueDecimal, qty), entryPrice);
            }
            else
            {
                decimal closePrice = _tab.PriceBestBid - _tab.PriceBestBid * (Slippage.ValueDecimal / 100);
                List<Position> openPositions = _tab.PositionsOpenAll;
                if (openPositions != null && openPositions.Count != 0)
                    _tab.CloseAtLimit(openPositions[0], closePrice,
                        Math.Min(maxCountForTrade.ValueDecimal, Math.Min(openPositions[0].OpenVolume, qty)));
            }
        }

    }
}