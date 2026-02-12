namespace MsignProxy.Models
{
    public class SignRequestDto
    {
        public string FileName { get; set; } = "document.pdf";
        public string FileBase64 { get; set; } = string.Empty;
        public string Description { get; set; } = "Digital Signature Request";
        public string ReturnUrl { get; set; } = "";
    }
    public class SignInitiateResponse{
        public string IdSign { get; set; } = string.Empty;
        public string RedirectUrl { get; set; } = string.Empty;
    }
}
