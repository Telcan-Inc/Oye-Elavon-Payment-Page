// ASP.NET Web Site's CodeDOM compiler does not define SDK target symbols.
#if !NETCOREAPP && !NETFRAMEWORK
#define NETFRAMEWORK
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public sealed class ElavonGateway
{
    readonly Uri host;
    readonly string credentials;
    readonly string secret;
    readonly string defaultCurrency;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public ElavonGateway(string host, string merchantAlias, string secretKey, string currency = "CAD")
    {
        if (!Uri.TryCreate(host, UriKind.Absolute, out this.host) || this.host.Scheme != "https"
            || this.host.AbsolutePath != "/" || this.host.UserInfo != "" || this.host.Query != "" || this.host.Fragment != ""
            || string.IsNullOrWhiteSpace(merchantAlias) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Configure the Elavon HTTPS host, merchant alias and secret key.");
        secret = secretKey;
        credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(merchantAlias + ":" + secretKey));
        defaultCurrency = currency;
#if NETFRAMEWORK
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
#endif
    }

#if NETFRAMEWORK
    public static ElavonGateway FromAppSettings()
    {
        var s = System.Configuration.ConfigurationManager.AppSettings;
        return new ElavonGateway(s["Elavon.Host"], s["Elavon.MerchantAlias"], s["Elavon.SecretKey"]);
    }
#endif

    public Dictionary<string, object> CreateOrder(decimal amount, string currency, string reference, string description)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount) throw new ArgumentOutOfRangeException("amount");
        return Request(new Uri(host, "orders"), new Dictionary<string, object> {
            { "total", new Dictionary<string, object> { { "amount", amount.ToString("0.00", Invariant) }, { "currencyCode", currency ?? defaultCurrency } } },
            { "orderReference", reference }, { "description", description }
        });
    }

    public Dictionary<string, object> CreatePaymentSession(string orderHref, string returnUrl, string cancelUrl, string reference)
    {
        return Request(new Uri(host, "payment-sessions"), new Dictionary<string, object> {
            { "order", Resource(orderHref, "orders").AbsoluteUri }, { "returnUrl", returnUrl },
            { "cancelUrl", cancelUrl }, { "customReference", reference }, { "doCreateTransaction", "true" }
        });
    }

    public Dictionary<string, object> GetPaymentSession(string id)
    {
        return Request(Resource(new Uri(host, "payment-sessions/" + Uri.EscapeDataString(id)).AbsoluteUri, "payment-sessions"), null);
    }

    public Dictionary<string, object> GetTransaction(string href) { return Request(Resource(href, "transactions"), null); }

    Uri Resource(string href, string collection)
    {
        Uri uri;
        if (!Uri.TryCreate(href, UriKind.Absolute, out uri) || uri.Scheme != host.Scheme || uri.Authority != host.Authority
            || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != ""
            || !Regex.IsMatch(uri.AbsolutePath, @"\A/" + collection + @"/[A-Za-z0-9_-]+\z"))
            throw new InvalidDataException("Unexpected Elavon resource URL.");
        return uri;
    }

    Dictionary<string, object> Request(Uri uri, Dictionary<string, object> body)
    {
#if NETCOREAPP
#pragma warning disable SYSLIB0014
#endif
        var req = (HttpWebRequest)WebRequest.Create(uri);
#if NETCOREAPP
#pragma warning restore SYSLIB0014
#endif
        req.AllowAutoRedirect = false;
        req.Timeout = req.ReadWriteTimeout = 15000;
        req.Method = body == null ? "GET" : "POST";
        req.Accept = req.ContentType = "application/json";
        req.Headers["Accept-Version"] = "1";
        req.Headers["Authorization"] = "Basic " + credentials;
        try
        {
            if (body != null)
            {
                byte[] data = Encoding.UTF8.GetBytes(Json.Write(body));
                req.ContentLength = data.Length;
                using (var stream = req.GetRequestStream()) stream.Write(data, 0, data.Length);
            }
            using (var response = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var content = reader.ReadToEnd();
                if ((int)response.StatusCode / 100 != 2) throw ApiError(response, content);
                return new Json(content).ReadObject();
            }
        }
        catch (WebException e)
        {
            using (var response = e.Response as HttpWebResponse)
            {
                if (response == null) throw new IOException("Elavon request failed (" + e.Status + ").");
                using (var reader = new StreamReader(response.GetResponseStream()))
                    throw ApiError(response, reader.ReadToEnd());
            }
        }
    }

    IOException ApiError(HttpWebResponse response, string content)
    {
        return new IOException("Elavon HTTP " + (int)response.StatusCode + ": " +
            content.Replace(credentials, "[redacted]").Replace(secret, "[redacted]"));
    }

    public enum Outcome { Approved, Declined, Pending, Cancelled, Error }
    public sealed class Result
    {
        public Outcome Status;
        public decimal? Amount;
        public string Currency, TransactionId, AuthCode, CardBrand, Last4, Reference, RawState, Date, Reason;
    }

    public static string Field(Dictionary<string, object> obj, string key)
    {
        object value;
        return obj != null && obj.TryGetValue(key, out value) ? value as string : null;
    }

    static object Value(Dictionary<string, object> obj, string key)
    {
        object value;
        return obj.TryGetValue(key, out value) ? value : null;
    }

    public Result VerifyPayment(string sessionId)
    {
        var s = GetPaymentSession(sessionId);
        var r = new Result { Status = Outcome.Pending, RawState = Field(s, "state"), Reference = Field(s, "customReference") };
        var href = Field(s, "transaction");
        if (href == null)
        {
            DateTimeOffset expiry;
            if (r.RawState == "expired" || r.RawState == "cancelled" || r.RawState == "canceled" ||
                (DateTimeOffset.TryParse(Field(s, "expiresAt"), Invariant, DateTimeStyles.AssumeUniversal, out expiry) && expiry <= DateTimeOffset.UtcNow))
                r.Status = Outcome.Cancelled;
            return r;
        }
        var t = GetTransaction(href);
        var total = Value(t, "total") as Dictionary<string, object>;
        var card = Value(t, "card") as Dictionary<string, object>;
        r.RawState = Field(t, "state");
        r.TransactionId = Field(t, "id");
        r.AuthCode = Field(t, "authorizationCode");
        r.Date = Field(t, "createdAt");
        r.CardBrand = Field(card, "brand") ?? Field(card, "scheme");
        r.Last4 = Field(card, "last4");
        r.Currency = Field(total, "currencyCode");
        decimal amount;
        if (decimal.TryParse(Field(total, "amount"), NumberStyles.AllowDecimalPoint, Invariant, out amount)) r.Amount = amount;
        switch (r.RawState)
        {
            case "declined":
            case "rejected":
                r.Status = Outcome.Declined;
                var failures = Value(t, "failures") as List<object>;
                if (failures != null && failures.Count > 0) r.Reason = Field(failures[0] as Dictionary<string, object>, "description");
                break;
            case "cancelled": case "canceled": case "voided": case "expired":
                r.Status = Outcome.Cancelled;
                break;
            case "authorized": case "captured": case "settled":
                if (Equals(Value(t, "isHeldForReview"), true)) break;
                r.Status = Field(t, "type") == "sale" && Equals(Value(t, "isAuthorized"), true)
                    && r.Amount.HasValue && !string.IsNullOrEmpty(r.Currency) && !string.IsNullOrEmpty(r.TransactionId)
                    ? Outcome.Approved : Outcome.Error;
                break;
        }
        return r;
    }

    // The gateway sends JSON objects and arrays. Request payloads use only objects and strings.
    sealed class Json
    {
        readonly string input;
        int pos;
        public Json(string input) { this.input = input; }
        static FormatException Invalid() { return new FormatException("Invalid Elavon JSON response."); }
        char Peek() { return pos < input.Length ? input[pos] : '\0'; }
        void Space() { while (pos < input.Length && " \t\r\n".IndexOf(input[pos]) >= 0) pos++; }
        bool Take(char c) { Space(); if (Peek() != c) return false; pos++; return true; }
        void Need(char c) { if (!Take(c)) throw Invalid(); }
        public Dictionary<string, object> ReadObject()
        {
            var obj = Read(0) as Dictionary<string, object>;
            Space();
            if (obj == null || pos != input.Length) throw Invalid();
            return obj;
        }
        static readonly Regex Number = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?");
        object Read(int depth)
        {
            if (depth > 64) throw Invalid();
            Space();
            if (Peek() == '"') return StringValue();
            if (Take('{'))
            {
                var obj = new Dictionary<string, object>();
                if (Take('}')) return obj;
                do { string k = StringValue(); Need(':'); obj.Add(k, Read(depth + 1)); } while (Take(','));
                Need('}'); return obj;
            }
            if (Take('['))
            {
                var list = new List<object>();
                if (Take(']')) return list;
                do { list.Add(Read(depth + 1)); } while (Take(','));
                Need(']'); return list;
            }
            foreach (string token in new[] { "true", "false", "null" })
                if (string.CompareOrdinal(input, pos, token, 0, token.Length) == 0)
                {
                    pos += token.Length;
                    return token == "null" ? null : (object)(token == "true");
                }
            Match m = Number.Match(input, pos);
            if (!m.Success) throw Invalid();
            pos += m.Length;
            return decimal.Parse(m.Value, NumberStyles.Float, Invariant);
        }
        string StringValue()
        {
            Need('"');
            var sb = new StringBuilder();
            while (pos < input.Length)
            {
                char c = input[pos++];
                if (c == '"') return sb.ToString();
                if (c < 32) throw Invalid();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= input.Length) throw Invalid();
                c = input[pos++];
                if (c == 'u')
                {
                    ushort code;
                    if (pos + 4 > input.Length || !ushort.TryParse(input.Substring(pos, 4), NumberStyles.AllowHexSpecifier, Invariant, out code)) throw Invalid();
                    sb.Append((char)code); pos += 4;
                }
                else
                {
                    int i = "\"\\/bfnrt".IndexOf(c);
                    if (i < 0) throw Invalid();
                    sb.Append("\"\\/\b\f\n\r\t"[i]);
                }
            }
            throw Invalid();
        }
        public static string Write(object value)
        {
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                var pairs = new List<string>();
                foreach (var p in obj) pairs.Add(Write(p.Key) + ":" + Write(p.Value));
                return "{" + string.Join(",", pairs.ToArray()) + "}";
            }
            var sb = new StringBuilder("\"");
            foreach (char c in (string)value)
            {
                if (c == '"' || c == '\\') sb.Append('\\');
                if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", Invariant));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
