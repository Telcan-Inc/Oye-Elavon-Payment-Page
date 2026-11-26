using System;

public partial class PayOnlineReturn : BasePage
{
    protected void Page_Load(object sender, EventArgs e)
    {
        Response.Cache.SetNoStore();
        string reference = Request.QueryString["ref"], appendedId = Request.QueryString["sessionId"];
        var attempt = Session["oye:attempt:" + reference] as OyeCheckout.Attempt;
        string retry = ResolveUrl("~/PayOnline.aspx");
        string issue = OyeCheckout.Missing;
        if (attempt != null && (appendedId == null || appendedId == attempt.SessionId))
        {
            retry += "?acct=" + attempt.Account;
            try
            {
                var result = ElavonGateway.FromAppSettings().VerifyPayment(attempt.SessionId);
                issue = OyeCheckout.Check(attempt, result);
                if (issue == null)
                {
                    // Web Forms holds the exclusive writable session lock for this request.
                    string key = "oye:processed:" + result.TransactionId;
                    if (Session[key] == null)
                    {
                        CreditAccount(attempt.Account, attempt.Amount, result.TransactionId);
                        Session[key] = true;
                    }
                    ResultCard.Text = OyeCheckout.Receipt(attempt, result);
                    return;
                }
            }
            catch (Exception) { issue = OyeCheckout.VerifyError; }
        }
        ResultCard.Text = OyeCheckout.Failure(issue, retry);
    }

    private void CreditAccount(string account, decimal amount, string transactionId)
    {
        // TODO Tahir: atomically credit the ledger once per unique Elavon transaction ID.
    }
}
