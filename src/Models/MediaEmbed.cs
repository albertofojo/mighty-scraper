namespace MightyScraper.Models;

/// <summary>A video embed found on a lesson page (provider + id + private hash + original embed URL).</summary>
public sealed record MediaEmbed(string Provider, string Id, string? Hash, string EmbedUrl);
