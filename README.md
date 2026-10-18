# Supplier Research Assistant

Research supplier information or local PDF evidence. PDF findings cite extracted page passages and are validated against retrieved text; citations do not prove factual correctness. Hosted live calls are disabled.

## Run locally

```powershell
dotnet run --project src/SupplierResearch
```

The Development profile enables arbitrary local queries; fixture search needs no credentials. Open the local address printed by `dotnet run` to see the saved Tetra Pak provider excerpts. For live local search, use the existing masked key helper to store `TAVILY_API_KEY` in this application's User Secrets, then select Tavily for the app process:

```powershell
$env:Search__Mode = 'Tavily'
dotnet run --project src/SupplierResearch
Remove-Item Env:\Search__Mode
```

Store `GEMINI_API_KEY` in the same User Secrets store to enable structured findings. Never put key values in commands, files, browser code, or chat. The temporary M0 probe's secrets are not shared with this application.

In the same local app, upload up to three text-based PDFs (5 MB and 200 pages each), enter a research question, inspect the retrieved passages, and accept, reject, or flag each generated finding. Scanned image PDFs are not OCR processed. Uploads, extracted text, findings, and review decisions are stored in `src/SupplierResearch/App_Data/evidence-workspace.sqlite3`, which is excluded from Git and is not served as static content. Local PDF research uses Gemini when `GEMINI_API_KEY` is set; without it, the app shows retrieved passages only. The local Gemini path observes the same process limits (one concurrent request, three per minute, twenty per process lifetime). Use the CSV link to export reviewed findings.

## Check and container

```powershell
dotnet test tests/SupplierResearch.Tests
$docker = 'C:\Users\Felicia Chen\AppData\Local\Programs\DockerDesktop\resources\bin\docker.exe'
$revision = git rev-parse --short HEAD
& $docker build --build-arg "SOURCE_REVISION=$revision" --tag supplier-ra:local .
```

The single GitHub Actions workflow runs the test suite and builds the image on pushes to `main` and pull requests. On `main`, it publishes a commit-tagged image to the private GHCR package; it does not deploy. The production image uses hosted preset mode, fixture search, and `Hosting:LiveEnabled=false` by default; it contains no User Secrets or local settings.

## Manual Azure deployment

The existing app is `sra-m0-quickstart-20261017` in resource group `rg-supplier-ra-m0-20261017` (Canada Central). Keep its current min/max replica settings (0/1) and target port 8080. Use the already configured private GHCR registry secret `ghcrio-feliciachen01`; never place its value in a command, file, or chat. After the commit is published, deploy that commit tag:

```powershell
$revision = git rev-parse HEAD
$image = "ghcr.io/feliciachen01/supplier-ra:$revision"
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' containerapp update --resource-group rg-supplier-ra-m0-20261017 --name sra-m0-quickstart-20261017 --image $image
```

Do not enable hosted live calls or add Azure/GitHub deployment automation.

From a PowerShell session that can reach the host, verify `/health`, `/`, and `/api/examples/tetra-pak-recycling`. Hosted mode serves saved provider excerpts; live preset controls are not shown, arbitrary research returns 404, and hosted live calls remain disabled. The saved search was captured on 2026-10-18. No validated AI findings were produced; excerpts are not independently verified original pages.

To disable hosted live calls, leave `Hosting:LiveEnabled=false`; this is also the image default. To clean up the temporary hosting resources when no longer needed:

```powershell
& 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd' group delete --name rg-supplier-ra-m0-20261017 --yes
```

Deleting that resource group removes the app and its Container Apps environment. The app's admission limits are process-local (one concurrent request, three requests per rolling minute, twenty per process lifetime), not a durable or cross-instance spending cap. There is no database, authentication, original-page verification, or hosted live research in this deployment.
