<%@ Page Language="C#" Inherits="Citrix.Web.AuthControllers.Helpers.ProxyFormsViewBase<Object>" %>
<?xml version="1.0" encoding="UTF-8"?>
<AuthenticationStatus xmlns="http://citrix.com/delivery-services/webAPI/2-6/authStatus" <%if ((bool)ViewData["IsWebLogoffUrlProvided"]){%>xmlns:ext="http://citrix.com/delivery-services/webAPI/2-6/weblogoff"<%}%> >
  <Result>success</Result>
  <AuthType><% =ViewData["AuthType"] %></AuthType>
  <%if ((bool)ViewData["IsChangePasswordEnabled"]){%><IsChangePasswordEnabled>true</IsChangePasswordEnabled>
  <%} if ((bool)ViewData["IsExpiryNotificationEnabled"]){%><IsExpiryNotificationEnabled>true</IsExpiryNotificationEnabled>
  <TimeRemaining><%=ViewData["TimeRemaining"]%></TimeRemaining>
  <%}%>
  <%if ((bool)ViewData["IsWebLogoffUrlProvided"]){%>
      <ext:webLogoffUrl><%=ViewData["WebLogoffUrl"]%></ext:webLogoffUrl>
  <%}%>
</AuthenticationStatus>