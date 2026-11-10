using System.IO;

namespace JitHub.Services;

/// <summary>Marks a truncated Camo SVG body as a retryable transport failure.</summary>
internal sealed class TransientCamoSvgPayloadException()
    : IOException("GitHub Camo returned an incomplete SVG payload.");
