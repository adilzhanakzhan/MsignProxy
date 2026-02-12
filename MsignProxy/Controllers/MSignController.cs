using Microsoft.AspNetCore.Mvc;
using MsignProxy.Services;
using MsignProxy.Models;

namespace MsignProxy.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MSignController : ControllerBase
    {
        private readonly IMSignService _msign;


        public MSignController(IMSignService msign)
        {
            _msign = msign;
        }

        [HttpPost("initiate")]
        public async Task<IActionResult> Initiate([FromBody] SignRequestDto request)
        {
            if (string.IsNullOrEmpty(request.FileBase64)) return BadRequest("File content is missing.");
            try
            {
                var response = await _msign.StartSigningProcess(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
        [HttpGet("status/{id}")]
        public async Task<IActionResult> CheckStatus(string id)
        {
            var result = await _msign.GetStatus(id);
            return Ok(result);
        }
        [HttpGet("GetSignResult/{id}")]
        public async Task<IActionResult> GetFullResponse(string id)
        {
            try
            {
                // Получаем полный объект от сервиса
                var fullData = await _msign.GetFinalSignData(id);

                // Возвращаем как есть (ASP.NET сам сериализует это в JSON)
                return Ok(fullData);
            }
            catch (Exception ex)
            {
                // Если ошибка связи с WSDL, мы увидим её здесь
                return BadRequest(new
                {
                    message = "Ошибка при получении данных от MSign",
                    detail = ex.Message
                });
            }
        }
    }
}
