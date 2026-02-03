using MsignStaging;
using MsignProxy.Models;
using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;

namespace MsignProxy.Services
{
    public interface IMSignService
    {
        Task<SignInitiateResponse> StartSigningProcess(SignRequestDto dto);
        Task<SignResponse> GetStatus(string requestId);
    }

    public class MsignService : IMSignService
    {
        private MSignClient _client;

        public MsignService(IWebHostEnvironment env , IConfiguration configuration)
        {
            string certPath = configuration["MSignConfig:CertPath"]!;
            string certPassword = configuration["MSignConfig:CertPassword"]!;
            var certificate = new X509Certificate2(certPath, certPassword);

            // Создаём клиент по умолчанию, затем корректируем binding/endpoint если необходимо
            _client = new MSignClient(MSignClient.EndpointConfiguration.BasicHttpBinding_IMSign);

            // Если binding - BasicHttpBinding, убедимся, что он настроен на Transport (HTTPS) и клиентскую сертификацию
            if (_client.Endpoint.Binding is BasicHttpBinding basicBinding)
            {
                basicBinding.Security.Mode = BasicHttpSecurityMode.Transport;
                basicBinding.Security.Transport.ClientCredentialType = HttpClientCredentialType.Certificate;
            }

            // Если в сгенерированном endpoint указан http, заменим схему на https (если ваш сервис использует HTTPS)
            var currentUri = _client.Endpoint.Address.Uri;
            if (!string.Equals(currentUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                var httpsUri = new UriBuilder(currentUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
                var endpointAddress = new EndpointAddress(httpsUri);

                // Пытаемся пересоздать клиента с тем же binding и новым адресом
                var binding = _client.Endpoint.Binding;
                _client = new MSignClient(binding, endpointAddress);
            }

            // Передаём клиентский сертификат (для Transport или Message security в зависимости от конфигурации сервера)
            _client.ClientCredentials.ClientCertificate.Certificate = certificate;

            // Опционально отключить строгую валидацию сервера в среде разработки
#if DEBUG
            _client.ClientCredentials.ServiceCertificate.Authentication.CertificateValidationMode = X509CertificateValidationMode.None;
#endif
        }

        public async Task<SignInitiateResponse> StartSigningProcess(SignRequestDto dto)
        {
            var request = new SignRequest
            {
                ContentType = ContentType.Pdf,
                ShortContentDescription = dto.Description,
                Contents = new[] {
                    new SignContent {
                        Content = Convert.FromBase64String(dto.FileBase64),
                        Name = dto.FileName
                    }
                }
            };

            string idSign = await _client.PostSignRequestAsync(request);

            return new SignInitiateResponse
            {
                IdSign = idSign,
                RedirectUrl = $"https://msign.staging.egov.md/start?requestid={idSign}"
            };
        }

        public async Task<SignResponse> GetStatus(string requestId)
        {
            return await _client.GetSignResponseAsync(requestId, "en");
        }
    }
}
