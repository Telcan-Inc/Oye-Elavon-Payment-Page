using System;

public partial class PayOnline : BasePage
{
    protected string FormToken;
    protected void Page_Load(object sender, EventArgs e)
    {
        Response.Cache.SetNoStore();
        FormToken = Session["oye:token"] as string;
        if (FormToken == null) Session["oye:token"] = FormToken = Guid.NewGuid().ToString("N");
        Fields.Text = OyeCheckout.Fields(Request.QueryString["acct"], "10", "",
            Request.QueryString["cancelled"] == "1" ? OyeCheckout.Cancelled : null);
    }

    protected void Pay_Click(object sender, EventArgs e)
    {
        string account = Request.Form["acct"], choice = Request.Form["choice"], other = Request.Form["other"];
        decimal amount;
        string issue = OyeCheckout.Validate(account, choice, other, out amount);
        if (Request.Form["oyeToken"] != FormToken) issue = OyeCheckout.FormExpired;
        if (issue == null)
        {
            try
            {
                string redirect;
                var attempt = OyeCheckout.Start(ElavonGateway.FromAppSettings(), account, amount,
                    AbsolutePage("PayOnlineReturn.aspx"), AbsolutePage("PayOnline.aspx"), out redirect);
                Session["oye:attempt:" + attempt.Reference] = attempt;
                Response.Redirect(redirect, false);
                Context.ApplicationInstance.CompleteRequest();
                return;
            }
            catch (Exception) { issue = OyeCheckout.StartError; }
        }
        Fields.Text = OyeCheckout.Fields(account, choice, other, issue);
    }

    // Relative to this page's folder (e.g. /pay/), so the pages work in any folder.
    string AbsolutePage(string page) { return new Uri(Request.Url, page).AbsoluteUri; }
}
