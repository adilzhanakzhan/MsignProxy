using MsignStaging;
using MsignProxy.Models;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Security;
using Polly;
using Polly.Retry;

namespace MsignProxy.Services
{
    public interface IMSignService
    {
        Task<SignInitiateResponse> StartSigningProcess(SignRequestDto dto);
        Task<SignResponse> GetSignResponse(string requestId);
    }

    public class MsignService : IMSignService, IDisposable
    {
        private MSignClient _client;
        private readonly object _lock = new();
        private readonly ILogger<MsignService> _logger;
        private readonly X509Certificate2 _certificate;
        private readonly AsyncRetryPolicy _retryPolicy;
        private bool _disposed;

        public MsignService(IWebHostEnvironment env, IConfiguration configuration, ILogger<MsignService> logger)
        {
            _logger = logger;

            // ── Certificate ──────────────────────────────────────────────
            var certPathConfigured = configuration["MSignConfig:CertPath"]
                ?? throw new InvalidOperationException("MSignConfig:CertPath is not configured.");
            var certPassword = configuration["MSignConfig:CertPassword"]
                ?? throw new InvalidOperationException("MSignConfig:CertPassword is not configured.");

            var certPath = Path.IsPathRooted(certPathConfigured)
                ? certPathConfigured
                : Path.Combine(env.ContentRootPath ?? AppContext.BaseDirectory, certPathConfigured);

            if (!File.Exists(certPath))
                throw new FileNotFoundException($"Certificate not found at: {certPath}");

            _certificate = new X509Certificate2(certPath, certPassword);
            _logger.LogInformation("Certificate loaded: {Subject}, Expires: {Expiry}",
                _certificate.Subject, _certificate.GetExpirationDateString());

            // ── Polly retry policy ────────────────────────────────────────
            // Retries 3 times on transient WCF/network errors with exponential backoff:
            // 1st retry after 2s, 2nd after 4s, 3rd after 8s
            _retryPolicy = Policy
                .Handle<CommunicationException>()
                .Or<TimeoutException>()
                .Or<EndpointNotFoundException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (exception, delay, attempt, _) =>
                    {
                        _logger.LogWarning(
                            "MSign call failed (attempt {Attempt}), retrying in {Delay}s. Error: {Error}",
                            attempt, delay.TotalSeconds, exception.Message);
                    });

            // ── Create initial WCF client ─────────────────────────────────
            _client = CreateClient();
            _logger.LogInformation("MsignService initialized successfully.");
        }

        // ── Client factory ────────────────────────────────────────────────
        private MSignClient CreateClient()
        {
            var client = new MSignClient(MSignClient.EndpointConfiguration.BasicHttpBinding_IMSign);

            if (client.Endpoint.Binding is BasicHttpBinding binding)
            {
                binding.Security.Mode = BasicHttpSecurityMode.Transport;
                binding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;

                // Explicit timeouts — prevents thread pool starvation under load
                binding.OpenTimeout = TimeSpan.FromSeconds(15);
                binding.SendTimeout = TimeSpan.FromSeconds(45);
                binding.ReceiveTimeout = TimeSpan.FromSeconds(45);
                binding.CloseTimeout = TimeSpan.FromSeconds(10);

                // Increase limits to handle larger PDFs
                binding.MaxBufferSize = 10 * 1024 * 1024; // 10 MB
                binding.MaxReceivedMessageSize = 10 * 1024 * 1024; // 10 MB
            }

            // Enforce HTTPS scheme on the endpoint
            var currentUri = client.Endpoint.Address.Uri;
            if (!string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                var httpsUri = new UriBuilder(currentUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                client = new MSignClient(client.Endpoint.Binding, new EndpointAddress(httpsUri));
            }

            client.ClientCredentials.ClientCertificate.Certificate = _certificate;

#if DEBUG
            // Skip strict server cert validation in local development only
            client.ClientCredentials.ServiceCertificate.Authentication.CertificateValidationMode =
                X509CertificateValidationMode.None;
#endif

            return client;
        }

        // ── Channel health check + auto-recovery ──────────────────────────
        // WCF channels go into Faulted state permanently after any communication error.
        // This method transparently recreates the client when that happens.
        private MSignClient GetHealthyClient()
        {
            if (_client.State != CommunicationState.Faulted &&
                _client.State != CommunicationState.Closed)
                return _client;

            lock (_lock)
            {
                // Double-check inside the lock — another thread may have already recreated it
                if (_client.State == CommunicationState.Faulted ||
                    _client.State == CommunicationState.Closed)
                {
                    _logger.LogWarning(
                        "WCF channel is in {State} state — recreating client.", _client.State);

                    try { _client.Abort(); } catch { /* ignore — channel is already broken */ }

                    _client = CreateClient();
                    _logger.LogInformation("WCF client recreated successfully.");
                }
            }

            return _client;
        }

        // ── Public API ────────────────────────────────────────────────────

        public async Task<SignInitiateResponse> StartSigningProcess(SignRequestDto dto)
        {
            _logger.LogInformation("Initiating sign process. File: {FileName}, Description: {Desc}",
                dto.FileName, dto.Description);

            var request = new SignRequest
            {
                ContentType = ContentType.Pdf,
                ShortContentDescription = dto.Description,
                Contents = new[]
                {
                    new SignContent
                    {
                        Content = Convert.FromBase64String(dto.FileBase64),
                        Name    = dto.FileName
                    }
                }
            };

            string idSign = await _retryPolicy.ExecuteAsync(async () =>
            {
                var client = GetHealthyClient();
                return await client.PostSignRequestAsync(request);
            });

            var encodedReturnUrl = System.Net.WebUtility.UrlEncode(dto.ReturnUrl);

            _logger.LogInformation("Sign process initiated successfully. IdSign: {IdSign}", idSign);

            return new SignInitiateResponse
            {
                IdSign = idSign,
                RedirectUrl = $"https://msign.staging.egov.md/{idSign}?returnUrl={encodedReturnUrl}"
            };
        }

        public async Task<SignResponse> GetSignResponse(string requestId)
        {
            _logger.LogInformation("Fetching sign response for RequestId: {RequestId}", requestId);

            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                var client = GetHealthyClient();
                return await client.GetSignResponseAsync(requestId, "en");
            });

            _logger.LogInformation(
                "Sign response retrieved. RequestId: {RequestId}, Status: {Status}",
                requestId, response.Status);

            return response;
        }

        // ── Dispose ───────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_client.State == CommunicationState.Opened)
                    _client.Close();
                else
                    _client.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing WCF client during dispose.");
                _client.Abort();
            }
        }
    }
}