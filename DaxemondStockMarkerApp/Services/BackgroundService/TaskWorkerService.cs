using System.Diagnostics;
using FunctionAppMovers.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DaxemondStockMarkerApp.Services
{

    public class TaskWorkerService : BackgroundService
    {
        private readonly TimeSpan[] _runTimes = new TimeSpan[] { }; 

        private readonly TimeZoneInfo _hkTz = GetHkTimeZone();
        private readonly TelemetryClient _logger;
        private readonly IMoverService _service;

        public TaskWorkerService(TelemetryClient logger,IMoverService service)
        {
            _logger = logger;
            _service = service;
            string timelines = Environment.GetEnvironmentVariable("FREQUENCE") ?? "9:15,16:37";
            string[] times = timelines.Split(",");
            _runTimes = times.Select(x => new
            {
                hour = int.Parse(x.Split(":")[0]),
                min = int.Parse(x.Split(":")[1])
            }).Select(x => new TimeSpan(x.hour,x.min,0)).ToArray();
        } 

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var nowHk = TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.Utc, _hkTz);
                var next = GetNextRun(nowHk);
                var delay = next - nowHk;

                _logger.TrackTrace($"Next run at {next} HKT (in {delay})",SeverityLevel.Information);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                    await StartProcess();
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // avoid double-fire
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private DateTime GetNextRun(DateTime nowHk)
        {
            foreach (var t in _runTimes)
            {
                var candidate = nowHk.Date + t;
                if (candidate > nowHk) return candidate;
            }
            return nowHk.Date.AddDays(1) + _runTimes[0];
        }

        private async Task StartProcess()
        {
            try
            {
                _logger.TrackTrace($"Starting process at {DateTime.Now}",SeverityLevel.Information);
                await _service.MainAsync();
            }
            catch (Exception ex)
            {
                _logger.TrackException(ex);
            }
        }

        private static TimeZoneInfo GetHkTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); } // Windows (HK is UTC+8)
            catch { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Hong_Kong"); }    // Linux/macOS
        }

    }
}
