using OsEngine.Attributes;
using OsEngine.Entity;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;

namespace OsEngine.Robots
{
    [Bot("SDKLQDT")] // We create an attribute so that we don't write anything to the BotFactory
    public class SDKLQDT : BotPanel
    {
        private BotTabSimple _tab;

        // Basic Settings
        [Parameter("Off", new[] { "Off", "On" })]
        private StrategyParameterString Regime;
        [Parameter("rub")]
        public StrategyParameterString TradeAssetInPortfolio;
        [Parameter(0.1, 0, 20, 1)]
        private StrategyParameterDecimal Slippage;
        [Parameter(500.0, "Max count lots for trade")]
        private StrategyParameterDecimal maxCountForTrade;

        [Parameter(false)]
        private StrategyParameterBool showErrorMessage;

        // Trade periods
        private NonTradePeriods _tradePeriodsSettings;
        private StrategyParameterButton _tradePeriodsShowDialogButton;

        public SDKLQDT(string name, StartProgram startProgram) : base(name, startProgram)
        {
            TabCreate(BotTabType.Simple);
            _tab = TabsSimple[0];

            _tradePeriodsSettings = new NonTradePeriods(name);
            _tradePeriodsSettings.Load();

            _tradePeriodsShowDialogButton = CreateParameterButton("Non trade periods");
            _tradePeriodsShowDialogButton.UserClickOnButtonEvent += _tradePeriodsShowDialogButton_UserClickOnButtonEvent;

            // Subscribe to the indicator update event
            ParametrsChangeByUser += _ParametrsChangeByUser;

            //Подписка на получение событий/команд из телеграма - Subscribe to receive events/commands from Telegram
            //ServerTelegram.GetServer().TelegramCommandEvent += TelegramCommandHandler;

            // Subscribe to the candle finished event
            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
        }

        private void _tradePeriodsShowDialogButton_UserClickOnButtonEvent()
        {
            _tradePeriodsSettings.ShowDialog();
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

            if (_tradePeriodsSettings.CanTradeThisTime(_tab.TimeServerCurrent) == false)
            {
                return;
            }

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