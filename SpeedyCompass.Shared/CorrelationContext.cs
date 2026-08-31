using System;
using System.Threading;

namespace SpeedyCompass.Shared;

public static class CorrelationContext
{
    private static readonly AsyncLocal<string> _correlationId = new();

    public static string Current
    {
        get => _correlationId.Value;
        set => _correlationId.Value = value;
    }

    // Centralized Generator: Creates the ID and instantly drops it in the backpack
    public static string GenerateNew()
    {
        var newId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
        Current = newId;
        return newId;
    }
}