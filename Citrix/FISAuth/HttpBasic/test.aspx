<%@ Page Language="C#" %>
<%-- Copyright Citrix Systems, Inc. All rights reserved. --%>
<%@ Import Namespace="System.Security.Principal" %>
<%@ Import Namespace="System" %>

<script runat="server">
    public static int GetPasswordLength()
    {
        string header = HttpContext.Current.Request.Headers["Authorization"];
        string basicAuthScheme = "Basic";
        int basicAuthSchemeLength = basicAuthScheme.Length;
    
        if (!string.IsNullOrEmpty(header) &&
            (header.Length > basicAuthSchemeLength) &&
                header.Contains(basicAuthScheme))
        {
            if (!header.StartsWith(basicAuthScheme))
            {
                int index = header.IndexOf(basicAuthScheme, StringComparison.Ordinal);
                header = header.Substring(index).Trim();
            }

            string authHeader = header.Substring(basicAuthSchemeLength).Trim();

            int basicEnd = authHeader.IndexOf(',');
            if (basicEnd > 0)
            {
                authHeader = authHeader.Substring(0, basicEnd);
            }

            byte[] authHeaderBytes = Convert.FromBase64String(authHeader);
            Encoding latin1 = Encoding.GetEncoding("iso-8859-1");

            authHeader = latin1.GetString(authHeaderBytes);
            string[] parsed = authHeader.Split(':');
            if (parsed.Length > 1)
            {
                return parsed[1].Length;
            }
        }

        return 0;
    }
</script>

<%
    WindowsIdentity identity = (WindowsIdentity)Context.User.Identity;
%>
<html><body>
<h1>Http Basic Authentication</h1>
<ul><li>User: <%=identity.Name%></li><li>Authentication Method: <%=identity.AuthenticationType%></li><li>Retrieved Password Length: <%=GetPasswordLength()%></li></ul>
</body></html>