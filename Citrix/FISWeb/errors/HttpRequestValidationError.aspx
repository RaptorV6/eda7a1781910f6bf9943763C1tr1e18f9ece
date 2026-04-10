<%@ Page Language="C#" %>
<%
Response.StatusCode = 400;
Response.TrySkipIisCustomErrors = true;
%>
<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Http Request Validation Error</title>
  </head>
  <body>
    <h1>400 Bad Request</h1><p>HTTP request validation error</p>
  </body>
</html>
