@echo off
REM Local test launcher for PortalComponent.
REM Sets development environment + binds to localhost:8089.
REM No admin rights / no .NET install needed (runtime is bundled in the exe).

set ASPNETCORE_URLS=http://localhost:8089
set ASPNETCORE_ENVIRONMENT=Development

start "" "http://localhost:8089"
PortalComponent.exe
