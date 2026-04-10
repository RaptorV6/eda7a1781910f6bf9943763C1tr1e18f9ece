<%@ Page Language="C#" Inherits="Citrix.Web.AuthControllers.Helpers.ProxyFormsViewBase<Object>" %>
<?xml version="1.0" encoding="UTF-8"?>
<SelfServiceAccountManagementStatus xmlns="http://citrix.com/delivery-services/webAPI/3-7/selfServiceAccountManagementStatus">
  <AllowSelfServiceAccountManagement><% =ViewData["AllowSelfServiceAccountManagement"] %></AllowSelfServiceAccountManagement>
  <%if ((bool)ViewData["AllowSelfServiceAccountManagement"]){%><SelfServiceAccountManagementURL><% =ViewData["SelfServiceAccountManagementURL"] %></SelfServiceAccountManagementURL><%}%>
</SelfServiceAccountManagementStatus>