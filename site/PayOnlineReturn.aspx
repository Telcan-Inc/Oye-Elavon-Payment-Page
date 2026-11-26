<%@ Page Title="OYE - Payment result" Language="C#" MasterPageFile="~/site/site.master" AutoEventWireup="true" CodeFile="PayOnlineReturn.aspx.cs" Inherits="PayOnlineReturn" %>
<asp:Content ID="Head" ContentPlaceHolderID="cphHead" runat="server">
    <link runat="server" rel="stylesheet" href="~/PayOnline.css" />
</asp:Content>
<asp:Content ID="Main" ContentPlaceHolderID="cphMain" runat="server">
    <section class="oye-card"><asp:Literal ID="ResultCard" runat="server" /></section>
</asp:Content>
