using CorpMsg.DTOs;
using CorpMsg.Service;
using CorpMsg.Services;
using CorpMsg.SupportClasses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CorpMsg.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FileController : ControllerBase
    {
        private readonly IFileStorageService _fileStorageService;
        private readonly ILogger<FileController> _logger;

        public FileController(IFileStorageService fileStorageService, ILogger<FileController> logger)
        {
            _fileStorageService = fileStorageService;
            _logger = logger;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadFile([FromForm] UploadFileRequest request)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var result = await _fileStorageService.UploadFileAsync(request.File, request.ChatId, currentUserId);

            if (result.IsSuccess)
                return Ok(result.Data);

            return StatusCode(result.Error.StatusCode, result.Error);
        }

        [HttpGet("download")]
        public async Task<IActionResult> DownloadFile([FromQuery] string fileUrl)
        {
            try
            {
                var currentUserId = GetCurrentUserId();
                if (currentUserId == Guid.Empty)
                    return Unauthorized("Пользователь не аутентифицирован");

                var stream = await _fileStorageService.DownloadFileAsync(fileUrl);
                var contentType = GetContentType(fileUrl);

                return File(stream, contentType, Path.GetFileName(fileUrl));
            }
            catch (FileNotFoundException)
            {
                return NotFound("Файл не найден");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading file: {FileUrl}", fileUrl);
                return StatusCode(500, new ApiError
                {
                    Message = "Ошибка при загрузке файла",
                    StatusCode = 500
                });
            }
        }

        [HttpGet("presigned-url")]
        public async Task<IActionResult> GetPresignedUrl([FromQuery] string fileUrl, [FromQuery] int expirySeconds = 3600)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId == Guid.Empty)
                return Unauthorized("Пользователь не аутентифицирован");

            var url = await _fileStorageService.GeneratePresignedUrlAsync(fileUrl, TimeSpan.FromSeconds(expirySeconds));
            return Ok(new { Url = url });
        }

        private Guid GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            return userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId) ? userId : Guid.Empty;
        }

        private string GetContentType(string fileUrl)
        {
            var extension = Path.GetExtension(fileUrl).ToLowerInvariant();
            return extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }
    }
}