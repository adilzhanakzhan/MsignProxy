using Microsoft.AspNetCore.Mvc;
using MsignProxy.Services;
using MsignProxy.Models;

namespace MsignProxy.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class MSignController : ControllerBase
    {
        private readonly IMSignService _msign;
        private readonly ILogger<MSignController> _logger;

        public MSignController(IMSignService msign, ILogger<MSignController> logger)
        {
            _msign = msign;
            _logger = logger;
        }

      
        // Initiates a new signing request and returns a redirect URL for the user.
        
        [HttpPost("initiate")]
        [ProducesResponseType(typeof(SignInitiateResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Initiate([FromBody] SignRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.FileBase64))
                return BadRequest(new { error = "FileBase64 is required." });

            if (string.IsNullOrWhiteSpace(request.FileName))
                return BadRequest(new { error = "FileName is required." });

            // Basic base64 validation before sending to MSign
            try { Convert.FromBase64String(request.FileBase64); }
            catch { return BadRequest(new { error = "FileBase64 is not valid base64." }); }

            try
            {
                var response = await _msign.StartSigningProcess(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initiate signing for file: {FileName}", request.FileName);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "Failed to communicate with MSign service.",
                    detail = ex.Message
                });
            }
        }

        
        // Returns the current status and result of a signing request.
        // Status values: Pending = 0, Success = 1, Failure = 2, Expired = 3
        
        [HttpGet("result/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<IActionResult> GetResult(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { error = "Sign request ID is required." });

            try
            {
                var result = await _msign.GetSignResponse(id);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch sign result for RequestId: {Id}", id);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "Failed to retrieve sign result from MSign service.",
                    detail = ex.Message
                });
            }
        }

        // ── Backward-compat aliases (kept so existing Elma365 calls don't break) ──

        /// <summary>
        /// Deprecated: use GET /result/{id} instead.
        /// </summary>
        [HttpGet("status/{id}")]
        [ApiExplorerSettings(IgnoreApi = true)] // hide from Swagger but still works
        public Task<IActionResult> CheckStatus(string id) => GetResult(id);

        /// <summary>
        /// Deprecated: use GET /result/{id} instead.
        /// </summary>
        [HttpGet("GetSignResult/{id}")]
        [ApiExplorerSettings(IgnoreApi = false)] // hide from Swagger but still works
        public Task<IActionResult> GetFullResponse(string id) => GetResult(id);
    }
}