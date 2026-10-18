# Supplier Research Assistant

The current vertical slice accepts a supplier and research question, searches for public sources, and bounds the returned excerpts. It does not generate findings or verify supplier claims.

## Run locally

```powershell
dotnet run --project src/SupplierResearch
```

The default `Fixture` mode needs no credentials. The page labels its placeholder results as test fixtures; they are not real supplier research.

To enable live Tavily search, initialize this project's User Secrets store with `dotnet user-secrets init --project src/SupplierResearch`, then use the local secret-entry helper targeting this project to set `TAVILY_API_KEY`. Set `Search__Mode` to `Tavily` in the app process environment before running. Do not put key values in commands, files, browser code, or chat. The temporary M0 probe's User Secrets store is separate and is not loaded by this application. Live mode fails at startup if its key is missing and does not fall back to fixture mode.

## Check

```powershell
dotnet test tests/SupplierResearch.Tests
```
