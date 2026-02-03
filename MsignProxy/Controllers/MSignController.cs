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

            var response = await _msign.StartSigningProcess(request);
            return Ok(response);
        }

        [HttpGet("status/{id}")]
        public async Task<IActionResult> CheckStatus(string id)
        {
            var result = await _msign.GetStatus(id);
            return Ok(result);
        }
    }
}
