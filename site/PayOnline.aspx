<%@ Page Title="OYE - Pay online" Language="C#" MasterPageFile="~/site/site.master" AutoEventWireup="true" CodeFile="PayOnline.aspx.cs" Inherits="PayOnline" %>
<asp:Content ID="Head" ContentPlaceHolderID="cphHead" runat="server">
    <link runat="server" rel="stylesheet" href="~/PayOnline.css" />
</asp:Content>
<asp:Content ID="Main" ContentPlaceHolderID="cphMain" runat="server">
    <section class="oye-card"><asp:Literal ID="Fields" runat="server" /><input type="hidden" name="oyeToken" value="<%= FormToken %>" /><asp:Button ID="Pay" runat="server" CssClass="oye-action" Text="Pay" OnClick="Pay_Click" CausesValidation="false" /></section>
</asp:Content>
