using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FunctionAppMovers.Services
{
    public interface IGraphMailer
    {
        Task SendAsync(string subject, string body);
    }
    public class GraphMailer : IGraphMailer
    {
        public async Task SendAsync(string subject, string body)
        {
            string[] needed = { "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "MAIL_SENDER" };
            var missing = needed.Where(k => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"Missing environment variable(s): {string.Join(", ", missing)}");

            var tenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID")!;
            var sender = Environment.GetEnvironmentVariable("MAIL_SENDER")!;
            var recipients = (Environment.GetEnvironmentVariable("ALERT_TO") ?? sender)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // 1) access token
            var tokResp = await http.PostAsync(
                $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")!,
                    ["client_secret"] = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET")!,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                }));
            tokResp.EnsureSuccessStatusCode();
            using var tokDoc = JsonDocument.Parse(await tokResp.Content.ReadAsStringAsync());
            var token = tokDoc.RootElement.GetProperty("access_token").GetString()!;

            // 2) send the mail (Graph returns 202 Accepted)
            var payload = new
            {
                message = new
                {
                    subject,
                    body = new { contentType = "Text", content = body },
                    toRecipients = recipients.Select(a => new { emailAddress = new { address = a } }).ToArray()
                },
                saveToSentItems = false
            };

            var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/users/{sender}/sendMail")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await http.SendAsync(req);
            if ((int)resp.StatusCode != 202)
                throw new InvalidOperationException(
                    $"Graph sendMail failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        }
    }
}
