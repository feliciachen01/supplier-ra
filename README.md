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

The existing app is `sra-m0-quickstart-20261017` in resource group `rg-supplier-ra-m0-20261017` (Canada Central). Keep its current min/max replica settings (0/1). No other Azure resources are required. The verified image is the private GHCR digest below. The Container App has a registry entry pointing at its existing secret `ghcrio-feliciachen01`; Azure must be able to use a GitHub classic PAT with `read:packages` and access to this package. Never place the token in a command, file, or chat.

```powershell
$image = 'ghcr.io/feliciachen01/supplier-ra@sha256:670f6bb71518f48ceeb3ff10a96d7d691bc249d1045658f21d75f3f21a82223b'
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' containerapp update --resource-group rg-supplier-ra-m0-20261017 --name sra-m0-quickstart-20261017 --image $image
```

**Deployment status (2026-10-17): blocked in Azure image pull.** The stored Container App secret authenticated to GHCR and read the exact digest in a temporary Docker configuration; Azure CLI also reports `ghcr.io` mapped to username `feliciachen01` and secret reference `ghcrio-feliciachen01`. Even so, the active revision `sra-m0-quickstart-20261017--latest` fails with `BuildFailed: Authentication failed when pulling container image` and asks for `registryCredentials`. This evidence points to Azure's revision pull path, not GitHub package access. The existing public hostname still returns the old quickstart (`/health` and `/` return 200, without the Supplier Research page title), so application routes are not deployed. Do not change package visibility or broaden token scopes. The next action is to ask Azure support to inspect registry-credential resolution for this app and revision; include the error and safe registry metadata, never the token. No resources were created, replica settings remain 0/1, and hosted live calls remain disabled.

Do not enable hosted live calls or add Azure/GitHub deployment automation.

Verify `/health`, `/`, `/api/demo/presets`, `POST /api/demo/tetra-pak-sustainability`, `POST /api/research`, and `/api/examples/northwind-packaging`. Hosted mode should return healthy/page content, live preset requests should be disabled, and arbitrary research should return 404. The saved example is a dated, explicitly synthetic illustration, not a live result.

To disable hosted live calls, leave `Hosting:LiveEnabled=false`; this is also the image default. To clean up the temporary hosting resources when no longer needed:

```powershell
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' group delete --name rg-supplier-ra-m0-20261017 --yes
```

Deleting that resource group removes the app and its Container Apps environment. The app's admission limits are process-local (one concurrent request, three requests per rolling minute, twenty per process lifetime), not a durable or cross-instance spending cap. There is no database, authentication, original-page verification, or hosted live research in this deployment.
