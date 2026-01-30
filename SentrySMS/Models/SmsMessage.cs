using System.ComponentModel.DataAnnotations;

namespace SentrySMS.Models;

public class SmsMessage
{
    [Required]
    [Phone]
    public string MobileNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(160, ErrorMessage = "SMS messages must be 160 characters or fewer.")]
    public string TextMessage { get; set; } = string.Empty;
}
