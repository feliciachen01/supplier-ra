$ErrorActionPreference = 'Stop'

$image = 'ghcr.io/feliciachen01/supplier-ra@sha256:670f6bb71518f48ceeb3ff10a96d7d691bc249d1045658f21d75f3f21a82223b'
$secretName = 'ghcrio-feliciachen01'
$resourceGroup = 'rg-supplier-ra-m0-20261017'
$appName = 'sra-m0-quickstart-20261017'
$apiVersion = '2025-07-01'
$azureCli = 'C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
$result = [ordered]@{ status = 'blocked'; stage = 'input'; image = $image }
$plainPat = $null
$securePat = $null
$dockerConfig = $null
$originalDockerConfig = [Environment]::GetEnvironmentVariable('DOCKER_CONFIG', 'Process')
$hadDockerConfig = $null -ne $originalDockerConfig

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$Arguments,
        [AllowNull()][string]$StandardInput
    )

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $null -ne $StandardInput
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if ($null -ne $StandardInput) {
        $process.StandardInput.WriteLine($StandardInput)
        $process.StandardInput.Close()
    }
    if (-not $process.WaitForExit(60000)) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ ExitCode = 124; StdOut = ''; StdErr = '' }
    }
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdoutTask.Result
        StdErr = $stderrTask.Result
    }
}

function Get-HttpStatusCode {
    param([Parameter(Mandatory = $true)]$ErrorRecord)
    try { return [int]$ErrorRecord.Exception.Response.StatusCode } catch { return 0 }
}

function Invoke-AppRequest {
    param(
        [string]$BaseUrl,
        [string]$Method,
        [string]$Path,
        [string]$Body
    )
    try {
        $parameters = @{ Uri = ($BaseUrl + $Path); Method = $Method; TimeoutSec = 20; UseBasicParsing = $true }
        if ($null -ne $Body) {
            $parameters.ContentType = 'application/json'
            $parameters.Body = $Body
        }
        $response = Invoke-WebRequest @parameters
        return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = [string]$response.Content }
    } catch {
        return [pscustomobject]@{ StatusCode = (Get-HttpStatusCode $_); Content = '' }
    }
}

try {
    $securePat = Read-Host 'Enter the GitHub classic PAT (input is masked)' -AsSecureString
    if ($securePat.Length -eq 0) {
        $result.failure = 'empty_pat'
        $result.nextAction = 'Run again and enter the intended classic PAT through the masked prompt.'
    } else {
        $patPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePat)
        try { $plainPat = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($patPointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($patPointer) }

        $docker = Get-Command docker.exe -CommandType Application -ErrorAction Stop
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $dockerConfig = Join-Path $tempRoot ('supplier-ra-ghcr-' + [guid]::NewGuid().ToString('N'))
        $fullDockerConfig = [IO.Path]::GetFullPath($dockerConfig)
        if (-not $fullDockerConfig.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Temporary Docker configuration path validation failed.'
        }
        [void](New-Item -ItemType Directory -Path $fullDockerConfig)
        [Environment]::SetEnvironmentVariable('DOCKER_CONFIG', $fullDockerConfig, 'Process')

        $result.stage = 'ghcr_login'
        $login = Invoke-CapturedProcess -FilePath $docker.Source -Arguments 'login ghcr.io --username feliciachen01 --password-stdin' -StandardInput $plainPat
        if ($login.ExitCode -ne 0) {
            $result.failure = 'ghcr_authentication_failed'
            $result.nextAction = 'Use a valid, unexpired classic PAT with read:packages; confirm its owner can access the private supplier-ra package.'
        } else {
            $result.login = 'succeeded'
            $result.stage = 'ghcr_digest_access'
            $manifest = Invoke-CapturedProcess -FilePath $docker.Source -Arguments ('manifest inspect ' + $image) -StandardInput $null
            if ($manifest.ExitCode -ne 0) {
                $result.failure = 'ghcr_digest_access_failed'
                if ($manifest.StdErr -match '(?i)(unauthorized|denied|not found|no basic auth|requested access)') {
                    $result.nextAction = 'Use a classic PAT with read:packages and ensure the token owner has access to this private package.'
                } else {
                    $result.nextAction = 'The registry check failed for a non-authentication reason; no Azure change was made.'
                }
            } else {
                $result.imageAccess = 'succeeded_for_requested_digest'
                $result.stage = 'temporary_credential_cleanup'
                Remove-Item -LiteralPath $fullDockerConfig -Recurse -Force
                if (Test-Path -LiteralPath $fullDockerConfig) {
                    $result.failure = 'temporary_credentials_cleanup_failed'
                    $result.nextAction = 'Remove the temporary supplier-ra-ghcr-* directory under the Windows temp folder before retrying.'
                } else {
                    $dockerConfig = $null
                    if ($hadDockerConfig) {
                        [Environment]::SetEnvironmentVariable('DOCKER_CONFIG', $originalDockerConfig, 'Process')
                    } else {
                        [Environment]::SetEnvironmentVariable('DOCKER_CONFIG', $null, 'Process')
                    }

                    $result.stage = 'azure_secret_update'
                    $subscriptionId = & $azureCli account show --query id -o tsv 2>$null
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($subscriptionId)) {
                        $result.failure = 'azure_cli_context_unavailable'
                        $result.nextAction = 'Run this helper in the PowerShell session authenticated to the existing Azure subscription.'
                    } else {
                        $armToken = & $azureCli account get-access-token --resource 'https://management.azure.com/' --query accessToken -o tsv 2>$null
                        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($armToken)) {
                            $result.failure = 'azure_management_token_unavailable'
                            $result.nextAction = 'Refresh the existing Azure CLI login in this PowerShell session, then rerun the helper.'
                        } else {
                            $armToken = $armToken.Trim()
                            $headers = @{ Authorization = ('Bearer ' + $armToken) }
                            $appUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.App/containerApps/$appName`?api-version=$apiVersion"
                            $app = Invoke-RestMethod -Uri $appUri -Headers $headers -Method Get -TimeoutSec 30
                            $secretNames = @($app.properties.configuration.secrets | ForEach-Object { $_.name })
                            if ($secretNames.Count -ne 1 -or $secretNames[0] -ne $secretName) {
                                $result.failure = 'existing_secret_shape_changed'
                                $result.nextAction = 'The app secret set differs from the reviewed state; inspect it before changing Azure.'
                            } else {
                                $registries = @($app.properties.configuration.registries | ForEach-Object {
                                    if ($_.server -eq 'ghcr.io') {
                                        [pscustomobject]@{ server = 'ghcr.io'; username = 'feliciachen01'; passwordSecretRef = $secretName }
                                    } else {
                                        [pscustomobject]@{ server = $_.server; username = $_.username; passwordSecretRef = $_.passwordSecretRef; identity = $_.identity }
                                    }
                                })
                                if (@($registries | Where-Object { $_.server -eq 'ghcr.io' }).Count -ne 1) {
                                    $result.failure = 'existing_ghcr_registry_binding_missing_or_ambiguous'
                                    $result.nextAction = 'Inspect the existing app registry binding before changing Azure.'
                                } else {
                                    $patch = @{ properties = @{ configuration = @{
                                        secrets = @(@{ name = $secretName; value = $plainPat })
                                        registries = $registries
                                    } } }
                                    $json = ConvertTo-Json -InputObject $patch -Depth 8 -Compress
                                    $null = Invoke-RestMethod -Uri $appUri -Headers $headers -Method Patch -ContentType 'application/json' -Body $json -TimeoutSec 60
                                    $result.stage = 'azure_secret_confirmation'
                                    $secretUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.App/containerApps/$appName/listSecrets`?api-version=$apiVersion"
                                    $storedSecrets = Invoke-RestMethod -Uri $secretUri -Headers $headers -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 30
                                    $storedPat = [string](($storedSecrets.value | Where-Object { $_.name -eq $secretName } | Select-Object -First 1).value)
                                    $secretMatchesInput = [string]::Equals($storedPat, $plainPat, [StringComparison]::Ordinal)
                                    $storedPat = $null
                                    $storedSecrets = $null
                                    $plainPat = $null
                                    $json = $null
                                    $app = Invoke-RestMethod -Uri $appUri -Headers $headers -Method Get -TimeoutSec 30
                                    $registry = @($app.properties.configuration.registries | Where-Object { $_.server -eq 'ghcr.io' })[0]
                                    $ready = $secretMatchesInput -and $registry.username -eq 'feliciachen01' -and $registry.passwordSecretRef -eq $secretName
                                    $result.azureSecretMatchesInput = $secretMatchesInput
                                    if (-not $ready) {
                                        $result.failure = 'azure_secret_update_not_confirmed'
                                        $result.nextAction = 'Inspect the existing Container App configuration state before another deployment attempt.'
                                    } else {
                                        $result.stage = 'image_update'
                                        $null = & $azureCli containerapp update --resource-group $resourceGroup --name $appName --image $image --only-show-errors -o none 2>&1
                                        $updateExit = $LASTEXITCODE
                                        $result.stage = 'revision_readiness'
                                        $readyRevision = $null
                                        for ($attempt = 0; $attempt -lt 24; $attempt++) {
                                            $app = Invoke-RestMethod -Uri $appUri -Headers $headers -Method Get -TimeoutSec 30
                                            $revisionName = $app.properties.latestRevisionName
                                            if (-not [string]::IsNullOrWhiteSpace($revisionName)) {
                                                $revisionUri = "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.App/containerApps/$appName/revisions/$revisionName`?api-version=$apiVersion"
                                                $revision = Invoke-RestMethod -Uri $revisionUri -Headers $headers -Method Get -TimeoutSec 30
                                                if ($revision.properties.provisioningState -eq 'Succeeded' -and $revision.properties.runningState -eq 'Running') {
                                                    $readyRevision = $revision
                                                    break
                                                }
                                                if ($revision.properties.provisioningState -eq 'Failed') { break }
                                            }
                                            if ($updateExit -ne 0) { break }
                                            Start-Sleep -Seconds 5
                                        }
                                        if ($null -eq $readyRevision) {
                                            $result.failure = 'azure_revision_not_ready'
                                            $provisioningError = [string]$revision.properties.provisioningError
                                            if ($provisioningError -match '(?i)authentication failed when pulling') {
                                                if ($result.azureSecretMatchesInput) {
                                                    $result.nextAction = 'The stored secret matched the verified input and can read this digest from GHCR, but Azure rejected the pull. Escalate the revision registry-credential resolution to Azure support; do not change GitHub package access.'
                                                } else {
                                                    $result.nextAction = 'Azure did not confirm the exact input in its secret store; correct the existing secret before another image update.'
                                                }
                                            } elseif ($provisioningError -match '(?i)(image|manifest).*(not found|pull)') {
                                                $result.nextAction = 'Azure could not retrieve the configured image; inspect the failed revision before retrying.'
                                            } else {
                                                $result.nextAction = 'Inspect the failed Azure revision provisioning state before retrying.'
                                            }
                                            $result.revision = $revisionName
                                            $result.provisioningState = $revision.properties.provisioningState
                                        } else {
                                            $result.revision = $readyRevision.name
                                            $result.provisioningState = $readyRevision.properties.provisioningState
                                            $result.runningState = $readyRevision.properties.runningState
                                            $result.configuredImage = $app.properties.template.containers[0].image
                                            if ($result.configuredImage -ne $image -or -not $readyRevision.properties.active) {
                                                $result.failure = 'active_revision_image_mismatch'
                                                $result.nextAction = 'Inspect the active revision and image configuration before serving traffic.'
                                            } else {
                                                $baseUrl = 'https://' + $app.properties.configuration.ingress.fqdn
                                                $health = Invoke-AppRequest -BaseUrl $baseUrl -Method Get -Path '/health' -Body $null
                                                $page = Invoke-AppRequest -BaseUrl $baseUrl -Method Get -Path '/' -Body $null
                                                $presetsResponse = Invoke-AppRequest -BaseUrl $baseUrl -Method Get -Path '/api/demo/presets' -Body $null
                                                $presetPost = Invoke-AppRequest -BaseUrl $baseUrl -Method Post -Path '/api/demo/tetra-pak-sustainability' -Body '{}'
                                                $arbitraryPost = Invoke-AppRequest -BaseUrl $baseUrl -Method Post -Path '/api/research' -Body '{}'
                                                $exampleResponse = Invoke-AppRequest -BaseUrl $baseUrl -Method Get -Path '/api/examples/northwind-packaging' -Body $null
                                                $presets = $null
                                                $example = $null
                                                try { $presets = $presetsResponse.Content | ConvertFrom-Json } catch { }
                                                try { $example = $exampleResponse.Content | ConvertFrom-Json } catch { }
                                                $routeChecks = [ordered]@{
                                                    health = ($health.StatusCode -eq 200 -and $health.Content -match '"status"\s*:\s*"ok"')
                                                    page = ($page.StatusCode -eq 200 -and $page.Content -match '<title>Supplier Research</title>')
                                                    hostedLiveOff = ($presetsResponse.StatusCode -eq 200 -and $presets.mode -eq 'hosted' -and -not $presets.liveEnabled)
                                                    livePresetDisabled = ($presetPost.StatusCode -eq 503)
                                                    arbitraryQueryUnavailable = ($arbitraryPost.StatusCode -eq 404)
                                                    savedExampleSynthetic = ($exampleResponse.StatusCode -eq 200 -and $example.mode -eq 'saved_example' -and $example.notice -match 'synthetic content')
                                                }
                                                $result.url = $baseUrl
                                                $result.routes = $routeChecks
                                                if (@($routeChecks.Values | Where-Object { -not $_ }).Count -eq 0) {
                                                    $result.status = 'deployed_and_verified'
                                                    $result.liveResearch = 'disabled'
                                                    $result.savedExample = 'explicitly synthetic'
                                                } else {
                                                    $result.failure = 'application_route_verification_failed'
                                                    $result.nextAction = 'Inspect only the failed route check before changing the deployment.'
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            $armToken = $null
                            $headers = $null
                        }
                    }
                }
            }
        }
    }
} catch {
    $result.failure = 'helper_step_failed'
    if ($result.stage -eq 'ghcr_login' -or $result.stage -eq 'ghcr_digest_access') {
        $result.nextAction = 'No Azure change was made. Check Docker CLI access and repeat only after correcting the GHCR check failure.'
    } elseif ($result.stage -like 'azure*' -or $result.stage -like 'image_update' -or $result.stage -like 'revision*') {
        $result.nextAction = 'Azure error details were suppressed. Inspect the named existing app and latest revision in Azure Portal before another attempt.'
    } else {
        $result.nextAction = 'Review the helper prerequisites; no credential or sensitive error details were printed.'
    }
} finally {
    $plainPat = $null
    $armToken = $null
    $headers = $null
    if ($null -ne $securePat) { $securePat.Dispose() }
    if ($null -ne $dockerConfig -and (Test-Path -LiteralPath $dockerConfig)) {
        try { Remove-Item -LiteralPath $dockerConfig -Recurse -Force } catch { }
    }
    if ($hadDockerConfig) {
        [Environment]::SetEnvironmentVariable('DOCKER_CONFIG', $originalDockerConfig, 'Process')
    } else {
        [Environment]::SetEnvironmentVariable('DOCKER_CONFIG', $null, 'Process')
    }
}

ConvertTo-Json -InputObject $result -Depth 8 -Compress
