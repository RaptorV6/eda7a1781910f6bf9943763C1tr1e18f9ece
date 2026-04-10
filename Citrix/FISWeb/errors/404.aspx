<%@ Page Language="C#" %>
<%
Response.StatusCode = 404;
Response.TrySkipIisCustomErrors = true;
%>
<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>404 Not Found</title>
  </head>
  <body>
    <h1>404 Not Found</h1>
  </body>
</html>
