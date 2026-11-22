using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DaxemondStockMarkerApp.Services
{
    public interface IYahooService
    {
        Task<Dictionary<string, QuoteResult>> CheckManyAsync(IEnumerable<string> symbols);
        Task<Dictionary<string, (double? Trailing, double? Forward)>> FetchPeAsync(IEnumerable<string> symbols);
    }
    public record QuoteResult(string Date, double Prev, double Open, double Last, double Pct, double NowPct);
    internal class YahooService : IYahooService
    {
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _gate = new(8);   // max parallel chart requests
        private readonly string _priceMode = (Environment.GetEnvironmentVariable("COMPARE_PRICE") ?? "open").ToLowerInvariant();
        private string? _crumb;
        private ILogger<YahooService> _log;

        //private readonly string PriceMode = (Environment.GetEnvironmentVariable("COMPARE_PRICE") ?? "open").ToLowerInvariant();
        public YahooService(ILogger<YahooService> log)
        {
            _log = log;
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        }

        private async Task<QuoteResult?> GetChartAsync(string symbol)
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=10d&interval=1d";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var resp = await _http.GetAsync(url);
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(2000 * (attempt + 1));
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync();
                    return ParseChart(json);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("Chart fetch failed for {Symbol}: {Msg}", symbol, ex.Message);
                    await Task.Delay(500);
                }
            }
            return null;
        }

        private QuoteResult? ParseChart(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result");
            if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;

            var r0 = result[0];
            int gmtOffset = r0.GetProperty("meta").TryGetProperty("gmtoffset", out var g) ? g.GetInt32() : 0;
            if (!r0.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Array) return null;

            var quote = r0.GetProperty("indicators").GetProperty("quote")[0];
            var opens = quote.GetProperty("open");
            var closes = quote.GetProperty("close");

            // keep only rows with a Close (like df.dropna(subset=["Close"]))
            var rows = new List<(string Date, double? Open, double Close)>();
            for (int i = 0; i < ts.GetArrayLength(); i++)
            {
                double? close = Num(closes, i);
                if (close == null) continue;
                var date = DateTimeOffset.FromUnixTimeSeconds(ts[i].GetInt64() + gmtOffset)
                                         .UtcDateTime.ToString("yyyy-MM-dd");
                rows.Add((date, Num(opens, i), close.Value));
            }
            if (rows.Count < 2) return null;

            double prev = rows[^2].Close;
            double last = rows[^1].Close;
            double op = rows[^1].Open is double o && o > 0 ? o : last;
            if (prev <= 0) return null;

            double refPrice = _priceMode == "open" ? op : last;
            return new QuoteResult(
                rows[^1].Date, prev, op, last,
                (refPrice - prev) / prev * 100,
                (last - prev) / prev * 100);
        }

        private double? Num(JsonElement arr, int i) => i < arr.GetArrayLength() && arr[i].ValueKind == JsonValueKind.Number ? arr[i].GetDouble() : null;
        private async Task EnsureCrumbAsync()
        {
            if (_crumb != null) return;
            try { await _http.GetAsync("https://fc.yahoo.com"); } catch { /* only need the cookie */ }
            _crumb = await _http.GetStringAsync("https://query1.finance.yahoo.com/v1/test/getcrumb");
        }

        public async Task<Dictionary<string, (double? Trailing, double? Forward)>> FetchPeAsync(IEnumerable<string> symbols)
        {
            var output = new Dictionary<string, (double?, double?)>();
            try
            {
                await EnsureCrumbAsync();
                foreach (var batch in symbols.Distinct().Chunk(40))
                {
                    try
                    {
                        var url = "https://query1.finance.yahoo.com/v7/finance/quote?symbols=" +
                                  Uri.EscapeDataString(string.Join(",", batch)) +
                                  "&crumb=" + Uri.EscapeDataString(_crumb!);
                        var json = await _http.GetStringAsync(url);
                        using var doc = JsonDocument.Parse(json);
                        foreach (var q in doc.RootElement.GetProperty("quoteResponse").GetProperty("result").EnumerateArray())
                        {
                            var sym = q.GetProperty("symbol").GetString()!;
                            output[sym] = (Prop(q, "trailingPE"), Prop(q, "forwardPE"));
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("PE lookup failed for batch starting {S}: {Msg}", batch[0], ex.Message);
                    }
                    await Task.Delay(300);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("PE lookup failed: {Msg}", ex.Message);
            }
            return output;
        }

        private double? Prop(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        public async Task<Dictionary<string, QuoteResult>> CheckManyAsync(IEnumerable<string> symbols)
        {
            var results = new Dictionary<string, QuoteResult>();
            var tasks = symbols.Select(async s =>
            {
                await _gate.WaitAsync();
                try
                {
                    var r = await GetChartAsync(s);
                    if (r != null) lock (results) results[s] = r;
                }
                finally { _gate.Release(); }
            });
            await Task.WhenAll(tasks);
            return results;
        }
    }
}
