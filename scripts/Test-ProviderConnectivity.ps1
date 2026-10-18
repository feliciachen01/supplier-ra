$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$secretRoot = Join-Path $env:APPDATA 'Microsoft\UserSecrets'
$appProject = Join-Path $repoRoot 'src\SupplierResearch\SupplierResearch.csproj'
$probeProject = Join-Path $env:TEMP 'supplier-ra-m0-provider-check\supplier-ra-m0-provider-check.csproj'

function Get-ProjectSecret([string]$projectPath, [string]$secretName) {
    if (-not (Test-Path -LiteralPath $projectPath)) { return $null }
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $idNode = $project.SelectSingleNode("//*[local-name()='UserSecretsId']")
    if ($null -eq $idNode -or [string]::IsNullOrWhiteSpace($idNode.InnerText)) { return $null }
    $secretFile = Join-Path (Join-Path $secretRoot $idNode.InnerText) 'secrets.json'
    if (-not (Test-Path -LiteralPath $secretFile)) { return $null }
    $secrets = Get-Content -LiteralPath $secretFile -Raw | ConvertFrom-Json
    $property = $secrets.PSObject.Properties[$secretName]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function Protect-Message([string]$message, [string]$key) {
    if ([string]::IsNullOrWhiteSpace($message)) { return $null }
    if (-not [string]::IsNullOrEmpty($key)) {
        $message = $message.Replace($key, '[redacted]')
    }
    $message = [regex]::Replace($message, '(?i)Bearer\s+\S+', 'Bearer [redacted]')
    $message = [regex]::Replace($message, '(?i)(api[_ -]?key|token|authorization|x-goog-api-key)\s*[:=]\s*[^\s,;]+', '$1=[redacted]')
    $message = [regex]::Replace($message, 'https?://\S+', '[url]')
    $message = [regex]::Replace($message, '[\r\n\t]+', ' ').Trim()
    if ($message.Length -gt 240) { $message = $message.Substring(0, 240) }
    return $message
}

function Get-ProviderError([string]$body, [string]$key) {
    try {
        $json = $body | ConvertFrom-Json
        foreach ($name in @('detail', 'message', 'error')) {
            $property = $json.PSObject.Properties[$name]
            if ($null -ne $property) {
                if ($property.Value -is [string]) { return Protect-Message $property.Value $key }
                if ($null -ne $property.Value.message) { return Protect-Message ([string]$property.Value.message) $key }
            }
        }
    } catch { }
    return $null
}

function Invoke-OneCheck([string]$name, [string]$uri, [hashtable]$headers, [string]$key, [string]$jsonBody, [scriptblock]$summarize) {
    $response = $null
    try {
        $response = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' `
            -Body $jsonBody -TimeoutSec 20 -MaximumRedirection 0 -UseBasicParsing
        $shape = & $summarize ([string]$response.Content)
        [pscustomobject]@{ provider = $name; outcome = 'success'; status = [int]$response.StatusCode; shape = $shape }
    } catch {
        $status = $null
        $responseBody = $null
        if ($null -ne $_.Exception.Response) {
            $errorResponse = $_.Exception.Response
            try { $status = [int]$errorResponse.StatusCode } catch { }
            try {
                if ($null -ne $errorResponse.Content) {
                    $responseBody = $errorResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                } else {
                    $stream = $errorResponse.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = [IO.StreamReader]::new($stream)
                        $responseBody = $reader.ReadToEnd()
                        $reader.Dispose()
                    }
                }
            } catch { }
        }
        $baseException = $_.Exception.GetBaseException()
        $baseMessage = $baseException.Message
        $safeError = Get-ProviderError $responseBody $key
        if ($null -eq $safeError) { $safeError = Protect-Message $baseMessage $key }
        $outcome = 'transport'
        if ($null -ne $status) {
            $outcome = 'upstream_http'
        } elseif ($baseException -is [TimeoutException] -or
            $baseException -is [System.Threading.Tasks.TaskCanceledException] -or
            $baseException -is [OperationCanceledException] -or
            ($baseException -is [Net.WebException] -and $baseException.Status -eq [Net.WebExceptionStatus]::Timeout)) {
            $outcome = 'timeout'
        }
        [pscustomobject]@{
            provider = $name
            outcome = $outcome
            status = $status
            error = $safeError
        }
    }
}

$tavilyKey = Get-ProjectSecret $appProject 'TAVILY_API_KEY'
$geminiKey = Get-ProjectSecret $probeProject 'GEMINI_API_KEY'
$proxyNames = @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy')
$proxyPresence = foreach ($proxyName in $proxyNames) {
    [pscustomobject]@{
        name = $proxyName
        present = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($proxyName, 'Process'))
    }
}
[pscustomobject]@{ context = 'normal-PowerShell'; proxyVariables = @($proxyPresence) }

if ([string]::IsNullOrWhiteSpace($tavilyKey)) {
    [pscustomobject]@{ provider = 'Tavily'; outcome = 'credential_missing'; requestMade = $false }
} else {
    $tavilyBody = @{
        query = 'supplier packaging sustainability'
        search_depth = 'basic'
        max_results = 1
        chunks_per_source = 1
        include_answer = $false
        include_raw_content = $false
        include_images = $false
        auto_parameters = $false
    } | ConvertTo-Json -Compress
    $tavilySummary = {
        param($content)
        $json = $content | ConvertFrom-Json
        [pscustomobject]@{
            topLevelFields = @($json.PSObject.Properties.Name)
            resultCount = @($json.results).Count
            firstResultFields = if (@($json.results).Count -gt 0) { @($json.results[0].PSObject.Properties.Name) } else { @() }
        }
    }
    Invoke-OneCheck 'Tavily' 'https://api.tavily.com/search' @{ Authorization = "Bearer $tavilyKey" } `
        $tavilyKey $tavilyBody $tavilySummary
}

if ([string]::IsNullOrWhiteSpace($geminiKey)) {
    [pscustomobject]@{ provider = 'Gemini'; outcome = 'credential_missing'; requestMade = $false }
} else {
    $model = 'gemini-3.1-flash-lite'
    $geminiBody = @{
        contents = @(@{ parts = @(@{ text = 'Return JSON with status set to ok.' }) })
        generationConfig = @{
            responseMimeType = 'application/json'
            responseSchema = @{ type = 'OBJECT'; properties = @{ status = @{ type = 'STRING' } }; required = @('status') }
            maxOutputTokens = 32
        }
    } | ConvertTo-Json -Depth 8 -Compress
    $geminiSummary = {
        param($content)
        $json = $content | ConvertFrom-Json
        [pscustomobject]@{
            modelVersion = $json.modelVersion
            candidateCount = @($json.candidates).Count
            firstCandidateFields = if (@($json.candidates).Count -gt 0) { @($json.candidates[0].PSObject.Properties.Name) } else { @() }
            usage = $json.usageMetadata
        }
    }
    Invoke-OneCheck 'Gemini' "https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent" `
        @{ 'x-goog-api-key' = $geminiKey } $geminiKey $geminiBody $geminiSummary
}
