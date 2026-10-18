# Supplier Research Assistant

Search public supplier information, return bounded provider excerpts, and generate attributed findings locally. Excerpts are not independently verified original pages. Hosted live calls are disabled.

## Run locally

```powershell
dotnet run --project src/SupplierResearch
```

The Development profile enables arbitrary local queries; fixture search needs no credentials. Open the local address printed by `dotnet run`, choose a public preset, and use the saved example to see clearly labeled synthetic content. For live local search, use the existing masked key helper to store `TAVILY_API_KEY` in this application's User Secrets, then select Tavily for the app process:

```powershell
$env:Search__Mode = 'Tavily'
dotnet run --project src/SupplierResearch
Remove-Item Env:\Search__Mode
```

Store `GEMINI_API_KEY` in the same User Secrets store to enable structured findings. Never put key values in commands, files, browser code, or chat. The temporary M0 probe's secrets are not shared with this application.

## Check and container

```powershell
dotnet test tests/SupplierResearch.Tests
$docker = 'C:\Users\Felicia Chen\AppData\Local\Programs\DockerDesktop\resources\bin\docker.exe'
$revision = git rev-parse --short HEAD
& $docker build --build-arg "SOURCE_REVISION=$revision" --tag supplier-ra:local .
```

The single GitHub Actions workflow runs the test suite and builds the image on pushes to `main` and pull requests. On `main`, it publishes a commit-tagged image to the private GHCR package; it does not deploy. The production image uses hosted preset mode, fixture search, and `Hosting:LiveEnabled=false` by default; it contains no User Secrets or local settings.

## Manual Azure deployment

The existing app is `sra-m0-quickstart-20261017` in resource group `rg-supplier-ra-m0-20261017` (Canada Central). Keep its current min/max replica settings (0/1). The private commit tag below resolves to the published digest shown. The Container App uses its existing GHCR registry secret `ghcrio-feliciachen01`; never place its value in a command, file, or chat.

```powershell
$image = 'ghcr.io/feliciachen01/supplier-ra:83e2aa1282f58749768971b889b2b1d7dfb332bc'
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' containerapp update --resource-group rg-supplier-ra-m0-20261017 --name sra-m0-quickstart-20261017 --image $image
```

**Cloud deployment status: pending route and cost verification.** The private commit tag was confirmed against digest `sha256:670f6bb71518f48ceeb3ff10a96d7d691bc249d1045658f21d75f3f21a82223b`; the initial `buildx imagetools` check failed locally because the temporary Docker config hid the Buildx plugin, and Docker's built-in manifest inspection was used instead. The existing Container App now references the commit tag; Azure reports its active revision `sra-m0-quickstart-20261017--latest` as `Provisioned` (2026-10-18 06:30:31 UTC), with the existing 0/1 replica limits. This execution context cannot reach the public hostname: its configured local proxy at `127.0.0.1` refused the connection, so application routes are not yet verified. Azure reports no consumption budgets and no subscription spending limit; usable credit is unconfirmed. Zero out-of-pocket cost cannot be established, so do not make further cloud changes. The repository and GHCR package remain private; hosted live calls stay disabled.

Do not enable hosted live calls or add Azure/GitHub deployment automation.

From a PowerShell session that can reach the host, verify `/health`, `/`, `/api/demo/presets`, `POST /api/demo/tetra-pak-sustainability`, `POST /api/research`, and `/api/examples/northwind-packaging`. Hosted mode should return healthy/page content, live preset requests should be disabled, and arbitrary research should return 404. The saved example is a dated, explicitly synthetic illustration, not a live result.

To disable hosted live calls, leave `Hosting:LiveEnabled=false`; this is also the image default. To clean up the temporary hosting resources when no longer needed:

```powershell
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' group delete --name rg-supplier-ra-m0-20261017 --yes
```

Deleting that resource group removes the app and its Container Apps environment. The app's admission limits are process-local (one concurrent request, three requests per rolling minute, twenty per process lifetime), not a durable or cross-instance spending cap. There is no database, authentication, original-page verification, or hosted live research in this deployment.
