// DTOs/FileDto.cs
namespace CorpMsg.DTOs
{
    public class UploadFileRequest
    {
        public IFormFile File { get; set; }
        public Guid ChatId { get; set; }
        public string? MediaType { get; set; } = "avatars";
    }

}