using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

public static class OyeCheckout
{
    public const decimal Minimum = 5m, Maximum = 500m;
    public const string Currency = "CAD";
    public const string Cancelled = "Payment cancelled. No money was taken.";
    public const string Missing = "This payment could not be found in your browser session.";
    public const string StartError = "We could not open the payment page. Please try again.";
    public const string VerifyError = "We could not verify this payment. Refresh this page to check again.";
    public const string FormExpired = "This form has expired. Please try again.";
    public static string Money(decimal n) { return n.ToString("0.00", CultureInfo.InvariantCulture); }
    public static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

    [Serializable]
    public sealed class Attempt
    {
        public string Reference { get; set; }
        public string SessionId { get; set; }
        public string Account { get; set; }
        public decimal Amount { get; set; }
    }
    public static string Validate(string account, string choice, string other, out decimal amount)
    {
        amount = 0;
        if (!Regex.IsMatch(account ?? "", @"\A[0-9]{10,15}\z")) return "Enter an account number with 10 to 15 digits.";
        string raw = choice == "other" ? other : choice;
        if ((choice != "other" && choice != "10" && choice != "20" && choice != "50")
            || !Regex.IsMatch(raw ?? "", @"\A[0-9]+(\.[0-9]{1,2})?\z")
            || !decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
            || amount < Minimum || amount > Maximum)
            return "Enter an amount from CAD $" + Money(Minimum) + " to $" + Money(Maximum) + " with up to two decimal places.";
        return null;
    }

    public static Attempt Start(ElavonGateway gateway, string account, decimal amount, string returnPage, string payPage, out string redirect)
    {
        var a = new Attempt { Account = account, Amount = amount, Reference = "OYE-" +
            DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") };
        var order = gateway.CreateOrder(amount, Currency, a.Reference, "Oye add funds " + account);
        var session = gateway.CreatePaymentSession(ElavonGateway.Field(order, "href"), returnPage + "?ref=" + a.Reference,
            payPage + "?cancelled=1&acct=" + account, a.Reference);
        a.SessionId = ElavonGateway.Field(session, "id");
        Uri hosted;
        if (string.IsNullOrEmpty(a.SessionId) || !Uri.TryCreate(ElavonGateway.Field(session, "url"), UriKind.Absolute, out hosted)
            || hosted.Scheme != "https" || hosted.UserInfo != "") throw new InvalidOperationException(StartError);
        redirect = hosted.AbsoluteUri;
        return a;
    }

    // A null message means the verified sale is eligible for the credit hook.
    public static string Check(Attempt a, ElavonGateway.Result r)
    {
        switch (r.Status)
        {
            case ElavonGateway.Outcome.Approved:
                return r.Reference == a.Reference && r.Amount == a.Amount && r.Currency == Currency
                    ? null : "The payment details do not match your request. Contact Oye before paying again.";
            case ElavonGateway.Outcome.Declined: return r.Reason ?? "Your payment was declined.";
            case ElavonGateway.Outcome.Cancelled: return "The payment was cancelled or expired.";
            case ElavonGateway.Outcome.Pending: return "Your payment is pending. Refresh this page to check again before paying again.";
            default: return VerifyError;
        }
    }

    public static string Fields(string account, string choice, string other, string message)
    {
        string options = "";
        foreach (var value in new[] { "10", "20", "50", "other" })
            options += "<label class='oye-option'><input type='radio' name='choice' value='" + value + "'" +
                (value == choice ? " checked" : "") + "> " + (value == "other" ? "Other amount" : "$" + value) + "</label>";
        return "<h1>Pay online</h1>" + (string.IsNullOrEmpty(message) ? "" : "<p role='status' class='oye-message'>" + H(message) + "</p>") +
            "<label for='oye-account'>Oye account number</label><input id='oye-account' type='tel' name='acct' inputmode='numeric' required pattern='[0-9]{10,15}' value='" + H(account) + "'>" +
            "<fieldset><legend>Amount (CAD)</legend><div class='oye-options'>" + options + "</div></fieldset>" +
            "<label for='oye-other'>Other amount ($" + Money(Minimum) + " to $" + Money(Maximum) + ")</label>" +
            "<input id='oye-other' name='other' inputmode='decimal' value='" + H(other) + "' oninput=\"this.form.elements.choice.value='other'\">" +
            "<p>You will enter your card on Elavon's secure payment page.</p>";
    }

    public static string Failure(string message, string retry)
    {
        return "<h1>Payment not confirmed</h1><p>" + H(message) + "</p><a class='oye-action' href='" + H(retry) + "'>Try again</a>";
    }

    public static string Receipt(Attempt a, ElavonGateway.Result r)
    {
        DateTimeOffset date;
        string when = DateTimeOffset.TryParse(r.Date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date)
            ? date.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "Unavailable";
        return "<h1>Payment approved</h1><dl><dt>Amount</dt><dd>CAD $" + Money(a.Amount) +
            "</dd><dt>Oye account number</dt><dd>" + H(a.Account) + "</dd><dt>Transaction ID</dt><dd>" + H(r.TransactionId) +
            "</dd><dt>Date</dt><dd>" + when + "</dd></dl>";
    }
}
