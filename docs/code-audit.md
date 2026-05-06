# Code audit & refactor guide

Honest review of the PoC codebase for production hardening. Identifies DRY violations, missing OOP abstractions, and prioritises what to refactor first.

## File structure (current)

| File | Lines | Status | Notes |
|------|-------|--------|-------|
| `Program.cs` | 1435 | ❌ god file | 5 endpoints + 4 internal classes in one |
| `Pages/Index.cshtml` | 1060 | ❌ god page | HTML + CSS + JS all inline |
| `Pages/Index.cshtml.cs` | 70 | ✅ clean | Single responsibility |
| `Models/*.cs` | 14-74 | ✅ clean | POCOs, single purpose |

## DRY violations (repeated code)

### 1. `HttpClientHandler` setup repeated 4×

Every endpoint that calls Citrix builds the same handler. Extract to `IHttpClientFactory` registration with named client `"citrix-storefront"`.

### 2. CSRF token + HTTPS header value extraction repeated 4×

```csharp
var httpsHeaderValue = string.Equals(uri.Scheme, Uri.UriSchemeHttps, ...) ? "Yes" : "No";
var csrfToken = CitrixExplicitAuth.GetCookieValue(cookies, uri, "CsrfToken");
```

Move to extension methods on `Uri` and `CookieContainer`.

### 3. Logger creation repeated

```csharp
var logger = loggerFactory.CreateLogger("CitrixProxy");
```

Use typed loggers `ILogger<CitrixProxyEndpoint>` via DI.

### 4. Bootstrap chain logic shared between two endpoints

`explicit-login` and `server-probe` both walk the bootstrap chain. Extract to `CitrixBootstrapper.RunAsync(...)`.

### 5. `fileFetchUrl` rewrite logic

JSON parse + replace + reserialize in `launch-status`. Use typed `LaunchStatusResponse` DTO with `RewriteFileFetchUrl(host)` method.

## OOP / architecture issues

### Issue 1: All endpoints inline in `Program.cs`

Recommended fix — endpoint groups via extension method:

```csharp
// CitrixEndpoints.cs
public static class CitrixEndpoints {
    public static IEndpointRouteBuilder MapCitrix(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/citrix-diagnostics");
        group.MapPost("/explicit-login", ExplicitLoginHandler);
        group.MapPost("/server-probe", ServerProbeHandler);
        // ...
        return app;
    }
}

// Program.cs
app.MapCitrix();
```

### Issue 2: No service layer

Add typed service:

```csharp
public interface ICitrixStoreFrontClient {
    Task<CitrixLoginResult> LoginAsync(string username, string password, string domain, ...);
    Task<JsonElement> GetResourcesAsync(CookieContainer cookies, ...);
    Task<LaunchStatus> GetLaunchStatusAsync(CookieContainer cookies, string resourceId, ...);
}
```

Endpoint handlers shrink to ~15 lines: input validation → service call → return.

### Issue 3: Magic strings for configuration

```csharp
configuration["CitrixDiagnostics:PublicGatewayHost"]   // typo prone
```

Use `IOptions<CitrixOptions>`:

```csharp
public sealed class CitrixOptions {
    public string BaseUrl { get; init; } = "";
    public string PublicGatewayHost { get; init; } = "";
    public string PublicStorePath { get; init; } = "";
    public string PanelTitle { get; init; } = "Citrix aplikace";
    public int BodyPreviewLimit { get; init; } = 1200;
}

builder.Services.Configure<CitrixOptions>(
    builder.Configuration.GetSection("CitrixDiagnostics"));
```

### Issue 4: `CitrixExplicitAuth` static class

For pure functions (`Preview`, `TryParseAuthForm`, `TryExtractMetaRefreshUrl`) — fine. For HttpClient-integrated methods — move to service for testability.

### Issue 5: `Index.cshtml` god page

Split into:
- `wwwroot/css/citrix-portal.css` — extract `<style>` block
- `wwwroot/js/citrix-portal.js` — extract `<script>` block as ES module
- `Index.cshtml` keeps HTML + asset references

## What's already good — keep it

- ✅ **Models are clean POCOs** — single purpose
- ✅ **`CitrixSessionCache`** — proper encapsulation, primary constructor, TTL semantics
- ✅ **Page vs API headers separation** (`CreatePageHeaders` vs `CreateBaseHeaders`) — solid abstraction with documented reason
- ✅ **Anti-SSRF whitelist** in proxy — defense in depth
- ✅ **Comments explain WHY** not what — historical context preserved
- ✅ **Czech strings preserved** — intentional, documented (`Přihlásit` button value)
- ✅ **Password never logged** — explicit policy
- ✅ **GUID session token** — opaque, no info leak
- ✅ **Parser fallback** — `TryParseAuthForm` has hard-coded fallback for robustness

## Refactor priority for production

| Priority | What | Why |
|----------|------|-----|
| **P1** | Split `Program.cs` into endpoint files + service layer | Maintainability, testability |
| **P1** | Typed `CitrixOptions` instead of magic strings | Compile-time safety |
| **P2** | `IHttpClientFactory` instead of `using` per handler | Connection pooling, performance |
| **P2** | Typed loggers `ILogger<T>` | Standard pattern |
| **P3** | Extract bootstrap chain into `CitrixBootstrapper` | DRY |
| **P3** | CSS + JS extraction from `Index.cshtml` | Maintainability |
| **P4** | Unit tests (Citrix client mocked) | Regression protection |
| **P4** | Integration tests (real StoreFront) | Production confidence |

## Suggested target structure

```
src/CitrixPoc/
├── Program.cs                              (~50 lines — wire-up only)
├── CitrixPoc.csproj
├── appsettings.json
├── Configuration/
│   └── CitrixOptions.cs                    (typed config)
├── Endpoints/
│   ├── CitrixEndpoints.cs                  (group registration)
│   ├── ExplicitLoginEndpoint.cs            (handler)
│   ├── LaunchStatusEndpoint.cs
│   ├── CitrixProxyEndpoint.cs
│   └── ClientLogEndpoint.cs
├── Services/
│   ├── ICitrixStoreFrontClient.cs          (contract)
│   ├── CitrixStoreFrontClient.cs           (implementation)
│   ├── CitrixBootstrapper.cs               (bootstrap chain logic)
│   ├── CitrixSessionCache.cs               (existing — relocated)
│   └── CitrixAuthFormParser.cs             (XML parsing)
├── Models/
│   ├── CitrixLoginRequest.cs
│   ├── CitrixLoginResponse.cs
│   ├── LaunchStatusResponse.cs             (new — typed)
│   └── CitrixAuthFormDefinition.cs
├── Pages/
│   ├── Index.cshtml                        (HTML only — slim)
│   └── Index.cshtml.cs
└── wwwroot/
    ├── css/citrix-portal.css               (extracted)
    └── js/citrix-portal.js                 (extracted)
```

Tests project alongside:

```
tests/CitrixPoc.Tests/
├── CitrixPoc.Tests.csproj
├── ServiceTests/
│   ├── CitrixStoreFrontClientTests.cs      (mocked HttpClient)
│   └── CitrixSessionCacheTests.cs
└── EndpointTests/
    └── ExplicitLoginEndpointTests.cs       (TestServer)
```
