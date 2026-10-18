# Supplier Research Assistant

The current vertical slice accepts a supplier and research question, searches for public sources, and bounds the returned excerpts. It does not generate findings or verify supplier claims.

## Run locally

```powershell
dotnet run --project src/SupplierResearch
```

The default `Fixture` mode needs no credentials. The page labels its placeholder results as test fixtures; they are not real supplier research.

To enable live Tavily search, use `scripts/Compare-Set-TavilyKey.ps1` to enter the key through a masked prompt and store it in this application's User Secrets store. Then set `Search__Mode` to `Tavily` in the app process environment before running. Do not put key values in commands, files, browser code, or chat. The temporary M0 probe's User Secrets store is separate and is not loaded by this application. Live mode fails at startup if its key is missing and does not fall back to fixture mode.

## Check

```powershell
dotnet test tests/SupplierResearch.Tests
```
