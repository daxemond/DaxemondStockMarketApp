using System;
using System.Collections.Generic;
using System.Text;
using ClosedXML.Excel;

namespace FunctionAppMovers.Services
{
    public interface IHkexLoader
    {
        Task<Dictionary<string, string>> LoadAsync();
    }
    public class HkexLoader : IHkexLoader
    {
        private const string HkexUrl = "https://www.hkex.com.hk/eng/services/trading/securities/securitieslists/ListOfSecurities.xlsx";
        public async Task<Dictionary<string, string>> LoadAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            var bytes = await http.GetByteArrayAsync(HkexUrl);

            using var wb = new XLWorkbook(new MemoryStream(bytes));
            var ws = wb.Worksheet(1);

            // find header row (first column == "Stock Code")
            int hdrRow = -1;
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (int r = 1; r <= lastRow; r++)
            {
                if (ws.Cell(r, 1).GetString().Trim() == "Stock Code") { hdrRow = r; break; }
            }
            if (hdrRow < 0) throw new InvalidOperationException("Header row 'Stock Code' not found.");

            int lastCol = ws.Row(hdrRow).LastCellUsed()?.Address.ColumnNumber ?? 0;
            var cols = new Dictionary<string, int>();
            for (int c = 1; c <= lastCol; c++) cols[ws.Cell(hdrRow, c).GetString().Trim()] = c;

            int cCode = cols["Stock Code"], cCat = cols["Category"], cName = cols["Name of Securities"];

            var stocks = new Dictionary<string, string>();
            for (int r = hdrRow + 1; r <= lastRow; r++)
            {
                var category = ws.Cell(r, cCat).GetString();
                if (!category.StartsWith("Equity")) continue;

                var codeStr = ws.Cell(r, cCode).GetString().Trim();
                if (codeStr.Length == 0 || !codeStr.All(char.IsDigit)) continue;

                int n = int.Parse(codeStr);
                if (n >= 80000 && n < 90000) continue;   // RMB counters duplicate the HKD counters

                stocks[$"{n:D4}.HK"] = ws.Cell(r, cName).GetString().Trim();
            }
            return stocks;
        }
    }
}
