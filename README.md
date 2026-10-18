# Supplier Research Assistant

Search public supplier information, return bounded provider excerpts, and generate attributed findings locally. Excerpts are not independently verified original pages. Hosted live calls are disabled.

## Run locally

```powershell
dotnet run --project src/SupplierResearch
```

The Development profile enables arbitrary local queries; default search uses labeled fixtures and needs no credentials. For live local search, use the existing masked key helper to store `TAVILY_API_KEY` in this application's User Secrets, then select Tavily for the app process:

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

The existing app is `sra-m0-quickstart-20261017` in resource group `rg-supplier-ra-m0-20261017` (Canada Central). Keep its current min/max replica settings (0/1). No other Azure resources are required. A successful `main` workflow publishes the commit-tagged image to a **private** GitHub Container Registry package linked to this repository. Azure needs a GitHub classic PAT with `read:packages` to pull that private image. Configure that credential in the Container App's Registries settings without putting the token in a command, file, or chat. Then update the existing app to the reviewed image:

```powershell
$image = 'ghcr.io/feliciachen01/supplier-ra:83e2aa1282f58749768971b889b2b1d7dfb332bc'
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' containerapp update --resource-group rg-supplier-ra-m0-20261017 --name sra-m0-quickstart-20261017 --image $image
```

Do not enable hosted live calls or add Azure/GitHub deployment automation.

Verify `/health`, `/`, `/api/demo/presets`, `POST /api/demo/tetra-pak-sustainability`, `POST /api/research`, and `/api/examples/northwind-packaging`. Hosted mode should return healthy/page content, live preset requests should be disabled, and arbitrary research should return 404. The saved example is a dated, explicitly synthetic illustration, not a live result.

To disable hosted live calls, leave `Hosting:LiveEnabled=false`; this is also the image default. To clean up the temporary hosting resources when no longer needed:

```powershell
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' group delete --name rg-supplier-ra-m0-20261017 --yes
```

Deleting that resource group removes the app and its Container Apps environment. The app's admission limits are process-local (one concurrent request, three requests per rolling minute, twenty per process lifetime), not a durable or cross-instance spending cap. There is no database, authentication, original-page verification, or hosted live research in this deployment.
