namespace Cop.Lang;

public class EvaluationContext
{
    private readonly Dictionary<string, object> _captured = new();
    private readonly Dictionary<string, object> _ancestors = new();

    public void Capture(string modelType, object item)
    {
        _captured[modelType] = item;
    }

    public void PushAncestor(string paramType, object item)
    {
        _ancestors[paramType] = item;
    }

    public object? GetAncestor(string name)
    {
        return _ancestors.TryGetValue(name, out var item) ? item : null;
    }

    public object? Get(string modelType) =>
        _captured.TryGetValue(modelType, out var item) ? item : null;

    public EvaluationContext Clone()
    {
        var clone = new EvaluationContext();
        foreach (var (key, value) in _captured)
            clone._captured[key] = value;
        foreach (var (key, value) in _ancestors)
            clone._ancestors[key] = value;
        return clone;
    }
}
