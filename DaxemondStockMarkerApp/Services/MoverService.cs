using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FunctionAppMovers.Services
{
    public interface IMoverService
    {
        Task MainAsync();
    }
    internal class MoverService : IMoverService
    {
        private readonly Dictionary<string, string> Indices = new()
        {
            ["^HSI"] = "Hang Seng Index",
            ["^HSCE"] = "Hang Seng China Enterprises (HSCEI)",
            ["3033.HK"] = "Hang Seng TECH (CSOP ETF proxy)",
        };

        private record Row(double Pct, string Symbol, string Name, double Prev, double Open, double Last, double NowPct);
        private string FmtPe(double? v) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
        private static double EnvDouble(string key, double dflt) => double.TryParse(Environment.GetEnvironmentVariable(key), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v) ? v : dflt;

        private readonly double IndexDrop = EnvDouble("INDEX_DROP_PCT", 2.0);
        private readonly int TopN = (int)EnvDouble("TOP_N", 20);
        private readonly double MinPrice = EnvDouble("MIN_PRICE", 0);


        private readonly ILogger<MoverService> _log;
        private readonly IYahooService _yahoo;
        private readonly IHkexLoader _loader;
        private readonly IGraphMailer _mailer;
        public MoverService(ILogger<MoverService> log,IYahooService service,
            IHkexLoader loader,
            IGraphMailer mailer)
        {
            _log = log;
            _yahoo = service;
            _loader = loader;
            _mailer = mailer;
        }



        public async Task MainAsync()
        {
            // ---- indices ----
            var idxRes = await _yahoo.CheckManyAsync(Indices.Keys);
            var idxLines = new List<string>();
            var idxDrops = new List<string>();
            foreach (var (sym, name) in Indices)
            {
                if (!idxRes.TryGetValue(sym, out var r))
                {
                    _log.LogWarning("WARN {Sym}: no data returned", sym);
                    continue;
                }
                var line = $"{name} ({sym}): yesterday close {r.Prev:N2} -> open {r.Open:N2} " +
                           $"({r.Pct:+0.00;-0.00}%), now {r.Last:N2} ({r.NowPct:+0.00;-0.00}%) [{r.Date}]";
                idxLines.Add(line);
                if (r.Pct <= -IndexDrop) idxDrops.Add(line);
            }

            // ---- all stocks ----
            var ranked = new List<Row>();
            int total = 0;
            string refDate = "n/a";
            Dictionary<string, string> names = new();
            try
            {
                names = await _loader.LoadAsync();
                _log.LogInformation("Loaded {Count} HKEX equities", names.Count);
                var res = await _yahoo.CheckManyAsync(names.Keys);
                _log.LogInformation("loading data...");
                if (res.Count > 0)
                {
                    // Ignore suspended / stale stocks: keep only those trading on the latest market date
                    refDate = res.Values.GroupBy(r => r.Date).OrderByDescending(g => g.Count()).First().Key;
                    var live = res.Where(kv => kv.Value.Date == refDate && kv.Value.Last >= MinPrice).ToList();
                    total = live.Count;
                    ranked = live
                        .Select(kv => new Row(kv.Value.Pct, kv.Key, names[kv.Key], kv.Value.Prev,
                                              kv.Value.Open, kv.Value.Last, kv.Value.NowPct))
                        .OrderBy(x => x.Pct).ThenBy(x => x.Symbol, StringComparer.Ordinal)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("WARN stock scan failed: {Msg}", ex.Message);
            }

            var drops = ranked.Where(r => r.Pct < 0).Take(TopN).ToList();                       // most negative first
            var rises = Enumerable.Reverse(ranked).Where(r => r.Pct > 0).Take(TopN).ToList();   // most positive first

            var pe = await _yahoo.FetchPeAsync(drops.Concat(rises).Select(r => r.Symbol));

            IEnumerable<string> Fmt(IEnumerable<Row> rows) => rows.Select(r =>
            {
                pe.TryGetValue(r.Symbol, out var p);
                return $"{r.Symbol}  {r.Name}  yesterday close {r.Prev:0.000} -> open {r.Open:0.000} " +
                       $"({r.Pct:+0.00;-0.00}%), now {r.Last:0.000} ({r.NowPct:+0.00;-0.00}%)  " +
                       $"P/E {FmtPe(p.Trailing)} (fwd {FmtPe(p.Forward)})";
            });

            // ---- report ----
            var parts = new List<string>();
            if (idxLines.Count > 0)
            {
                var flag = idxDrops.Count > 0 ? $"  (** index down {IndexDrop}%+ **)" : "";
                parts.Add("INDICES" + flag + ":\n" + string.Join("\n", idxLines));
            }
            if (drops.Count > 0)
                parts.Add($"TOP {drops.Count} PRICE DROPS (open vs yesterday close; {total} stocks scanned, session {refDate}):\n"
                          + string.Join("\n", Fmt(drops)));
            if (rises.Count > 0)
                parts.Add($"TOP {rises.Count} PRICE RISES (open vs yesterday close):\n" + string.Join("\n", Fmt(rises)));

            var report = string.Join("\n\n", parts);
            _log.LogInformation("{Report}", parts.Count > 0 ? report : "No data.");

            if (parts.Count > 0)
            {
                var worst = drops.Count > 0 ? $"{drops[0].Symbol} {drops[0].Pct:+0.0;-0.0}%" : "n/a";
                var best = rises.Count > 0 ? $"{rises[0].Symbol} {rises[0].Pct:+0.0;-0.0}%" : "n/a";
                await _mailer.SendAsync($"HK movers {refDate}: worst {worst}, best {best}", report);
                _log.LogInformation("Email sent.");
            }
            else
            {
                _log.LogInformation("No data; no email sent.");
            }
        }


    }
}
