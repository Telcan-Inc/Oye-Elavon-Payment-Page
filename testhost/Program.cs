using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

const string origin = "http://localhost:5080";
const string cookie = "Oye.Sandbox";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(origin);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = cookie;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.IdleTimeout = TimeSpan.FromHours(1);
});

var configPath = Path.Combine(builder.Environment.ContentRootPath, "..", ".env.sandbox");
var config = new Dictionary<string, string>();
foreach (var line in File.ReadLines(configPath))
{
    string setting = line.Trim();
    if (setting.StartsWith('#') || setting.Length == 0) continue;
    if (setting.StartsWith("export ")) setting = setting.Substring(7).TrimStart();
    int split = setting.IndexOf('=');
    if (split < 1) continue;
    string value = setting.Substring(split + 1).Trim();
    if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
    config[setting.Substring(0, split).Trim()] = value;
}
var gateway = new ElavonGateway(config.GetValueOrDefault("ELAVON_HOST"),
    config.GetValueOrDefault("ELAVON_MERCHANT_ALIAS"), config.GetValueOrDefault("ELAVON_SECRET_KEY"));

var app = builder.Build();
// Core sessions do not lock. Serialize requests for a cookie through session load AND commit.
// Fixed stripes bound memory use for this single-process test host.
var locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    var sessionCookie = ctx.Request.Cookies[cookie];
    if (sessionCookie == null || ctx.Request.Path == "/PayOnline.css") { await next(); return; }
    var gate = locks[(uint)sessionCookie.GetHashCode() % (uint)locks.Length];
    await gate.WaitAsync(ctx.RequestAborted);
    try { await next(); }
    finally { gate.Release(); }
});
app.UseSession();

app.MapGet("/PayOnline.css", () => Results.File(Path.Combine(AppContext.BaseDirectory, "PayOnline.css"), "text/css"));
app.MapGet("/", (HttpContext ctx) =>
{
    return Form(ctx, ctx.Request.Query["acct"], "10", "",
        ctx.Request.Query["cancelled"] == "1" ? OyeCheckout.Cancelled : null);
});
app.MapPost("/", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType) return Results.BadRequest();
    var form = await ctx.Request.ReadFormAsync();
    string account = form["acct"], choice = form["choice"], other = form["other"];
    string issue = OyeCheckout.Validate(account, choice, other, out var amount);
    string token = ctx.Session.GetString("oye:token");
    if (token == null || token != (string)form["oyeToken"]) issue = OyeCheckout.FormExpired;
    if (issue == null)
    {
        try
        {
            var attempt = OyeCheckout.Start(gateway, account, amount, origin + "/return", origin + "/", out var redirect);
            ctx.Session.SetString("oye:attempt:" + attempt.Reference, JsonSerializer.Serialize(attempt));
            return Results.Redirect(redirect);
        }
        catch (Exception) { issue = OyeCheckout.StartError; }
    }
    return Form(ctx, account, choice, other, issue);
});

app.MapGet("/return", (HttpContext ctx) =>
{
    string data = ctx.Session.GetString("oye:attempt:" + ctx.Request.Query["ref"]);
    var attempt = data == null ? null : JsonSerializer.Deserialize<OyeCheckout.Attempt>(data);
    var query = ctx.Request.Query;
    string issue = OyeCheckout.Missing, retry = "/";
    if (attempt != null && (!query.ContainsKey("sessionId") || (string)query["sessionId"] == attempt.SessionId))
    {
        retry += "?acct=" + attempt.Account;
        try
        {
            var result = gateway.VerifyPayment(attempt.SessionId);
            issue = OyeCheckout.Check(attempt, result);
            if (issue == null)
            {
                string key = "oye:processed:" + result.TransactionId;
                if (ctx.Session.GetString(key) == null)
                {
                    CreditAccount(attempt.Account, attempt.Amount, result.TransactionId);
                    ctx.Session.SetString(key, "1");
                }
                return Page("Payment result", OyeCheckout.Receipt(attempt, result));
            }
        }
        catch (Exception) { issue = OyeCheckout.VerifyError; }
    }
    return Page("Payment result", OyeCheckout.Failure(issue, retry));
});
app.Run();

static void CreditAccount(string account, decimal amount, string transactionId)
{
    // Independent sandbox hook. Account balances are not connected.
}

static IResult Form(HttpContext ctx, string account, string choice, string other, string issue)
{
    string token = ctx.Session.GetString("oye:token") ?? Guid.NewGuid().ToString("N");
    ctx.Session.SetString("oye:token", token);
    return Page("Pay online", "<form action='/' method='post'>" + OyeCheckout.Fields(account, choice, other, issue)
        + "<input type='hidden' name='oyeToken' value='" + token + "'><button class='oye-action'>Pay</button></form>");
}

static IResult Page(string title, string contents)
{
    return Results.Content("<!doctype html><html lang='en'><head><meta charset='utf-8'>" +
        "<meta name='viewport' content='width=device-width, initial-scale=1'><title>OYE - " + title +
        "</title><link rel='stylesheet' href='/PayOnline.css'></head><body><main class='oye-card'>" +
        contents + "</main></body></html>", "text/html; charset=utf-8");
}
