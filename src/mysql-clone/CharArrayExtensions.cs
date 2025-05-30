using System;

namespace mysql_clone;

internal static class CharArrayExtensions
{
    internal static char[] PadToLength(
        this char[] data,
        int requiredLength,
        char padWith
    )
    {
        if (data.Length > requiredLength)
        {
            throw new Exception($"Can't pad '{data}' to {requiredLength}: already exceeds this length");
        }

        if (data.Length == requiredLength)
        {
            return data;
        }

        var result = new char[requiredLength];
        data.CopyTo(result, 0);
        for (var i = data.Length; i < result.Length; i++)
        {
            result[i] = padWith;
        }

        return result;
    }
}
