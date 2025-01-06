/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System.Collections.Generic;
using OsEngine.Entity;
using OsEngine.Indicators;
using OsEngine.OsTrader.Panels.Tab;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Attributes;
using OsEngine.Market.Servers;
using OsEngine.Market;
using System;

namespace OsEngine.Robots.SDKRobots
{
    [Bot("SDKPriceChannelTrendAtrFilterScreener")]
    public class SDKPriceChannelTrendAtrFilterScreener : BotPanel
    {
        private BotTabScreener _tab;
        
        public StrategyParameterString Regime;
        public StrategyParameterInt PriceChannelLength;
        public StrategyParameterInt AtrLength;
        public StrategyParameterBool AtrFilterIsOn;
        public StrategyParameterDecimal AtrGrowPercent;
        public StrategyParameterInt AtrGrowLookBack;
        public StrategyParameterString VolumeType;
        public StrategyParameterDecimal Volume;
        public StrategyParameterString TradeAssetInPortfolio;

        public SDKPriceChannelTrendAtrFilterScreener(string name, StartProgram startProgram)
            : base(name, startProgram)
        {
            TabCreate(BotTabType.Screener);
            _tab = TabsScreener[0];

            Regime = CreateParameter("Regime", "Off", new[] { "Off", "On", "OnlyLong", "OnlyShort", "OnlyClosePosition" });

            VolumeType = CreateParameter("Volume type", "Deposit percent", new[] { "Contracts", "Contract currency", "Deposit percent" });
            Volume = CreateParameter("Volume", 20, 1.0m, 50, 4);
            TradeAssetInPortfolio = CreateParameter("Asset in portfolio", "Prime");

            PriceChannelLength = CreateParameter("Price channel length", 50, 10, 80, 3);
            AtrLength = CreateParameter("Atr length", 25, 10, 80, 3);

            AtrFilterIsOn = CreateParameter("Atr filter is on", false);
            AtrGrowPercent = CreateParameter("Atr grow percent", 3, 1.0m, 50, 4);
            AtrGrowLookBack = CreateParameter("Atr grow look back", 20, 1, 50, 4);

            _tab.CreateCandleIndicator(1, "PriceChannel", null, "Prime");
            _tab.CreateCandleIndicator(2, "ATR", null, "AtrArea");

            _tab.CandleFinishedEvent += _tab_CandleFinishedEvent;
        }

        public override string GetNameStrategyType()
        {
            return "SDKPriceChannelTrendAtrFilterScreener";
        }

        public override void ShowIndividualSettingsDialog()
        {

        }

        // logic

        private void _tab_CandleFinishedEvent(List<Candle> candles, BotTabSimple tab)
        {
            if (Regime.ValueString == "Off")
            {
                return;
            }

            Aindicator pc = (Aindicator)tab.Indicators[0];
            Aindicator atr = (Aindicator)tab.Indicators[1];

            if (PriceChannelLength.ValueInt != pc.ParametersDigit[0].Value ||
                PriceChannelLength.ValueInt != pc.ParametersDigit[1].Value)
            {
                pc.ParametersDigit[0].Value = PriceChannelLength.ValueInt;
                pc.ParametersDigit[1].Value = PriceChannelLength.ValueInt;
                pc.Save();
                pc.Reload();
            }

            if (atr.ParametersDigit[0].Value != AtrLength.ValueInt)
            {
                atr.ParametersDigit[0].Value = AtrLength.ValueInt;
                atr.Save();
                atr.Reload();
            }

            if (pc.DataSeries[0].Values == null 
                || pc.DataSeries[1].Values == null)
            {
                return;
            }

            if (pc.DataSeries[0].Values.Count < pc.ParametersDigit[0].Value + 2 
                || pc.DataSeries[1].Values.Count < pc.ParametersDigit[1].Value + 2)
            {
                return;
            }

            List<Position> openPositions = tab.PositionsOpenAll;

            if (openPositions == null || openPositions.Count == 0)
            {
                if (Regime.ValueString == "OnlyClosePosition")
                {
                    return;
                }
                LogicOpenPosition(candles, tab);
            }
            else
            {
                LogicClosePosition(candles, openPositions[0], tab);
            }
        }

        private void LogicOpenPosition(List<Candle> candles, BotTabSimple tab)
        {
            decimal lastPrice = candles[candles.Count - 1].Close;

            Aindicator pc = (Aindicator)tab.Indicators[0];
            Aindicator atr = (Aindicator)tab.Indicators[1];

            decimal lastPcUp = pc.DataSeries[0].Values[pc.DataSeries[0].Values.Count - 2];
            decimal lastPcDown = pc.DataSeries[1].Values[pc.DataSeries[1].Values.Count - 2];

            if(lastPcUp == 0 
                || lastPcDown == 0)
            {
                return;
            }

            if (lastPrice > lastPcUp
                && Regime.ValueString != "OnlyShort")
            {
                if (AtrFilterIsOn.ValueBool == true)
                {
                    if (atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt <= 0)
                    {
                        return;
                    }

                    decimal atrLast = atr.DataSeries[0].Values[atr.DataSeries[0].Values.Count - 1];
                    decimal atrLookBack =
                        atr.DataSeries[0].Values[atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt];

                    if (atrLast == 0
                        || atrLookBack == 0)
                    {
                        return;
                    }

                    decimal atrGrowPercent = atrLast / (atrLookBack / 100) - 100;

                    if (atrGrowPercent < AtrGrowPercent.ValueDecimal)
                    {
                        return;
                    }
                }

                tab.BuyAtMarket(GetVolume(tab));
            }
            if (lastPrice < lastPcDown
                && Regime.ValueString != "OnlyLong")
            {
                if (AtrFilterIsOn.ValueBool == true)
                {
                    if (atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt <= 0)
                    {
                        return;
                    }

                    decimal atrLast = atr.DataSeries[0].Values[atr.DataSeries[0].Values.Count - 1];
                    decimal atrLookBack =
                        atr.DataSeries[0].Values[atr.DataSeries[0].Values.Count - 1 - AtrGrowLookBack.ValueInt];

                    if (atrLast == 0
                        || atrLookBack == 0)
                    {
                        return;
                    }

                    decimal atrGrowPercent = atrLast / (atrLookBack / 100) - 100;

                    if (atrGrowPercent < AtrGrowPercent.ValueDecimal)
                    {
                        return;
                    }
                }

                tab.SellAtMarket(GetVolume(tab));
            }
        }

        private void LogicClosePosition(List<Candle> candles, Position position, BotTabSimple tab)
        {
            Aindicator pc = (Aindicator)tab.Indicators[0];

            decimal lastPcUp = pc.DataSeries[0].Values[pc.DataSeries[0].Values.Count - 1];
            decimal lastPcDown = pc.DataSeries[1].Values[pc.DataSeries[1].Values.Count - 1];

            if (position.Direction == Side.Buy)
            {
                tab.CloseAtTrailingStopMarket(position, lastPcDown);
            }
            if (position.Direction == Side.Sell)
            {
                tab.CloseAtTrailingStopMarket(position, lastPcUp);
            }
        }

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
                    volume = Math.Round(volume / tab.Security.Lot, 6);
                }
            }
            else if (VolumeType.ValueString == "Deposit percent")
            {
                Portfolio myPortfolio = tab.Portfolio;

                if (myPortfolio == null)
                {
                    return 0;
                }

                decimal portfolioPrimeAsset = 0;
                decimal portfolioPrimeAssetBlocked = 0;

                if (TradeAssetInPortfolio.ValueString == "Prime")
                {
                    portfolioPrimeAsset = myPortfolio.ValueCurrent;
                    portfolioPrimeAssetBlocked = myPortfolio.ValueBlocked;
                }
                else
                {
                    List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

                    if (positionOnBoard == null)
                    {
                        return 0;
                    }

                    for (int i = 0; i < positionOnBoard.Count; i++)
                    {
                        if (positionOnBoard[i].SecurityNameCode == TradeAssetInPortfolio.ValueString)
                        {
                            portfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                            portfolioPrimeAssetBlocked = positionOnBoard[i].ValueBlocked;
                            break;
                        }
                    }
                }

                if (portfolioPrimeAsset == 0)
                {
                    SendNewLogMessage("Can`t found portfolio " + TradeAssetInPortfolio.ValueString, Logging.LogMessageType.Error);
                    return 0;
                }

                decimal moneyOnPosition = Math.Min(portfolioPrimeAsset * (Volume.ValueDecimal / 100), portfolioPrimeAsset - portfolioPrimeAssetBlocked);

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