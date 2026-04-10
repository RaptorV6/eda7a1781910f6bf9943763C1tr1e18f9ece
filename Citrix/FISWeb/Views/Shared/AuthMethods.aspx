<%@ Page Language="C#" Inherits="Citrix.Web.AuthControllers.Helpers.ProxyFormsViewBase<List<Citrix.Web.AuthControllers.Controllers.AuthMethodsViewModel>>" %>

<?xml version="1.0" encoding="UTF-8"?>
<authMethods xmlns="http://citrix.com/delivery-services/webAPI/2-6/authMethods">
<% for (int i = 0; i < Model.Count; i++) { %>
    <method name="<%=Model[i].Name %>" url="<%=Model[i].Url %>"/>
<% } %>
</authMethods>
