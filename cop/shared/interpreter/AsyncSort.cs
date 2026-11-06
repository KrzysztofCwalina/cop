// Sorts buffered values while awaiting comparisons sequentially.

namespace Cop.Lang.Interpreter;

/// <summary>Provides stable buffered sorting for comparisons that can suspend.</summary>
internal static class AsyncSort
{
    /// <summary>Sorts in source order for equal keys, awaiting each comparison before the next.</summary>
    public static async ValueTask SortAsync<T>(this List<T> items,
        Func<T, T, CancellationToken, ValueTask<int>> comparison,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count < 2)
        {
            return;
        }
        T[] source = items.ToArray();
        T[] destination = new T[source.Length];
        for (int width = 1; width < source.Length;
            width = width > source.Length / 2 ? source.Length : width * 2)
        {
            for (int start = 0; start < source.Length;)
            {
                int middle = (int)Math.Min((long)start + width, source.Length);
                int end = (int)Math.Min((long)start + 2L * width, source.Length);
                int left = start;
                int right = middle;
                for (int output = start; output < end; output++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (left < middle && (right == end
                        || await comparison(source[left], source[right], cancellationToken)
                            .ConfigureAwait(false) <= 0))
                    {
                        destination[output] = source[left++];
                    }
                    else
                    {
                        destination[output] = source[right++];
                    }
                }
                start = end;
            }
            (source, destination) = (destination, source);
        }
        for (int index = 0; index < source.Length; index++)
        {
            items[index] = source[index];
        }
    }
}
