namespace MarkdownRenderer.Math.Internal.Scenes;

internal static class MathSceneValidation
{
    internal static void ThrowIfNotFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Scene coordinates must be finite.");
    }

    internal static void ThrowIfNotFiniteOrNegative(float value, string parameterName)
    {
        ThrowIfNotFinite(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
    }
}
