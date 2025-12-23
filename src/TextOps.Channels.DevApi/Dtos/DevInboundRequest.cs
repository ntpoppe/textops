using System.ComponentModel.DataAnnotations;

namespace TextOps.Channels.DevApi.Dtos;

public sealed record DevInboundRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "Field 'from' is required and cannot be empty.")]
    public required string From { get; init; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Field 'conversation' is required and cannot be empty.")]
    public required string Conversation { get; init; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "Field 'body' is required and cannot be empty.")]
    public required string Body { get; init; }

    public string? ProviderMessageId { get; init; }
}

