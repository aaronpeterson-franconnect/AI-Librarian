namespace AiLibrarian.Api.BlobStorage;

public sealed record UploadedBlobInfo(string BlobUri, string OriginalFileName, string? ContentType);
