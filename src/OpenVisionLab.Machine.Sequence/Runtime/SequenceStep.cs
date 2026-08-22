namespace OpenVisionLab.Machine.Sequence.Runtime;

public sealed class SequenceStep
{
    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<SequenceStep> Children { get; }

    public SequenceStep(string id, string name, IEnumerable<SequenceStep>? children = null)
    {
        Id = id;
        Name = name;
        Children = children?.ToList() ?? new List<SequenceStep>();
    }
}
