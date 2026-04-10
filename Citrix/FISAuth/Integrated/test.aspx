<%@ Page Language="C#" %>
<%-- Copyright Citrix Systems, Inc. All rights reserved. --%>
<%@ Import Namespace="System.Security.Principal" %>
<%@ Import Namespace="System" %>
<% 
WindowsIdentity identity = (WindowsIdentity)Context.User.Identity;
%>
<html><body>
<h1>Windows Authentication</h1>
<ul><li>User: <%=identity.Name%></li><li>Authentication Method: <%=identity.AuthenticationType%></li></ul>
</body></html>