using OsEngine.Entity;
using OsEngine.Market.Servers;
using OsEngine.Market;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Tab;

using System;
using System.Collections.Generic;

namespace OsEngine.Robots
{
    public class SDKVolume
    {
        private BotPanel panel;
        public StrategyParameterString VolumeType;
        public StrategyParameterDecimal Volume;
        public StrategyParameterString TradeAssetInPortfolio;
        public StrategyParameterString FullTradeAssetInPortfolio;
        public SDKVolume(BotPanel panel)
        {
            this.panel = panel;
            CreateVolumeParameters();
        }

        public void CreateVolumeParameters()
        {
            VolumeType = panel.CreateParameter("Volume type", "Deposit percent", new[] { "Contracts", "Contract currency", "Deposit percent" }, "Volume");
            Volume = panel.CreateParameter("Volume", 20, 1.0m, 50, 4, "Volume");
            FullTradeAssetInPortfolio = panel.CreateParameter("Full Asset in portfolio", "Prime", "Volume");
            TradeAssetInPortfolio = panel.CreateParameter("Asset in portfolio (limit)", "Prime", "Volume");
        }

        public decimal GetVolume(BotTabSimple tab)
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

                if (panel.StartProgram == StartProgram.IsOsTrader)
                {
                    IServerPermission serverPermission = ServerMaster.GetServerPermission(tab.Connector.ServerType);

                    if (serverPermission != null &&
                        serverPermission.IsUseLotToCalculateProfit &&
                    tab.Security.Lot != 0 &&
                        tab.Security.Lot > 1)
                    {
                        volume = Volume.ValueDecimal / (contractPrice * tab.Security.Lot);
                    }

                    volume = Math.Round(volume, tab.Security.DecimalsVolume, MidpointRounding.ToNegativeInfinity);
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
                decimal fullPortfolioPrimeAsset = 0;

                if (FullTradeAssetInPortfolio.ValueString == "Prime")
                    fullPortfolioPrimeAsset = myPortfolio.ValueCurrent;
                if (TradeAssetInPortfolio.ValueString == "Prime")
                    portfolioPrimeAsset = myPortfolio.ValueCurrent;
                if (panel.StartProgram == StartProgram.IsOsTrader)
                {
                    List<PositionOnBoard> positionOnBoard = myPortfolio.GetPositionOnBoard();

                    if (positionOnBoard == null)
                    {
                        return 0;
                    }

                    for (int i = 0; i < positionOnBoard.Count; i++)
                    {
                        if (positionOnBoard[i].SecurityNameCode == TradeAssetInPortfolio.ValueString)
                            portfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                        if (positionOnBoard[i].SecurityNameCode == FullTradeAssetInPortfolio.ValueString)
                            fullPortfolioPrimeAsset = positionOnBoard[i].ValueCurrent;
                    }
                }

                if (portfolioPrimeAsset == 0 || fullPortfolioPrimeAsset == 0)
                {
                    panel.SendNewLogMessage("Can`t found portfolio " + TradeAssetInPortfolio.ValueString, Logging.LogMessageType.Error);
                    return 0;
                }

                decimal moneyOnPosition = Math.Min(fullPortfolioPrimeAsset * (Volume.ValueDecimal / 100), portfolioPrimeAsset);

                decimal qty = moneyOnPosition / tab.PriceBestAsk / tab.Security.Lot;

                if (tab.StartProgram == StartProgram.IsOsTrader)
                {
                    if (tab.Security.DecimalsVolume == 0)
                        qty = (int)qty;
                    else
                        qty = Math.Round(qty, tab.Security.DecimalsVolume, MidpointRounding.ToNegativeInfinity);
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

