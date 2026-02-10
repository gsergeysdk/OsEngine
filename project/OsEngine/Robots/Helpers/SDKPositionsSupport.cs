using Google.Protobuf.WellKnownTypes;
using OsEngine.Entity;
using OsEngine.Market;
using OsEngine.Market.Servers;
using OsEngine.Market.Servers.GateIo.GateIoFutures.Entities;
using OsEngine.OsTrader.Panels;
using OsEngine.OsTrader.Panels.Tab;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Documents;
using TL;
using static OsEngine.Market.Servers.Deribit.Entity.ResponseMessageError;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace OsEngine.Robots.Helpers
{
    public class SDKPositionsSupport
    {
        private BotPanel panel;
        private BotTabScreener tabScreener = null;

        // open filters
        public StrategyParameterBool checkByHistoryTradesBestPrice;
        public StrategyParameterBool checkByHistoryTradesWorstPrice;
        public StrategyParameterDecimal blockDaysAfterProfitTrade;
        public StrategyParameterDecimal blockDaysAfterLossTrade;
        public StrategyParameterInt maxUnsafePositions;
        public StrategyParameterDecimal unsafePositionPercent;

        public StrategyParameterBool checkDividendsCutOff;
        public StrategyParameterDecimal blockDaysBeforeDividends;
        public StrategyParameterBool closePositionBeforeDayCutOff;
        public StrategyParameterTimeOfDay closePositionBeforeDayCutOffTime;
        public StrategyParameterString filePathDividends;
        private StrategyParameterButton setFilePathDividends;
        private StrategyParameterButton loadDividendsButton;

        public SDKPositionsSupport(BotPanel panel, BotTabScreener tab_screener = null)
        {
            this.panel = panel;
            tabScreener = tab_screener;
            CreateSupportParameters();
            LoadDividendsTable();
        }

        public void CreateSupportParameters()
        {
            panel.CreateParameterLabel("label1", "", "Check new position by history trades", 20, 10, System.Drawing.Color.White, "Support");
            checkByHistoryTradesBestPrice = panel.CreateParameter("With best price", false, "Support");
            checkByHistoryTradesWorstPrice = panel.CreateParameter("With worst price", true, "Support");
            blockDaysAfterProfitTrade = panel.CreateParameter("Block days after profit trade", 1m, 1, 10, 1, "Support");
            blockDaysAfterLossTrade = panel.CreateParameter("Block days after loss trade", 7m, 1, 10, 1, "Support");
            maxUnsafePositions = panel.CreateParameter("Max unsafe positions", 0, 1, 10, 1, "Support");
            unsafePositionPercent = panel.CreateParameter("Unsafe position percent", 5m, 1, 10, 1, "Support");

            panel.CreateParameterLabel("label2", "", "Dividends", 20, 10, System.Drawing.Color.White, "Support");
            checkDividendsCutOff = panel.CreateParameter("Check dividends", false, "Support");
            closePositionBeforeDayCutOff = panel.CreateParameter("Close position before dividend", true, "Support");
            closePositionBeforeDayCutOffTime = panel.CreateParameterTimeOfDay("Close at time", 14, 0, 0, 0, "Support");
            blockDaysBeforeDividends = panel.CreateParameter("Block days before dividends", 2m, 1, 10, 1, "Support");
            filePathDividends = panel.CreateParameter("File dividends", "Engine\\dividends.csv", "Support");;
            setFilePathDividends = panel.CreateParameterButton("Set file dividends", "Support");
            setFilePathDividends.UserClickOnButtonEvent += setFilePathDividendsCb;
            loadDividendsButton = panel.CreateParameterButton("ReloadDividendsTable", "Support");
            loadDividendsButton.UserClickOnButtonEvent += LoadDividendsTableForce;
        }

        public bool CanOpenNewPosition(BotTabSimple tab, List<Candle> candles, decimal price, Side Direction)
        {
            CheckDividendsTable();
            bool allowOpen = true;
            if (checkByHistoryTradesBestPrice || checkByHistoryTradesWorstPrice)
            {
                // find last closed position
                Position position = null;
                for (int i = tab.PositionsAll.Count - 1; i >= 0; i--)
                {
                    Position value = tab.PositionsAll[i];
                    if (value.State == PositionStateType.Done)
                    {
                        position = value;
                        break;
                    }
                }
                if (position != null)
                {
                    TimeSpan diffTime = tab.TimeServerCurrent - position.TimeClose;
                    if (Direction == Side.Buy && checkByHistoryTradesBestPrice && position.ClosePrice > price)
                    {
                        bool positionProfit = position.ClosePrice > position.EntryPrice;
                        allowOpen &= positionProfit ? diffTime.TotalDays > (double)blockDaysAfterProfitTrade.ValueDecimal :
                            diffTime.TotalDays > (double)blockDaysAfterLossTrade.ValueDecimal;
                    }
                    if (Direction == Side.Buy && checkByHistoryTradesWorstPrice && position.ClosePrice < price)
                    {
                        bool positionProfit = position.ClosePrice > position.EntryPrice;
                        allowOpen &= positionProfit ? diffTime.TotalDays > (double)blockDaysAfterProfitTrade.ValueDecimal :
                            diffTime.TotalDays > (double)blockDaysAfterLossTrade.ValueDecimal;
                    }
                    if (Direction == Side.Sell && checkByHistoryTradesBestPrice && position.ClosePrice < price)
                    {
                        bool positionProfit = position.ClosePrice < position.EntryPrice;
                        allowOpen &= positionProfit ? diffTime.TotalDays > (double)blockDaysAfterProfitTrade.ValueDecimal :
                            diffTime.TotalDays > (double)blockDaysAfterLossTrade.ValueDecimal;
                    }
                    if (Direction == Side.Sell && checkByHistoryTradesWorstPrice && position.ClosePrice > price)
                    {
                        bool positionProfit = position.ClosePrice < position.EntryPrice;
                        allowOpen &= positionProfit ? diffTime.TotalDays > (double)blockDaysAfterProfitTrade.ValueDecimal :
                            diffTime.TotalDays > (double)blockDaysAfterLossTrade.ValueDecimal;
                    }
                }
            }

            if (blockDaysBeforeDividends > 0)
            {
                DividendData dividend = GetNearestDividend(tab);
                if (dividend != null)
                {
                    TimeSpan diffTime = dividend.dividendLastDayDate - tab.TimeServerCurrent;
                    allowOpen &= diffTime.TotalDays > (double)blockDaysBeforeDividends.ValueDecimal;
                }
            }

            if (allowOpen && maxUnsafePositions > 0 && tabScreener != null)
            {
                int countUnsafe = 0;
                for (int i = tabScreener.PositionsOpenAll.Count - 1; i >= 0; i--)
                {
                    Position position = tabScreener.PositionsOpenAll[i];
                    decimal ProfitOperationPercent = -100m;
                    if (position.StopOrderPrice == 0m)
                    {
                        for (int j = tabScreener.Tabs.Count - 1; j >= 0; j--)
                        {
                            BotTabSimple tabSimple = tabScreener.Tabs[j];
                            if (position.SecurityName == tabSimple.Security.Name)
                                ProfitOperationPercent = tabSimple.PriceBestBid / position.EntryPrice * 100 - 100;
                        }
                    }
                    if ((position.State == PositionStateType.Open || position.State == PositionStateType.Closing) &&
                        ((position.StopOrderPrice == 0m && ProfitOperationPercent < unsafePositionPercent) ||
                        (position.StopOrderPrice != 0m &&
                        ((position.StopOrderPrice < position.EntryPrice && position.Direction == Side.Buy) ||
                        (position.StopOrderPrice > position.EntryPrice && position.Direction == Side.Sell)))))
                        countUnsafe++;
                }
                allowOpen = countUnsafe < maxUnsafePositions;
            }

            return allowOpen;
        }

        public class DividendData
        {
            public string SecName;
            public DateTime dividendCutOffDate;
            public DateTime dividendLastDayDate;
            public decimal dividendValue;
        }

        public List<DividendData> dividends;
        public DateTime timeUpatedDividends = DateTime.MinValue;

        public void CheckDividendsTable()
        {
            if (checkDividendsCutOff && (DateTime.Now - timeUpatedDividends).Days > 1)
            {
                dividends = null;
                LoadDividendsTable();
            }
        }

        public void LoadDividendsTableForce()
        {
            if (!checkDividendsCutOff)
                return;
            dividends = null;
            LoadDividendsTable();
            if (dividends != null && dividends.Count > 1)
            {
                CustomMessageBoxUi ui = new CustomMessageBoxUi("Successfully loaded.");
                ui.ShowDialog();
            }
        }

        public void LoadDividendsTable()
        {
            if (!checkDividendsCutOff || (dividends != null && dividends.Count > 1))
                return;
            try
            {
                timeUpatedDividends = DateTime.Now;
                dividends = new List<DividendData>();
                //Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var lines = File.ReadAllLines(filePathDividends);//, Encoding.GetEncoding(1251));
                bool firstLine = true;
                foreach (var line in lines)
                {
                    var cells = line.Split(',');
                    if (firstLine)
                    {
                        firstLine = false;
                        continue;
                    }
                    var item = new DividendData();
                    item.SecName = cells[1];
                    var cultureInfo = new CultureInfo("ru-RU");
                    item.dividendCutOffDate = DateTime.Parse(cells[6], cultureInfo);
                    item.dividendLastDayDate = DateTime.Parse(cells[5], cultureInfo);
                    item.dividendValue = cells[3].ToDecimal();
                    dividends.Add(item);
                }
            }
            catch (Exception error)
            {
                CustomMessageBoxUi ui = new CustomMessageBoxUi(error.ToString());
                ui.ShowDialog();
            }
        }

        public void setFilePathDividendsCb()
        {
            try
            {
                System.Windows.Forms.OpenFileDialog openFileDialog = new System.Windows.Forms.OpenFileDialog();
                openFileDialog.Filter = "csv files (*.csv)|*.csv";
                openFileDialog.InitialDirectory = System.Windows.Forms.Application.StartupPath;
                openFileDialog.RestoreDirectory = true;
                openFileDialog.ShowDialog();

                if (string.IsNullOrEmpty(openFileDialog.FileName))
                {
                    return;
                }

                string filePath = openFileDialog.FileName;

                if (File.Exists(filePath) == false)
                {
                    return;
                }

                filePathDividends.ValueString = filePath;
                panel.ParamGuiSettings.RePaintParameterTables();
                LoadDividendsTableForce();
            }
            catch (Exception ex)
            {
                CustomMessageBoxUi ui = new CustomMessageBoxUi(ex.ToString());
                ui.ShowDialog();
            }
        }

        public bool IsNeedClosePosition(BotTabSimple tab, Position position)
        {
            CheckDividendsTable();
            if (!checkDividendsCutOff || dividends == null)
                return false;
            string SecName = tab.Security.Name.Split('.')[0];
            DateTime nearestCutOffDate = DateTime.MinValue;
            DateTime nearestLastDayDate = DateTime.MinValue;
            TimeSpan minDiff = TimeSpan.MaxValue;
            foreach (var div in dividends)
            {
                if (div.SecName == SecName)
                {
                    TimeSpan diff = div.dividendCutOffDate - tab.TimeServerCurrent;
                    if (diff.Ticks > 0 && diff < minDiff)
                    {
                        nearestCutOffDate = div.dividendCutOffDate;
                        nearestLastDayDate = div.dividendLastDayDate;
                        minDiff = diff;
                    }
                }
            }

            if (closePositionBeforeDayCutOff && nearestLastDayDate != DateTime.MinValue &&
                tab.TimeServerCurrent > nearestLastDayDate && closePositionBeforeDayCutOffTime.Value < tab.TimeServerCurrent)
                return true;

            return false;
        }

        public DividendData GetNearestDividend(BotTabSimple tab)
        {
            CheckDividendsTable();
            if (!checkDividendsCutOff || dividends == null)
                return null;
            string SecName = tab.Security.Name.Split('.')[0];
            DividendData dividend = null;
            TimeSpan minDiff = TimeSpan.MaxValue;
            foreach (var div in dividends)
            {
                if (div.SecName == SecName)
                {
                    TimeSpan diff = div.dividendCutOffDate - tab.TimeServerCurrent;
                    if (diff.Ticks > 0 && diff < minDiff)
                    {
                        dividend = div;
                        minDiff = diff;
                    }
                }
            }

            return dividend;
        }
        //public bool IsDividendCutOffOnDate(BotTabSimple tab, DateTime date)
        //{
        //    return true;
        //}


    }
}

