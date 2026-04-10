<%@ Page Language="C#" %>
<%-- Copyright Citrix Systems, Inc. All rights reserved. --%>
<%@ Import Namespace="System" %>
<% 
HttpClientCertificate clientCertificate = Request.ClientCertificate;
%>
<html><body>
<h1>Client Certificate</h1>
<table>
 <tr><th>Property</th><th>Value</th></tr>
 <tr><td>Subject</td><td><%=clientCertificate.Subject%></td></tr>
 <tr><td>Issuer</td><td><%=clientCertificate.Issuer%></td></tr>
 <tr><td>Serial Number</td><td><%=clientCertificate.SerialNumber%></td></tr>
 <tr><td>Key Size</td><td><%=clientCertificate.KeySize%></td></tr>
 <tr><td>Valid From</td><td><%=clientCertificate.ValidFrom%></td></tr>
 <tr><td>Valid To</td><td><%=clientCertificate.ValidUntil%></td></tr>
</table>
</body></html>