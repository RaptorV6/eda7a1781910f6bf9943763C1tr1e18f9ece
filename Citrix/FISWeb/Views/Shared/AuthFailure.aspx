<%@ Page Language="C#" Inherits="Citrix.Web.AuthControllers.Helpers.ProxyFormsViewBase<Object>" %>
<?xml version="1.0" encoding="UTF-8"?>
<AuthenticationStatus xmlns="http://citrix.com/delivery-services/webAPI/2-6/authStatus">
  <Result>fail</Result>
<% if (! string.IsNullOrEmpty((string) ViewData["Message"])) { %>
  <LogMessage><%= ViewData["Message"]%></LogMessage>
<% } %>
</AuthenticationStatus>
